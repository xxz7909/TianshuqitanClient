package com.tianshuqitan.headless.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class ProtocolTest {
    @Test
    fun heartbeatVectorsMatchRecordedClient() {
        var authCode = decryptInitialAuthCode("0cd6e9fe0a518c86ca")
        assertEquals(484_026_905L, authCode)
        val expected = listOf(
            0x4a8b5968L, 0x30418f52L, 0x6cf8560fL, 0x148f2ee7L, 0x09820a02L,
            0x68206565L, 0xd3f430d1L, 0x416eaa3fL, 0x0f1df96dL, 0xc8d3e352L,
            0x600aa08fL, 0x12f17837L, 0x094e532cL, 0xa819ee8aL, 0x7bf361f6L,
            0xf66e9063L, 0x85bdf631L, 0x57a7e2ebL, 0x91e52082L, 0x792cc835L,
            0xd615bd2bL,
        )
        for (value in expected) {
            authCode = transformAuthCode(authCode)
            assertEquals(value, authCode)
        }
    }

    @Test
    fun heartbeatBuildsByteExactBootstrapResponses() {
        val stream = FrameStream()
        val bootstrap = stream.push(hexToBytes(BOOTSTRAP_REQUEST)).single()
        val second = stream.push(hexToBytes(SECOND_REQUEST)).single()
        val writer = ClientFrameWriter()
        repeat(9) { writer.encode(20) }
        val state = HeartbeatState()
        assertEquals(BOOTSTRAP_RESPONSE, state.respond(bootstrap, writer).response.toHexUpper())
        assertEquals(SECOND_RESPONSE, state.respond(second, writer).response.toHexUpper())
    }

    @Test
    fun frameStreamBuffersSplitTcpData() {
        val bytes = hexToBytes(SECOND_REQUEST)
        val stream = FrameStream()
        assertTrue(stream.push(bytes.copyOfRange(0, 3)).isEmpty())
        assertTrue(stream.push(bytes.copyOfRange(3, 17)).isEmpty())
        val frames = stream.push(bytes.copyOfRange(17, bytes.size))
        assertEquals(1, frames.size)
        assertEquals(0x0053, frames.single().opcode)
    }

    @Test
    fun loginSignatureAndUnsequencedTicketMatchRecordedProtocol() {
        assertEquals("5373df0c4d11c90d419e8a6b16f2ce02 ", buildClientSignature(0, "9051"))
        val writer = ClientFrameWriter()
        val frame = buildUserToken2Frame(writer, "A".repeat(64), true, 0, "9051")
        assertEquals(113, frame.size)
        assertEquals(113, readUInt16(frame, 0))
        assertEquals(6, readUInt16(frame, 2))
        assertEquals(0, writer.currentMessageNumber)
    }

    @Test
    fun enterGameUsesTheTwoRecordedUiActionsAndSharedSequence() {
        val writer = ClientFrameWriter()
        assertEquals(
            "0018002E000000CE00000003FFFFFFFF0000000000000001",
            buildEnterGamePrepareFrame(writer).toHexUpper(),
        )
        assertEquals(
            "0018002E0000004700000000000000000000000000000002",
            buildEnterGameReadyFrame(writer).toHexUpper(),
        )
        assertEquals(2, writer.currentMessageNumber)
    }

    @Test
    fun gamePacketBuildersUseRecordedLayouts() {
        assertEquals(
            "0018002E000003210000000100000003000000000000002A",
            GameProtocol.buildOneKeyRecovery(42).toHexUpper(),
        )
        assertEquals(
            "0018002E0000013C00000056000000000000000000000020",
            GameProtocol.buildSelectMap(86, 32).toHexUpper(),
        )
        assertEquals(
            "0018002E000000AC00000056000000000000000000000021",
            GameProtocol.buildTeleportToMap(86, 33).toHexUpper(),
        )
        assertEquals(
            "001C00B5000000FE000335373500023836000533333D363500000005",
            GameProtocol.buildTravelRequest(TravelTarget(575, 86, 33, 65), 5).toHexUpper(),
        )
        assertEquals(
            "001800C1000001A08DB539C000000064068004C0000000B6",
            GameProtocol.buildMovement(1_789_083_859_392L, 100, 1664, 1216, 182).toHexUpper(),
        )
    }

    @Test
    fun runLoopTaskParserKeepsOnlyRunLoopTask() {
        val html = """
            <a href='event:x:31,y:125,m:72,n:93290'>柳先元(31,125)</a>
            第 1 环，将清风露送到尚其村的柳先元（31,125）处领取下一环任务
        """.trimIndent()
        val frame = serverFrame(
            GameOpcode.SERVER_TASK_UPDATE,
            BinaryWriter()
                .writeString(GameProtocol.RUN_LOOP_TASK_ID)
                .writeUInt16(0)
                .writeString("跑环任务")
                .writeString("跑环任务（第1环）")
                .writeString(html)
                .toByteArray(),
        )
        val task = GameProtocol.parseRunLoopTask(Frame(GameOpcode.SERVER_TASK_UPDATE, frame.copyOfRange(4, frame.size), frame).bytes)
        assertNotNull(task)
        assertEquals(1, task.ringNumber)
        assertEquals(RunLoopTaskKind.DELIVER_ITEM, task.kind)
        assertEquals(93290, task.turnInNpcId)
        assertEquals(72, task.turnInMapId)
    }

    @Test
    fun mapCatalogRejectsUnrecordedMapsAndIncludesRecordedTotems() {
        val sanjie = MapTeleportCatalog.required(86)
        assertEquals(575, sanjie.totemNpcId)
        assertEquals(33, sanjie.totemX)
        assertEquals(null, MapTeleportCatalog.find(999999))
    }
}

private const val BOOTSTRAP_REQUEST = "003C005300000000000001A05FFBF554000003E8000000123063643665396665306135313863383663610010323032362D30392D30322031303A3339"
private const val BOOTSTRAP_RESPONSE = "0018004200000000000001A05FFBF5544A8B59680000000A"
private const val SECOND_REQUEST = "0032005300000001000001A05FFC6A84000000090009000837323434313738370010323032362D30392D30322031303A3339"
private const val SECOND_RESPONSE = "0018004200000001000001A05FFC6A8430418F520000000B"

private fun serverFrame(opcode: Int, body: ByteArray = ByteArray(0)): ByteArray = buildFrame(opcode, body)
