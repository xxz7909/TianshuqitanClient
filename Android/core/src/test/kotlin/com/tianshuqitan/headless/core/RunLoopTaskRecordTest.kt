package com.tianshuqitan.headless.core

import java.io.ByteArrayOutputStream
import java.util.Base64
import java.util.zip.DeflaterOutputStream
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull

class RunLoopTaskRecordTest {
    @Test fun recordedWindowsTaskUpdatesKeepTheirActualRandomDestinations() {
        val talk = assertNotNull(GameProtocol.parseRunLoopTask(recordedTalkUpdate))
        assertEquals(2, talk.ringNumber)
        assertEquals(RunLoopTaskKind.TALK_TO_NPC, talk.kind)
        assertEquals("十字路口", talk.turnInMapName)
        assertEquals("冯奇", talk.turnInNpcName)
        assertEquals(17, talk.turnInX)
        assertEquals(73, talk.turnInY)

        val hunt = assertNotNull(GameProtocol.parseRunLoopTask(recordedHuntUpdate))
        assertEquals(4, hunt.ringNumber)
        assertEquals(RunLoopTaskKind.HUNT, hunt.kind)
        assertEquals("祭牙台地", hunt.huntMapName)
        assertEquals("周猎户", hunt.turnInNpcName)
        assertEquals(0, hunt.progress)
        assertEquals(5, hunt.required)
    }

    @Test fun recordedSecondRingIsIsolatedFromFollowingRandomTask() {
        val next = record("1987", "普通任务", "第19环纪念活动",
            "与新月村的<a href='event:x:19,y:99,m:3,n:5008'>出云子</a>(19,99)对话，领取下一环任务。", "其他物品 (25/25),")
        val task = assertNotNull(GameProtocol.parseRunLoopTaskList(listFrame(liveFullSecondRing, next)))
        assertEquals(2, task.ringNumber)
        assertEquals(RunLoopTaskKind.HUNT, task.kind)
        assertEquals("怒焰祭坛", task.huntMapName)
        assertEquals("钱不与", task.turnInNpcName)
        assertEquals("灵昌城", task.turnInMapName)
        assertEquals(19, task.turnInX)
        assertEquals(99, task.turnInY)
        assertEquals(0, task.turnInNpcId, "An unrelated task link must not replace 钱不与")
        assertEquals(0, task.progress)
        assertEquals(5, task.required)
        assertFalse(task.isComplete)
    }

    @Test fun recordedIncompleteTaskDoesNotBorrowDescriptionFromNextRecord() {
        val next = record("1987", "普通任务", "第19环纪念活动",
            "与新月村的吕仁(17,61)对话，领取下一环任务。", "其他物品 (25/25),")
        assertNull(GameProtocol.parseRunLoopTaskList(listFrame(liveIncompleteSecondRing, next)))
    }

    @Test fun referenceToTaskIdBeforeItsRecordDoesNotHideActualRunLoop() {
        val unrelated = record("1987", "普通任务", "普通说明",
            "关联任务号 140000000", "其他物品 (25/25),")
        val task = assertNotNull(GameProtocol.parseRunLoopTaskList(listFrame(unrelated, liveFullSecondRing)))
        assertEquals(2, task.ringNumber)
        assertEquals("钱不与", task.turnInNpcName)
    }

    @Test fun taskListDoesNotTruncateLongEncodedDescriptionAt2048Bytes() {
        val description = "<font data-padding='${"a".repeat(2400)}'>与十字路口的冯奇(17,73)对话，领取下一环任务。</font>"
        val encoded = Base64.getEncoder().encodeToString(description.encodeToByteArray())
        val task = assertNotNull(GameProtocol.parseRunLoopTaskList(listFrame(
            record(GameProtocol.RUN_LOOP_TASK_ID, "跑环任务", "跑环任务（第13环）", encoded, ""))))
        assertEquals(13, task.ringNumber)
        assertEquals("冯奇", task.turnInNpcName)
    }

    @Test fun updateMustBelongToRunLoopEvenWhenOtherDescriptionMentionsItsId() {
        val unrelated = record("1987", "普通任务", "跑环任务（第7环）",
            "140000000 与新月村的吕仁(17,61)对话，领取下一环任务。", "")
        assertNull(GameProtocol.parseRunLoopTask(buildFrame(GameOpcode.SERVER_TASK_UPDATE, unrelated)))
    }

