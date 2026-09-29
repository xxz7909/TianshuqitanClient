package com.tianshuqitan.headless.core

import java.security.MessageDigest

object LoginOpcode {
    const val CS_USER_PASS = 0x0000
    const val CS_USER_TOKEN2 = 0x0006
    const val CS_SELECT_ROLE = 0x000a
    const val SC_OPERATE_FAIL = 0x0000
    const val SC_ACCOUNT_INFO = 0x0001
    const val SC_ROLE_INFO_LIST = 0x000c
    const val SC_ROLE_START_POINT = 0x0016
    const val SC_CLIENT_TOKEN = 0x00c8
    const val SC_GAMESERVER_LIST = 0x00c9
    const val SC_SERVER_PING = 0x0053
}

data class LoginRequest(
    val loginHost: String = "124.250.115.168",
    val loginPorts: List<Int> = listOf(7800, 7801, 7802, 7803),
    val username: String,
    val password: String,
    val line: Int = 4,
    val roleSlot: Int = 1,
    val forceLogin: Boolean = false,
    val operationCom: Int = 0,
)

data class GameServer(
    val id: String,
    val name: String,
    val address: String,
    val ports: List<Int>,
    val state: Int,
)

data class RoleSummary(
    val characterId: Int,
    val name: String,
    val gender: String,
    val job: String,
    val level: Int,
    val status: Int,
)

data class RoleStartPoint(val x: Int, val y: Int)

fun buildUserPasswordFrame(writer: ClientFrameWriter, username: String, password: String): ByteArray {
    val body = BinaryWriter().writeString(username).writeString(password).toByteArray()
    return try {
        writer.encode(LoginOpcode.CS_USER_PASS, body)
    } finally {
        body.fill(0)
    }
}

fun buildUserToken2Frame(
    writer: ClientFrameWriter,
    token: String,
    forceLogin: Boolean,
    operationCom: Int,
    serverId: String,
): ByteArray {
    val body = BinaryWriter()
        .writeString(token)
        .writeInt32(if (forceLogin) 1 else 0)
        .writeInt32(operationCom)
        .writeString(buildClientSignature(operationCom, serverId))
        .toByteArray()
    return try {
        writer.encode(LoginOpcode.CS_USER_TOKEN2, body)
    } finally {
        body.fill(0)
    }
}

fun buildSelectRoleFrame(writer: ClientFrameWriter, characterId: Int): ByteArray =
    writer.encode(LoginOpcode.CS_SELECT_ROLE, BinaryWriter().writeInt32(characterId).toByteArray())

/**
 * Flash enterGame() sends these two CS_UI_ACTION frames after every
 * SC_ROLE_START_POINT, including task-link teleports. 0x0047 is the action
 * value inside opcode 0x002e; it is not a standalone client opcode.
 */
fun buildEnterGamePrepareFrame(writer: ClientFrameWriter): ByteArray =
    writer.encode(
        GameOpcode.CLIENT_UI_ACTION,
        BinaryWriter()
            .writeUInt32(ENTER_GAME_PREPARE_ACTION.toLong())
            .writeUInt32(3)
            .writeUInt32(0xffff_ffffL)
            .writeUInt32(0)
            .toByteArray(),
    )

fun buildEnterGameReadyFrame(writer: ClientFrameWriter): ByteArray =
    writer.encode(
        GameOpcode.CLIENT_UI_ACTION,
        BinaryWriter()
            .writeUInt32(ENTER_GAME_READY_ACTION.toLong())
            .writeUInt32(0)
            .writeUInt32(0)
            .writeUInt32(0)
            .toByteArray(),
    )

const val ENTER_GAME_PREPARE_ACTION = 0x00ce
const val ENTER_GAME_READY_ACTION = 0x0047

fun buildClientSignature(operationCom: Int, serverId: String): String =
    md5("$operationCom$serverId${md5("tspk")}") + " "

fun parseClientToken(frame: Frame): String {
    require(frame.opcode == LoginOpcode.SC_CLIENT_TOKEN) { "not a SC_CLIENT_TOKEN frame" }
    val reader = BinaryReader(frame.body)
    val token = reader.readString()
    reader.assertEnd("SC_CLIENT_TOKEN")
    require(Regex("^[A-Za-z0-9+/]+={0,2}$").matches(token) && token.length % 4 == 0) {
        "SC_CLIENT_TOKEN is not valid Base64 text"
    }
    return token
}

fun parseGameServerList(frame: Frame): List<GameServer> {
    require(frame.opcode == LoginOpcode.SC_GAMESERVER_LIST) { "not a SC_GAMESERVER_LIST frame" }
    val reader = BinaryReader(frame.body)
    val count = reader.readInt32()
    require(count in 0..100) { "unreasonable game server count $count" }
    val result = mutableListOf<GameServer>()
    repeat(count) { index ->
        val id = reader.readString()
        val name = reader.readString()
        val address = reader.readString()
        val portsText = reader.readString()
        val state = reader.readInt32()
        val ports = portsText.split(",").map { it.trim().toIntOrNull() ?: -1 }
        require(ports.isNotEmpty() && ports.all { it in 1..65535 }) { "server ${index + 1} has invalid port data" }
        result += GameServer(id, name, address, ports, state)
    }
    reader.assertEnd("SC_GAMESERVER_LIST")
    return result
}

