package com.tianshuqitan.headless.core

import java.io.ByteArrayOutputStream
import java.util.zip.DeflaterOutputStream
import kotlin.test.*

class NetworkRecoveryTest {
    @Test fun wifiToMobileIgnoresLateLossAndDuplicateCapabilities() {
        val state = DefaultNetworkState<String>()
        assertFalse(state.available("wifi").becameUsable)
        assertTrue(state.capabilities("wifi", true).becameUsable)
        assertTrue(state.available("mobile").invalidateConnection)
        assertFalse(state.usable)
        assertTrue(state.capabilities("mobile", true).becameUsable)
        assertEquals(DefaultNetworkState.Change(), state.lost("wifi"))
        assertEquals(DefaultNetworkState.Change(), state.capabilities("wifi", false))
        assertEquals(DefaultNetworkState.Change(), state.capabilities("mobile", true))
        assertTrue(state.usable)
        assertEquals("mobile", state.current)
    }

    @Test fun lossCaptivePortalAndRapidNetworkChangesWaitForTheLatestValidatedNetwork() {
        val state = DefaultNetworkState<Int>()
        state.available(1)
        state.capabilities(1, true)
        assertTrue(state.lost(1).invalidateConnection)
        state.available(2)
        assertFalse(state.capabilities(2, false).becameUsable)
        state.available(3)
        assertFalse(state.capabilities(2, true).becameUsable)
        assertTrue(state.capabilities(3, true).becameUsable)
        assertTrue(state.capabilities(3, false).invalidateConnection)
        assertFalse(state.usable)
        assertTrue(state.capabilities(3, true).becameUsable)
    }

    @Test fun enteredRequiresCurrentMapsGridAndAnActualTaskListEvenWhenEmpty() {
        val ready = SessionReadiness()
        ready.mapInfo(MapInfo(101, "十字路口", 0, 0))
        val epoch = ready.startPoint(RoleStartPoint(100, 200))
        ready.acknowledge(epoch)
        assertFalse(ready.ready)
        ready.mapData(grid(101))
        assertFalse(ready.ready)
        assertEquals("任务列表", ready.missing())
        ready.tasksReceived()
        assertTrue(ready.ready)
        ready.publish()
        ready.mapInfo(MapInfo(100, "怒焰祭坛", 0, 0))
        ready.acknowledge(epoch) // Delayed callback from the old map cannot unlock the new one.
        ready.mapData(grid(101))
        assertFalse(ready.ready)
        ready.mapData(grid(100))
        ready.acknowledge(ready.startPoint(RoleStartPoint(300, 400)))
        assertTrue(ready.ready)
    }

    @Test fun delayedMapInfoDoesNotCancelTheStartPointHandshake() {
        val ready = SessionReadiness()
        val epoch = ready.startPoint(RoleStartPoint(100, 200))
        ready.acknowledge(epoch)
        ready.mapData(grid(101))
        ready.tasksReceived()
        assertFalse(ready.ready)
        ready.mapInfo(MapInfo(101, "十字路口", 0, 0))
        assertTrue(ready.ready)
        assertEquals(epoch, ready.epoch)
    }

    @Test fun aValidEmptyTaskListIsDifferentFromMissingOrTruncatedTaskData() {
        val compressed = ByteArrayOutputStream().also { out ->
            DeflaterOutputStream(out).use { it.write(BinaryWriter().writeUInt16(0).toByteArray()) }
        }.toByteArray()
        fun list(data: ByteArray) = buildFrame(GameOpcode.SERVER_TASK_LIST,
            BinaryWriter().writeUInt16(data.size).writeBytes(data).toByteArray())
        assertTrue(GameProtocol.isValidTaskList(list(compressed)))
        // Windows recordings omit the Adler32 checksum; those lists are still usable.
        assertTrue(GameProtocol.isValidTaskList(list(compressed.dropLast(4).toByteArray())))
        assertFalse(GameProtocol.isValidTaskList(list(compressed.dropLast(6).toByteArray())))
        assertFalse(GameProtocol.isValidTaskList(list(compressed).dropLast(1).toByteArray()))
        assertFalse(GameProtocol.isValidTaskList(buildFrame(GameOpcode.SERVER_TASK_LIST, byteArrayOf())))
    }

    @Test fun bountyRestoredDuringBattleAdvancesOnceAndWaitsForBattleEndBeforeTravelling() {
        var now = System.currentTimeMillis()
        val world = WorldModel { now }
        val task = requireNotNull(javaClass.getResource("/runloop-recorded/bounty-510.hex"))
            .readText().trim().chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        world.learnFrame(FrameStream().push(task).single())
        fun frame(hex: String) = FrameStream().push(hex.chunked(2).map { it.toInt(16).toByte() }.toByteArray()).single()
        world.learnFrame(frame("000A002C00000000033F"))
        val automation = BountyAutomation(2, false)
        val sent = mutableListOf<ByteArray>()
        var sequence = 10L
        val ctx = AutomationContext(world, { _, make -> sent += make(sequence++) },
            { _, _, _ -> }, { fail("Unexpected completion: $it") }, { fail(it) })
        automation.onStart(ctx)
        assertEquals("InBattle", automation.state)
        assertTrue(sent.isEmpty(), "No teleport while the restored role is still fighting")
        now += 649
        automation.onTick(ctx, now)
        assertTrue(sent.isEmpty())
        now++
        automation.onTick(ctx, now)
        assertContentEquals(GameProtocol.buildBattleAdvance(10), sent.single())
        val prompt = frame("000A002C00000000033F")
        world.learnFrame(prompt)
        automation.onServerFrame(ctx, prompt)
        now += 650
        automation.onTick(ctx, now)
        assertEquals(1, sent.size)
        val removed = FrameStream().push(buildFrame(GameOpcode.SERVER_NPC_REMOVED,
            BinaryWriter().writeUInt32(90005).toByteArray())).single()
        world.learnFrame(removed)
        automation.onServerFrame(ctx, removed)
        assertEquals(1, sent.size, "NPC deletion alone must not teleport during battle")
        val end = frame("0008002F00000000")
        world.learnFrame(end)
        automation.onServerFrame(ctx, end)
        assertContentEquals(GameProtocol.buildTravelRequest(TravelTarget(8011, 13, 30, 100), 11), sent.last())
    }

    private fun grid(id: Int) = MapGrid(id, 64, 32, 1, 2, byteArrayOf(1, 1))
}
