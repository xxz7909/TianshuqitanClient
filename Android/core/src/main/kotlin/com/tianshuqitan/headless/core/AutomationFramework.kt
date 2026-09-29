package com.tianshuqitan.headless.core

import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

enum class TeleportMode { DIRECT_TELEPORT, LEGACY_TASK_LINK, FLY_TO_COORDINATE }

sealed interface AutomationCommand {
    data class RunLoop(val plannedRounds: Int = 6, val recovery: Boolean = true) : AutomationCommand
    data class StandaloneCombat(val recovery: Boolean = true) : AutomationCommand
    data class Bounty(val maxRounds: Int = 2, val allowWarrantRound: Boolean = false) : AutomationCommand
    data object MountainClimb : AutomationCommand
    data class MapTeleport(val mapId: Int, val mode: TeleportMode = TeleportMode.DIRECT_TELEPORT, val x: Int = 0, val y: Int = 0) : AutomationCommand
    data class Donation(val maxItems: Int = 20, val npcId: Int = GameProtocol.DONATION_NPC_ID) : AutomationCommand
    data object DailyAll : AutomationCommand
}

internal fun dailyAllCommands(): List<AutomationCommand> =
    listOf(
        AutomationCommand.Bounty(),
        AutomationCommand.RunLoop(),
    )

data class RunLoopWalkOptions(
    val stepIntervalMs: Long = 200,
    val initialDelayMs: Long = 200,
    val patrolPointCount: Int = 4,
    val mapDataWaitTimeoutMs: Long = 10_000,
    val noProgressTimeoutMs: Long = 75_000,
    val recoveryIntervalMs: Long = 4_000,
    val maximumHuntRetries: Int = 3,
) {
    init {
        require(stepIntervalMs > 0 && initialDelayMs >= 0 && patrolPointCount in 2..16)
        require(mapDataWaitTimeoutMs > 0 && noProgressTimeoutMs > 0 && recoveryIntervalMs > 0 && maximumHuntRetries >= 0)
    }
}

class WorldModel(private val clock: () -> Long = System::currentTimeMillis) {
    internal val battle = HeadlessBattleController(clock)
    internal var lastCombatRecoveryAt = 0L
    internal var bountyTask: BountyTask? = null
    var currentMap: MapInfo? = null
    var currentGrid: MapGrid? = null
    private var pendingGrid: MapGrid? = null
    private var pendingPoint: RoleStartPoint? = null
    var runLoopTask: RunLoopTask? = null
        private set
    private var runLoopTaskAt = 0L
    private var pendingRunLoopRing = 0
    private var expectedRunLoopRing = 0
    val entities: MutableMap<String, TravelTarget> = linkedMapOf()
    val inventory: MutableMap<Int, InventoryItem> = linkedMapOf()

    init { entities.putAll(RunLoopDestinations.npcs) }

    fun recentRunLoopTask(): RunLoopTask? {
        if (clock() - runLoopTaskAt >= 30 * 60_000) runLoopTask = null
        return runLoopTask
    }

    internal fun beginRunLoopTurnIn(ring: Int) { pendingRunLoopRing = ring }

    private fun learnRunLoopTask(task: RunLoopTask) {
        if (pendingRunLoopRing != 0 || (expectedRunLoopRing != 0 && task.ringNumber != expectedRunLoopRing)) return
        val old = runLoopTask
        if (old != null && (old.ringNumber != task.ringNumber || task.progress < old.progress)) return
        expectedRunLoopRing = 0
        runLoopTask = task
        runLoopTaskAt = clock()
    }

    fun reset() {
        battle.reset()
        lastCombatRecoveryAt = 0L
        bountyTask = null
        currentMap = null
        currentGrid = null
        pendingGrid = null
        pendingPoint = null
        runLoopTask = null
        runLoopTaskAt = 0
        pendingRunLoopRing = 0
        expectedRunLoopRing = 0
        entities.clear()
        entities.putAll(RunLoopDestinations.npcs)
        inventory.clear()
    }

