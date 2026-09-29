package com.tianshuqitan.headless.core

import java.io.ByteArrayOutputStream
import java.util.zip.Deflater
import java.util.zip.DeflaterOutputStream
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Replays the Windows coordinator's protocol order using a controllable clock. */
class RunLoopParityTest {
    @Test fun arrivalAndNpcActionsUseWindowsDelaysAndMapIdentity() {
        val h = RunLoopHarness()
        h.start()
        h.feed(confirmation("是否传送"))
        assertEquals(1, h.sent.size)
        h.advance(159)
        assertEquals(1, h.sent.size)
        h.advance(1)
        assertEquals(GameOpcode.CLIENT_DIALOG_RESPONSE, h.lastOpcode())
        h.feed(frame(GameOpcode.SERVER_MAP_START))
        h.feed(mapFrame(3, "新月村"))
        h.advance(500)
        assertEquals(2, h.sent.size)
        h.feed(mapFrame(72, "尚其村"))
        h.feed(frame(GameOpcode.SERVER_MAP_START))
        h.advance(449)
        assertEquals(2, h.sent.size)
        h.advance(1)
        assertEquals(GameOpcode.CLIENT_NPC_OPEN, h.lastOpcode())
        assertEquals(93290, readUInt32(h.sent.last(), 4).toInt())
        h.feed(npcDialog(8011, GameProtocol.RUN_LOOP_ACCEPT_FUNCTION_ID))
        h.advance(200)
        assertEquals(3, h.sent.size)
        h.feed(npcDialog(93290, GameProtocol.RUN_LOOP_ACCEPT_FUNCTION_ID))
        h.advance(179)
        assertEquals(3, h.sent.size)
        h.advance(1)
        assertEquals(GameOpcode.CLIENT_NPC_FUNCTION, h.lastOpcode())
        assertTrue(h.sent.last().decodeToString().contains("140000001"))
        assertEquals("WaitingForTask", h.automation.state)
    }

    @Test fun ignoresUnrelatedConfirmationAndRefreshesAnUnfinishedTaskOnce() {
        val h = RunLoopHarness()
        h.start()
        h.feed(confirmation("其它操作"))
        h.advance(500)
        assertEquals(1, h.sent.size)
        h.arriveAndSelect(93290, 72, GameProtocol.RUN_LOOP_ACCEPT_FUNCTION_ID)
        h.feed(frame(GameOpcode.SERVER_SYSTEM_MESSAGE, BinaryWriter().writeString("您身上还有未完成的任务。").toByteArray()))
        assertEquals(AutomationCommand.RunLoop(1, false), h.refreshCommand)
        assertTrue(h.refreshReason!!.contains("同步任务列表"))
        h.advance(15_000)
        assertNull(h.failure)
    }

    @Test fun serverDailyLimitCompletesWithoutRetry() {
        val h = RunLoopHarness()
        h.start()
        h.feed(frame(GameOpcode.SERVER_SYSTEM_MESSAGE, BinaryWriter().writeString("今日跑环次数已达上限").toByteArray()))
        assertNotNull(h.completed)
        h.advance(60_000)
        assertEquals(1, h.sent.size)
        assertNull(h.failure)
    }

    @Test fun taskListWithoutChecksumResumesExistingRingInsteadOfAcceptingAgain() {
        val list = taskList(7)
        assertEquals(7, GameProtocol.parseRunLoopTaskList(list.bytes)?.ringNumber)
        val h = RunLoopHarness()
        h.world.learnFrame(list)
        h.start()
        val travel = travelTarget(h.sent.single())
        assertEquals(TravelTarget(1002, 3, 17, 61), travel)
        assertEquals("Traveling", h.automation.state)
    }

