package com.tianshuqitan.headless.core

import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.atomic.AtomicLong

class GameSession internal constructor(
    private val loginReadTimeoutMillis: Int,
    private val socketFactory: () -> Socket = { Socket() },
    private val emit: (ClientEvent) -> Unit,
) : AutoCloseable {
    constructor(emit: (ClientEvent) -> Unit) : this(DEFAULT_LOGIN_READ_TIMEOUT_MS, emit = emit)
    constructor(socketFactory: () -> Socket, emit: (ClientEvent) -> Unit) :
        this(DEFAULT_LOGIN_READ_TIMEOUT_MS, socketFactory, emit)

    init { require(loginReadTimeoutMillis > 0) }
    private val executor = Executors.newSingleThreadExecutor { runnable ->
        Thread(runnable, "tianshu-game-session").apply { isDaemon = true }
    }
    private val commandExecutor = Executors.newSingleThreadExecutor { runnable ->
        Thread(runnable, "tianshu-command").apply { isDaemon = true }
    }
    private val protocolScheduler = Executors.newSingleThreadScheduledExecutor { runnable ->
        Thread(runnable, "tianshu-map-ready").apply { isDaemon = true }
    }
    private val sendLock = Any()
    private val writerLock = Any()
    private val connectionLock = Any()
    private val automationManager = AutomationManager(
        sendPacket = { description, factory -> sendAutomationPacket(description, factory) },
        emit = emit,
        requireMapReady = true,
    )

    @Volatile private var socket: Socket? = null
    @Volatile private var closedByUser = false
    @Volatile private var generation = 0
    @Volatile private var mapGeneration = 0
    @Volatile private var mapReadyTimeout: ScheduledFuture<*>? = null
    @Volatile private var snapshot = SessionSnapshot()
    private var readiness = SessionReadiness()
    private var writer = ClientFrameWriter()
    private var heartbeat = HeartbeatState()
    private val automationCommandGeneration = AtomicLong()

    fun login(request: LoginRequest): CompletableFuture<Void> {
        disconnect("开始新的登录", notify = false)
        validateLoginRequest(request)
        val generationValue = synchronized(connectionLock) {
            closedByUser = false
            ++generation
        }
        return CompletableFuture.runAsync({ runLogin(request, generationValue) }, executor)
    }

    fun disconnect(reason: String = "用户断开", notify: Boolean = true) {
        retireConnection(reason, notify)
    }

    private fun retireConnection(reason: String, notify: Boolean, expectedGeneration: Int? = null, error: Throwable? = null) {
        val current = synchronized(connectionLock) {
            if (expectedGeneration != null && !isCurrent(expectedGeneration)) return
            automationCommandGeneration.incrementAndGet()
            closedByUser = true
            generation += 1
            snapshot = SessionSnapshot()
            socket.also { socket = null }
        }
        // Close before taking automation/writer locks: a write on a dead mobile
        // route may be blocked while holding those locks, and close releases it.
        current?.closeQuietly()
        mapGeneration += 1
        mapReadyTimeout?.cancel(false)
        mapReadyTimeout = null
        automationManager.reset()
        if (error != null) emit(ClientEvent.Error(error.message ?: error.toString()))
        if (notify) emit(ClientEvent.Disconnected(reason))
    }

    fun startAutomation(command: AutomationCommand) {
        val requestedGeneration = generation
        val commandGeneration = automationCommandGeneration.get()
        commandExecutor.execute {
            try {
                if (!isCurrent(requestedGeneration) || commandGeneration != automationCommandGeneration.get()) return@execute
                if (!snapshot.online) error("角色尚未在线，不能启动自动化")
                automationManager.start(command) {
                    isCurrent(requestedGeneration) && commandGeneration == automationCommandGeneration.get()
                }
            } catch (error: Throwable) {
                if (isCurrent(requestedGeneration)) {
                    emit(ClientEvent.Warning("启动自动化失败：${error.message ?: error.javaClass.simpleName}"))
                }
            }
        }
    }

    fun stopAutomation() {
        automationCommandGeneration.incrementAndGet()
        automationManager.stop("用户停止")
    }

    fun reconnectLast(request: LoginRequest): CompletableFuture<Void> = login(request)

    fun getSnapshot(): SessionSnapshot {
        // Never wait for the automation lock here: UI/network callbacks must be
        // able to close a socket even while an automation write is blocked.
        val automation = automationManager.status
        return synchronized(connectionLock) {
            if (closedByUser) SessionSnapshot() else snapshot.copy(
                currentAutomation = automation.owner, automationState = automation.state, runLoopTask = automation.task,
            )
        }
    }

    private fun updateSnapshot(expectedGeneration: Int, update: (SessionSnapshot) -> SessionSnapshot) {
        synchronized(connectionLock) {
            if (isCurrent(expectedGeneration)) snapshot = update(snapshot)
        }
    }

    override fun close() {
        disconnect("关闭客户端")
        executor.shutdownNow()
        commandExecutor.shutdownNow()
        protocolScheduler.shutdownNow()
        automationManager.close()
    }

    private fun runLogin(request: LoginRequest, generationValue: Int) {
        var phase = "入口服认证"
        try {
            // Drain/cancel commands from the old connection before replacing any
            // writer, world state or synchronization barrier.
            commandExecutor.submit {
                if (!isCurrent(generationValue)) return@submit
                automationManager.reset()
                synchronized(writerLock) { writer = ClientFrameWriter(); heartbeat = HeartbeatState() }
                readiness = SessionReadiness()
                mapGeneration = readiness.epoch
                updateSnapshot(generationValue) { SessionSnapshot() }
            }.get()
            if (!isCurrent(generationValue)) return
            emit(ClientEvent.State("连接入口服", "${request.loginHost}:${request.loginPorts.joinToString(",")}"))
            val entrance = connectAny(request.loginHost, request.loginPorts, generationValue)
            if (!isCurrent(generationValue)) {
                entrance.closeQuietly()
                return
            }
            synchronized(writerLock) {
                if (!isCurrent(generationValue)) return
                send(buildUserPasswordFrame(writer, request.username, request.password), sensitive = true)
            }
            emit(ClientEvent.State("入口服认证", "凭据已发送，等待票据与线路列表"))
            val entranceData = readEntranceUntilReady(entrance, generationValue)
            entrance.closeQuietly()
            if (!isCurrent(generationValue)) return

            val selected = findPhysicalLine(entranceData.servers, request.line)
            emit(ClientEvent.Servers(entranceData.servers, selected.id))
            emit(ClientEvent.State("连接游戏服", "${selected.name} ${selected.address}:${selected.ports.joinToString(",")}"))
            val game = connectAny(selected.address, selected.ports, generationValue)
            if (!isCurrent(generationValue)) {
                game.closeQuietly()
                return
            }
            phase = "游戏服进图"
            synchronized(writerLock) {
                if (!isCurrent(generationValue)) return
                send(buildUserToken2Frame(writer, entranceData.token, request.forceLogin, request.operationCom, selected.id), sensitive = true)
            }
            emit(ClientEvent.State("游戏服认证", "票据已发送，等待角色列表"))
            commandExecutor.submit {
                if (isCurrent(generationValue)) armMapReadyTimeout(generationValue, readiness.epoch)
            }.get()
            readGameLoop(game, request, generationValue)
        } catch (error: Throwable) {
            if (isCurrent(generationValue)) {
                if (error is SocketTimeoutException && phase == "游戏服进图") {
                    commandExecutor.submit {
                        if (isCurrent(generationValue)) fail(mapInitializationTimeout(), generationValue)
                    }.get()
                } else {
                    fail(if (error is SocketTimeoutException) IllegalStateException("${phase}超时，未收到服务器响应。", error) else error, generationValue)
                }
            }
        }
    }

    private fun readEntranceUntilReady(socket: Socket, generationValue: Int): EntranceData {
        val stream = FrameStream()
        val input = socket.getInputStream()
        val buffer = ByteArray(8192)
        var token: String? = null
        var servers: List<GameServer>? = null
        while (isCurrent(generationValue)) {
            val read = input.read(buffer)
            if (read < 0) error("入口连接已关闭")
            for (frame in stream.push(buffer.copyOf(read))) {
                packet(PacketDirection.SERVER_TO_CLIENT, frame)
                if (frame.opcode == LoginOpcode.SC_OPERATE_FAIL) {
                    val failure = parseOperateFail(frame)
                    error("服务端拒绝操作（${failure.first}）：${failure.second}")
                }
                if (frame.opcode == LoginOpcode.SC_CLIENT_TOKEN) token = parseClientToken(frame)
                if (frame.opcode == LoginOpcode.SC_GAMESERVER_LIST) servers = parseGameServerList(frame)
                val currentToken = token
                val currentServers = servers
                if (currentToken != null && currentServers != null) return EntranceData(currentToken, currentServers)
            }
        }
        error("入口登录已取消")
    }

    private fun readGameLoop(socket: Socket, request: LoginRequest, generationValue: Int) {
        val stream = FrameStream()
        val input = socket.getInputStream()
        val buffer = ByteArray(8192)
        while (isCurrent(generationValue)) {
            val read = try {
                input.read(buffer)
            } catch (timeout: SocketTimeoutException) {
                // A read already blocked when the ready callback cleared
                // SO_TIMEOUT can still expire once. Online liveness is checked
                // by the heartbeat watchdog instead of the login deadline.
                if (snapshot.online && isCurrent(generationValue)) continue
                throw timeout
            }
            if (read < 0) error("游戏连接已关闭")
            for (frame in stream.push(buffer.copyOf(read))) {
                // Server frames, user commands and delayed map-ready callbacks share
                // one queue so a stale callback cannot unlock a newer map transition.
                commandExecutor.submit {
                if (!isCurrent(generationValue)) return@submit
                packet(PacketDirection.SERVER_TO_CLIENT, frame)
                if (frame.opcode == LoginOpcode.SC_OPERATE_FAIL) {
                    val failure = parseOperateFail(frame)
                    error("服务端拒绝操作（${failure.first}）：${failure.second}")
                }
                if (frame.opcode == LoginOpcode.SC_ROLE_INFO_LIST) {
                    val roles = parseRoleInfoList(frame)
                    emit(ClientEvent.Roles(roles, request.roleSlot))
                    val selected = roles.getOrNull(request.roleSlot - 1) ?: error("第 ${request.roleSlot} 个角色槽位不存在")
                    if (selected.status == 1) error("第 ${request.roleSlot} 个角色处于删除/恢复状态，已拒绝进入")
                    synchronized(writerLock) { send(buildSelectRoleFrame(writer, selected.characterId)) }
                    emit(ClientEvent.State("选择角色", "已选择第 ${request.roleSlot} 个角色，等待地图起点"))
                    return@submit
                }
                val map = GameProtocol.parseMapInfo(frame.bytes)
                if (map != null) {
                    readiness.mapInfo(map)
                    mapGeneration = readiness.epoch
                    automationManager.onMapTransition()
                    armMapReadyTimeout(generationValue, mapGeneration)
                    updateSnapshot(generationValue) { it.copy(currentMap = map, mapReady = false) }
                }
                GameProtocol.parseMapData(frame.bytes)?.let(readiness::mapData)
                if (GameProtocol.isValidTaskList(frame.bytes)) readiness.tasksReceived()
                if (frame.opcode == LoginOpcode.SC_ROLE_START_POINT && frame.bytes.size == 8) {
                    val point = parseRoleStartPoint(frame)
                    mapGeneration = readiness.startPoint(point)
                    automationManager.onMapTransition()
                    updateSnapshot(generationValue) { it.copy(mapReady = false) }
                    acknowledgeMapReady(generationValue)
                }
                if (frame.opcode == HeartbeatOpcode.SERVER_PING) {
                    val started = System.nanoTime()
                    val response = synchronized(writerLock) {
                        heartbeat.respond(frame, writer).also { send(it.response) }
                    }
                    val latencyMs = (System.nanoTime() - started) / 1_000_000.0
                    updateSnapshot(generationValue) { it.copy(lastHeartbeatAtMillis = System.currentTimeMillis()) }
                    emit(
                        ClientEvent.Heartbeat(
                            response.ping.counter,
                            response.ping.systemTime,
                            latencyMs,
                            "0x${response.authCode.toString(16).padStart(8, '0').uppercase()}",
                        ),
                    )
                }
                automationManager.onServerFrame(frame)
                publishReadyIfComplete(generationValue)
                val worldMap = if (automationManager.isMapReady) automationManager.world.currentMap else null
                updateSnapshot(generationValue) { it.copy(currentMap = worldMap ?: it.currentMap) }
                }.get()
            }
        }
    }

    private fun attach(value: Socket, generationValue: Int) {
        value.tcpNoDelay = true
        value.keepAlive = true
        // Until Entered, the online heartbeat watchdog cannot detect a silent
        // maintenance server. Bound this phase so the service can retry it.
        value.soTimeout = loginReadTimeoutMillis
        synchronized(connectionLock) {
            if (!isCurrent(generationValue)) {
                value.closeQuietly()
                error("登录已取消")
            }
            socket = value
        }
    }

    private fun sendAutomationPacket(description: String, factory: (Long) -> ByteArray) {
        val generationValue = generation
        try {
            val (sequence, frame) = synchronized(writerLock) {
                if (!isCurrent(generationValue)) return
                val next = writer.nextSequence()
                val packet = factory(next)
                send(packet)
                next to packet
            }
            emit(
                ClientEvent.Automation(
                    automationManager.currentOwner ?: "Auto",
                    "Packet",
                    "$description opcode=0x${readUInt16(frame, 2).toString(16).padStart(4, '0').uppercase()} seq=$sequence len=${frame.size}",
                ),
            )
        } catch (error: java.io.IOException) {
            // Transport loss is a session failure, not completion/failure of the
            // task itself. Keep the service's recovery command intact.
            fail(error, generationValue)
        }
    }

    private fun acknowledgeMapReady(generationValue: Int) {
        val mapGenerationValue = readiness.epoch
        armMapReadyTimeout(generationValue, mapGenerationValue)
        synchronized(writerLock) { send(buildEnterGamePrepareFrame(writer)) }
        protocolScheduler.schedule(
            {
                if (!isCurrent(generationValue)) return@schedule
                commandExecutor.execute {
                    if (!isCurrent(generationValue) || mapGenerationValue != mapGeneration) return@execute
                    try {
                        synchronized(writerLock) { send(buildEnterGameReadyFrame(writer)) }
                        readiness.acknowledge(mapGenerationValue)
                        publishReadyIfComplete(generationValue)
                        if (!readiness.ready) emit(ClientEvent.State("同步地图和任务", "等待本次连接的${readiness.missing()}。"))
                    } catch (error: Throwable) {
                        if (isCurrent(generationValue)) fail(error, generationValue)
                    }
                }
            },
            MAP_READY_ACTION_DELAY_MS,
            TimeUnit.MILLISECONDS,
        )
    }

    private fun publishReadyIfComplete(generationValue: Int) {
        if (!isCurrent(generationValue)) return
        updateSnapshot(generationValue) { it.copy(taskListReady = readiness.taskListReceived) }
        if (!readiness.ready || readiness.published) return
        readiness.publish()
        mapReadyTimeout?.cancel(false)
        mapReadyTimeout = null
        automationManager.onMapReady()
        val worldMap = automationManager.world.currentMap
        updateSnapshot(generationValue) { it.copy(currentMap = worldMap, mapReady = true) }
        if (!isCurrent(generationValue)) return
        if (!snapshot.online) {
            socket?.soTimeout = 0
            updateSnapshot(generationValue) { it.copy(online = true, lastHeartbeatAtMillis = System.currentTimeMillis()) }
            if (!isCurrent(generationValue)) return
            val point = requireNotNull(readiness.point)
            emit(ClientEvent.State("在线", "本次连接的地图、可走格、任务列表与进图确认已同步。"))
            emit(ClientEvent.Entered(point.x, point.y))
        }
    }

    private fun armMapReadyTimeout(generationValue: Int, mapGenerationValue: Int) {
        mapReadyTimeout?.cancel(false)
        mapReadyTimeout = protocolScheduler.schedule({
            if (!isCurrent(generationValue)) return@schedule
            commandExecutor.execute {
                if (isCurrent(generationValue) && mapGenerationValue == mapGeneration && !automationManager.isMapReady) {
                    fail(mapInitializationTimeout(), generationValue)
                }
            }
        }, loginReadTimeoutMillis.toLong(), TimeUnit.MILLISECONDS)
    }

    private fun mapInitializationTimeout() = IllegalStateException(
        "地图初始化超时，缺少本次连接的${readiness.missing()}，已断开以便恢复会话。",
    )

    private fun send(frame: ByteArray, sensitive: Boolean = false) {
        try {
            val current = socket ?: error("当前没有可写的服务器连接")
            synchronized(sendLock) {
                if (current.isClosed) error("当前服务器连接已关闭")
                packet(PacketDirection.CLIENT_TO_SERVER, frame)
                current.getOutputStream().write(frame)
                current.getOutputStream().flush()
            }
        } finally {
            if (sensitive) frame.fill(0)
        }
    }

    private fun packet(direction: PacketDirection, frame: Frame) = packet(direction, frame.bytes)

    private fun packet(direction: PacketDirection, bytes: ByteArray) {
        val opcode = readUInt16(bytes, 2)
        emit(ClientEvent.Packet(direction, opcode, bytes.size, opcodeName(opcode, direction)))
    }

    private fun connectAny(host: String, ports: List<Int>, generationValue: Int): Socket {
        val errors = mutableListOf<String>()
        for (port in ports) {
            if (!isCurrent(generationValue)) error("登录已取消")
            val candidate = socketFactory()
            try {
                attach(candidate, generationValue) // Also makes an in-flight connect cancellable.
                candidate.connect(InetSocketAddress(host, port), 10_000)
                return candidate
            } catch (error: Throwable) {
                candidate.closeQuietly()
                errors += "$port: ${error.message ?: error.javaClass.simpleName}"
            }
        }
        error("无法连接 $host 的候选端口：${errors.joinToString("；")}")
    }

    private fun validateLoginRequest(request: LoginRequest) {
        require(request.loginHost.isNotBlank()) { "入口服地址不能为空" }
        require(request.username.isNotEmpty()) { "账号不能为空" }
        require(request.password.isNotEmpty()) { "密码不能为空" }
        require(request.loginPorts.isNotEmpty() && request.loginPorts.all { it in 1..65535 }) { "入口服端口无效" }
        require(request.line in 1..4) { "线路必须为 1 到 4" }
        require(request.roleSlot in 1..5) { "角色槽位必须为 1 到 5" }
    }

    private fun fail(error: Throwable, generationValue: Int) {
        val cause = if (error is java.util.concurrent.ExecutionException) error.cause ?: error else error
        retireConnection("发生错误，连接已停止", notify = true, expectedGeneration = generationValue, error = cause)
    }

    private fun isCurrent(generationValue: Int): Boolean = generationValue == generation && !closedByUser

    private data class EntranceData(val token: String, val servers: List<GameServer>)

    private companion object {
        const val MAP_READY_ACTION_DELAY_MS = 60L
        const val DEFAULT_LOGIN_READ_TIMEOUT_MS = 30_000
    }
}

private fun Socket.closeQuietly() {
    try {
        close()
    } catch (_: Exception) {
    }
}