    fun learnFrame(frame: Frame, runLoopRunning: Boolean = false) {
        battle.observe(frame)
        GameProtocol.parseMapInfo(frame.bytes)?.let { map ->
            currentMap = pendingPoint?.let { map.copy(scaledX = it.x, scaledY = it.y) } ?: map
            pendingPoint = null
            currentGrid = pendingGrid?.takeIf { it.mapId == map.mapId }
            pendingGrid = null
        }
        GameProtocol.parseMapData(frame.bytes)?.let {
            pendingGrid = it
            if (it.mapId == currentMap?.mapId) currentGrid = it
        }
        if (frame.opcode == LoginOpcode.SC_ROLE_START_POINT && frame.bytes.size == 8) {
            val point = parseRoleStartPoint(frame)
            currentMap = currentMap?.copy(scaledX = point.x, scaledY = point.y)
            if (currentMap == null) pendingPoint = point
        }
        GameProtocol.parseRunLoopTask(frame.bytes)?.let(::learnRunLoopTask)
        GameProtocol.parseRunLoopTaskList(frame.bytes)?.let(::learnRunLoopTask)
        GameProtocol.applyRunLoopTaskStatus(frame.bytes, runLoopTask)?.let(::learnRunLoopTask)
        if (GameProtocol.isValidTaskList(frame.bytes)) bountyTask = GameProtocol.parseBountySnapshot(frame.bytes)
        else GameProtocol.parseBountySnapshot(frame.bytes)?.let { bountyTask = it }
        if (GameProtocol.isTaskRemoved(frame.bytes, "1984") ||
            GameProtocol.parseNpcRemoved(frame.bytes)?.let { it == bountyTask?.target?.npcId } == true) bountyTask = null
        if (GameProtocol.isRunLoopTaskRemoved(frame.bytes) && (!runLoopRunning || pendingRunLoopRing != 0)) {
            val pending = pendingRunLoopRing
            val cached = runLoopTask?.ringNumber
            if (pending != 0 || cached != null) {
                expectedRunLoopRing = when {
                    pending != 0 -> pending % 20 + 1
                    !runLoopRunning && cached == 20 -> 1
                    else -> 0
                }
                pendingRunLoopRing = 0
                runLoopTask = null
                runLoopTaskAt = 0
            }
        }
        for (entity in GameProtocol.extractEntities(frame.bytes)) {
            val mapId = currentMap?.mapId ?: 0
            entities["${entity.name}@$mapId"] = TravelTarget(entity.id, mapId, entity.x, entity.y)
        }
        parseInventorySnapshot(frame.bytes).forEach { item -> if (item.isTargetFragment) inventory[item.slot] = item }
        parseInventoryUpdate(frame.bytes)?.let { item ->
            if (item.isTargetFragment) inventory[item.slot] = item
        }
        parseInventoryRemoval(frame.bytes)?.let { (bagId, slot) ->
            if (bagId == 1) inventory.remove(slot)
        }
    }

    fun findNpc(name: String, mapId: Int): TravelTarget? {
        val normalized = GameProtocol.normalizeName(name)
        if (normalized.isBlank()) return null
        entities["$normalized@$mapId"]?.let { return it }
        fun matches(key: String): Boolean {
            val candidate = key.substringBeforeLast('@')
            return candidate == normalized || candidate.endsWith(normalized) || normalized.endsWith(candidate)
        }
        return entities.entries.firstOrNull { it.value.mapId == mapId && matches(it.key) }?.value
            ?: entities.entries.firstOrNull { matches(it.key) }?.value
    }
}

data class InventoryItem(
    val bagId: Int,
    val slot: Int,
    val count: Int,
    val name: String = "",
    val templateId: String = "",
    val isTargetFragment: Boolean = false,
)