    @Test fun liveSecondRingTaskListResumesAtAngerAltarWithFullProgress() {
        val description = "去怒焰祭坛消灭魔军侍僧（精英），得到5个火石后，到灵昌城的钱不与(19,99)处领取下一环任务。"
        val list = taskList(2, description, "火石 (0/5), 钱不与(19,99)")
        val task = GameProtocol.parseRunLoopTaskList(list.bytes)
        assertNotNull(task)
        assertEquals(2, task.ringNumber)
        assertEquals(RunLoopTaskKind.HUNT, task.kind)
        assertEquals("怒焰祭坛", task.huntMapName)
        assertEquals("火石", task.objectiveName)
        assertEquals(0, task.progress)
        assertEquals(5, task.required)
        assertEquals("灵昌城", task.turnInMapName)
        assertEquals("钱不与", task.turnInNpcName)
        assertEquals(19, task.turnInX)
        assertEquals(99, task.turnInY)

        val h = RunLoopHarness()
        h.world.learnFrame(list)
        h.start()
        assertEquals(TravelTarget(586, 100, 26, 76), travelTarget(h.sent.single()))
        assertEquals("Traveling", h.automation.state)

        h.feed(taskFrame(2, description, "火石 (5/5), 钱不与(19,99)"))
        assertEquals(TravelTarget(8025, 12, 19, 99), travelTarget(h.sent.last()))
        h.arriveAndSelect(8025, 12, GameProtocol.RUN_LOOP_TURN_IN_FUNCTION_ID)
        h.feed(removed())
        assertNull(h.world.runLoopTask)
        h.feed(taskFrame(3))
        assertEquals(TravelTarget(1002, 3, 17, 61), travelTarget(h.sent.last()))
    }

    @Test fun everyRingUsesTheNpcMapAndCoordinatesFromItsCurrentRandomTask() {
        val huntTargets = listOf(
            "怒焰祭坛" to TravelTarget(586, 100, 26, 76),
            "祭牙台地" to TravelTarget(584, 95, 21, 42),
            "百鸟树林" to TravelTarget(50100166, 110, 2, 84),
        )
        for (ring in 1..20) {
            val (huntMap, huntTravel) = huntTargets[(ring - 1) % huntTargets.size]
            val liveNpcId = 95000 + ring
            val liveMapId = 300 + ring
            val x = 10 + ring
            val y = 40 + ring
            val description = "去${huntMap}消灭随机目标，得到1个随机物品后，到随机地图${ring}的" +
                "<a href='event:x:$x,y:$y,m:$liveMapId,n:$liveNpcId'><u>随机路人${ring}($x,$y)</u></a>" +
                "处领取下一环任务。"
            val initial = taskList(ring, description, "随机目标 (0/1),")
            val parsed = GameProtocol.parseRunLoopTaskList(initial.bytes)
            assertNotNull(parsed)
            assertEquals(ring, parsed.ringNumber)
            assertEquals(huntMap, parsed.huntMapName)
            assertEquals(liveNpcId, parsed.turnInNpcId)
            assertEquals(liveMapId, parsed.turnInMapId)

            val h = RunLoopHarness()
            h.world.learnFrame(initial)
            h.start()
            assertEquals(huntTravel, travelTarget(h.sent.single()), "第 $ring 环应使用本次战斗地图")
            h.feed(taskFrame(ring, description, "随机目标 (1/1),"))
            assertEquals(
                TravelTarget(liveNpcId, liveMapId, x, y),
                travelTarget(h.sent.last()),
                "第 $ring 环应使用本次任务链接中的随机交付 NPC",
            )
        }
    }

    @Test fun windowsNpcCatalogIncludesUnvisitedNpcsAndUsesTaskCoordinates() {
        val h = RunLoopHarness()
        assertEquals(656, RunLoopDestinations.npcs.size)
        assertEquals(1002, h.world.findNpc("吕仁", 3)?.npcId)
        assertEquals(93195, h.world.findNpc("武诚初", 110)?.npcId)
        h.world.learnFrame(taskFrame(3, "与新月村的吕仁(18,62)对话，领取下一环任务。"))
        h.start()
        assertEquals(TravelTarget(1002, 3, 18, 62), travelTarget(h.sent.single()))
    }

    @Test fun inactiveManualRemovalMatchesWindowsOldTaskRules() {
        val ordinary = WorldModel()
        ordinary.learnFrame(taskFrame(5))
        ordinary.learnFrame(removed())
        ordinary.learnFrame(taskFrame(5))
        assertEquals(5, ordinary.runLoopTask?.ringNumber)

        val twentieth = WorldModel()
        twentieth.learnFrame(taskFrame(20))
        twentieth.learnFrame(removed())
        twentieth.learnFrame(taskFrame(20))
        assertNull(twentieth.runLoopTask)
        twentieth.learnFrame(taskFrame(1))
        assertEquals(1, twentieth.runLoopTask?.ringNumber)
    }