    @Test fun alternativeHuntVerbKeepsRandomMapAndTurnInNpc() {
        val task = assertNotNull(GameProtocol.parseRunLoopTask(buildFrame(GameOpcode.SERVER_TASK_UPDATE,
            record(GameProtocol.RUN_LOOP_TASK_ID, "跑环任务", "跑环任务（第11环）",
                "前往怒焰祭坛消灭魔军侍僧（精英），得到5个火石后，到灵昌城的钱不与(19,99)处领取下一环任务。", "火石 (3/5),"))))
        assertEquals("怒焰祭坛", task.huntMapName)
        assertEquals("钱不与", task.turnInNpcName)
        assertEquals(3, task.progress)
    }

    @Test fun malformedProgressAndLinksDoNotThrowOrOverrideValidFields() {
        val task = assertNotNull(GameProtocol.parseRunLoopTask(buildFrame(GameOpcode.SERVER_TASK_UPDATE,
            record(GameProtocol.RUN_LOOP_TASK_ID, "跑环任务", "跑环任务（第8环）",
                "去怒焰祭坛消灭5个魔军侍僧后，到灵昌城的钱不与(19,99)处领取下一环任务。",
                "错误 (999999999999999999999/5),") + BinaryWriter().writeString("魔军侍僧 (3/5),").toByteArray())))
        assertEquals(3, task.progress)
        assertEquals(emptyList(), GameProtocol.extractTravelLinks("event:x:19,y:99,m:12,n:999999999999999999999999"))
    }

    @Test fun sameRandomRingCanHaveDifferentLiveNpcAndHuntMap() {
        for (ring in 1..20) {
            val first = assertNotNull(GameProtocol.parseRunLoopTask(buildFrame(GameOpcode.SERVER_TASK_UPDATE,
                record(GameProtocol.RUN_LOOP_TASK_ID, "跑环任务", "跑环任务（第${ring}环）",
                    "去怒焰祭坛消灭魔军侍僧（精英），得到5个火石后，到灵昌城的钱不与(19,99)处领取下一环任务。", "火石 (2/5),"))))
            val second = assertNotNull(GameProtocol.parseRunLoopTask(buildFrame(GameOpcode.SERVER_TASK_UPDATE,
                record(GameProtocol.RUN_LOOP_TASK_ID, "跑环任务", "跑环任务（第${ring}环）",
                    "去祭牙台地消灭2个金翅雏鸟后，到近天回廊的天空远征军斥候武诚初(20,66)处领取下一环任务。", "金翅雏鸟 (1/2),"))))
            assertEquals(ring, first.ringNumber)
            assertEquals(ring, second.ringNumber)
            assertEquals("钱不与", first.turnInNpcName)
            assertEquals("怒焰祭坛", first.huntMapName)
            assertEquals("天空远征军斥候武诚初", second.turnInNpcName)
            assertEquals("祭牙台地", second.huntMapName)
            assertEquals(2, second.required)
        }
    }

    @Test fun trackerAndRewardCannotReplaceActualTitleOrDescription() {
        val record = record(GameProtocol.RUN_LOOP_TASK_ID, "跑环任务", "跑环任务（第9环）",
            "与十字路口的冯奇(17,73)对话，领取下一环任务。",
            "第20环纪念奖励；与新月村的吕仁(17,61)对话，领取下一环任务。")
        val task = assertNotNull(GameProtocol.parseRunLoopTask(buildFrame(GameOpcode.SERVER_TASK_UPDATE, record)))
        assertEquals(9, task.ringNumber)
        assertEquals("冯奇", task.turnInNpcName)
        assertEquals("十字路口", task.turnInMapName)
    }

    @Test fun missingDescriptionCannotBeReconstructedFromTrackerOrReward() {
        assertNull(GameProtocol.parseRunLoopTask(buildFrame(GameOpcode.SERVER_TASK_UPDATE,
            record(GameProtocol.RUN_LOOP_TASK_ID, "跑环任务", "跑环任务（第3环）", "",
                "与新月村的吕仁(17,61)对话，领取下一环任务。"))))
    }

    @Test fun malformedDestinationCoordinatesAreRejectedWithoutThrowing() {
        for (description in listOf(
            "与新月村的吕仁(999999999999999999999999,61)对话，领取下一环任务。",
            "将桃木送到新月村的吕仁(17,999999999999999999999999)处领取下一环任务。",
            "去怒焰祭坛消灭5个魔军侍僧后，到灵昌城的钱不与(999999999999999999999999,99)处领取下一环任务。",
        )) {
            assertNull(GameProtocol.parseRunLoopTask(buildFrame(GameOpcode.SERVER_TASK_UPDATE,
                record(GameProtocol.RUN_LOOP_TASK_ID, "跑环任务", "跑环任务（第8环）", description, ""))))
        }
    }