internal class AutomationManager(
    private val sendPacket: (String, (Long) -> ByteArray) -> Unit,
    private val emit: (ClientEvent) -> Unit,
    private val walkOptions: RunLoopWalkOptions = RunLoopWalkOptions(),
    private val requireMapReady: Boolean = false,
) : AutoCloseable {
    val world = WorldModel()
    private val lock = Any()
    private val ticker = Executors.newSingleThreadScheduledExecutor { runnable ->
        Thread(runnable, "tianshu-automation-tick").apply { isDaemon = true }
    }
    private var active: HeadlessAutomation? = null
    private var queue: ArrayDeque<AutomationCommand> = ArrayDeque()
    private var mapReady = !requireMapReady
    private val pendingMapFrames = ArrayDeque<Frame>()
    private var pendingMapBytes = 0
    data class Status(val owner: String? = null, val state: String = "Idle", val task: RunLoopTask? = null)
    @Volatile var status = Status()
        private set

    private fun refreshStatusLocked() {
        status = Status(active?.owner, active?.state ?: "Idle", world.runLoopTask)
    }

    val isMapReady: Boolean get() = synchronized(lock) { mapReady }
    val currentRunLoopTask: RunLoopTask? get() = synchronized(lock) { world.runLoopTask }

    val currentOwner: String?
        get() = synchronized(lock) { active?.owner }

    val currentState: String
        get() = synchronized(lock) { active?.state ?: "Idle" }

    init {
        ticker.scheduleAtFixedRate({ tick() }, 100, 100, TimeUnit.MILLISECONDS)
    }

    fun start(command: AutomationCommand, stillCurrent: () -> Boolean = { true }) {
        synchronized(lock) {
            if (!stillCurrent()) return
            check(mapReady) { "地图尚未就绪，请等待进图完成。" }
            if (command is AutomationCommand.DailyAll) {
                check(active == null) { "当前已有自动化流程正在运行：${active!!.owner}" }
                queue = ArrayDeque(dailyAllCommands())
                startLocked(queue.removeFirst())
                return
            }
            if (command is AutomationCommand.RunLoop && active?.owner == AutoCombatAutomation.OWNER) {
                stopLocked("启动跑环，接管独立自动战斗")
            }
            if (active != null) error("当前已有自动化流程正在运行：${active!!.owner}")
            queue.clear()
            startLocked(command)
        }
    }

    fun stop(reason: String) {
        synchronized(lock) {
            queue.clear()
            stopLocked(reason)
        }
    }

    fun reset() {
        synchronized(lock) {
            queue.clear()
            stopLocked("重置会话")
            world.reset()
            mapReady = !requireMapReady
            pendingMapFrames.clear()
            pendingMapBytes = 0
            refreshStatusLocked()
        }
    }

    fun onServerFrame(frame: Frame) {
        synchronized(lock) {
            if (requireMapReady && frame.opcode == GameOpcode.SERVER_MAP_INFO) mapReady = false
            if (!mapReady) {
                // Flash completes enterGame() before automation may act on a new map.
                // Preserve arrival/task/progress order until the headless handshake finishes.
                check(pendingMapBytes + frame.bytes.size <= 4 * 1024 * 1024) { "地图初始化报文超过上限，未继续自动化。" }
                pendingMapFrames.addLast(frame)
                pendingMapBytes += frame.bytes.size
                return
            }
            dispatchFrameLocked(frame)
        }
    }

    fun onMapTransition() {
        synchronized(lock) { if (requireMapReady) mapReady = false }
    }

    fun onMapReady() {
        synchronized(lock) {
            mapReady = true
            while (pendingMapFrames.isNotEmpty()) dispatchFrameLocked(pendingMapFrames.removeFirst())
            pendingMapBytes = 0
        }
    }

    private fun dispatchFrameLocked(frame: Frame) {
        try {
            world.learnFrame(frame, active?.owner == "AutoRunLoop")
            active?.onServerFrame(contextLocked(), frame)
        } catch (error: Throwable) {
            if (active != null) failLocked("处理服务器报文失败：${error.message ?: error.javaClass.simpleName}")
            else emit(ClientEvent.Warning("忽略无法解析的服务器报文：${error.message ?: error.javaClass.simpleName}"))
        } finally {
            refreshStatusLocked()
        }
    }

    override fun close() {
        stop("关闭自动化")
        ticker.shutdownNow()
    }

    private fun tick() {
        synchronized(lock) {
            if (!mapReady) return
            try {
                active?.onTick(contextLocked(), System.currentTimeMillis())
            } catch (error: Throwable) {
                if (active != null) failLocked("自动化执行失败：${error.message ?: error.javaClass.simpleName}")
            } finally {
                refreshStatusLocked()
            }
        }
    }

    private fun startLocked(command: AutomationCommand) {
        val automation = createAutomation(command)
        active = automation
        publishLocked(automation.owner, "Starting", "自动化已启动。", false)
        try {
            automation.onStart(contextLocked())
        } catch (error: Throwable) {
            active = null
            queue.clear()
            publishLocked(
                automation.owner,
                "Failed",
                "启动失败：${error.message ?: error.javaClass.simpleName}",
                true,
            )
            throw error
        } finally {
            refreshStatusLocked()
        }
    }

    private fun stopLocked(reason: String) {
        val old = active ?: return
        old.onStop(contextLocked(), reason)
        active = null
        publishLocked(old.owner, "Stopped", reason, false)
    }

    private fun completeLocked(message: String) {
        val old = active ?: return
        old.onStop(contextLocked(), message)
        publishLocked(old.owner, "Completed", message, false)
        active = null
        if (queue.isNotEmpty()) startLocked(queue.removeFirst())
        refreshStatusLocked()
    }

    private fun failLocked(message: String) {
        val old = active ?: return
        old.onStop(contextLocked(), message)
        publishLocked(old.owner, "Failed", message, true)
        active = null
        queue.clear()
        refreshStatusLocked()
    }

    private fun contextLocked(): AutomationContext =
        AutomationContext(
            world = world,
            send = sendPacket,
            publish = { state, detail, error -> active?.let { publishLocked(it.owner, state, detail, error) } },
            complete = { completeLocked(it) },
            fail = { failLocked(it) },
            requestSessionRefresh = { command, reason -> emit(ClientEvent.SessionRefreshRequired(command, reason)) },
        )

    private fun publishLocked(owner: String, state: String, detail: String, error: Boolean) {
        refreshStatusLocked()
        emit(ClientEvent.Automation(owner, state, detail, error))
    }

    private fun createAutomation(command: AutomationCommand): HeadlessAutomation =
        when (command) {
            is AutomationCommand.StandaloneCombat -> AutoCombatAutomation(command.recovery, walkOptions)
            is AutomationCommand.RunLoop -> RunLoopAutomation(command.plannedRounds, command.recovery, walkOptions)
            is AutomationCommand.Bounty -> BountyAutomation(command.maxRounds, command.allowWarrantRound)
            AutomationCommand.MountainClimb -> MountainClimbAutomation()
            is AutomationCommand.MapTeleport -> MapTeleportAutomation(command.mapId, command.mode, command.x, command.y)
            is AutomationCommand.Donation -> DonationAutomation(command.maxItems, command.npcId)
            AutomationCommand.DailyAll -> error("DailyAll is expanded before automation creation")
        }
}

