package com.tianshuqitan.headless.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class AutomationTest {
    @Test
    fun ownerLockRejectsCompetingAutomationButRunLoopTakesOverStandaloneCombat() {
        val sent = mutableListOf<ByteArray>()
        val events = mutableListOf<ClientEvent>()
        val manager = AutomationManager(
            sendPacket = { _, factory -> sent += factory((sent.size + 1).toLong()) },
            emit = events::add,
            walkOptions = RunLoopWalkOptions(stepIntervalMs = 10, initialDelayMs = 0),
        )
        try {
            manager.world.currentMap = MapInfo(95, "祭牙台地", 0, 32)
            manager.world.currentGrid = horizontalGrid(95)
            manager.start(AutomationCommand.StandaloneCombat(recovery = false))
            assertEquals("AutoCombat", manager.currentOwner)
            assertFailsWith<IllegalStateException> { manager.start(AutomationCommand.Bounty()) }

            manager.start(AutomationCommand.RunLoop(plannedRounds = 1, recovery = false))
            assertEquals("AutoRunLoop", manager.currentOwner)
            assertTrue(sent.any { readUInt16(it, 2) == GameOpcode.CLIENT_TRAVEL_LINK })
        } finally {
            manager.close()
        }
    }

    @Test
    fun runLoopStartsByTravelingToStartNpc() {
        val sent = mutableListOf<ByteArray>()
        val manager = AutomationManager(
            sendPacket = { _, factory -> sent += factory((sent.size + 1).toLong()) },
            emit = {},
        )
        try {
            manager.start(AutomationCommand.RunLoop(plannedRounds = 1, recovery = false))
            assertEquals("AutoRunLoop", manager.currentOwner)
            assertEquals(1, sent.size)
            val first = sent.single()
            assertEquals(GameOpcode.CLIENT_TRAVEL_LINK, readUInt16(first, 2))
            val body = BinaryReader(first.copyOfRange(4, first.size))
            assertEquals(0xfe, body.readUInt32().toInt())
            assertEquals("93290", body.readString())
            assertEquals("72", body.readString())
            assertEquals("23=132", body.readString())
            assertEquals(1, body.readUInt32().toInt())
            body.assertEnd("run loop start travel")
        } finally {
            manager.close()
        }
    }

    @Test
    fun failedStartReleasesAutomationOwner() {
        val events = mutableListOf<ClientEvent>()
        val manager = AutomationManager(
            sendPacket = { _, _ -> error("write failed") },
            emit = events::add,
        )
        try {
            assertFailsWith<IllegalStateException> {
                manager.start(AutomationCommand.RunLoop(plannedRounds = 1, recovery = false))
            }
            assertEquals(null, manager.currentOwner)
            assertTrue(
                events.any {
                    it is ClientEvent.Automation &&
                        it.owner == "AutoRunLoop" &&
                        it.state == "Failed" &&
                        it.detail.contains("write failed")
                },
            )
        } finally {
            manager.close()
        }
    }

    @Test
    fun failedDelayedSendReleasesAutomationOwnerAndKeepsTickerAlive() {
        val events = mutableListOf<ClientEvent>()
        val manager = AutomationManager(
            sendPacket = { _, _ -> error("delayed write failed") },
            emit = events::add,
            walkOptions = RunLoopWalkOptions(stepIntervalMs = 10, initialDelayMs = 0),
        )
        try {
            manager.world.currentMap = MapInfo(95, "祭牙台地", 0, 32)
            manager.world.currentGrid = horizontalGrid(95)
            manager.start(AutomationCommand.StandaloneCombat(recovery = false))
            Thread.sleep(350)
            assertEquals(null, manager.currentOwner)
            assertTrue(events.any { it is ClientEvent.Automation && it.state == "Failed" && it.detail.contains("delayed write failed") })

            manager.start(AutomationCommand.StandaloneCombat(recovery = false))
            assertEquals("AutoCombat", manager.currentOwner)
            Thread.sleep(350)
            assertEquals(null, manager.currentOwner)
            assertTrue(events.count { it is ClientEvent.Automation && it.state == "Failed" } >= 2)
        } finally {
            manager.close()
        }
    }

    @Test
    fun standaloneCombatWalksOnlyWhenMapGridIsKnown() {
        val sent = mutableListOf<ByteArray>()
        val events = mutableListOf<ClientEvent>()
        val manager = AutomationManager(
            sendPacket = { _, factory -> sent += factory((sent.size + 1).toLong()) },
            emit = events::add,
            walkOptions = RunLoopWalkOptions(stepIntervalMs = 10, initialDelayMs = 0, recoveryIntervalMs = 1_000),
        )
        try {
            manager.world.currentMap = MapInfo(95, "祭牙台地", 0, 32)
            manager.world.currentGrid = horizontalGrid(95)
            manager.start(AutomationCommand.StandaloneCombat(recovery = false))
            Thread.sleep(350)
            assertTrue(sent.count { readUInt16(it, 2) == GameOpcode.CLIENT_MOVEMENT } >= 2)
            val points = sent.filter { readUInt16(it, 2) == GameOpcode.CLIENT_MOVEMENT }
                .take(3)
                .map { GameProtocol.parseMovement(it) }
            assertEquals(listOf(Point2(96, 16), Point2(160, 16), Point2(224, 16)), points)
        } finally {
            manager.close()
        }
    }

    @Test
    fun mapTeleportSendsTwoPacketDirectTeleportSequence() {
        val sent = mutableListOf<ByteArray>()
        val manager = AutomationManager(
            sendPacket = { _, factory -> sent += factory((sent.size + 1).toLong()) },
            emit = {},
        )
        try {
            manager.start(AutomationCommand.MapTeleport(86, TeleportMode.DIRECT_TELEPORT))
            Thread.sleep(900)
            assertEquals(listOf(GameOpcode.CLIENT_UI_ACTION, GameOpcode.CLIENT_UI_ACTION), sent.map { readUInt16(it, 2) })
            assertEquals(316, readUInt32(sent[0], 4).toInt())
            assertEquals(172, readUInt32(sent[1], 4).toInt())
        } finally {
            manager.close()
        }
    }

    @Test
    fun bountyOpensGiverAfterServerMapStartAndConfirmsBountyDialog() {
        val sent = mutableListOf<ByteArray>()
        val manager = AutomationManager(
            sendPacket = { _, factory -> sent += factory((sent.size + 1).toLong()) },
            emit = {},
        )
        try {
            manager.start(AutomationCommand.Bounty(maxRounds = 1))
            assertEquals(GameOpcode.CLIENT_TRAVEL_LINK, readUInt16(sent.last(), 2))

            manager.onServerFrame(serverFrame(GameOpcode.SERVER_CONFIRMATION_DIALOG, confirmationBody("传送", "前往舞修罗")))
            assertEquals(GameOpcode.CLIENT_DIALOG_RESPONSE, readUInt16(sent.last(), 2))

            manager.onServerFrame(serverFrame(GameOpcode.SERVER_MAP_START))
            Thread.sleep(550)
            val openGiver = sent.last()
            assertEquals(GameOpcode.CLIENT_NPC_OPEN, readUInt16(openGiver, 2))
            assertEquals(GameProtocol.BOUNTY_GIVER_NPC_ID, readUInt32(openGiver, 4).toInt())

            manager.onServerFrame(serverFrame(GameOpcode.SERVER_NPC_DIALOG, npcDialogBody()))
            val selectBounty = sent.last()
            assertEquals(GameOpcode.CLIENT_NPC_FUNCTION, readUInt16(selectBounty, 2))
            assertTrue(selectBounty.decodeToString().contains(GameProtocol.BOUNTY_FUNCTION_ID))

            manager.onServerFrame(serverFrame(GameOpcode.SERVER_CONFIRMATION_DIALOG, confirmationBody("除暴安良", "是否接取")))
            assertEquals(GameOpcode.CLIENT_DIALOG_RESPONSE, readUInt16(sent.last(), 2))
            assertEquals("AutoBounty", manager.currentOwner)
        } finally {
            manager.close()
        }
    }

    @Test
    fun dailyAllStartsWithBounty() {
        val manager = AutomationManager(
            sendPacket = { _, _ -> },
            emit = {},
        )
        try {
            manager.start(AutomationCommand.DailyAll)
            assertEquals("AutoBounty", manager.currentOwner)
        } finally {
            manager.close()
        }
    }

    @Test
    fun dailyAllQueueExcludesDonationAndMountainClimb() {
        assertEquals(
            listOf(AutomationCommand.Bounty(), AutomationCommand.RunLoop()),
            dailyAllCommands(),
        )
    }
}

private fun horizontalGrid(mapId: Int): MapGrid =
    MapGrid(
        mapId = mapId,
        rpWidth = 64,
        rpHeight = 32,
        rows = 3,
        cols = 5,
        cells = byteArrayOf(
            0, 0, 0, 0, 0,
            1, 1, 1, 1, 0,
            0, 0, 0, 0, 0,
        ),
    )

private fun serverFrame(opcode: Int, body: ByteArray = ByteArray(0)): Frame {
    val bytes = buildFrame(opcode, body)
    return Frame(opcode, body, bytes)
}

private fun confirmationBody(title: String, content: String): ByteArray =
    BinaryWriter()
        .writeUInt32(123)
        .writeString(title)
        .writeString(content)
        .writeString("token-${title.hashCode().toUInt()}")
        .toByteArray()

private fun npcDialogBody(): ByteArray =
    BinaryWriter()
        .writeUInt32(GameProtocol.BOUNTY_GIVER_NPC_ID.toLong())
        .writeString(GameProtocol.BOUNTY_FUNCTION_LABEL)
        .writeString(GameProtocol.BOUNTY_FUNCTION_ID)
        .toByteArray()
