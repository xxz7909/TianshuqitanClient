package com.tianshuqitan.headless.core

/**
 * The battle work normally performed by the Flash client, separate from patrol/task logic.
 *
 * Evidence: Windows BountyAutomation.HandleBattlePrompt schedules CS 0x001F after 650 ms.
 * Recorded session-20260911-073910-503.sqlite frames 1718..1768 and combat-example.sqlite
 * frames 543..587 show SC 0x002A -> 0x002B (seconds, displayed round) -> 0x002C -> CS 0x001F ->
 * SC 0x002D/0x002E, repeated until SC 0x002F. No extra client end-battle packet is present.
 * Flash receiveStartTimer/receiveChooseBattleAction/receiveStopTimer define 0x002B as
 * (i32 seconds, i32 round), 0x002C as (u16 player/pet, u32 action mask), and 0x002D as
 * (u16 fighter position). In particular, 0x002D is not an acknowledgement of our action.
 *
 * Call this from the same serialized owner context as the automation. It has no executor,
 * socket, or sequence counter: sendPacket always uses the live session's shared writer.
 */
internal class HeadlessBattleController(
    private val clock: () -> Long = System::currentTimeMillis,
    private val advanceDelayMs: Long = 650,
) {
    enum class Update { NONE, STARTED, PROMPT, FINISHED }

    var isInBattle: Boolean = false
        private set
    private var pendingAdvanceAt: Long? = null
    private var pendingActor: Int? = null
    private var currentRound: Long? = null
    private val advancedActors = mutableSetOf<Int>()
    private var lastActivityAt = 0L

    init { require(advanceDelayMs >= 0) }

    fun onServerFrame(ctx: AutomationContext, frame: Frame): Update = observe(frame).also {
        if (it == Update.PROMPT) ctx.publish("InBattle", "收到服务端战斗行动提示，等待推进当前回合。")
    }

    /** Keep observing even without an automation owner, so starting/taking over mid-battle is safe. */
    fun observe(frame: Frame, nowMillis: Long = clock()): Update {
        val bytes = frame.bytes
        return when {
            frame.opcode == SERVER_BATTLE_START && validFrame(bytes, SERVER_BATTLE_START, 12) -> {
                if (isInBattle) Update.NONE else {
                    reset()
                    isInBattle = true
                    lastActivityAt = nowMillis
                    Update.STARTED
                }
            }
            frame.opcode == SERVER_BATTLE_START_TIMER && validFrame(bytes, SERVER_BATTLE_START_TIMER, 12) -> {
                // Flash ignores this sentinel entirely; an ordinary countdown need not be 25 s.
                if (readUInt32(bytes, 4) != IGNORED_TIMER_SECONDS) {
                    val round = readUInt32(bytes, 8)
                    if (currentRound != round) clearActionWindow()
                    currentRound = round
                    if (isInBattle) lastActivityAt = nowMillis
                }
                Update.NONE
            }
            frame.opcode == GameOpcode.SERVER_BATTLE_PROMPT && validFrame(bytes, GameOpcode.SERVER_BATTLE_PROMPT, 10) -> {
                isInBattle = true
                val actor = readUInt16(bytes, 4)
                if (pendingActor == actor || actor in advancedActors) Update.NONE else {
                    lastActivityAt = nowMillis
                    // CS 0x001F has no actor field. Answer only the latest selection window;
                    // queuing player and pet packets would apply both to the server's current actor.
                    pendingActor = actor
                    pendingAdvanceAt = nowMillis + advanceDelayMs
                    Update.PROMPT
                }
            }
            frame.opcode == SERVER_BATTLE_STOP_TIMER && validFrame(bytes, SERVER_BATTLE_STOP_TIMER, 6) -> {
                if (isInBattle) lastActivityAt = nowMillis
                Update.NONE
            }
            frame.opcode == SERVER_BATTLE_ROUND_INFO && validFrame(bytes, SERVER_BATTLE_ROUND_INFO, 6) -> {
                // Round results close action selection. This also permits the next prompt when
                // the server omits a displayed-round timer (e.g. taking over mid-battle).
                clearActionWindow()
                if (isInBattle) lastActivityAt = nowMillis
                Update.NONE
            }
            frame.opcode == SERVER_BATTLE_END && validFrame(bytes, SERVER_BATTLE_END, 8) -> {
                val wasInBattle = isInBattle
                reset()
                if (wasInBattle) Update.FINISHED else Update.NONE
            }
            else -> Update.NONE
        }
    }

    fun onTick(ctx: AutomationContext, nowMillis: Long) {
        val dueAt = pendingAdvanceAt ?: return
        if (!isInBattle || nowMillis < dueAt) return
        val actor = pendingActor ?: return
        pendingAdvanceAt = null
        pendingActor = null
        advancedActors += actor
        ctx.sendPacket("推进战斗回合", GameProtocol::buildBattleAdvance)
    }

    fun isStalled(nowMillis: Long, timeoutMs: Long): Boolean =
        isInBattle && nowMillis - lastActivityAt >= timeoutMs

    fun reset() {
        isInBattle = false
        clearActionWindow()
        currentRound = null
        lastActivityAt = 0L
    }

    private fun clearActionWindow() {
        pendingAdvanceAt = null
        pendingActor = null
        advancedActors.clear()
    }

    private fun validFrame(bytes: ByteArray, opcode: Int, minimumLength: Int): Boolean =
        bytes.size >= minimumLength && hasOpcode(bytes, opcode) && readUInt16(bytes, 0) == bytes.size

    companion object {
        const val SERVER_BATTLE_START = 0x002a
        const val SERVER_BATTLE_START_TIMER = 0x002b
        const val SERVER_BATTLE_STOP_TIMER = 0x002d
        const val SERVER_BATTLE_ROUND_INFO = 0x002e
        const val SERVER_BATTLE_END = 0x002f
        private const val IGNORED_TIMER_SECONDS = 1000L
    }
}