    @Test fun sixRounds120RingsRequireRemovalAndRejectLateSnapshots() {
        val h = RunLoopHarness(rounds = 6)
        h.start()
        repeat(6) { round ->
            h.arriveAndSelect(93290, 72, GameProtocol.RUN_LOOP_ACCEPT_FUNCTION_ID)
            for (ring in 1..20) {
                val current = taskFrame(ring)
                h.feed(current)
                assertEquals(GameOpcode.CLIENT_TRAVEL_LINK, h.lastOpcode())
                assertEquals(1002, travelTarget(h.sent.last()).npcId)
                h.arriveAndSelect(1002, 3, GameProtocol.RUN_LOOP_TURN_IN_FUNCTION_ID)
                val beforeRemoval = h.sent.size
                h.feed(current)
                h.feed(taskList(ring))
                h.feed(frame(GameOpcode.SERVER_TASK_REMOVED, BinaryWriter().writeString("other-task").toByteArray()))
                h.advance(100)
                assertEquals(beforeRemoval, h.sent.size)
                h.feed(removed())
                assertNull(h.world.runLoopTask)
                h.feed(current) // Old task after deletion must not resurrect it.
                h.feed(taskList(ring))
                h.feed(removed()) // Duplicate removal cannot count twice.
                assertNull(h.world.runLoopTask)
                assertEquals(beforeRemoval, h.sent.size)
                if (ring == 20 && round < 5) {
                    h.advance(249)
                    assertEquals(beforeRemoval, h.sent.size)
                    h.advance(1)
                    assertEquals(TravelTarget(93290, 72, 23, 132), travelTarget(h.sent.last()))
                }
            }
        }
        assertTrue(h.completed!!.contains("120 环"))
        assertTrue(h.completed!!.contains("6/6"))
        assertNull(h.failure)
        val count = h.sent.size
        h.advance(60_000)
        assertEquals(count, h.sent.size)
        val sequences = h.sent.map { readUInt32(it, it.size - 4) }
        assertEquals((1L..count.toLong()).toList(), sequences)
    }

    @Test fun huntProgressDoesNotTeleportAgainAndCompleteTaskStopsPatrol() {
        val h = RunLoopHarness(recovery = true)
        h.world.learnFrame(huntTask(0))
        h.start()
        h.feed(mapFrame(95, "祭牙台地", 0, 32))
        h.world.currentGrid = grid()
        h.advance(200)
        assertEquals(Point2(64, 32), GameProtocol.parseMovement(h.sent.last()))
        h.feed(huntTask(1))
        h.feed(huntTask(1))
        h.feed(huntTask(0))
        h.advance(200)
        assertEquals(Point2(128, 32), GameProtocol.parseMovement(h.sent.last()))
        assertEquals(1, h.sent.count { readUInt16(it, 2) == GameOpcode.CLIENT_TRAVEL_LINK })
        h.advance(3_600)
        assertTrue(h.sent.any { readUInt16(it, 2) == GameOpcode.CLIENT_UI_ACTION })
        h.feed(huntTask(2))
        assertEquals(GameOpcode.CLIENT_TRAVEL_LINK, h.lastOpcode())
        assertEquals(93195, travelTarget(h.sent.last()).npcId)
        val count = h.sent.size
        h.advance(4_000)
        assertEquals(count, h.sent.size)
        assertNull(h.failure)
    }

    @Test fun rejectedMovementStopsAfterTheFirstPacket() {
        val h = RunLoopHarness()
        h.world.learnFrame(huntTask(0))
        h.start()
        h.feed(mapFrame(95, "祭牙台地", 0, 32))
        h.world.currentGrid = grid()
        h.advance(200)
        val count = h.sent.size
        h.feed(frame(GameOpcode.SERVER_SYSTEM_MESSAGE,
            BinaryWriter().writeString("你的当前状态不能走动！").toByteArray()))
        assertTrue(h.failure!!.contains("停止继续发包"))
        h.advance(2_000)
        assertEquals(count, h.sent.size)
    }

