package com.tianshuqitan.headless.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Exercises the Flash client's battle responsibilities through the actual automation owners. */
class RunLoopBattleIntegrationTest {
    @Test fun standaloneCombatPausesOnBattleStartAndAdvancesEachRecordedRoundOnce() {
        val h = BattleRunLoopHarness()
        h.enterHuntMap()
        h.startCombat()
        h.advance(200)
        assertEquals(1, h.count(GameOpcode.CLIENT_MOVEMENT))

        h.feed(recordedBattleStart())
        h.advance(500)
        assertEquals(1, h.count(GameOpcode.CLIENT_MOVEMENT), "SC 2A must stop patrol before any action prompt")
        h.feed(recordedRound(1))
        h.feed(recordedPrompt())
        h.advance(649)
        assertEquals(0, h.count(GameOpcode.CLIENT_BATTLE_ADVANCE))
        h.advance(1)
        assertEquals(1, h.count(GameOpcode.CLIENT_BATTLE_ADVANCE))
        h.feed(recordedPrompt())
        h.feed(recordedActionAck())
        h.feed(recordedPrompt())
        h.advance(650)
        assertEquals(1, h.count(GameOpcode.CLIENT_BATTLE_ADVANCE), "An acknowledged round still cannot be sent twice")
        assertEquals(1, h.count(GameOpcode.CLIENT_MOVEMENT))

        h.feed(recordedRound(2))
        h.feed(recordedPrompt())
        h.advance(650)
        assertEquals(2, h.count(GameOpcode.CLIENT_BATTLE_ADVANCE))
        h.feed(recordedActionAck())
        h.feed(recordedBattleEnd())
        h.advance(200)
        assertEquals(2, h.count(GameOpcode.CLIENT_MOVEMENT))
        assertNull(h.failure)
        h.assertSharedSequences()
    }

    @Test fun completedHuntTaskWaitsForBattleEndBeforeItsRandomNpcTurnIn() {
        val h = BattleRunLoopHarness()
        val destination = TravelTarget(95011, 311, 31, 82)
        h.feed(dynamicTask(11, destination, hunt = true, progress = 0))
        h.startRunLoop()
        h.enterHuntMap()
        h.advance(200)
        h.feed(recordedBattleStart())
        h.feed(recordedRound(1))
        h.feed(recordedPrompt())
        // A delayed rejection for the last pre-battle step must not abort a known battle.
        h.feed(battleFrame(GameOpcode.SERVER_SYSTEM_MESSAGE,
            BinaryWriter().writeString("你的当前状态不能走动！").toByteArray()))
        h.advance(650)
        assertEquals(1, h.count(GameOpcode.CLIENT_BATTLE_ADVANCE))
        val stepsBeforeCompletion = h.count(GameOpcode.CLIENT_MOVEMENT)
        h.feed(huntStatus(1)) // Real server may send only SC27, without resending SC3E.
        assertEquals(1, h.world.runLoopTask?.progress)
        h.advance(1_000)
        assertEquals(1, h.count(GameOpcode.CLIENT_TRAVEL_LINK), "Task progress may arrive before the battle actually ends")
        assertEquals(stepsBeforeCompletion, h.count(GameOpcode.CLIENT_MOVEMENT))
        assertNull(h.failure)

        h.feed(recordedActionAck())
        h.feed(recordedBattleEnd())
        assertEquals(destination, h.lastTravel())
        assertEquals(2, h.count(GameOpcode.CLIENT_TRAVEL_LINK))
        h.arriveAndSelect(destination, GameProtocol.RUN_LOOP_TURN_IN_FUNCTION_ID)
        val beforeRemoval = h.sent.size
        h.feed(dynamicTask(11, destination, hunt = true, progress = 1))
        h.advance(500)
        assertEquals(beforeRemoval, h.sent.size, "The completed task is retained until its deletion acknowledgement")
        h.feed(runLoopRemoved())
        assertNull(h.world.runLoopTask)
        h.feed(huntStatus(1)) // Status has no ring number and cannot recreate a deleted task.
        assertNull(h.world.runLoopTask)
        val next = TravelTarget(95012, 312, 7, 66)
        h.feed(dynamicTask(12, next, hunt = false))
        assertEquals(next, h.lastTravel())
        assertNull(h.failure)
        h.assertSharedSequences()
    }