internal class AutomationContext(
    val world: WorldModel,
    private val send: (String, (Long) -> ByteArray) -> Unit,
    private val publish: (String, String, Boolean) -> Unit,
    private val complete: (String) -> Unit,
    private val fail: (String) -> Unit,
    private val requestSessionRefresh: (AutomationCommand, String) -> Unit = { _, _ -> },
) {
    fun sendPacket(description: String, factory: (Long) -> ByteArray) = send(description, factory)
    fun publish(state: String, detail: String, error: Boolean = false) = publish.invoke(state, detail, error)
    fun complete(message: String) = complete.invoke(message)
    fun fail(message: String) = fail.invoke(message)
    fun requestSessionRefresh(command: AutomationCommand, reason: String) = requestSessionRefresh.invoke(command, reason)

    fun recoverCombatIfDue(nowMillis: Long, intervalMs: Long) {
        if (nowMillis - world.lastCombatRecoveryAt < intervalMs) return
        world.lastCombatRecoveryAt = nowMillis
        sendPacket("战斗一键恢复 HP/MP", GameProtocol::buildOneKeyRecovery)
    }
}

internal interface HeadlessAutomation {
    val owner: String
    val state: String
    fun onStart(ctx: AutomationContext)
    fun onServerFrame(ctx: AutomationContext, frame: Frame) {}
    fun onTick(ctx: AutomationContext, nowMillis: Long) {}
    fun onStop(ctx: AutomationContext, reason: String) {}
}

