package com.tianshuqitan.headless.core

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class HeadlessBattleControllerTest {
    @Test fun recordedTwoRoundBattleUsesWindowsDelayAndLiveSequences() {
        val h = BattleHarness(sequence = 0xb3)
        // Session 20260911-073910: 1720/1721 -> 1732/1733, then 1740/1741 -> 1742.
        h.feed("000C002B0000001900000001")
        assertEquals(HeadlessBattleController.Update.PROMPT, h.feed("000A002C00000000033F"))
        assertTrue(h.battle.isInBattle)
        h.advance(649)
        assertTrue(h.sent.isEmpty())
        h.advance(1)
        assertContentEquals(hex("0008001F000000B3"), h.sent.single())
        h.feed("0006002D0002")
        h.feed("000C002B000003E800000000")
        h.feed("000C002B0000001900000002")
        h.feed("000A002C00000000033F")
        h.advance(650)
        assertContentEquals(hex("0008001F000000B4"), h.sent.last())
        h.feed("0006002D0002")
        assertEquals(HeadlessBattleController.Update.FINISHED, h.feed("0008002F00000000"))
        assertFalse(h.battle.isInBattle)
        h.advance(60_000)
        assertEquals(2, h.sent.size, "Battle end has no speculative client acknowledgement")
    }

    @Test fun duplicatePromptsCannotAdvanceOneRoundTwice() {
        val h = BattleHarness()
        h.feed("000C002B0000001900000001")
        h.feed("000A002C00000000033F")
        h.advance(300)
        h.feed("000A002C00000000033F")
        h.advance(350)
        assertEquals(1, h.sent.size)
        h.feed("000A002C00000000033F")
        h.advance(1_000)
        assertEquals(1, h.sent.size)
        h.feed("0006002D0002")
        h.feed("000A002C00000000033F")
        h.advance(1_000)
        assertEquals(1, h.sent.size)
    }

    @Test fun countdownIsSecondsAndNextRoundDoesNotRequireStopTimer() {
        val h = BattleHarness()
        h.feed("000C002B0000001E00000001") // 30 seconds, round 1.
        h.feed("000A002C00000000033F")
        h.advance(650)
        h.feed("000C002B0000000F00000002") // 15 seconds, round 2; no SC_STOP_TIMER.
        h.feed("000A002C00000000033F")
        h.advance(650)
        assertEquals(2, h.sent.size)
        h.feed("000C002B0000001400000002") // Refresh countdown in the same round.
        h.feed("000A002C00000000033F")
        h.advance(650)
        assertEquals(2, h.sent.size, "A different countdown is not a new action window")
    }

    @Test fun playerAndPetSelectionsInSameRoundAreBothAnsweredWithoutAnAck() {
        val h = BattleHarness()
        h.feed("000C002B0000001900000001")
        h.feed("000A002C00000000033F")
        h.advance(650)
        h.feed("000A002C00010000013F") // Pet selection, still round 1.
        h.advance(649)
        assertEquals(1, h.sent.size)
        h.advance(1)
        assertEquals(2, h.sent.size)
        h.feed("0006002D0002") // Fighter position is not the player/pet selector.
        h.feed("000A002C00010000013F")
        h.advance(650)
        assertEquals(2, h.sent.size)
    }

    @Test fun newActorSupersedesPendingSelectionBecauseAutoPacketHasNoActorField() {
        val h = BattleHarness()
        h.feed("000A002C00000000033F")
        h.advance(300)
        h.feed("000A002C00010000013F")
        h.advance(350)
        assertTrue(h.sent.isEmpty())
        h.advance(300)
        assertEquals(1, h.sent.size)
        h.advance(1_000)
        assertEquals(1, h.sent.size, "Do not queue two actorless packets for the latest selection")
    }

    @Test fun sentinelTimerAndOtherFightersStoppingDoNotAuthorizeAnotherAction() {
        val h = BattleHarness()
        h.feed("000C002B0000001900000001")
        h.feed("000A002C00000000033F")
        h.advance(650)
        h.feed("000C002B000003E800000000")
        h.feed("0006002D0007")
        h.feed("0006002D000C")
        h.feed("000A002C00000000013F") // Permission mask changes, same actor and round.
        h.advance(650)
        assertEquals(1, h.sent.size)
    }

    @Test fun roundResultsClosePendingSelectionAndAllowNextPromptWithoutTimer() {
        val h = BattleHarness()
        h.feed("000A002C00000000033F")
        h.advance(650)
        h.feed("0006002E0000") // Structurally valid empty round-result list.
        h.feed("000A002C00000000033F")
        h.advance(650)
        assertEquals(2, h.sent.size)
        h.feed("0006002E0000")
        h.feed("000A002C00000000033F")
        h.feed("0006002E0000") // Server resolves the turn before our delayed action.
        h.advance(650)
        assertEquals(2, h.sent.size)
    }

    @Test fun battleEndCancelsPendingActionAndNextBattleCanReuseRoundOne() {
        val h = BattleHarness()
        h.feed("000C002B0000001900000001")
        h.feed("000A002C00000000033F")
        h.feed("0008002F00000000")
        h.advance(1_000)
        assertTrue(h.sent.isEmpty())
        h.feed("000C002B0000001900000001")
        h.feed("000A002C00000000033F")
        h.advance(650)
        assertEquals(1, h.sent.size)
        h.battle.reset()
        assertFalse(h.battle.isInBattle)
    }

    @Test fun ordinaryPacketsAndTruncatedBattlePacketsDoNotAdvanceBattle() {
        val h = BattleHarness()
        h.feed("0006002C0000")
        h.feed("0008001900015F95") // A different NPC disappearing is not a battle end.
        h.feed("0008001603200390")
        h.advance(60_000)
        assertFalse(h.battle.isInBattle)
        assertTrue(h.sent.isEmpty())
        h.feed("000A002C00000000033F")
        h.feed("0008001900015F95")
        assertTrue(h.battle.isInBattle)
    }

    @Test fun battleStartPausesPatrolBeforeTheFirstActionPrompt() {
        val h = BattleHarness()
        val start = buildFrame(0x002a, ByteArray(187))
        assertEquals(HeadlessBattleController.Update.STARTED, h.battle.onServerFrame(h.ctx, Frame(0x002a, start.copyOfRange(4, start.size), start)))
        assertTrue(h.battle.isInBattle)
        h.advance(10_000)
        assertTrue(h.sent.isEmpty(), "Only a real server prompt authorizes an action")
    }

    private class BattleHarness(sequence: Long = 1) {
        var now = 1_000L
        private var nextSequence = sequence
        val battle = HeadlessBattleController(clock = { now })
        val sent = mutableListOf<ByteArray>()
        val ctx = AutomationContext(
            world = WorldModel(),
            send = { _, factory -> sent += factory(nextSequence++) },
            publish = { _, _, _ -> },
            complete = { error("Unexpected completion: $it") },
            fail = { error(it) },
        )
        fun feed(value: String): HeadlessBattleController.Update {
            val bytes = hex(value)
            return battle.onServerFrame(ctx, Frame(readUInt16(bytes, 2), bytes.copyOfRange(4, bytes.size), bytes))
        }
        fun advance(milliseconds: Long) {
            now += milliseconds
            battle.onTick(ctx, now)
        }
    }

    companion object {
        private fun hex(value: String): ByteArray = value.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    }
}