    @Test fun takingOverAnAdvancedBattleDoesNotRepeatItsRoundOrRecovery() {
        val h = BattleRunLoopHarness()
        val destination = TravelTarget(97017, 417, 26, 77)
        h.feed(dynamicTask(17, destination, hunt = true, progress = 1))
        h.enterHuntMap()
        h.startCombat(recovery = true)
        h.advance(4_000)
        assertEquals(1, h.count(GameOpcode.CLIENT_UI_ACTION))
        h.feed(recordedBattleStart())
        h.feed(recordedRound(1))
        h.feed(recordedPrompt())
        h.advance(650)
        assertEquals(1, h.count(GameOpcode.CLIENT_BATTLE_ADVANCE))
        val beforeTakeover = h.sent.size

        h.startRunLoop(recovery = true)
        assertEquals(beforeTakeover, h.sent.size, "Starting run-loop mid-battle must not teleport or heal immediately")
        h.feed(recordedPrompt())
        h.feed(recordedActionAck())
        h.feed(recordedPrompt())
        h.advance(650)
        assertEquals(1, h.count(GameOpcode.CLIENT_BATTLE_ADVANCE))
        assertEquals(1, h.count(GameOpcode.CLIENT_UI_ACTION))
        assertEquals(0, h.count(GameOpcode.CLIENT_TRAVEL_LINK))

        h.feed(recordedBattleEnd())
        assertEquals(destination, h.lastTravel(), "Resume the current random task's NPC, not the first-ring NPC")
        assertNull(h.failure)
        h.assertSharedSequences()
    }

    @Test fun takingOverPendingPromptKeepsItsOriginalDeadline() {
        val h = BattleRunLoopHarness()
        val destination = TravelTarget(92007, 307, 22, 63)
        h.feed(dynamicTask(7, destination, hunt = false))
        h.enterHuntMap()
        h.startCombat()
        h.feed(recordedBattleStart())
        h.feed(recordedRound(1))
        h.feed(recordedPrompt())
        h.advance(300)
        h.startRunLoop()
        h.advance(349)
        assertEquals(0, h.count(GameOpcode.CLIENT_BATTLE_ADVANCE))
        assertEquals(0, h.count(GameOpcode.CLIENT_TRAVEL_LINK))
        h.advance(1)
        assertEquals(1, h.count(GameOpcode.CLIENT_BATTLE_ADVANCE), "The original server prompt remains scheduled across owner handoff")
        h.feed(recordedBattleEnd())
        assertEquals(destination, h.lastTravel())
        assertNull(h.failure)
        h.assertSharedSequences()
    }

