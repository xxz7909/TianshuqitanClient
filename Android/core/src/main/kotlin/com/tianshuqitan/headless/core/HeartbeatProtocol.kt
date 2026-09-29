package com.tianshuqitan.headless.core

object HeartbeatOpcode {
    const val SERVER_PING = 0x0053
    const val CLIENT_PING = 0x0042
}

private const val AUTH_FIRST_SEND = 0
private const val AUTH_SEED_KEY = "The.tianshu.game.need.your.support"
private const val AUTH_INCREMENT = 931_209_513L

data class ServerPing(
    val counter: Int,
    val serverTimeHigh: Long,
    val serverTimeLow: Long,
    val serverEpochMs: Long,
    val cityRate: Int,
    val authFlag: Int,
    val authText: String,
    val systemTime: String,
)

data class HeartbeatResponse(val ping: ServerPing, val response: ByteArray, val authCode: Long)

fun parseServerPing(frame: Frame): ServerPing {
    require(frame.opcode == HeartbeatOpcode.SERVER_PING) { "not a SC_SERVER_PING frame" }
    val reader = BinaryReader(frame.body)
    val counter = reader.readInt32()
    val high = reader.readUInt32()
    val low = reader.readUInt32()
    val cityRate = reader.readInt32()
    val authFlag = reader.readInt16()
    val authText = reader.readString()
    val systemTime = reader.readString()
    reader.assertEnd("SC_SERVER_PING")
    return ServerPing(
        counter = counter,
        serverTimeHigh = high,
        serverTimeLow = low,
        serverEpochMs = ((high shl 32) or low),
        cityRate = cityRate,
        authFlag = authFlag,
        authText = authText,
        systemTime = systemTime,
    )
}

fun rc4(input: ByteArray, keyText: String = AUTH_SEED_KEY): ByteArray {
    val key = keyText.toByteArray(Charsets.ISO_8859_1)
    require(key.isNotEmpty()) { "RC4 key must not be empty" }
    val state = IntArray(256) { it }
    var j = 0
    for (i in 0 until 256) {
        j = (j + state[i] + (key[i % key.size].toInt() and 0xff)) and 0xff
        val temp = state[i]
        state[i] = state[j]
        state[j] = temp
    }
    val output = ByteArray(input.size)
    var i = 0
    j = 0
    for (offset in input.indices) {
        i = (i + 1) and 0xff
        j = (j + state[i]) and 0xff
        val temp = state[i]
        state[i] = state[j]
        state[j] = temp
        output[offset] = (input[offset].toInt() xor state[(state[i] + state[j]) and 0xff]).toByte()
    }
    return output
}

fun decryptInitialAuthCode(cipherHex: String): Long {
    require(Regex("^(?:[0-9a-fA-F]{2})+$").matches(cipherHex)) {
        "initial heartbeat auth text is not an even-length hex string"
    }
    val plainText = rc4(hexToBytes(cipherHex)).toString(Charsets.ISO_8859_1)
    require(Regex("^\\d+$").matches(plainText)) { "initial heartbeat auth seed is not decimal" }
    val value = plainText.toLong()
    require(value in 0..0xffffffffL) { "initial heartbeat auth seed is outside uint32 range" }
    return value
}

fun transformAuthCode(authCode: Long): Long {
    val sum = (authCode + AUTH_INCREMENT) and 0xffffffffL
    return ((sum ushr 3) or ((sum and 0x7L) shl 29)) and 0xffffffffL
}

class HeartbeatState {
    private var authCode: Long? = null

    fun respond(frame: Frame, writer: ClientFrameWriter): HeartbeatResponse {
        val ping = parseServerPing(frame)
        if (ping.authFlag == AUTH_FIRST_SEND) authCode = decryptInitialAuthCode(ping.authText)
        val current = authCode ?: error("heartbeat bootstrap was missed: no authentication seed is available")
        val next = transformAuthCode(current)
        authCode = next
        val body = BinaryWriter()
            .writeUInt32(ping.counter.toLong() and 0xffffffffL)
            .writeUInt32(ping.serverTimeHigh)
            .writeUInt32(ping.serverTimeLow)
            .writeUInt32(next)
            .toByteArray()
        return HeartbeatResponse(ping, writer.encode(HeartbeatOpcode.CLIENT_PING, body), next)
    }
}