    @Test fun huntRetriesUseTaskProgressNotBattlePrompts() {
        val h = RunLoopHarness()
        h.world.learnFrame(huntTask(0))
        h.start()
        repeat(4) {
            h.feed(mapFrame(95, "祭牙台地"))
            h.feed(frame(GameOpcode.SERVER_BATTLE_PROMPT))
            h.advance(75_000)
        }
        assertTrue(h.failure!!.contains("3 次"))
        assertEquals(4, h.sent.count { readUInt16(it, 2) == GameOpcode.CLIENT_TRAVEL_LINK })
    }

    @Test fun missingGridStopsAndStopCancelsPendingNpcOpen() {
        val h = RunLoopHarness()
        h.world.learnFrame(huntTask(0))
        h.start()
        h.feed(mapFrame(95, "祭牙台地"))
        h.advance(10_000)
        assertNotNull(h.failure)
        assertEquals(1, h.sent.size)
        val stopped = RunLoopHarness()
        stopped.start()
        stopped.feed(mapFrame(72, "尚其村"))
        stopped.stop()
        stopped.advance(1_000)
        assertEquals(1, stopped.sent.size)
    }

    @Test fun birdForestUsesWindowsTwoStageRoute() {
        val h = RunLoopHarness()
        h.world.learnFrame(taskFrame(1, "去百鸟树林消灭2个金翅雏鸟后，到近天回廊的天空远征军斥候武诚初(20,66)处领取下一环任务。", "金翅雏鸟 (0/2),"))
        h.start()
        assertEquals(TravelTarget(50100166, 110, 2, 84), travelTarget(h.sent.last()))
        h.feed(mapFrame(110, "近天回廊"))
        h.advance(349)
        assertEquals(1, h.sent.size)
        h.advance(1)
        assertEquals(TravelTarget(608, 111, 6, 84), travelTarget(h.sent.last()))
        h.feed(mapFrame(111, "百鸟树林"))
        assertEquals("WalkingForEncounter", h.automation.state)
    }

    @Test fun windowsRawDeflateMapParsesAndRejectsTruncatedOrOversizedData() {
        val cells = ByteArray(25).apply { for (column in 0..3) this[10 + column] = 1 }
        val recordedShape = mapDataFrame(95, 5, 5, cells)
        val parsed = GameProtocol.parseMapData(recordedShape.bytes)
        assertNotNull(parsed)
        assertEquals(95, parsed.mapId)
        assertEquals(5, parsed.rows)
        assertTrue(parsed.isWalkable(0, 2))

        val truncated = recordedShape.bytes.copyOf(recordedShape.bytes.size - 1)
        assertNull(GameProtocol.parseMapData(truncated))
        assertNull(GameProtocol.parseMapData(mapDataFrame(95, 65_535, 65_535, byteArrayOf(1)).bytes))
    }
}

private class RunLoopHarness(rounds: Int = 1, recovery: Boolean = false) {
    var now = System.currentTimeMillis()
    val world = WorldModel()
    val sent = mutableListOf<ByteArray>()
    val messages = mutableListOf<String>()
    var completed: String? = null
    var failure: String? = null
    var refreshCommand: AutomationCommand? = null
    var refreshReason: String? = null
    private var running = false
    val automation = RunLoopAutomation(rounds, recovery, RunLoopWalkOptions()) { now }
    private val ctx = AutomationContext(world,
        send = { _, factory -> sent += factory(sent.size + 1L) },
        publish = { _, text, _ -> messages += text },
        complete = { completed = it; stop() },
        fail = { failure = it; stop() },
        requestSessionRefresh = { command, reason -> refreshCommand = command; refreshReason = reason },
    )
    fun start() { running = true; automation.onStart(ctx) }
    fun stop() { running = false; automation.onStop(ctx, "停止") }
    fun feed(value: Frame) { world.learnFrame(value, running); if (running) automation.onServerFrame(ctx, value) }
    fun advance(ms: Long) { now += ms; if (running) automation.onTick(ctx, now) }
    fun lastOpcode() = readUInt16(sent.last(), 2)
    fun arriveAndSelect(npc: Int, map: Int, function: String) {
        feed(mapFrame(map, if (map == 72) "尚其村" else "新月村"))
        advance(450)
        assertEquals(GameOpcode.CLIENT_NPC_OPEN, lastOpcode())
        feed(npcDialog(npc, function))
        advance(180)
        assertEquals(GameOpcode.CLIENT_NPC_FUNCTION, lastOpcode())
    }
}

