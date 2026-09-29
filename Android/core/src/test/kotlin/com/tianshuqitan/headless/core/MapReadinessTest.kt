package com.tianshuqitan.headless.core

import java.util.concurrent.CopyOnWriteArrayList
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class MapReadinessTest {
    @Test fun mapInfoCannotStartNpcActionsBeforeTheHeadlessReadyHandshake() {
        val sent = CopyOnWriteArrayList<ByteArray>()
        AutomationManager(
            sendPacket = { _, factory -> sent += factory(sent.size + 1L) },
            emit = {},
            requireMapReady = true,
        ).use { manager ->
            assertFailsWith<IllegalStateException> { manager.start(AutomationCommand.RunLoop()) }
            manager.onMapReady()
            manager.start(AutomationCommand.RunLoop())
            assertEquals(1, sent.size)
            manager.onServerFrame(map(72, "尚其村", 10, 20))
            manager.onServerFrame(startPoint(368, 2112))
            Thread.sleep(600) // Longer than Windows' 450 ms NPC-open delay.
            assertEquals(1, sent.size, "地图就绪前不能访问 NPC")
            assertEquals("Traveling", manager.currentState)
            manager.onMapReady()
            assertEquals("OpeningNpc", manager.currentState)
            assertEquals(MapInfo(72, "尚其村", 368, 2112), manager.world.currentMap)
            Thread.sleep(600)
            assertEquals(GameOpcode.CLIENT_NPC_OPEN, readUInt16(sent.last(), 2))
            assertEquals(93290, readUInt32(sent.last(), 4).toInt())
        }
    }

    @Test fun sessionResetDropsBufferedArrivalFromThePreviousConnection() {
        val sent = CopyOnWriteArrayList<ByteArray>()
        AutomationManager(
            sendPacket = { _, factory -> sent += factory(sent.size + 1L) }, emit = {}, requireMapReady = true,
        ).use { manager ->
            manager.onMapReady()
            manager.start(AutomationCommand.RunLoop())
            manager.onServerFrame(map(72, "尚其村", 10, 20))
            manager.reset()
            manager.onMapReady()
            assertNull(manager.world.currentMap)
            assertNull(manager.currentOwner)
            Thread.sleep(600)
            assertEquals(1, sent.size)
        }
    }

    @Test fun dailyQueueCannotReplaceAnotherActiveOwner() {
        AutomationManager(sendPacket = { _, _ -> }, emit = {}).use { manager ->
            manager.start(AutomationCommand.RunLoop())
            assertFailsWith<IllegalStateException> { manager.start(AutomationCommand.DailyAll) }
            assertEquals("AutoRunLoop", manager.currentOwner)
        }
    }

    @Test fun startingPointUpdatesThePositionWithoutChangingTheCurrentMap() {
        val world = WorldModel()
        world.learnFrame(map(100, "怒焰祭坛", 416, 1216))
        world.learnFrame(startPoint(1664, 1216))
        assertEquals(MapInfo(100, "怒焰祭坛", 1664, 1216), world.currentMap)
    }

    private fun map(id: Int, name: String, x: Int, y: Int) = server(
        GameOpcode.SERVER_MAP_INFO,
        BinaryWriter().writeInt32(id).writeString(name).writeInt32(x).writeInt32(y).toByteArray(),
    )
    private fun startPoint(x: Int, y: Int) = server(
        LoginOpcode.SC_ROLE_START_POINT, BinaryWriter().writeInt16(x).writeInt16(y).toByteArray(),
    )
    private fun server(opcode: Int, body: ByteArray) = Frame(opcode, body, buildFrame(opcode, body))
}
