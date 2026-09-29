package com.tianshuqitan.headless.core

import java.io.EOFException
import java.io.InputStream
import java.io.ByteArrayOutputStream
import java.util.zip.DeflaterOutputStream
import java.net.ServerSocket
import java.net.Socket
import java.util.Collections
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class GameSessionIntegrationTest {
    @Test
    fun networkReconnectWaitsForDelayedFreshSnapshotsAndResumesRunLoopThenBounty() {
        ServerSocket(0).use { entrance ->
            ServerSocket(0).use { game ->
                val enteredCount = java.util.concurrent.atomic.AtomicInteger()
                val events = Collections.synchronizedList(mutableListOf<ClientEvent>())
                lateinit var session: GameSession
                session = GameSession { event ->
                    events.add(event)
                    if (event is ClientEvent.Entered) {
                        val command = if (enteredCount.getAndIncrement() == 0) AutomationCommand.RunLoop(recovery = false)
                            else AutomationCommand.Bounty()
                        session.startAutomation(command)
                    }
                }
                try {
                    repeat(2) { attempt ->
                        val server = CompletableFuture.runAsync {
                            serveEntrance(entrance, game.localPort)
                            game.accept().use { socket ->
                                socket.soTimeout = 3_000
                                readClientFrame(socket)
                                socket.getOutputStream().write(buildFrame(LoginOpcode.SC_ROLE_INFO_LIST, oneRole()))
                                readClientFrame(socket)
                                // Reconnection packets are deliberately staggered. A start point
                                // and the two enter-map actions must not publish "online" yet.
                                sendStartPoint(socket, 400 + attempt, 500, includeWorld = false)
                                assertMapReadyPair(socket, 1)
                                Thread.sleep(850) // Longer than the old service's fixed 750 ms wait.
                                assertFalse(session.getSnapshot().online)
                                assertEquals(null, session.getSnapshot().currentAutomation)
                                socket.getOutputStream().write(recordedFrame(240))
                                socket.getOutputStream().write(recordedFrame(241))
                                socket.soTimeout = 150
                                kotlin.test.assertFailsWith<java.net.SocketTimeoutException> { readClientFrame(socket) }
                                assertFalse(session.getSnapshot().online, "Map received without a task list is still syncing")
                                val record = if (attempt == 0) recordedFrame(214) else recordedBounty()
                                socket.getOutputStream().write(taskList(record))
                                socket.soTimeout = 3_000
                                val expected = if (attempt == 0) TravelTarget(10102, 101, 17, 73) else TravelTarget(90005, 15, 12, 57)
                                assertEquals(GameProtocol.buildTravelRequest(expected, 3).toHexUpper(), readClientFrame(socket).bytes.toHexUpper())
                                assertTrue(session.getSnapshot().mapReady)
                                assertTrue(session.getSnapshot().taskListReady)
                                assertEquals(400 + attempt, session.getSnapshot().currentMap?.scaledX)
                                assertEquals(500, session.getSnapshot().currentMap?.scaledY)
                                assertEquals(if (attempt == 0) 2 else null, session.getSnapshot().runLoopTask?.ringNumber)
                                socket.setSoLinger(true, 0) // Sudden transport loss, like the old Wi-Fi route disappearing.
                            }
                        }
                        val login = session.login(LoginRequest("127.0.0.1", listOf(entrance.localPort), "fixture-user", "fixture-password", line = 1))
                        server.get(6, TimeUnit.SECONDS)
                        login.get(3, TimeUnit.SECONDS)
                        assertFalse(session.getSnapshot().online)
                        assertEquals(null, session.getSnapshot().currentMap)
                        assertEquals(null, session.getSnapshot().runLoopTask)
                    }
                    assertEquals(2, enteredCount.get())
                    assertEquals(2, synchronized(events) { events.filterIsInstance<ClientEvent.Disconnected>().size })
                } finally { session.close() }
            }
        }
    }

    @Test
    fun missingTaskListCannotBecomeOnlineAndReportsTheMissingData() {
        ServerSocket(0).use { entrance ->
            ServerSocket(0).use { game ->
                val events = Collections.synchronizedList(mutableListOf<ClientEvent>())
                val session = GameSession(loginReadTimeoutMillis = 400, emit = events::add)
                val server = CompletableFuture.runAsync {
                    serveEntrance(entrance, game.localPort)
                    game.accept().use { socket ->
                        socket.soTimeout = 2_000
                        readClientFrame(socket)
                        socket.getOutputStream().write(buildFrame(LoginOpcode.SC_ROLE_INFO_LIST, oneRole()))
                        readClientFrame(socket)
                        socket.getOutputStream().write(recordedFrame(240))
                        socket.getOutputStream().write(recordedFrame(241))
                        sendStartPoint(socket, 100, 200, includeWorld = false)
                        assertMapReadyPair(socket, 1)
                        assertEquals(-1, socket.getInputStream().read())
                    }
                }
                try {
                    val login = session.login(LoginRequest("127.0.0.1", listOf(entrance.localPort), "fixture-user", "fixture-password", line = 1))
                    server.get(3, TimeUnit.SECONDS)
                    login.get(3, TimeUnit.SECONDS)
                    assertFalse(session.getSnapshot().online)
                    assertFalse(events.any { it is ClientEvent.Entered })
                    assertTrue(events.filterIsInstance<ClientEvent.Error>().single().message.contains("任务列表"))
                    assertEquals(1, events.filterIsInstance<ClientEvent.Disconnected>().size)
                } finally { session.close() }
            }
        }
    }

    @Test
    fun disconnectClosesTheSocketBeforeWaitingForABlockedAutomationWrite() {
        ServerSocket(0).use { entrance ->
            ServerSocket(0).use { game ->
                val blocked = BlockingTravelSocket()
                val created = java.util.concurrent.atomic.AtomicInteger()
                lateinit var session: GameSession
                session = GameSession(socketFactory = { if (created.incrementAndGet() == 2) blocked else Socket() }) { event ->
                    if (event is ClientEvent.Entered) session.startAutomation(AutomationCommand.RunLoop())
                }
                val server = CompletableFuture.runAsync {
                    serveEntrance(entrance, game.localPort)
                    game.accept().use { socket ->
                        socket.soTimeout = 3_000
                        readClientFrame(socket)
                        socket.getOutputStream().write(buildFrame(LoginOpcode.SC_ROLE_INFO_LIST, oneRole()))
                        readClientFrame(socket)
                        sendStartPoint(socket, 100, 200)
                        assertMapReadyPair(socket, 1)
                        assertEquals(-1, socket.getInputStream().read())
                    }
                }
                try {
                    session.login(LoginRequest("127.0.0.1", listOf(entrance.localPort), "fixture-user", "fixture-password", line = 1))
                    assertTrue(blocked.writing.await(2, TimeUnit.SECONDS))
                    assertTrue(CompletableFuture.supplyAsync { session.getSnapshot() }.get(1, TimeUnit.SECONDS).online)
                    CompletableFuture.runAsync { session.disconnect("network changed") }.get(2, TimeUnit.SECONDS)
                    server.get(3, TimeUnit.SECONDS)
                    assertFalse(session.getSnapshot().online)
                } finally { blocked.close(); session.close() }
            }
        }
    }

    private class BlockingTravelSocket : Socket() {
        val writing = CountDownLatch(1)
        private val released = CountDownLatch(1)
        override fun getOutputStream(): java.io.OutputStream {
            val delegate = super.getOutputStream()
            return object : java.io.OutputStream() {
                override fun write(value: Int) = delegate.write(value)
                override fun write(bytes: ByteArray, offset: Int, length: Int) {
                    if (length >= 4 && readUInt16(bytes, offset + 2) == GameOpcode.CLIENT_TRAVEL_LINK) {
                        writing.countDown()
                        check(released.await(5, TimeUnit.SECONDS)) { "Socket was not closed before waiting for the automation lock" }
                        throw java.net.SocketException("old route closed")
                    }
                    delegate.write(bytes, offset, length)
                }
            }
        }
        override fun close() { released.countDown(); super.close() }
    }

    private fun recordedBounty(): ByteArray = requireNotNull(javaClass.getResource("/runloop-recorded/bounty-510.hex"))
        .readText().trim().chunked(2).map { it.toInt(16).toByte() }.toByteArray()

    @Test
    fun recordedNpcTurnInRunsThroughTheTcpSession() {
        ServerSocket(0).use { entrance ->
            ServerSocket(0).use { game ->
                val events = Collections.synchronizedList(mutableListOf<ClientEvent>())
                lateinit var session: GameSession
                session = GameSession { event ->
                    events.add(event)
                    if (event is ClientEvent.Entered) session.startAutomation(AutomationCommand.RunLoop(recovery = false))
                }
                val server = CompletableFuture.runAsync {
                    serveEntrance(entrance, game.localPort)
                    game.accept().use { socket ->
                        socket.soTimeout = 5_000
                        assertEquals(LoginOpcode.CS_USER_TOKEN2, readClientFrame(socket).opcode)
                        val output = socket.getOutputStream()
                        output.write(buildFrame(LoginOpcode.SC_ROLE_INFO_LIST, oneRole()))
                        assertEquals(LoginOpcode.CS_SELECT_ROLE, readClientFrame(socket).opcode)
                        output.write(recordedFrame(214))
                        sendStartPoint(socket, 100, 200)
                        assertMapReadyPair(socket, 1)
                        assertEquals(GameProtocol.buildTravelRequest(TravelTarget(10102, 101, 17, 73), 3).toHexUpper(),
                            readClientFrame(socket).bytes.toHexUpper())

                        output.write(buildFrame(GameOpcode.SERVER_CONFIRMATION_DIALOG,
                            BinaryWriter().writeUInt32(987).writeString("是否传送")
                                .writeString("本次本地回放确认").writeString("current-fixture-token").toByteArray()))
                        assertEquals(GameProtocol.buildDialogResponse(987, "current-fixture-token", true, 4).toHexUpper(),
                            readClientFrame(socket).bytes.toHexUpper())
                        output.write(recordedFrame(240))
                        output.write(recordedFrame(241))
                        output.write(recordedFrame(243))
                        assertMapReadyPair(socket, 5)
                        assertEquals(GameProtocol.buildNpcOpen(10102, 7).toHexUpper(), readClientFrame(socket).bytes.toHexUpper())
                        output.write(recordedFrame(248))
                        assertEquals(GameProtocol.buildNpcFunction(10102, GameProtocol.RUN_LOOP_TURN_IN_FUNCTION_ID, 8).toHexUpper(),
                            readClientFrame(socket).bytes.toHexUpper())
                        output.write(buildFrame(GameOpcode.SERVER_TASK_REMOVED,
                            BinaryWriter().writeString(GameProtocol.RUN_LOOP_TASK_ID).toByteArray()))
                        output.write(recordedFrame(270))
                        assertEquals(GameProtocol.buildTravelRequest(TravelTarget(8011, 13, 30, 100), 9).toHexUpper(),
                            readClientFrame(socket).bytes.toHexUpper())
                        assertEquals(3, session.getSnapshot().runLoopTask?.ringNumber)
                        assertTrue(synchronized(events) { events.none { it is ClientEvent.Error || (it is ClientEvent.Automation && it.error) } })
                    }
                }
                try {
                    session.login(LoginRequest("127.0.0.1", listOf(entrance.localPort), "fixture-user", "fixture-password", line = 1))
                    server.get(10, TimeUnit.SECONDS)
                } finally { session.close() }
            }
        }
    }

    private fun recordedFrame(id: Int): ByteArray =
        requireNotNull(javaClass.getResource("/runloop-recorded/frame-$id.hex")).readText().trim()
            .chunked(2).map { it.toInt(16).toByte() }.toByteArray()

    @Test
    fun missingStartPointAfterAnOnlineMapTransitionTimesOut() {
        ServerSocket(0).use { entrance ->
            ServerSocket(0).use { game ->
                val events = Collections.synchronizedList(mutableListOf<ClientEvent>())
                val entered = CountDownLatch(1)
                val disconnected = CountDownLatch(1)
                val session = GameSession(loginReadTimeoutMillis = 500) { event ->
                    events.add(event)
                    if (event is ClientEvent.Entered) entered.countDown()
                    if (event is ClientEvent.Disconnected) disconnected.countDown()
                }
                val server = CompletableFuture.runAsync {
                    serveEntrance(entrance, game.localPort)
                    game.accept().use { socket ->
                        socket.soTimeout = 3_000
                        assertEquals(LoginOpcode.CS_USER_TOKEN2, readClientFrame(socket).opcode)
                        socket.getOutputStream().write(buildFrame(LoginOpcode.SC_ROLE_INFO_LIST, oneRole()))
                        assertEquals(LoginOpcode.CS_SELECT_ROLE, readClientFrame(socket).opcode)
                        sendStartPoint(socket, 100, 200)
                        assertMapReadyPair(socket, 1)
                        assertTrue(entered.await(1, TimeUnit.SECONDS))
                        // Online socket has no login timeout. A map-info packet without
                        // its start point must nevertheless fail instead of freezing the owner.
                        socket.getOutputStream().write(buildFrame(GameOpcode.SERVER_MAP_INFO,
                            BinaryWriter().writeUInt32(100).writeString("怒焰祭坛")
                                .writeUInt32(416).writeUInt32(1216).toByteArray()))
                        assertEquals(-1, socket.getInputStream().read())
                    }
                }
                try {
                    session.login(LoginRequest("127.0.0.1", listOf(entrance.localPort), "fixture-user", "fixture-password", line = 1))
                    server.get(5, TimeUnit.SECONDS)
                    assertTrue(disconnected.await(1, TimeUnit.SECONDS))
                    assertTrue(synchronized(events) { events.filterIsInstance<ClientEvent.Error>().any { it.message.contains("地图初始化超时") } })
                    assertFalse(session.getSnapshot().online)
                } finally { session.close() }
            }
        }
    }

    @Test
    fun stoppingCancelsAnAlreadyQueuedStartWithoutDisconnecting() {
        ServerSocket(0).use { entrance ->
            ServerSocket(0).use { game ->
                val stopped = CountDownLatch(1)
                lateinit var session: GameSession
                session = GameSession { event ->
                    if (event is ClientEvent.Entered) {
                        // Called on the command executor, so the start is still queued.
                        session.startAutomation(AutomationCommand.RunLoop())
                        session.stopAutomation()
                        stopped.countDown()
                    }
                }
                val server = CompletableFuture.runAsync {
                    serveEntrance(entrance, game.localPort)
                    game.accept().use { socket ->
                        socket.soTimeout = 2_000
                        readClientFrame(socket)
                        socket.getOutputStream().write(buildFrame(LoginOpcode.SC_ROLE_INFO_LIST, oneRole()))
                        readClientFrame(socket)
                        sendStartPoint(socket, 100, 200)
                        assertMapReadyPair(socket, 1)
                        assertTrue(stopped.await(1, TimeUnit.SECONDS))
                        socket.soTimeout = 300
                        kotlin.test.assertFailsWith<java.net.SocketTimeoutException> { readClientFrame(socket) }
                        assertTrue(session.getSnapshot().online)
                        assertEquals(null, session.getSnapshot().currentAutomation)
                    }
                }
                try {
                    session.login(LoginRequest("127.0.0.1", listOf(entrance.localPort), "fixture-user", "fixture-password", line = 1))
                    server.get(5, TimeUnit.SECONDS)
                } finally { session.close() }
            }
        }
    }

    @Test
    fun silentEntranceTimesOutAndPublishesOneDisconnect() {
        ServerSocket(0).use { entrance ->
            val events = Collections.synchronizedList(mutableListOf<ClientEvent>())
            val session = GameSession(loginReadTimeoutMillis = 150, emit = events::add)
            val server = CompletableFuture.runAsync {
                entrance.accept().use { socket ->
                    socket.soTimeout = 2_000
                    assertEquals(LoginOpcode.CS_USER_PASS, readClientFrame(socket).opcode)
                    assertEquals(-1, socket.getInputStream().read(), "Timed out login must close the socket")
                }
            }
            try {
                session.login(LoginRequest("127.0.0.1", listOf(entrance.localPort), "fixture-user", "fixture-password"))
                    .get(3, TimeUnit.SECONDS)
                server.get(3, TimeUnit.SECONDS)
                val captured = synchronized(events) { events.toList() }
                assertEquals(1, captured.filterIsInstance<ClientEvent.Error>().size)
                assertTrue(captured.filterIsInstance<ClientEvent.Error>().single().message.contains("入口服认证超时"))
                assertEquals(1, captured.filterIsInstance<ClientEvent.Disconnected>().size)
                assertFalse(session.getSnapshot().online)
            } finally {
                session.close()
            }
        }
    }

    @Test
    fun gameAuthenticationWithoutMapEntryTimesOutInsteadOfStayingOnline() {
        ServerSocket(0).use { entrance ->
            ServerSocket(0).use { game ->
                val events = Collections.synchronizedList(mutableListOf<ClientEvent>())
                val session = GameSession(loginReadTimeoutMillis = 200, emit = events::add)
                val server = CompletableFuture.runAsync {
                    serveEntrance(entrance, game.localPort)
                    game.accept().use { socket ->
                        socket.soTimeout = 2_000
                        assertEquals(LoginOpcode.CS_USER_TOKEN2, readClientFrame(socket).opcode)
                        socket.getOutputStream().apply {
                            write(buildFrame(LoginOpcode.SC_ACCOUNT_INFO))
                            write(buildFrame(LoginOpcode.SC_ROLE_INFO_LIST, oneRole()))
                            flush()
                        }
                        assertEquals(LoginOpcode.CS_SELECT_ROLE, readClientFrame(socket).opcode)
                        assertEquals(-1, socket.getInputStream().read())
                    }
                }
                try {
                    session.login(LoginRequest("127.0.0.1", listOf(entrance.localPort), "fixture-user", "fixture-password", line = 1))
                        .get(3, TimeUnit.SECONDS)
                    server.get(3, TimeUnit.SECONDS)
                    val captured = synchronized(events) { events.toList() }
                    assertFalse(captured.any { it is ClientEvent.Entered })
                    assertTrue(captured.filterIsInstance<ClientEvent.Error>().single().message.contains("超时"))
                    assertFalse(session.getSnapshot().online)
                } finally {
                    session.close()
                }
            }
        }
    }

    @Test
    fun disconnectDuringMapPreparationCancelsDelayedReadyAndEntered() {
        ServerSocket(0).use { entrance ->
            ServerSocket(0).use { game ->
                val events = Collections.synchronizedList(mutableListOf<ClientEvent>())
                val session = GameSession(events::add)
                val server = CompletableFuture.runAsync {
                    serveEntrance(entrance, game.localPort)
                    game.accept().use { socket ->
                        socket.soTimeout = 2_000
                        assertEquals(LoginOpcode.CS_USER_TOKEN2, readClientFrame(socket).opcode)
                        socket.getOutputStream().apply {
                            write(buildFrame(LoginOpcode.SC_ROLE_INFO_LIST, oneRole()))
                            flush()
                        }
                        assertEquals(LoginOpcode.CS_SELECT_ROLE, readClientFrame(socket).opcode)
                        sendStartPoint(socket, 100, 200)
                        val prepare = readClientFrame(socket)
                        assertEquals(GameProtocol.buildUiAction(ENTER_GAME_PREPARE_ACTION, 3, -1, 0, 1).toHexUpper(), prepare.bytes.toHexUpper())
                        session.disconnect("fixture cancellation")
                        assertEquals(-1, socket.getInputStream().read())
                    }
                }
                try {
                    session.login(LoginRequest("127.0.0.1", listOf(entrance.localPort), "fixture-user", "fixture-password", line = 1))
                    server.get(3, TimeUnit.SECONDS)
                    assertFalse(session.getSnapshot().online)
                    assertFalse(synchronized(events) { events.any { it is ClientEvent.Entered } })
                } finally {
                    session.close()
                }
            }
        }
    }

    @Test
    fun mapReadyClearsTheLoginReadTimeoutForOnlineSessions() {
        ServerSocket(0).use { entrance ->
            ServerSocket(0).use { game ->
                val disconnected = CountDownLatch(1)
                val session = GameSession(loginReadTimeoutMillis = 200) { event ->
                    if (event is ClientEvent.Disconnected) disconnected.countDown()
                }
                val server = CompletableFuture.runAsync {
                    serveEntrance(entrance, game.localPort)
                    game.accept().use { socket ->
                        socket.soTimeout = 2_000
                        assertEquals(LoginOpcode.CS_USER_TOKEN2, readClientFrame(socket).opcode)
                        socket.getOutputStream().apply {
                            write(buildFrame(LoginOpcode.SC_ROLE_INFO_LIST, oneRole()))
                            flush()
                        }
                        assertEquals(LoginOpcode.CS_SELECT_ROLE, readClientFrame(socket).opcode)
                        sendStartPoint(socket, 100, 200)
                        assertMapReadyPair(socket, firstSequence = 1)
                        assertFalse(disconnected.await(400, TimeUnit.MILLISECONDS), "Online connection must outlive the login timeout")
                        assertTrue(session.getSnapshot().online)
                    }
                }
                try {
                    session.login(LoginRequest("127.0.0.1", listOf(entrance.localPort), "fixture-user", "fixture-password", line = 1))
                    server.get(3, TimeUnit.SECONDS)
                } finally {
                    session.close()
                }
            }
        }
    }

    @Test
    fun loginPacketsDoNotPublishOnlineAndEnteredAutomationFollowsMapReady() {
        ServerSocket(0).use { entrance ->
            ServerSocket(0).use { game ->
                val events = Collections.synchronizedList(mutableListOf<ClientEvent>())
                lateinit var session: GameSession
                session = GameSession { event ->
                    events.add(event)
                    if (event is ClientEvent.Entered) session.startAutomation(AutomationCommand.RunLoop())
                }
                val server = CompletableFuture.runAsync {
                    serveEntrance(entrance, game.localPort)
                    game.accept().use { socket ->
                        socket.soTimeout = 5_000
                        assertEquals(LoginOpcode.CS_USER_TOKEN2, readClientFrame(socket).opcode)
                        socket.getOutputStream().apply {
                            write(buildFrame(LoginOpcode.SC_ACCOUNT_INFO))
                            write(buildFrame(LoginOpcode.SC_ROLE_INFO_LIST, oneRole()))
                            flush()
                        }
                        assertEquals(LoginOpcode.CS_SELECT_ROLE, readClientFrame(socket).opcode)
                        assertFalse(session.getSnapshot().online, "Authentication packets are not successful map entry")
                        val map = BinaryWriter().writeUInt32(3).writeString("新月村").writeUInt32(0).writeUInt32(32).toByteArray()
                        val task = BinaryWriter().writeString(GameProtocol.RUN_LOOP_TASK_ID)
                            .writeString("跑环任务").writeString("跑环任务（第7环）")
                            .writeString("与灵昌城的钱不与(19,99)对话，领取下一环任务。")
                            .writeString("").toByteArray()
                        socket.getOutputStream().apply {
                            write(buildFrame(GameOpcode.SERVER_TASK_UPDATE, task))
                            flush()
                        }
                        sendStartPoint(socket, 100, 200)
                        assertMapReadyPair(socket, firstSequence = 1)
                        // The task snapshot and authoritative start point must already
                        // be available when Entered causes immediate automation start.
                        assertEquals(
                            GameProtocol.buildTravelRequest(TravelTarget(8025, 12, 19, 99), 3).toHexUpper(),
                            readClientFrame(socket).bytes.toHexUpper(),
                        )
                        assertTrue(session.getSnapshot().online)
                        assertEquals(100, session.getSnapshot().currentMap?.scaledX)
                        assertEquals(200, session.getSnapshot().currentMap?.scaledY)
                    }
                }
                try {
                    session.login(LoginRequest("127.0.0.1", listOf(entrance.localPort), "fixture-user", "fixture-password", line = 1))
                    server.get(8, TimeUnit.SECONDS)
                    val captured = synchronized(events) { events.toList() }
                    val enteredAt = captured.indexOfFirst { it is ClientEvent.Entered }
                    assertEquals(2, captured.take(enteredAt).count {
                        it is ClientEvent.Packet && it.direction == PacketDirection.CLIENT_TO_SERVER && it.opcode == 0x002e
                    })
                } finally {
                    session.close()
                }
            }
        }
    }

    @Test
    fun everyRoleStartPointSendsTheRecordedMapReadyPair() {
        ServerSocket(0).use { entrance ->
            ServerSocket(0).use { game ->
                val server = CompletableFuture.runAsync {
                    serveEntrance(entrance, game.localPort)
                    serveGame(game)
                }
                val events = Collections.synchronizedList(mutableListOf<ClientEvent>())
                val session = GameSession(events::add)
                try {
                    session.login(
                        LoginRequest(
                            loginHost = "127.0.0.1",
                            loginPorts = listOf(entrance.localPort),
                            username = "fixture-user",
                            password = "fixture-password",
                            line = 1,
                            roleSlot = 1,
                        ),
                    )
                    server.get(8, TimeUnit.SECONDS)
                    assertTrue(events.any { it is ClientEvent.Entered && it.x == 100 && it.y == 200 })
                } finally {
                    session.close()
                }
            }
        }
    }

    private fun serveEntrance(server: ServerSocket, gamePort: Int) {
        server.accept().use { socket ->
            socket.soTimeout = 5_000
            assertEquals(LoginOpcode.CS_USER_PASS, readClientFrame(socket).opcode)
            val token = BinaryWriter().writeString("A".repeat(64)).toByteArray()
            val servers = BinaryWriter()
                .writeInt32(1)
                .writeString("fixture-line")
                .writeString("测试一线")
                .writeString("127.0.0.1")
                .writeString(gamePort.toString())
                .writeInt32(0)
                .toByteArray()
            socket.getOutputStream().apply {
                write(buildFrame(LoginOpcode.SC_CLIENT_TOKEN, token))
                write(buildFrame(LoginOpcode.SC_GAMESERVER_LIST, servers))
                flush()
            }
        }
    }

    private fun serveGame(server: ServerSocket) {
        server.accept().use { socket ->
            socket.soTimeout = 5_000
            assertEquals(LoginOpcode.CS_USER_TOKEN2, readClientFrame(socket).opcode)
            socket.getOutputStream().apply {
                write(buildFrame(LoginOpcode.SC_ROLE_INFO_LIST, oneRole()))
                flush()
            }
            assertEquals(LoginOpcode.CS_SELECT_ROLE, readClientFrame(socket).opcode)

            sendStartPoint(socket, 100, 200)
            assertMapReadyPair(socket, firstSequence = 1)
            sendStartPoint(socket, 300, 400)
            assertMapReadyPair(socket, firstSequence = 3)
        }
    }

    private fun sendStartPoint(socket: Socket, x: Int, y: Int, includeWorld: Boolean = true) {
        socket.getOutputStream().apply {
            if (includeWorld) {
                write(recordedFrame(240))
                write(recordedFrame(241))
                write(taskList())
            }
            write(buildFrame(LoginOpcode.SC_ROLE_START_POINT, BinaryWriter().writeInt16(x).writeInt16(y).toByteArray()))
            flush()
        }
    }

    private fun taskList(vararg records: ByteArray): ByteArray {
        val body = BinaryWriter().writeUInt16(records.size)
        records.forEach { body.writeBytes(it.copyOfRange(4, it.size)) }
        val output = ByteArrayOutputStream()
        DeflaterOutputStream(output).use { it.write(body.toByteArray()) }
        val compressed = output.toByteArray()
        return buildFrame(GameOpcode.SERVER_TASK_LIST, BinaryWriter().writeUInt16(compressed.size).writeBytes(compressed).toByteArray())
    }

    private fun assertMapReadyPair(socket: Socket, firstSequence: Int) {
        val prepare = readClientFrame(socket).bytes
        val ready = readClientFrame(socket).bytes
        assertEquals(
            GameProtocol.buildUiAction(ENTER_GAME_PREPARE_ACTION, 3, -1, 0, firstSequence.toLong()).toHexUpper(),
            prepare.toHexUpper(),
        )
        assertEquals(
            GameProtocol.buildUiAction(ENTER_GAME_READY_ACTION, 0, 0, 0, (firstSequence + 1).toLong()).toHexUpper(),
            ready.toHexUpper(),
        )
    }

    private fun oneRole(): ByteArray = BinaryWriter()
        .writeUInt16(1)
        .writeInt32(12345)
        .writeString("测试角色")
        .writeString("男")
        .writeString("侠客")
        .writeInt32(103)
        .writeString("120000")
        .writeString("130002")
        .writeString("0")
        .writeString("")
        .apply { repeat(12) { writeInt32(0) } }
        .writeString("")
        .writeInt16(0)
        .toByteArray()

    private fun readClientFrame(socket: Socket): Frame {
        val input = socket.getInputStream()
        val first = input.read()
        val second = input.read()
        if (first < 0 || second < 0) throw EOFException("client closed before frame header")
        val length = (first shl 8) or second
        require(length >= 4)
        val bytes = ByteArray(length)
        bytes[0] = first.toByte()
        bytes[1] = second.toByte()
        input.readFully(bytes, 2, length - 2)
        return Frame(readUInt16(bytes, 2), bytes.copyOfRange(4, bytes.size), bytes)
    }
}

private fun InputStream.readFully(target: ByteArray, offset: Int, length: Int) {
    var position = offset
    val end = offset + length
    while (position < end) {
        val count = read(target, position, end - position)
        if (count < 0) throw EOFException("stream ended with ${end - position} byte(s) missing")
        position += count
    }
}
