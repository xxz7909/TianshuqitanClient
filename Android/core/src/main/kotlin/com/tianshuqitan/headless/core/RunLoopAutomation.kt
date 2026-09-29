package com.tianshuqitan.headless.core

/** Behavioral port of src/Protocol/RunLoopAutomation.cs; keep delays and guards in sync. */
internal class RunLoopAutomation(
    private val plannedRounds: Int,
    private val recovery: Boolean,
    private val options: RunLoopWalkOptions,
    private val clock: () -> Long = System::currentTimeMillis,
) : HeadlessAutomation {
    override val owner = "AutoRunLoop"
    override var state = "WaitingForTask"
        private set
    private enum class Purpose { START_NPC, TURN_IN_NPC, HUNT_MAP }
    private var purpose = Purpose.START_NPC
    private var route = emptyList<TravelTarget>()
    private var routeIndex = 0
    private val target get() = route.getOrNull(routeIndex)
    private var expectedPortalMap = 0
    private var task: RunLoopTask? = null
    private var pendingTurnInRing = 0
    private var completedRounds = 0
    private var deliveredRings = 0
    private var huntRetries = 0
    private var path: PatrolPath? = null
    private var walkStartedAt = 0L
    private var lastStepAt = 0L
    private var lastProgressAt = 0L
    private var deadline = 0L
    private var actionAt = 0L
    private var action: ((AutomationContext) -> Unit)? = null
    private var lastServerMessage = ""
    private var suspendedState: String? = null
    private var battlePausedAt = 0L
    private var dispatchAfterBattle = false

    init { require(plannedRounds in 1..6) }

    override fun onStart(ctx: AutomationContext) {
        ctx.world.beginRunLoopTurnIn(0)
        if (ctx.world.lastCombatRecoveryAt == 0L) ctx.world.lastCombatRecoveryAt = clock()
        ctx.publish(state, "自动跑环已启动：每轮 20 环，本次最多 $plannedRounds 轮，次数以服务端许可为准。")
        val current = ctx.world.recentRunLoopTask()
        if (current != null) handleTask(ctx, current) else if (ctx.world.battle.isInBattle) {
            dispatchAfterBattle = true
            pauseForBattle(ctx)
        } else travelToStart(ctx)
    }

    override fun onStop(ctx: AutomationContext, reason: String) {
        action = null
        deadline = 0
        path = null
    }

    override fun onServerFrame(ctx: AutomationContext, frame: Frame) {
        if (ctx.world.battle.isInBattle) pauseForBattle(ctx)
        else if (suspendedState != null) resumeAfterBattle(ctx)
        if (frame.opcode == GameOpcode.SERVER_TASK_UPDATE || frame.opcode == GameOpcode.SERVER_TASK_LIST ||
            frame.opcode == GameOpcode.SERVER_TASK_STATUS) {
            // WorldModel applies the same ring/progress/pending-removal guards as Windows.
            ctx.world.runLoopTask?.let { handleTask(ctx, it) }
            return
        }
        if (GameProtocol.isRunLoopTaskRemoved(frame.bytes)) {
            if (pendingTurnInRing == 0) return
            val ring = pendingTurnInRing
            pendingTurnInRing = 0
            task = null
            deliveredRings++
            if (ring == 20) completedRounds++
            transition("WaitingForTask", if (ring == 20) 0 else 15_000)
            if (ring == 20 && completedRounds >= plannedRounds) {
                ctx.complete("已交付第 20 环，完成 $completedRounds/$plannedRounds 轮，本次共交付 $deliveredRings 环。")
            } else {
                ctx.publish(state, "第 $ring 环交付已由服务端确认，旧任务已清除。" +
                    if (ring == 20) "返回柳先元重新领取下一轮。" else "等待第 ${ring + 1} 环任务。")
                if (ring == 20) schedule(250, ::travelToStart)
            }
            return
        }
        if (frame.opcode == GameOpcode.SERVER_SYSTEM_MESSAGE) {
            lastServerMessage = GameProtocol.extractStrings(frame.bytes)
                .filter { text -> text.any { it in '\u3400'..'\u9fff' } && text.none { it == '\u0000' } }
                .maxByOrNull { it.length }.orEmpty()
            if (purpose == Purpose.START_NPC && state == "WaitingForTask" &&
                GameProtocol.containsText(frame.bytes, "未完成") && GameProtocol.containsText(frame.bytes, "任务")) {
                ctx.world.recentRunLoopTask()?.let { handleTask(ctx, it); return }
                transition("RefreshingTask")
                ctx.publish(state, "服务器提示已有未完成任务，重新登录同步当前跑环任务。")
                ctx.requestSessionRefresh(
                    AutomationCommand.RunLoop(plannedRounds, recovery),
                    "服务器提示已有未完成任务，需要重新同步任务列表。",
                )
            } else if (GameProtocol.containsText(frame.bytes, "跑环") &&
                listOf("上限", "不能再", "无法再", "已满").any { GameProtocol.containsText(frame.bytes, it) }) {
                ctx.complete("服务端提示今日跑环次数已到上限，自动流程结束。$lastServerMessage")
            } else if (!ctx.world.battle.isInBattle && state == "WalkingForEncounter" && GameProtocol.containsText(frame.bytes, "不能走动")) {
                ctx.fail("服务器拒绝当前走步，已立即停止自动跑环并停止继续发包。$lastServerMessage")
            } else if (lastServerMessage.isNotBlank()) {
                ctx.publish(state, "服务器提示：$lastServerMessage")
            }
            return
        }
        if (ctx.world.battle.isInBattle) return
        val dialog = GameProtocol.parseConfirmationDialog(frame.bytes)
        if (dialog != null && state == "Traveling" && dialog.title.contains("传送")) {
            transition("ConfirmingTeleport")
            ctx.publish(state, "已解析本次服务端传送确认串，准备确认“${dialog.title}”。")
            schedule(160) {
                transition("WaitingForMap", 30_000)
                it.sendPacket("确认传送") { seq -> GameProtocol.buildDialogResponse(dialog.contextId, dialog.token, true, seq) }
            }
            return
        }
        // 0x0016 is a start-point frame, not the Windows map-arrival signal (0x0055).
        GameProtocol.parseMapInfo(frame.bytes)?.let {
            path = null
            handleMap(ctx, it)
            return
        }
        if (frame.opcode == GameOpcode.SERVER_MAP_DATA2 || frame.opcode == LoginOpcode.SC_ROLE_START_POINT) {
            path = null
            walkStartedAt = clock()
        }
        if (state == "WaitingForNpcDialog" && frame.opcode == GameOpcode.SERVER_NPC_DIALOG) {
            val npc = target ?: return
            val function = if (purpose == Purpose.START_NPC) GameProtocol.RUN_LOOP_ACCEPT_FUNCTION_ID
                else GameProtocol.RUN_LOOP_TURN_IN_FUNCTION_ID
            if (!GameProtocol.containsFunction(frame.bytes, npc.npcId, function)) return
            val turnInRing = if (purpose == Purpose.TURN_IN_NPC) task?.ringNumber else null
            if (purpose == Purpose.TURN_IN_NPC && turnInRing == null) {
                return ctx.fail("交付前当前跑环任务已丢失，未发送交付包。")
            }
            transition("SelectingFunction")
            ctx.publish(state, "功能列表已确认，准备选择跑环功能 $function。")
            schedule(180) {
                transition("WaitingForTask", 15_000)
                if (turnInRing != null) {
                    pendingTurnInRing = turnInRing
                    it.world.beginRunLoopTurnIn(pendingTurnInRing)
                }
                it.sendPacket("选择跑环功能 $function") { seq -> GameProtocol.buildNpcFunction(npc.npcId, function, seq) }
            }
        }
        // Windows run-loop watchdog is refreshed by task progress, not battle prompts.
    }

    override fun onTick(ctx: AutomationContext, nowMillis: Long) {
        if (ctx.world.battle.isInBattle) {
            pauseForBattle(ctx)
            if (ctx.world.battle.isStalled(nowMillis, options.noProgressTimeoutMs))
                return ctx.fail("战斗响应超时，未继续走步或传送，已停止跑环。")
            ctx.world.battle.onTick(ctx, nowMillis)
            if (recovery) ctx.recoverCombatIfDue(nowMillis, options.recoveryIntervalMs)
            return
        }
        val due = action
        if (due != null && nowMillis >= actionAt) {
            action = null
            due(ctx)
            return
        }
        if (deadline != 0L && nowMillis >= deadline) {
            return ctx.fail("等待 $state 超时，已停止跑环。" +
                if (lastServerMessage.isBlank()) "未收到预期的服务器确认。" else "最近服务器提示：$lastServerMessage")
        }
        if (state != "WalkingForEncounter") return
        val map = ctx.world.currentMap ?: return
        if (nowMillis - lastProgressAt >= options.noProgressTimeoutMs) {
            val current = task ?: return ctx.fail("没有可继续的战斗任务，已停止。")
            if (huntRetries >= options.maximumHuntRetries) return ctx.fail("战斗进度持续未变化，已重新到图 $huntRetries 次，停止跑环。")
            huntRetries++
            ctx.publish(state, "战斗进度未变化，重新到图重试 $huntRetries/${options.maximumHuntRetries}。")
            dispatchTask(ctx, current)
            return
        }
        if (nowMillis - lastStepAt >= options.stepIntervalMs) {
            if (path == null) {
                path = PatrolPath.create(ctx.world.currentGrid?.takeIf { it.mapId == map.mapId }, map.scaledX, map.scaledY, options.patrolPointCount)
                if (path == null && nowMillis - walkStartedAt >= options.mapDataWaitTimeoutMs)
                    return ctx.fail("未能从当前地图数据找到至少两个相连的可行走点，已停止自动遇怪。")
            }
            path?.let {
                lastStepAt = nowMillis
                val point = it.next()
                ctx.world.currentMap = map.copy(scaledX = point.x, scaledY = point.y)
                ctx.sendPacket("循环走步遇怪") { seq -> GameProtocol.buildMovement(nowMillis, map.mapId, point.x, point.y, seq) }
            }
        }
        if (recovery) ctx.recoverCombatIfDue(nowMillis, options.recoveryIntervalMs)
    }

    private fun handleTask(ctx: AutomationContext, next: RunLoopTask) {
        if (pendingTurnInRing != 0) return
        val previous = task
        if (previous != null && (previous.ringNumber != next.ringNumber || next.progress < previous.progress)) return
        task = next
        if (previous != null) {
            if (previous.kind != RunLoopTaskKind.HUNT || next.kind != RunLoopTaskKind.HUNT || next.progress <= previous.progress) return
            lastProgressAt = clock()
            huntRetries = 0
            ctx.publish(state, "第 ${next.ringNumber} 环战斗进度 ${next.progress}/${next.required}。")
            if (!next.isComplete) {
                if (ctx.world.battle.isInBattle) return
                if (state == "WalkingForEncounter") return
                if (RunLoopDestinations.mapId(next.huntMapName) == ctx.world.currentMap?.mapId) {
                    beginWalking(ctx)
                    return
                }
            }
        } else {
            ctx.publish("WaitingForTask", "收到第 ${next.ringNumber} 环任务，本次已交付 $deliveredRings 环。")
        }
        dispatchTask(ctx, next)
    }

    private fun dispatchTask(ctx: AutomationContext, current: RunLoopTask) {
        if (ctx.world.battle.isInBattle) {
            dispatchAfterBattle = true
            pauseForBattle(ctx)
            return
        }
        if (current.kind == RunLoopTaskKind.HUNT && !current.isComplete) {
            val nodes = RunLoopDestinations.huntRoute(current.huntMapName)
                ?: return ctx.fail("尚无“${current.huntMapName}”的已验证入口链，已停止。")
            travel(ctx, nodes, Purpose.HUNT_MAP)
        } else if (current.kind == RunLoopTaskKind.DELIVER_ITEM || current.kind == RunLoopTaskKind.TALK_TO_NPC || current.isComplete) {
            val npc = resolveTurnIn(ctx, current)
                ?: return ctx.fail("无法从已确认的 NPC 表解析“${current.turnInNpcName}”（${current.turnInMapName}），已停止。")
            travel(ctx, listOf(npc), Purpose.TURN_IN_NPC)
        } else {
            ctx.fail("第 ${current.ringNumber} 环任务类型尚未识别，已停止：${current.description}")
        }
    }

    private fun resolveTurnIn(ctx: AutomationContext, current: RunLoopTask): TravelTarget? {
        if (current.turnInNpcId > 0 && current.turnInMapId > 0)
            return TravelTarget(current.turnInNpcId, current.turnInMapId, current.turnInX, current.turnInY)
        val mapId = RunLoopDestinations.mapId(current.turnInMapName)
        val npc = ctx.world.findNpc(current.turnInNpcName, mapId ?: 0) ?: return null
        return npc.copy(mapId = mapId ?: npc.mapId, x = current.turnInX, y = current.turnInY)
    }

    private fun travelToStart(ctx: AutomationContext) =
        travel(ctx, listOf(TravelTarget(93290, 72, 23, 132)), Purpose.START_NPC)

    private fun travel(ctx: AutomationContext, nodes: List<TravelTarget>, newPurpose: Purpose) {
        route = nodes
        routeIndex = 0
        expectedPortalMap = 0
        purpose = newPurpose
        sendRouteTarget(ctx)
    }

    private fun sendRouteTarget(ctx: AutomationContext) {
        val npc = target ?: return ctx.fail("目标路线为空。")
        transition("Traveling", 30_000)
        ctx.publish(state, "任务链接传送 $npc。")
        ctx.sendPacket("任务链接传送 $npc") { seq -> GameProtocol.buildTravelRequest(npc, seq) }
    }

    private fun handleMap(ctx: AutomationContext, map: MapInfo) {
        if (expectedPortalMap != 0 && map.mapId == expectedPortalMap) {
            expectedPortalMap = 0
            beginWalking(ctx)
            return
        }
        if (state !in listOf("Traveling", "ConfirmingTeleport", "WaitingForMap") || map.mapId != target?.mapId) return
        if (routeIndex < route.lastIndex) {
            routeIndex++
            transition("Traveling")
            ctx.publish(state, "已到路线节点 ${map.mapName}，继续下一段。")
            schedule(350, ::sendRouteTarget)
        } else if (purpose == Purpose.HUNT_MAP && map.mapId == 99 && map.mapName == "焚石山") {
            expectedPortalMap = 100
            transition("WaitingForMap", 15_000)
            schedule(350) { it.sendPacket("访问怒焰祭坛入口") { seq -> GameProtocol.buildNpcOpen(233, seq) } }
        } else if (purpose == Purpose.HUNT_MAP) {
            beginWalking(ctx)
        } else {
            val npc = target ?: return
            transition("OpeningNpc", 12_000)
            ctx.publish(state, "已到 NPC ${npc.npcId}，准备打开跑环功能。")
            schedule(450) {
                transition("WaitingForNpcDialog", 12_000)
                it.sendPacket("访问 NPC ${npc.npcId}") { seq -> GameProtocol.buildNpcOpen(npc.npcId, seq) }
            }
        }
    }

    private fun beginWalking(ctx: AutomationContext) {
        transition("WalkingForEncounter")
        val now = clock()
        walkStartedAt = now
        lastStepAt = now + options.initialDelayMs - options.stepIntervalMs
        ctx.world.lastCombatRecoveryAt = now
        lastProgressAt = now
        ctx.publish(state, "已到战斗地图，每 ${options.stepIntervalMs}ms 在最多 ${options.patrolPointCount} 个有效点之间往返；战斗结果由任务进度确认。")
    }

    private fun pauseForBattle(ctx: AutomationContext) {
        if (suspendedState != null) return
        suspendedState = state
        battlePausedAt = clock()
        state = "WaitingForCombatResult"
        ctx.publish(state, "已进入战斗，暂停走步和任务操作，等待服务端回合与结束确认。")
    }

    private fun resumeAfterBattle(ctx: AutomationContext) {
        val previous = suspendedState ?: return
        suspendedState = null
        state = previous
        if (dispatchAfterBattle) {
            dispatchAfterBattle = false
            task?.let { dispatchTask(ctx, it) } ?: travelToStart(ctx)
            return
        }
        val paused = clock() - battlePausedAt
        if (deadline != 0L) deadline += paused
        if (action != null) actionAt += paused
        if (state == "WalkingForEncounter") {
            lastStepAt = clock() + options.initialDelayMs - options.stepIntervalMs
            // Keep the existing patrol and task-progress watchdog, just as Windows does.
            walkStartedAt += paused
        }
        ctx.publish(state, "战斗结束，按当前任务进度继续第 ${task?.ringNumber ?: 0} 环。")
    }

    private fun transition(next: String, timeout: Long = 0) {
        state = next
        deadline = if (timeout > 0) clock() + timeout else 0
        action = null
        path = null
    }

    private fun schedule(delay: Long, callback: (AutomationContext) -> Unit) {
        actionAt = clock() + delay
        action = callback
    }
}
