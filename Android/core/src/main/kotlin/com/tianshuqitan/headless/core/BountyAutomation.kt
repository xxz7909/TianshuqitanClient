package com.tianshuqitan.headless.core

internal class BountyAutomation(
    private val maxRounds: Int,
    private val allowWarrantRound: Boolean,
) : HeadlessAutomation {
    override val owner: String = "AutoBounty"
    override var state: String = "TravelingToGiver"
        private set

    private var target: TravelTarget? = null
    private var giver: TravelTarget = TravelTarget(GameProtocol.BOUNTY_GIVER_NPC_ID, 13, 30, 100)
    private var taskNumber = 0
    private var completedTasksInRound = 0
    private var completedRounds = 0
    private var claimRoundReward = false
    private var pendingOpen: TravelTarget? = null
    private var openDueAt = 0L
    private var deadlineAt = 0L
    private var returnAfterBattle = false
    private val roundLimit: Int = if (allowWarrantRound) maxRounds.coerceAtMost(3) else maxRounds.coerceAtMost(2)

    override fun onStart(ctx: AutomationContext) {
        val current = ctx.world.bountyTask
        if (current == null) travelToGiver(ctx) else {
            taskNumber = current.number
            target = current.target
            current.giver?.let { giver = it }
            ctx.publish("ResumingTask", "从本次任务列表恢复除暴第 $taskNumber 环。")
            if (ctx.world.battle.isInBattle) {
                state = "InBattle"
                deadlineAt = System.currentTimeMillis() + BATTLE_TIMEOUT_MS
            } else travelToTarget(ctx, current.target)
        }
    }

    override fun onServerFrame(ctx: AutomationContext, frame: Frame) {
        if (returnAfterBattle && !ctx.world.battle.isInBattle) {
            returnAfterBattle = false
            travelToGiver(ctx)
            return
        }
        val dialog = GameProtocol.parseConfirmationDialog(frame.bytes)
        if (dialog != null) {
            if (state == "ConfirmingTeleport") {
                state = "WaitingForMap"
                deadlineAt = System.currentTimeMillis() + MAP_WAIT_TIMEOUT_MS
                ctx.publish(state, "确认传送“${dialog.title}”。")
                ctx.sendPacket("确认传送") { sequence ->
                    GameProtocol.buildDialogResponse(dialog.contextId, dialog.token, true, sequence)
                }
                return
            }

            if (state == "WaitingForBountyConfirmation") {
                val text = "${dialog.title} ${dialog.content}"
                val bountyConfirmation = text.contains(GameProtocol.BOUNTY_FUNCTION_LABEL) ||
                    (allowWarrantRound && text.contains("通缉令"))
                if (bountyConfirmation) {
                    state = "WaitingForTask"
                    deadlineAt = System.currentTimeMillis() + TASK_WAIT_TIMEOUT_MS
                    ctx.publish(state, "确认除暴安良接取。")
                    ctx.sendPacket("确认除暴安良") { sequence ->
                        GameProtocol.buildDialogResponse(dialog.contextId, dialog.token, true, sequence)
                    }
                    return
                }
            }
        }

        if (state == "ConfirmingTeleport" || state == "WaitingForMap") {
            if (frame.opcode == GameOpcode.SERVER_MAP_START) {
                scheduleOpenAfterArrival(ctx, target ?: giver)
                return
            }

            val map = GameProtocol.parseMapInfo(frame.bytes)
            if (map != null) {
                val expected = target ?: giver
                if (map.mapId == expected.mapId) {
                    scheduleOpenAfterArrival(ctx, expected)
                }
                return
            }
        }

        if (frame.opcode == GameOpcode.SERVER_NPC_DIALOG && state == "WaitingForGiverDialog") {
            val parsed = GameProtocol.parseNpcFunction(frame.bytes, GameProtocol.BOUNTY_FUNCTION_LABEL)
            if (parsed == null || parsed.first != GameProtocol.BOUNTY_GIVER_NPC_ID) {
                ctx.fail("舞修罗功能列表中未找到“除暴安良”，没有发送猜测报文。")
                return
            }
            state = if (claimRoundReward) "WaitingForRoundReward" else "WaitingForBountyConfirmation"
            deadlineAt = System.currentTimeMillis() +
                if (claimRoundReward) ROUND_REWARD_TIMEOUT_MS else BOUNTY_CONFIRM_TIMEOUT_MS
            ctx.publish(
                "SelectingBounty",
                if (claimRoundReward) "选择除暴安良领取本轮奖励。" else "选择除暴安良。",
            )
            ctx.sendPacket("选择除暴安良") { sequence -> GameProtocol.buildNpcFunction(parsed.first, parsed.second, sequence) }
            return
        }

        if (state == "WaitingForTask" || state == "WaitingForBountyConfirmation") {
            GameProtocol.parseBountyTaskNumber(frame.bytes)?.let { taskNumber = it }
            val bounty = GameProtocol.parseBountyTask(frame.bytes)
            if (bounty != null) {
                target = bounty.first
                if (bounty.second != null) giver = bounty.second!!
                travelToTarget(ctx, bounty.first)
                return
            }
        }

        if (frame.opcode == GameOpcode.SERVER_BATTLE_PROMPT &&
            (state == "WaitingForBattle" || state == "InBattle")
        ) {
            state = "InBattle"
            deadlineAt = System.currentTimeMillis() + BATTLE_TIMEOUT_MS
            ctx.publish(state, "收到战斗行动提示，推进一回合。")
            return
        }

        val removed = GameProtocol.parseNpcRemoved(frame.bytes)
        if (removed != null &&
            (state == "WaitingForBattle" || state == "InBattle") &&
            removed == target?.npcId
        ) {
            if (taskNumber > 0) {
                completedTasksInRound = maxOf(completedTasksInRound, taskNumber)
            } else {
                completedTasksInRound += 1
            }
            claimRoundReward = completedTasksInRound >= 10
            ctx.publish(
                "ReturningToGiver",
                "除暴第 $completedTasksInRound 环完成，返回舞修罗${if (claimRoundReward) "领取轮次奖励" else ""}。",
            )
            if (ctx.world.battle.isInBattle) returnAfterBattle = true else travelToGiver(ctx)
            return
        }

        if (state == "WaitingForRoundReward" && GameProtocol.containsText(frame.bytes, "任务奖励")) {
            completedRounds += 1
            completedTasksInRound = 0
            taskNumber = 0
            claimRoundReward = false
            if (completedRounds >= roundLimit) {
                deadlineAt = 0L
                ctx.complete("自动除暴已完成 $completedRounds 轮。")
            } else {
                scheduleOpenAfterArrival(ctx, giver, ROUND_REWARD_SETTLE_DELAY_MS)
            }
            return
        }

        if (GameProtocol.containsText(frame.bytes, "次数") && GameProtocol.containsText(frame.bytes, "除暴")) {
            deadlineAt = 0L
            ctx.complete("服务端提示除暴次数已用尽。")
        }
    }

    override fun onTick(ctx: AutomationContext, nowMillis: Long) {
        if (state == "InBattle") ctx.world.battle.onTick(ctx, nowMillis)
        val pending = pendingOpen
        if (pending != null && nowMillis >= openDueAt) {
            pendingOpen = null
            if (pending.npcId == GameProtocol.BOUNTY_GIVER_NPC_ID) openGiver(ctx) else openTarget(ctx, pending)
            return
        }

        if (deadlineAt > 0 && nowMillis > deadlineAt) {
            ctx.fail("$state 阶段超时，自动除暴已停止。")
        }
    }

    override fun onStop(ctx: AutomationContext, reason: String) {
        pendingOpen = null
        deadlineAt = 0L
    }

    private fun travelToGiver(ctx: AutomationContext) {
        target = giver
        pendingOpen = null
        state = "ConfirmingTeleport"
        deadlineAt = System.currentTimeMillis() + TRAVEL_CONFIRM_TIMEOUT_MS
        ctx.publish("TravelingToGiver", "前往舞修罗。")
        ctx.sendPacket("前往舞修罗") { sequence -> GameProtocol.buildTravelRequest(giver, sequence) }
    }

    private fun travelToTarget(ctx: AutomationContext, destination: TravelTarget) {
        target = destination
        pendingOpen = null
        state = "ConfirmingTeleport"
        deadlineAt = System.currentTimeMillis() + TRAVEL_CONFIRM_TIMEOUT_MS
        ctx.publish("TravelingToTarget", "前往除暴目标 $destination。")
        ctx.sendPacket("前往除暴目标") { sequence -> GameProtocol.buildTravelRequest(destination, sequence) }
    }

    private fun scheduleOpenAfterArrival(
        ctx: AutomationContext,
        destination: TravelTarget,
        delayMs: Long = if (destination.npcId == GameProtocol.BOUNTY_GIVER_NPC_ID) 400L else 500L,
    ) {
        if (state == "WaitingForGiverDialog" || state == "WaitingForBattle") return
        pendingOpen = destination
        openDueAt = System.currentTimeMillis() + delayMs
        state = if (destination.npcId == GameProtocol.BOUNTY_GIVER_NPC_ID) "OpeningGiver" else "OpeningTarget"
        deadlineAt = openDueAt + if (destination.npcId == GameProtocol.BOUNTY_GIVER_NPC_ID) {
            GIVER_DIALOG_TIMEOUT_MS
        } else {
            BATTLE_START_TIMEOUT_MS
        }
        ctx.publish(
            state,
            if (destination.npcId == GameProtocol.BOUNTY_GIVER_NPC_ID) {
                "已到舞修罗所在地图，准备访问 NPC。"
            } else {
                "已到任务坐标，准备点击目标 NPC。"
            },
        )
    }

    private fun openGiver(ctx: AutomationContext) {
        state = "WaitingForGiverDialog"
        deadlineAt = System.currentTimeMillis() + GIVER_DIALOG_TIMEOUT_MS
        ctx.publish("OpeningGiver", "访问舞修罗。")
        ctx.sendPacket("访问舞修罗") { sequence -> GameProtocol.buildNpcOpen(GameProtocol.BOUNTY_GIVER_NPC_ID, sequence) }
    }

    private fun openTarget(ctx: AutomationContext, destination: TravelTarget) {
        state = "WaitingForBattle"
        deadlineAt = System.currentTimeMillis() + BATTLE_START_TIMEOUT_MS
        ctx.publish("OpeningTarget", "点击除暴目标。")
        ctx.sendPacket("点击除暴目标") { sequence -> GameProtocol.buildNpcOpen(destination.npcId, sequence) }
    }

    companion object {
        private const val TRAVEL_CONFIRM_TIMEOUT_MS = 25_000L
        private const val MAP_WAIT_TIMEOUT_MS = 30_000L
        private const val GIVER_DIALOG_TIMEOUT_MS = 12_000L
        private const val BOUNTY_CONFIRM_TIMEOUT_MS = 12_000L
        private const val TASK_WAIT_TIMEOUT_MS = 15_000L
        private const val BATTLE_START_TIMEOUT_MS = 20_000L
        private const val BATTLE_TIMEOUT_MS = 60_000L
        private const val ROUND_REWARD_TIMEOUT_MS = 15_000L
        private const val ROUND_REWARD_SETTLE_DELAY_MS = 1_800L
    }
}