internal class AutoCombatAutomation(
    private val recovery: Boolean,
    private val options: RunLoopWalkOptions,
    private val clock: () -> Long = System::currentTimeMillis,
) : HeadlessAutomation {
    override val owner: String = OWNER
    override var state: String = "WalkingForEncounter"
        private set
    private var mapId = 0
    private var startAt = 0L
    private var lastStepAt = 0L
    private var lastProgressAt = 0L
    private var path: PatrolPath? = null

    override fun onStart(ctx: AutomationContext) {
        val map = ctx.world.currentMap ?: return ctx.fail("独立自动战斗需要先进入游戏并收到当前地图。")
        mapId = map.mapId
        startAt = clock()
        lastStepAt = startAt + options.initialDelayMs - options.stepIntervalMs
        if (ctx.world.lastCombatRecoveryAt == 0L) ctx.world.lastCombatRecoveryAt = startAt
        lastProgressAt = startAt
        if (ctx.world.battle.isInBattle) state = "WaitingForCombatResult"
        ctx.publish(state, "独立自动战斗已在 ${map.mapName} 启动。")
    }

    override fun onServerFrame(ctx: AutomationContext, frame: Frame) {
        if (frame.opcode == GameOpcode.SERVER_BATTLE_PROMPT) lastProgressAt = clock()
        if (ctx.world.battle.isInBattle && state != "WaitingForCombatResult") {
            state = "WaitingForCombatResult"
            ctx.publish(state, "已进入战斗，暂停走步并响应服务端回合提示。")
        } else if (!ctx.world.battle.isInBattle && state == "WaitingForCombatResult") {
            state = "WalkingForEncounter"
            lastStepAt = clock() + options.initialDelayMs - options.stepIntervalMs
            lastProgressAt = clock()
            ctx.publish(state, "战斗结束，继续自动遇怪。")
        }
        if (frame.opcode == GameOpcode.SERVER_MAP_DATA2 || frame.opcode == LoginOpcode.SC_ROLE_START_POINT) {
            path = null
            startAt = clock()
        }
        if (!ctx.world.battle.isInBattle && frame.opcode == GameOpcode.SERVER_SYSTEM_MESSAGE &&
            GameProtocol.containsText(frame.bytes, "不能走动")) return ctx.fail("服务器拒绝当前走步，独立自动战斗已停止。")
        val map = GameProtocol.parseMapInfo(frame.bytes)
        if (map != null && map.mapId != mapId) ctx.fail("当前地图已切换，独立自动战斗停止。")
    }

    override fun onTick(ctx: AutomationContext, nowMillis: Long) {
        val map = ctx.world.currentMap ?: return
        if (map.mapId != mapId) return ctx.fail("当前地图已切换，独立自动战斗停止。")
        if (ctx.world.battle.isInBattle) {
            if (ctx.world.battle.isStalled(nowMillis, options.noProgressTimeoutMs)) return ctx.fail("战斗响应超时，独立自动战斗已停止。")
            ctx.world.battle.onTick(ctx, nowMillis)
            if (recovery) ctx.recoverCombatIfDue(nowMillis, options.recoveryIntervalMs)
            return
        }
        if (nowMillis - lastProgressAt > options.noProgressTimeoutMs) return ctx.fail("75 秒未收到战斗提示，独立自动战斗停止。")
        val grid = ctx.world.currentGrid
        if (path == null) {
            path = PatrolPath.create(grid?.takeIf { it.mapId == mapId }, map.scaledX, map.scaledY, options.patrolPointCount)
            if (path == null) {
                if (nowMillis - startAt > options.mapDataWaitTimeoutMs) ctx.fail("等待地图可走格超时，未发送猜测走步包。")
                return
            }
        }
        if (nowMillis - lastStepAt >= options.stepIntervalMs) {
            lastStepAt = nowMillis
            val point = path!!.next()
            ctx.world.currentMap = map.copy(scaledX = point.x, scaledY = point.y)
            ctx.sendPacket("循环走步遇怪") { sequence ->
                GameProtocol.buildMovement(nowMillis, mapId, point.x, point.y, sequence)
            }
        }
        if (recovery) ctx.recoverCombatIfDue(nowMillis, options.recoveryIntervalMs)
    }

    companion object {
        const val OWNER = "AutoCombat"
    }
}

