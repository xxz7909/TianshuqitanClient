package com.tianshuqitan.headless.core

import java.io.ByteArrayOutputStream

private const val MAX_FRAME_LENGTH = 0xffff

class BinaryReader(private val bytes: ByteArray) {
    var position: Int = 0
        private set

    val remaining: Int
        get() = bytes.size - position

    fun readUInt16(): Int {
        requireBytes(2)
        val value = ((bytes[position].toInt() and 0xff) shl 8) or
            (bytes[position + 1].toInt() and 0xff)
        position += 2
        return value
    }

    fun readInt16(): Int {
        val value = readUInt16()
        return if ((value and 0x8000) != 0) value - 0x10000 else value
    }

    fun readUInt32(): Long {
        requireBytes(4)
        val value = ((bytes[position].toLong() and 0xffL) shl 24) or
            ((bytes[position + 1].toLong() and 0xffL) shl 16) or
            ((bytes[position + 2].toLong() and 0xffL) shl 8) or
            (bytes[position + 3].toLong() and 0xffL)
        position += 4
        return value
    }

    fun readInt32(): Int = readUInt32().toInt()

    fun readUInt64(): Long {
        val high = readUInt32()
        val low = readUInt32()
        return (high shl 32) or low
    }

    fun readString(): String {
        val length = readUInt16()
        requireBytes(length)
        val value = bytes.decodeToString(position, position + length)
        position += length
        return value
    }

    fun assertEnd(label: String) {
        if (remaining != 0) error("$label has $remaining trailing byte(s)")
    }

    private fun requireBytes(length: Int) {
        if (length < 0 || position + length > bytes.size) {
            error("frame body is truncated at offset $position")
        }
    }
}

class BinaryWriter {
    private val output = ByteArrayOutputStream()

    fun writeUInt16(value: Int): BinaryWriter {
        output.write((value ushr 8) and 0xff)
        output.write(value and 0xff)
        return this
    }

    fun writeInt16(value: Int): BinaryWriter = writeUInt16(value and 0xffff)

    fun writeUInt32(value: Long): BinaryWriter {
        output.write(((value ushr 24) and 0xff).toInt())
        output.write(((value ushr 16) and 0xff).toInt())
        output.write(((value ushr 8) and 0xff).toInt())
        output.write((value and 0xff).toInt())
        return this
    }

    fun writeInt32(value: Int): BinaryWriter = writeUInt32(value.toLong() and 0xffffffffL)

    fun writeUInt64(value: Long): BinaryWriter {
        writeUInt32((value ushr 32) and 0xffffffffL)
        writeUInt32(value and 0xffffffffL)
        return this
    }

    fun writeString(value: String): BinaryWriter {
        val data = value.encodeToByteArray()
        require(data.size <= MAX_FRAME_LENGTH) { "string is too long for u16 framing" }
        writeUInt16(data.size)
        output.write(data)
        return this
    }

    fun writeBytes(value: ByteArray): BinaryWriter {
        output.write(value)
        return this
    }

    fun toByteArray(): ByteArray = output.toByteArray()
}

data class Frame(val opcode: Int, val body: ByteArray, val bytes: ByteArray)

class FrameStream {
    private var pending = ByteArray(0)

    fun push(chunk: ByteArray): List<Frame> {
        pending = if (pending.isEmpty()) chunk.copyOf() else pending + chunk
        val frames = mutableListOf<Frame>()
        var offset = 0
        while (pending.size - offset >= 4) {
            val length = readUInt16(pending, offset)
            if (length < 4) error("invalid frame length $length")
            if (pending.size - offset < length) break
            val bytes = pending.copyOfRange(offset, offset + length)
            frames += Frame(
                opcode = readUInt16(bytes, 2),
                body = bytes.copyOfRange(4, bytes.size),
                bytes = bytes,
            )
            offset += length
        }
        if (offset > 0) pending = pending.copyOfRange(offset, pending.size)
        return frames
    }
}

class ClientFrameWriter {
    var currentMessageNumber: Long = 0
        private set

    fun encode(opcode: Int, body: ByteArray = ByteArray(0)): ByteArray {
        val sequenced = opcode >= 20 && opcode < 2000
        val tailLength = if (sequenced) 4 else 0
        val frameLength = 4 + body.size + tailLength
        require(frameLength <= MAX_FRAME_LENGTH) { "frame exceeds u16 length limit" }
        val frame = ByteArray(frameLength)
        writeUInt16(frame, 0, frameLength)
        writeUInt16(frame, 2, opcode)
        body.copyInto(frame, 4)
        if (sequenced) writeUInt32(frame, frameLength - 4, nextSequence())
        return frame
    }

    fun nextSequence(): Long {
        currentMessageNumber = if (currentMessageNumber == Int.MAX_VALUE.toLong()) 1 else currentMessageNumber + 1
        return currentMessageNumber
    }
}

fun buildFrame(opcode: Int, body: ByteArray = ByteArray(0)): ByteArray {
    val frameLength = 4 + body.size
    require(frameLength <= MAX_FRAME_LENGTH) { "frame exceeds u16 length limit" }
    val frame = ByteArray(frameLength)
    writeUInt16(frame, 0, frameLength)
    writeUInt16(frame, 2, opcode)
    body.copyInto(frame, 4)
    return frame
}

fun hasOpcode(bytes: ByteArray, opcode: Int): Boolean =
    bytes.size >= 4 && readUInt16(bytes, 0) == bytes.size && readUInt16(bytes, 2) == opcode

fun readUInt16(bytes: ByteArray, offset: Int): Int =
    ((bytes[offset].toInt() and 0xff) shl 8) or (bytes[offset + 1].toInt() and 0xff)

fun readUInt32(bytes: ByteArray, offset: Int): Long =
    ((bytes[offset].toLong() and 0xffL) shl 24) or
        ((bytes[offset + 1].toLong() and 0xffL) shl 16) or
        ((bytes[offset + 2].toLong() and 0xffL) shl 8) or
        (bytes[offset + 3].toLong() and 0xffL)

fun writeUInt16(bytes: ByteArray, offset: Int, value: Int) {
    bytes[offset] = ((value ushr 8) and 0xff).toByte()
    bytes[offset + 1] = (value and 0xff).toByte()
}

fun writeUInt32(bytes: ByteArray, offset: Int, value: Long) {
    bytes[offset] = ((value ushr 24) and 0xff).toByte()
    bytes[offset + 1] = ((value ushr 16) and 0xff).toByte()
    bytes[offset + 2] = ((value ushr 8) and 0xff).toByte()
    bytes[offset + 3] = (value and 0xff).toByte()
}

fun ByteArray.toHexUpper(): String = joinToString("") { "%02X".format(it.toInt() and 0xff) }

fun hexToBytes(value: String): ByteArray {
    val compact = value.filterNot { it.isWhitespace() }
    require(compact.length % 2 == 0) { "hex text must have even length" }
    return ByteArray(compact.length / 2) { index ->
        compact.substring(index * 2, index * 2 + 2).toInt(16).toByte()
    }
}