    @Test fun sixRoundsOfMixedHuntAndTalkTasksCompleteOnlyAfter120DeletionConfirmations() {
        val h = BattleRunLoopHarness()
        val startNpc = TravelTarget(93290, 72, 23, 132)
        h.startRunLoop(rounds = 6)
        repeat(6) { round ->
            h.arriveAndSelect(startNpc, GameProtocol.RUN_LOOP_ACCEPT_FUNCTION_ID)
            for (ring in 1..20) {
                val index = round * 20 + ring
                val destination = TravelTarget(96000 + index, 300 + index, 10 + ring, 40 + ring)
                val hunt = ring % 2 == 1
                val initial = dynamicTask(ring, destination, hunt = hunt, progress = 0)
                h.feed(initial)
                if (hunt) {
                    assertEquals(TravelTarget(586, 100, 26, 76), h.lastTravel())
                    h.enterHuntMap()
                    h.advance(200)
                    val steps = h.count(GameOpcode.CLIENT_MOVEMENT)
                    val travels = h.count(GameOpcode.CLIENT_TRAVEL_LINK)
                    h.feed(recordedBattleStart())
                    h.feed(recordedRound(1))
                    h.feed(recordedPrompt())
                    h.advance(650)
                    h.feed(if (ring % 4 == 1) huntStatus(1) else dynamicTask(ring, destination, hunt = true, progress = 1))
                    h.advance(200)
                    assertEquals(travels, h.count(GameOpcode.CLIENT_TRAVEL_LINK), "Round ${round + 1}, ring $ring must finish battle first")
                    assertEquals(steps, h.count(GameOpcode.CLIENT_MOVEMENT))
                    h.feed(recordedActionAck())
                    h.feed(recordedBattleEnd())
                }
                assertEquals(destination, h.lastTravel(), "Round ${round + 1}, ring $ring uses its live task link")
                h.arriveAndSelect(destination, GameProtocol.RUN_LOOP_TURN_IN_FUNCTION_ID)
                val beforeRemoval = h.sent.size
                val completeTask = dynamicTask(ring, destination, hunt, progress = 1)
                h.feed(completeTask)
                h.advance(100)
                assertEquals(beforeRemoval, h.sent.size)
                assertNotNull(h.world.runLoopTask)
                h.feed(runLoopRemoved())
                h.feed(completeTask) // A late pre-deletion snapshot must not restart the old ring.
                h.feed(runLoopRemoved()) // A duplicate acknowledgement cannot count twice.
                assertNull(h.world.runLoopTask)
                assertEquals(beforeRemoval, h.sent.size)
                assertNull(h.failure, "Round ${round + 1}, ring $ring")
                if (ring == 20 && round < 5) {
                    h.advance(249)
                    assertEquals(beforeRemoval, h.sent.size)
                    h.advance(1)
                    assertEquals(startNpc, h.lastTravel())
                }
            }
        }
        assertTrue(h.completed.orEmpty().contains("120 环"))
        assertTrue(h.completed.orEmpty().contains("6/6"))
        assertEquals(60, h.count(GameOpcode.CLIENT_BATTLE_ADVANCE))
        assertEquals(126, h.count(GameOpcode.CLIENT_NPC_FUNCTION))
        val completeCount = h.sent.size
        h.advance(60_000)
        assertEquals(completeCount, h.sent.size)
        assertNull(h.failure)
        h.assertSharedSequences()
    }
}

private class BattleRunLoopHarness {
    var now = 1_000_000L
    val world = WorldModel(clock = { now })
    val sent = mutableListOf<ByteArray>()
    var failure: String? = null
    var completed: String? = null
    private var active: HeadlessAutomation? = null
    private val ctx = AutomationContext(world,
        send = { _, factory -> sent += factory(sent.size + 1L) },
        publish = { _, _, _ -> },
        complete = { completed = it; stop() },
        fail = { failure = it; stop() },
    )

    fun startRunLoop(rounds: Int = 1, recovery: Boolean = false) =
        start(RunLoopAutomation(rounds, recovery, RunLoopWalkOptions(), clock = { now }))

    fun startCombat(recovery: Boolean = false) =
        start(AutoCombatAutomation(recovery, RunLoopWalkOptions(), clock = { now }))

    private fun start(next: HeadlessAutomation) {
        stop()
        active = next
        next.onStart(ctx)
    }

    private fun stop() {
        val previous = active
        active = null
        previous?.onStop(ctx, "测试切换自动化")
    }

    fun feed(value: Frame) {
        world.learnFrame(value, active?.owner == "AutoRunLoop")
        active?.onServerFrame(ctx, value)
    }

    fun advance(ms: Long) {
        now += ms
        active?.onTick(ctx, now)
    }

    fun enterHuntMap() {
        feed(battleMap(100, "怒焰祭坛"))
        world.currentGrid = MapGrid(100, 64, 32, 5, 5, ByteArray(25).apply {
            for (col in 0..3) this[10 + col] = 1
        })
    }

    fun arriveAndSelect(destination: TravelTarget, function: String) {
        feed(battleMap(destination.mapId, "本次任务地图"))
        advance(450)
        assertEquals(GameOpcode.CLIENT_NPC_OPEN, readUInt16(sent.last(), 2))
        assertEquals(destination.npcId.toLong(), readUInt32(sent.last(), 4))
        feed(battleFrame(GameOpcode.SERVER_NPC_DIALOG,
            BinaryWriter().writeUInt32(destination.npcId.toLong()).writeString(function).toByteArray()))
        advance(180)
        assertEquals(GameOpcode.CLIENT_NPC_FUNCTION, readUInt16(sent.last(), 2))
    }

