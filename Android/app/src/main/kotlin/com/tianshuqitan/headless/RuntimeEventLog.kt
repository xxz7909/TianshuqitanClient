package com.tianshuqitan.headless

import android.content.Context
import com.tianshuqitan.headless.core.ClientEvent
import java.io.File
import java.time.Instant
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/** Bounded local diagnostic log; protocol payloads and credentials are never written. */
internal class RuntimeEventLog(context: Context) : AutoCloseable {
    private val file = File(context.filesDir, "runtime-events.log")
    private val previous = File(context.filesDir, "runtime-events.previous.log")
    private val pending = StringBuilder()
    private val flusher = Executors.newSingleThreadScheduledExecutor { runnable ->
        Thread(runnable, "tianshu-event-log").apply { isDaemon = true }
    }

    init {
        flusher.scheduleWithFixedDelay({ flush() }, FLUSH_INTERVAL_SECONDS, FLUSH_INTERVAL_SECONDS, TimeUnit.SECONDS)
    }

    @Synchronized
    fun append(event: ClientEvent) {
        pending.append(Instant.now()).append(' ').append(event.safeLogText()).append('\n')
        if (pending.length >= MAX_PENDING_CHARS) flushLocked()
    }

    @Synchronized
    fun flush() {
        flushLocked()
    }

    @Synchronized
    override fun close() {
        flusher.shutdownNow()
        flushLocked()
    }

    private fun flushLocked() {
        if (pending.isEmpty()) return
        runCatching {
            val text = pending.toString()
            val bytes = text.toByteArray(Charsets.UTF_8)
            if (file.length() + bytes.size >= MAX_BYTES) {
                if (previous.exists()) previous.delete()
                if (file.exists()) file.renameTo(previous)
            }
            file.appendBytes(bytes)
            pending.setLength(0)
        }
    }

    private companion object {
        const val MAX_BYTES = 1_048_576L
        const val MAX_PENDING_CHARS = 64 * 1024
        const val FLUSH_INTERVAL_SECONDS = 2L
    }
}

internal fun ClientEvent.safeLogText(): String = when (this) {
    is ClientEvent.State -> "State(state=$state, detail=$detail)"
    is ClientEvent.Servers -> "Servers(count=${servers.size}, selectedId=$selectedId)"
    is ClientEvent.Roles -> "Roles(count=${roles.size}, selectedSlot=$selectedSlot)"
    is ClientEvent.Entered -> "Entered(x=$x, y=$y)"
    is ClientEvent.Heartbeat -> "Heartbeat(counter=$counter, latencyMs=$latencyMs, authCode=$authCodeHex)"
    is ClientEvent.Packet -> "Packet(direction=$direction, opcode=$opcode, length=$length, name=$name)"
    is ClientEvent.Automation -> "Automation(owner=$owner, state=$state, detail=$detail, error=$error)"
    is ClientEvent.SessionRefreshRequired -> "SessionRefreshRequired(command=$command, reason=$reason)"
    is ClientEvent.Warning -> "Warning(message=$message)"
    is ClientEvent.Error -> "Error(message=$message)"
    is ClientEvent.Disconnected -> "Disconnected(reason=$reason)"
}