fun findPhysicalLine(servers: List<GameServer>, line: Int): GameServer {
    require(line in 1..4) { "line must be 1..4" }
    val marker = listOf("一线", "二线", "三线", "四线")[line - 1]
    return servers.firstOrNull { it.state != 4 && it.name.contains(marker) }
        ?: servers.firstOrNull { it.name.contains(marker) }
        ?: error("server list does not contain $marker")
}

fun parseRoleInfoList(frame: Frame): List<RoleSummary> {
    require(frame.opcode == LoginOpcode.SC_ROLE_INFO_LIST) { "not a SC_ROLE_INFO_LIST frame" }
    val reader = BinaryReader(frame.body)
    val count = reader.readUInt16()
    require(count <= 5) { "role count $count exceeds the five client slots" }
    val result = mutableListOf<RoleSummary>()
    repeat(count) {
        val characterId = reader.readInt32()
        val name = reader.readString()
        val gender = reader.readString()
        val job = reader.readString()
        val level = reader.readInt32()
        reader.readString()
        reader.readString()
        reader.readString()
        reader.readString()
        repeat(12) { reader.readInt32() }
        reader.readString()
        val status = reader.readInt16()
        result += RoleSummary(characterId, name, gender, job, level, status)
    }
    reader.assertEnd("SC_ROLE_INFO_LIST")
    return result
}

fun parseRoleStartPoint(frame: Frame): RoleStartPoint {
    require(frame.opcode == LoginOpcode.SC_ROLE_START_POINT) { "not a SC_ROLE_START_POINT frame" }
    val reader = BinaryReader(frame.body)
    val point = RoleStartPoint(reader.readInt16(), reader.readInt16())
    reader.assertEnd("SC_ROLE_START_POINT")
    return point
}

fun parseOperateFail(frame: Frame): Pair<Int, String> {
    require(frame.opcode == LoginOpcode.SC_OPERATE_FAIL) { "not a SC_OPERATE_FAIL frame" }
    val reader = BinaryReader(frame.body)
    val category = reader.readInt16()
    val message = reader.readString()
    reader.assertEnd("SC_OPERATE_FAIL")
    return category to message
}

fun opcodeName(opcode: Int, direction: PacketDirection): String {
    val names = if (direction == PacketDirection.CLIENT_TO_SERVER) {
        mapOf(
            LoginOpcode.CS_USER_PASS to "CS_USER_PASS",
            LoginOpcode.CS_USER_TOKEN2 to "CS_USER_TOKEN2",
            LoginOpcode.CS_SELECT_ROLE to "CS_SELECT_ROLE",
            HeartbeatOpcode.CLIENT_PING to "CS_CLIENT_PING",
            GameOpcode.CLIENT_NPC_OPEN to "CS_NPC_OPEN",
            GameOpcode.CLIENT_NPC_FUNCTION to "CS_NPC_FUNCTION",
            GameOpcode.CLIENT_BATTLE_ADVANCE to "CS_BATTLE_ADVANCE",
            GameOpcode.CLIENT_DIALOG_RESPONSE to "CS_DIALOG_RESPONSE",
            GameOpcode.CLIENT_TRAVEL_LINK to "CS_EVENT_TRAVEL_LINK",
            GameOpcode.CLIENT_MOVEMENT to "CS_MAP_MOVEMENT",
            GameOpcode.CLIENT_UI_ACTION to "CS_UI_ACTION",
        )
    } else {
        mapOf(
            LoginOpcode.SC_OPERATE_FAIL to "SC_OPERATE_FAIL",
            LoginOpcode.SC_ACCOUNT_INFO to "SC_ACCOUNT_INFO",
            LoginOpcode.SC_ROLE_INFO_LIST to "SC_ROLE_INFO_LIST",
            LoginOpcode.SC_ROLE_START_POINT to "SC_ROLE_START_POINT",
            LoginOpcode.SC_CLIENT_TOKEN to "SC_CLIENT_TOKEN",
            LoginOpcode.SC_GAMESERVER_LIST to "SC_GAMESERVER_LIST",
            LoginOpcode.SC_SERVER_PING to "SC_SERVER_PING",
            GameOpcode.SERVER_NPC_DIALOG to "SC_NPC_DIALOG",
            GameOpcode.SERVER_SYSTEM_MESSAGE to "SC_SYSTEM_MESSAGE",
            GameOpcode.SERVER_BATTLE_PROMPT to "SC_BATTLE_PROMPT",
            GameOpcode.SERVER_TASK_LIST to "SC_TASK_LIST",
            GameOpcode.SERVER_TASK_STATUS to "SC_TASK_STATUS",
            GameOpcode.SERVER_TASK_UPDATE to "SC_TASK_UPDATE",
            GameOpcode.SERVER_TASK_REMOVED to "SC_TASK_REMOVED",
            GameOpcode.SERVER_MAP_INFO to "SC_MAP_INFO",
            GameOpcode.SERVER_CONFIRMATION_DIALOG to "SC_CONFIRM_DIALOG",
        )
    }
    return names[opcode] ?: "0x${opcode.toString(16).padStart(4, '0').uppercase()}"
}

private fun md5(text: String): String =
    MessageDigest.getInstance("MD5")
        .digest(text.toByteArray(Charsets.UTF_8))
        .joinToString("") { "%02x".format(it.toInt() and 0xff) }

enum class PacketDirection { CLIENT_TO_SERVER, SERVER_TO_CLIENT }