    fun count(opcode: Int) = sent.count { readUInt16(it, 2) == opcode }

    fun lastTravel(): TravelTarget {
        val bytes = sent.last { readUInt16(it, 2) == GameOpcode.CLIENT_TRAVEL_LINK }
        val reader = BinaryReader(bytes.copyOfRange(8, bytes.size))
        val npc = reader.readString().toInt()
        val map = reader.readString().toInt()
        val xy = reader.readString().split('=')
        return TravelTarget(npc, map, xy[0].toInt(), xy[1].toInt())
    }

    fun assertSharedSequences() {
        assertEquals((1L..sent.size.toLong()).toList(), sent.map { readUInt32(it, it.size - 4) })
    }
}

private fun battleFrame(opcode: Int, body: ByteArray) = Frame(opcode, body, buildFrame(opcode, body))
private fun battleMap(id: Int, name: String): Frame = battleFrame(GameOpcode.SERVER_MAP_INFO,
    BinaryWriter().writeUInt32(id.toLong()).writeString(name).writeUInt32(0).writeUInt32(32).toByteArray())

private fun dynamicTask(ring: Int, npc: TravelTarget, hunt: Boolean, progress: Int = 0): Frame {
    val link = "<a href='event:x:${npc.x},y:${npc.y},m:${npc.mapId},n:${npc.npcId}'>" +
        "<u>本次路人${npc.npcId}(${npc.x},${npc.y})</u></a>"
    val description = if (hunt) "去怒焰祭坛消灭魔军侍僧（精英），得到1个火石后，到本次地图的${link}处领取下一环任务。"
        else "与本次地图的${link}对话，领取下一环任务。"
    return battleFrame(GameOpcode.SERVER_TASK_UPDATE,
        BinaryWriter().writeString(GameProtocol.RUN_LOOP_TASK_ID).writeString("跑环任务")
            .writeString("跑环任务（第${ring}环）").writeString(description)
            .writeString(if (hunt) "火石 ($progress/1)," else "").toByteArray())
}

private fun runLoopRemoved() = battleFrame(GameOpcode.SERVER_TASK_REMOVED,
    BinaryWriter().writeString(GameProtocol.RUN_LOOP_TASK_ID).toByteArray())

private fun huntStatus(progress: Int) = battleFrame(GameOpcode.SERVER_TASK_STATUS,
    BinaryWriter().writeString(GameProtocol.RUN_LOOP_TASK_ID).writeUInt32(1)
        .writeString("火石: $progress/1").writeUInt32(if (progress >= 1) 1 else 0).toByteArray())

// Existing Windows capture .build/combat-example.sqlite, battle-start frame 543.
// No connection details, account credentials, or session tickets are needed by these fixtures.
private fun recordedBattleStart(): Frame = recordedHex(
    "00C5002A000100000000000200010000000000110003000E000CE4BD9CE4B9B1E5A696E98193" +
        "00063334303032330006333430303233000130000019640000196400000082000000000066000130" +
        "FFFFFFFF000130000000000000000000000000000000000039822D0002000900190006E78E8BE993B6" +
        "0006313236303030000631333630303200063132363030360001164300011643000000890000000C" +
        "E8BEB0E697B6E5BA87E4BD910067000130000000000001300000000000000000000000000000",
)
private fun recordedRound(round: Int): Frame = battleFrame(0x002b,
    BinaryWriter().writeUInt32(25).writeUInt32(round.toLong()).toByteArray())
private fun recordedPrompt() = recordedHex("000A002C00000000033F")
private fun recordedActionAck() = recordedHex("0006002D0002")
private fun recordedBattleEnd() = recordedHex("0008002F00000000")
private fun recordedHex(hex: String): Frame {
    val bytes = hex.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    require(readUInt16(bytes, 0) == bytes.size)
    return Frame(readUInt16(bytes, 2), bytes.copyOfRange(4, bytes.size), bytes)
}