    private fun record(id: String, category: String, title: String, description: String, progress: String): ByteArray =
        BinaryWriter().writeString(id).writeUInt16(if (id == GameProtocol.RUN_LOOP_TASK_ID) 0 else 1)
            .writeString(category).writeString(title).writeString(description).writeString(progress).toByteArray()

    private fun listFrame(vararg records: ByteArray): ByteArray {
        val output = ByteArrayOutputStream()
        DeflaterOutputStream(output).use { stream ->
            stream.write(BinaryWriter().writeUInt16(records.size).toByteArray())
            records.forEach(stream::write)
        }
        val compressed = output.toByteArray().dropLast(4).toByteArray()
        return buildFrame(GameOpcode.SERVER_TASK_LIST, BinaryWriter().writeUInt16(compressed.size).writeBytes(compressed).toByteArray())
    }

    companion object {
        // Task-only 0x003E frames 214/351 from .build/runloop-demo.sqlite.
        private val recordedTalkUpdate = Base64.getDecoder().decode(
            "AMwAPgAJMTQwMDAwMDAwAAAADOi3keeOr+S7u+WKoQAZ6LeR546v5Lu75Yqh77yI56ysMueOr++8iQBA5LiO5Y2B5a2X6Lev5Y+j55qE5Yav5aWHKDE3LDczKeWvueivne+8jOmihuWPluS4i+S4gOeOr+S7u+WKoeOAggAAAAAAAjYwAAAAAAA8AAAADeWGr+WlhygxNyw3MykAAAABAAAAAAAAAAEAAAAJMTIwMDAxMTExAAzlpZblirHlhYPnpZ7//wAAAAAAAAAA")
        private val recordedHuntUpdate = Base64.getDecoder().decode(
            "ASEAPgAJMTQwMDAwMDAwAAAADOi3keeOr+S7u+WKoQAZ6LeR546v5Lu75Yqh77yI56ysNOeOr++8iQBy5Y6756Wt54mZ5Y+w5Zyw5raI54GtNeS4qumTgeeUsuicpeictO+8iOeyvuiLse+8ieWQju+8jOWIsOeBteaYjOWfjueahOWRqOeMjuaItygzMSwxMzAp5aSE6aKG5Y+W5LiL5LiA546v5Lu75Yqh44CCAAAAAAACNjAAAAAAADwAH+mTgeeUsuicpeictO+8iOeyvuiLse+8iSAoMC81KSwAEeWRqOeMjuaItygzMSwxMzApAAAAAQAAAAAAAAABAAAACTEyMDAwMTExMQAM5aWW5Yqx5YWD56We//8AAAAAAAAAAA==")
        // Only the run-loop record from the device's two 2026-09-15 0x003D lists.
        // No account/session data is stored in these fixtures. The first list
        // omitted the description; the second included the actual random ring.
        private val liveIncompleteSecondRing = Base64.getDecoder().decode(
            "AAkxNDAwMDAwMDAAAAAM6LeR546v5Lu75YqhABnot5Hnjq/ku7vliqHvvIjnrKwy546v77yJAAAAAAAAAAI2MAAAAAAAPAAO54Gr55+zICgwLzUpLCAAEOmSseS4jeS4jigxOSw5OSkAAAABAAAAAAAAAAEAAAAJMTIwMDAxMTExAAD//wAAAAAAAAAA")
        private val liveFullSecondRing = Base64.getDecoder().decode(
            "AAkxNDAwMDAwMDAAAAAM6LeR546v5Lu75YqhABnot5Hnjq/ku7vliqHvvIjnrKwy546v77yJAIDljrvmgJLnhLDnpa3lnZvmtojnga3prZTlhpvkvo3lg6fvvIjnsr7oi7HvvInvvIzlvpfliLA15Liq54Gr55+z5ZCO77yM5Yiw54G15piM5Z+O55qE6ZKx5LiN5LiOKDE5LDk5KeWkhOmihuWPluS4i+S4gOeOr+S7u+WKoeOAggAAAAAAAjYwAAAAAAA8AA7ngavnn7MgKDAvNSksIAAQ6ZKx5LiN5LiOKDE5LDk5KQAAAAEAAAAAAAAAAQAAAAkxMjAwMDExMTEADOWlluWKseWFg+elnv//AAAAAAAAAAA=")
    }
}