internal class MapTeleportAutomation(
    private val mapId: Int,
    private val mode: TeleportMode,
    private val x: Int,
    private val y: Int,
) : HeadlessAutomation {
    override val owner: String = "AutoMapTeleport"
    override var state: String = "Starting"
        private set
    private val destination = MapTeleportCatalog.required(mapId)
    private var selectedAt = 0L
    private var flightStage = 0

    override fun onStart(ctx: AutomationContext) {
        when (mode) {
            TeleportMode.DIRECT_TELEPORT -> {
                state = "SelectingMap"
                ctx.publish(state, "选择地图 ${destination.mapName}。")
                ctx.sendPacket("地图选择 ${destination.mapName}") { sequence -> GameProtocol.buildSelectMap(destination.mapId, sequence) }
                selectedAt = System.currentTimeMillis()
            }
            TeleportMode.LEGACY_TASK_LINK,
            TeleportMode.FLY_TO_COORDINATE,
            -> {
                state = "TravelingToTotem"
                ctx.publish(state, "通过旧任务链接前往 ${destination.mapName} 蟠龙图腾。")
                flightStage = 0
                ctx.sendPacket("旧任务链接到图腾 ${destination.mapName}") { sequence ->
                    GameProtocol.buildTravelRequest(destination.totemTarget(), sequence)
                }
            }
        }
    }

    override fun onServerFrame(ctx: AutomationContext, frame: Frame) {
        val dialog = GameProtocol.parseConfirmationDialog(frame.bytes)
        if (dialog != null && state == "TravelingToTotem") {
            state = "ConfirmingTotemTravel"
            ctx.publish(state, "确认传送到 ${destination.mapName}。")
            ctx.sendPacket("确认传送 ${dialog.title}") { sequence ->
                GameProtocol.buildDialogResponse(dialog.contextId, dialog.token, true, sequence)
            }
            state = "WaitingForMap"
            return
        }

        if (frame.opcode == GameOpcode.SERVER_MAP_START && state == "WaitingForMap") {
            handleMapArrival(ctx)
            return
        }

        val map = GameProtocol.parseMapInfo(frame.bytes)
        if (map != null && map.mapId == destination.mapId && state == "WaitingForMap") {
            handleMapArrival(ctx)
            return
        }

        if (frame.opcode == GameOpcode.SERVER_FLIGHT_LIST && state == "ActivatingFlight") {
            state = "FlyingToCoordinate"
            ctx.publish(state, "发送飞行坐标 ($x,$y)。")
            ctx.sendPacket("飞行到坐标") { sequence -> GameProtocol.buildFlyToCoordinate(x, y, sequence) }
            flightStage = 2
            state = "WaitingForMap"
        }
    }

    override fun onTick(ctx: AutomationContext, nowMillis: Long) {
        if (state == "SelectingMap" && nowMillis - selectedAt >= 750) {
            state = "RequestingTeleport"
            ctx.publish(state, "执行地图传送 ${destination.mapName}。")
            ctx.sendPacket("执行地图传送 ${destination.mapName}") { sequence -> GameProtocol.buildTeleportToMap(destination.mapId, sequence) }
            state = "WaitingForMap"
        }
    }

    private fun handleMapArrival(ctx: AutomationContext) {
        if (mode != TeleportMode.FLY_TO_COORDINATE || flightStage == 2) {
            ctx.complete(
                if (mode == TeleportMode.FLY_TO_COORDINATE) "已飞到 ${destination.mapName} ($x,$y)。" else "已到达 ${destination.mapName}。",
            )
            return
        }
        flightStage = 1
        state = "OpeningTotem"
        ctx.publish(state, "打开蟠龙图腾，准备飞行到 ($x,$y)。")
        ctx.sendPacket("访问蟠龙图腾 ${destination.totemNpcId}") { sequence ->
            GameProtocol.buildNpcOpen(destination.totemNpcId, sequence)
        }
        state = "ActivatingFlight"
        ctx.sendPacket("开启飞行列表") { sequence -> GameProtocol.buildActivateFlight(sequence) }
    }
}

private fun parseInventoryUpdate(bytes: ByteArray): InventoryItem? {
    if (!hasOpcode(bytes, GameOpcode.SERVER_ITEM_UPDATE) || bytes.size < 10) return null
    val text = bytes.decodeToString()
    val target = text.contains("115000140") || text.contains("上古神器碎片")
    return InventoryItem(
        bagId = readUInt16(bytes, 4),
        slot = readUInt16(bytes, 6),
        count = readUInt16(bytes, 8),
        name = if (target) "上古神器碎片" else "",
        templateId = if (target) "115000140" else "",
        isTargetFragment = target,
    )
}

private fun parseInventoryRemoval(bytes: ByteArray): Pair<Int, Int>? =
    if (hasOpcode(bytes, GameOpcode.SERVER_ITEM_REMOVE) && bytes.size >= 8) readUInt16(bytes, 4) to readUInt16(bytes, 6) else null

private fun parseInventorySnapshot(bytes: ByteArray): List<InventoryItem> {
    if (!hasOpcode(bytes, GameOpcode.SERVER_INVENTORY_SNAPSHOT_ZLIB)) return emptyList()
    val text = bytes.decodeToString()
    if (!text.contains("115000140") && !text.contains("上古神器碎片")) return emptyList()
    return listOf(InventoryItem(1, 0, 1, "上古神器碎片", "115000140", true))
}