private fun frame(op: Int, body: ByteArray = byteArrayOf()): Frame = Frame(op, body, buildFrame(op, body))
private fun mapFrame(map: Int, name: String, x: Int = 0, y: Int = 32): Frame = frame(GameOpcode.SERVER_MAP_INFO,
    BinaryWriter().writeUInt32(map.toLong()).writeString(name).writeUInt32(x.toLong()).writeUInt32(y.toLong()).toByteArray())
private fun npcDialog(npc: Int, function: String): Frame = frame(GameOpcode.SERVER_NPC_DIALOG,
    BinaryWriter().writeUInt32(npc.toLong()).writeString(function).toByteArray())
private fun confirmation(title: String): Frame = frame(GameOpcode.SERVER_CONFIRMATION_DIALOG,
    BinaryWriter().writeUInt32(123).writeString(title).writeString("本次传送").writeString("live-token").toByteArray())
private fun taskFrame(ring: Int, desc: String = "与新月村的吕仁(17,61)对话，领取下一环任务。", tracker: String = ""): Frame =
    frame(GameOpcode.SERVER_TASK_UPDATE, BinaryWriter().writeString(GameProtocol.RUN_LOOP_TASK_ID)
        .writeString("跑环任务").writeString("跑环任务（第${ring}环）").writeString(desc).writeString(tracker).toByteArray())
private fun huntTask(progress: Int): Frame = taskFrame(1,
    "去祭牙台地消灭2个金翅雏鸟后，到近天回廊的天空远征军斥候武诚初(20,66)处领取下一环任务。", "金翅雏鸟 ($progress/2),")
private fun removed(): Frame = frame(GameOpcode.SERVER_TASK_REMOVED, BinaryWriter().writeString(GameProtocol.RUN_LOOP_TASK_ID).toByteArray())
private fun travelTarget(bytes: ByteArray): TravelTarget {
    val reader = BinaryReader(bytes.copyOfRange(8, bytes.size))
    val npc = reader.readString().toInt()
    val map = reader.readString().toInt()
    val xy = reader.readString().split('=')
    return TravelTarget(npc, map, xy[0].toInt(), xy[1].toInt())
}
private fun taskList(
    ring: Int,
    description: String = "与新月村的吕仁(17,61)对话，领取下一环任务。",
    tracker: String = "",
): Frame {
    val prefix = BinaryWriter().writeUInt16(2).writeString("奇怪的萝卜")
        .writeString("<a href='event:x:19,y:115,m:9,n:5008'>出云子</a>").writeString("知客蜂 (5/25), ").toByteArray()
    val output = ByteArrayOutputStream()
    DeflaterOutputStream(output).use { it.write(prefix + taskFrame(ring, description, tracker).body) }
    // Windows replay fixtures retain the zlib header but omit the Adler32 checksum.
    val compressed = output.toByteArray().dropLast(4).toByteArray()
    return frame(GameOpcode.SERVER_TASK_LIST, BinaryWriter().writeUInt16(compressed.size).toByteArray() + compressed)
}
private fun grid(): MapGrid = MapGrid(95, 64, 32, 5, 5, ByteArray(25).apply { for (col in 0..3) this[10 + col] = 1 })
private fun mapDataFrame(map: Int, rows: Int, cols: Int, cells: ByteArray): Frame {
    val compressedOutput = ByteArrayOutputStream()
    DeflaterOutputStream(compressedOutput, Deflater(Deflater.DEFAULT_COMPRESSION, true)).use { it.write(cells) }
    val compressed = byteArrayOf(0x78, 0x9c.toByte()) + compressedOutput.toByteArray()
    val body = BinaryWriter().writeUInt16(map).writeString("祭牙台地")
    intArrayOf(180, 200, 150, rows, cols, 64, 32, rows, cols).forEach { body.writeUInt16(it) }
    body.writeUInt32(0).writeUInt16(0).writeUInt32(compressed.size.toLong()).writeBytes(compressed)
    return frame(GameOpcode.SERVER_MAP_DATA2, body.toByteArray())
}
