package com.tianshuqitan.headless.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class RunLoopTaskStatusTest {
    @Test fun recordedWindowsStatusPacketBelongsToTheCurrentRandomHunt() {
        // .build/runloop-demo.sqlite frame 352: only this task status, no account data.
        val bytes = hex("00360027000931343030303030303000000001001de99381e794b2e89ca5e89cb4efbc88e7b2bee88b" +
            "b1efbc893a20302f3500000000")
        val task = hunt(ring = 4, objective = "铁甲蜥蜴（精英）", progress = 0)
        assertEquals(task, GameProtocol.applyRunLoopTaskStatus(bytes, task))
        assertNull(GameProtocol.applyRunLoopTaskStatus(bytes, hunt(objective = "火石", progress = 0)))
    }

    @Test fun statusOnlyUpdatesProgressAndKeepsTheLiveRandomDestination() {
        for (ring in 1..20) {
            val task = hunt(ring = ring, progress = 2)
            val progress = assertNotNull(GameProtocol.applyRunLoopTaskStatus(status("火石: 3/5", 0), task))
            assertEquals(task.copy(progress = 3), progress)
            val complete = assertNotNull(GameProtocol.applyRunLoopTaskStatus(status("火石: 5/5", 1), progress))
            assertEquals(task.copy(progress = 5), complete)
            assertTrue(complete.isComplete)
        }
    }

    @Test fun noCachedTaskOrObjectiveCannotInventAResumeDestination() {
        val bytes = status("火石: 5/5", 1)
        assertNull(GameProtocol.applyRunLoopTaskStatus(bytes, null))
        assertNull(GameProtocol.applyRunLoopTaskStatus(bytes, hunt(objective = "")))
        assertNull(GameProtocol.applyRunLoopTaskStatus(bytes, hunt().copy(kind = RunLoopTaskKind.DELIVER_ITEM)))
    }

    @Test fun rejectsUnrelatedIdObjectiveDenominatorRegressionsAndFailedObjectives() {
        val current = hunt(progress = 3)
        assertNull(GameProtocol.applyRunLoopTaskStatus(status("火石: 5/5", 1, id = "1987"), current))
        assertNull(GameProtocol.applyRunLoopTaskStatus(status("高级火石: 5/5", 1), current))
        assertNull(GameProtocol.applyRunLoopTaskStatus(status("火石: 4/10", 0), current))
        assertNull(GameProtocol.applyRunLoopTaskStatus(status("火石: 2/5", 0), current))
        assertNull(GameProtocol.applyRunLoopTaskStatus(status("火石: 4/5", 2), current))
        assertNull(GameProtocol.applyRunLoopTaskStatus(status("火石: 5/5", 0), current))
        assertNull(GameProtocol.applyRunLoopTaskStatus(status("火石: 4/5", 1), current))
    }

    @Test fun validatesEveryEntryAndEndOfPacketBeforeReturningAnUpdate() {
        val current = hunt(progress = 0)
        val two = statuses(listOf("其他目标: 1/1" to 1, "火石: 3/5" to 0))
        assertEquals(3, GameProtocol.applyRunLoopTaskStatus(two, current)?.progress)
        assertNull(GameProtocol.applyRunLoopTaskStatus(two.dropLast(1).toByteArray(), current))
        assertNull(GameProtocol.applyRunLoopTaskStatus(two + byteArrayOf(0), current))
        assertNull(GameProtocol.applyRunLoopTaskStatus(statuses(listOf("火石: 3/5" to 0, "火石: 3/5" to 0)), current))
        assertNull(GameProtocol.applyRunLoopTaskStatus(statuses(listOf("火石: 3/5" to 0, "其他目标: 1/1" to 3)), current))
        assertNull(GameProtocol.applyRunLoopTaskStatus(statuses(emptyList()), current))
        assertNull(GameProtocol.applyRunLoopTaskStatus(statuses(List(33) { "其他目标: 1/1" to 1 }), current))
        val hugeCount = buildFrame(GameOpcode.SERVER_TASK_STATUS,
            BinaryWriter().writeString(GameProtocol.RUN_LOOP_TASK_ID).writeUInt32(0xffff_ffffL).toByteArray())
        assertNull(GameProtocol.applyRunLoopTaskStatus(hugeCount, current))
    }

    @Test fun malformedCountersAreIgnoredAndUiFormattingDoesNotChangeObjectiveIdentity() {
        val current = hunt(progress = 0)
        assertNull(GameProtocol.applyRunLoopTaskStatus(status("火石: 999999999999999999/5", 0), current))
        assertNull(GameProtocol.applyRunLoopTaskStatus(status("火石: 0/0", 0), current))
        assertNull(GameProtocol.applyRunLoopTaskStatus(status("火石: -1/5", 0), current))
        assertEquals(3, GameProtocol.applyRunLoopTaskStatus(status("<font>火石</font>： 3/5", 0), current)?.progress)
        assertEquals(6, GameProtocol.applyRunLoopTaskStatus(status("火石: 6/5", 1), current)?.progress)
    }

    private fun hunt(ring: Int = 2, objective: String = "火石", progress: Int = 2) = RunLoopTask(
        ringNumber = ring,
        kind = RunLoopTaskKind.HUNT,
        description = "去怒焰祭坛消灭魔军侍僧（精英），得到5个火石后，到灵昌城的钱不与(19,99)处领取下一环任务。",
        huntMapName = "怒焰祭坛",
        objectiveName = objective,
        turnInMapName = "灵昌城",
        turnInNpcName = "钱不与",
        turnInMapId = 12,
        turnInNpcId = 8025,
        turnInX = 19,
        turnInY = 99,
        progress = progress,
        required = 5,
    )

    private fun status(text: String, finish: Int, id: String = GameProtocol.RUN_LOOP_TASK_ID): ByteArray =
        statuses(listOf(text to finish), id)

    private fun statuses(entries: List<Pair<String, Int>>, id: String = GameProtocol.RUN_LOOP_TASK_ID): ByteArray {
        val body = BinaryWriter().writeString(id).writeUInt32(entries.size.toLong())
        entries.forEach { (text, finish) -> body.writeString(text).writeUInt32(finish.toLong()) }
        return buildFrame(GameOpcode.SERVER_TASK_STATUS, body.toByteArray())
    }

    private fun hex(value: String): ByteArray = value.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
}
