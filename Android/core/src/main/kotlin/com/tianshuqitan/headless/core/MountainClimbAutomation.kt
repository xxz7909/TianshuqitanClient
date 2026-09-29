package com.tianshuqitan.headless.core

internal class MountainClimbAutomation : HeadlessAutomation {
    override val owner: String = "AutoMountainClimb"
    override var state: String = "Starting"
        private set

    private enum class Kind { TRAVEL, PORTAL, WALK, NPC_TELEPORT, QUEST }

    private data class Action(
        val kind: Kind,
        val description: String,
        val target: TravelTarget? = null,
        val mapId: Int = 0,
        val expectedMapId: Int = 0,
        val npcId: Int = 0,
        val functionId: String = "",
        val taskId: String = "",
        val turnIn: Boolean = false,
        val route: List<Point2> = emptyList(),
    )

    private val actions = buildActions()
    private var index = 0
    private var walkIndex = 0
    private var lastWalkAt = 0L

    override fun onStart(ctx: AutomationContext) {
        next(ctx)
    }

    override fun onServerFrame(ctx: AutomationContext, frame: Frame) {
        val action = current() ?: return
        val dialog = GameProtocol.parseConfirmationDialog(frame.bytes)
        if (dialog != null && state == "ConfirmingTeleport") {
            state = "WaitingForMap"
            ctx.publish(state, "确认传送“${dialog.title}”。")
            ctx.sendPacket("确认传送") { sequence -> GameProtocol.buildDialogResponse(dialog.contextId, dialog.token, true, sequence) }
            return
        }

        if (frame.opcode == GameOpcode.SERVER_MAP_START && state == "WaitingForMap") {
            done(ctx)
            return
        }

        val map = GameProtocol.parseMapInfo(frame.bytes)
        if (map != null && state == "WaitingForMap") {
            val expectedMapId = action.target?.mapId ?: action.expectedMapId
            if (map.mapId == expectedMapId) done(ctx)
            return
        }

        if (frame.opcode == GameOpcode.SERVER_NPC_DIALOG && state == "WaitingForNpcDialog") {
            if (!GameProtocol.containsFunction(frame.bytes, action.npcId, action.functionId)) return
            state = if (action.kind == Kind.NPC_TELEPORT) "WaitingForMap" else "WaitingForQuestDetail"
            ctx.publish("SelectingFunction", "选择 NPC 功能 ${action.functionId}。")
            ctx.sendPacket("选择 NPC 功能 ${action.functionId}") { sequence ->
                GameProtocol.buildNpcFunction(action.npcId, action.functionId, sequence)
            }
            return
        }

        if (state == "WaitingForQuestDetail" &&
            frame.opcode == MOUNTAIN_SERVER_QUEST_DETAIL &&
            GameProtocol.containsText(frame.bytes, action.functionId)
        ) {
            state = "WaitingForTaskUpdate"
            ctx.publish(state, "确认${if (action.turnIn) "交付" else "接取"}任务 ${action.taskId}。")
            ctx.sendPacket("确认任务 ${action.taskId}") { sequence ->
                GameProtocol.buildQuestAction(action.npcId, action.functionId, action.turnIn, sequence)
            }
            return
        }

        if (state == "WaitingForTaskUpdate") {
            val accepted = !action.turnIn &&
                (GameProtocol.containsText(frame.bytes, action.taskId) &&
                    (frame.opcode == GameOpcode.SERVER_TASK_UPDATE || frame.opcode == MOUNTAIN_SERVER_TASK_LIST_ENTRY))
            val removed = action.turnIn && GameProtocol.isTaskRemoved(frame.bytes, action.taskId)
            if (accepted || removed) done(ctx)
        }
    }

    override fun onTick(ctx: AutomationContext, nowMillis: Long) {
        val action = current() ?: return
        if (state != "Walking") return
        val map = ctx.world.currentMap
        if (map != null && map.mapId != action.mapId) return ctx.fail("走步路线要求地图 ${action.mapId}，当前地图为 ${map.mapId}。")
        if (nowMillis - lastWalkAt < 500) return
        lastWalkAt = nowMillis
        val point = action.route.getOrNull(walkIndex++) ?: return done(ctx)
        ctx.sendPacket("${action.description} 走步 $walkIndex/${action.route.size}") { sequence ->
            GameProtocol.buildMovement(nowMillis, action.mapId, point.x, point.y, sequence)
        }
        if (walkIndex >= action.route.size) done(ctx)
    }

    private fun next(ctx: AutomationContext) {
        val action = current() ?: return ctx.complete("登山爬塔流程已完成。")
        when (action.kind) {
            Kind.TRAVEL -> {
                state = "ConfirmingTeleport"
                ctx.publish("Traveling", action.description)
                ctx.sendPacket(action.description) { sequence -> GameProtocol.buildTravelRequest(action.target!!, sequence) }
            }
            Kind.PORTAL -> {
                state = "WaitingForMap"
                ctx.publish(state, action.description)
                ctx.sendPacket(action.description) { sequence -> GameProtocol.buildNpcOpen(action.npcId, sequence) }
            }
            Kind.WALK -> {
                state = "Walking"
                walkIndex = 0
                lastWalkAt = 0
                ctx.publish(state, "${action.description}，按录制轨迹发送 ${action.route.size} 个走步帧。")
            }
            Kind.NPC_TELEPORT,
            Kind.QUEST,
            -> {
                state = "WaitingForNpcDialog"
                ctx.publish("OpeningNpc", action.description)
                ctx.sendPacket("访问 NPC ${action.npcId}") { sequence -> GameProtocol.buildNpcOpen(action.npcId, sequence) }
            }
        }
    }

    private fun done(ctx: AutomationContext) {
        val action = current() ?: return
        ctx.publish(state, "完成：${action.description}。")
        index += 1
        next(ctx)
    }

    private fun current(): Action? = actions.getOrNull(index)

    private fun buildActions(): List<Action> = buildList {
        travel("飞行到三界关高攀攀", 93063, 86, 36, 14)
        quest("勇闯通天塔", 93063, BRAVE_TOWER_TASK_ID, BRAVE_TOWER_ACCEPT_FUNCTION, false)
        quest("初探通天塔", 93063, FIRST_TOWER_TASK_ID, FIRST_TOWER_ACCEPT_FUNCTION, false)
        quest("勇攀时雨山", 93063, RAIN_MOUNTAIN_TASK_ID, RAIN_MOUNTAIN_ACCEPT_FUNCTION, false)

        travel("飞行到灵昌城皇宫入口", 14, 12, 24, 28)
        portal("进入皇城皇宫", 14, 13)
        travel("飞行到皇宫通天塔入口", 196, 13, 8, 84)
        portal("进入通天塔一层", 196, 25)
        npcTeleport("通过通天塔传送人直达无间境十层", 8054, BRAVE_TOWER_DIRECT_TELEPORT_FUNCTION, 65)
        walk("无间境十层前往酷酷探险者", 65, "416,688 480,720 544,752 640,736 704,704 768,672 800,656 896,608 960,576 1024,544 1088,512 1152,480 1088,416 992,400 960,384 896,352 800,304 768,288 672,240 608,208 544,208 480,240 384,256 288,208")
        quest("勇闯通天塔", 8075, BRAVE_TOWER_TASK_ID, BRAVE_TOWER_TURN_IN_FUNCTION, true)
        walk("无间境十层返回皇宫出口", 65, "416,208 480,208 544,208 608,208 544,208 416,240 352,272 307,270 320,256")
        portal("返回皇城皇宫", 214, 13)

        portal("再次进入通天塔一层", 196, 25)
        walk("通天塔一层前往元荒境入口", 25, "352,656 416,688 480,720 544,752 608,752 672,720 736,688 800,656 896,608 960,576 1024,544 1088,512 1152,480 1088,416 992,400 928,368 864,336 800,304 736,272 672,240 608,208 544,208 480,240 416,272 320,320 256,352 160,368")
        portal("进入元荒境一层", 71, 26)
        val towerFloorRoute = "352,656 416,688 480,720 544,752 608,752 672,720 736,688 800,656 896,608 960,576 1024,544 1088,512 1152,480 1088,416 992,400 960,384 896,352 800,304 736,272 672,240 608,208 544,208 480,240 416,272 352,304 288,336 160,336"
        for (mapId in 26..29) {
            walk("横穿元荒境${mapId - 25}层", mapId, towerFloorRoute)
            portal("进入元荒境${mapId - 24}层", mapId + 46, mapId + 1)
        }
        walk("元荒境五层前往高塔探险者", 30, "384,672 448,704 512,736 576,768 672,720 736,688 800,656 864,624 928,592 992,560 1056,528 1152,480 1120,432 1056,400 992,400 928,368 864,336 768,288 736,272 672,240 576,192 512,224 448,256 384,224")
        quest("初探通天塔", 8073, FIRST_TOWER_TASK_ID, FIRST_TOWER_TURN_IN_FUNCTION, true)
        walk("走到元荒境五层回城入口", 30, "256,224")
        portal("返回灵昌城", 246, 12)

        travel("飞行到灵昌城皇宫入口附近", 14, 12, 24, 28)
        travel("飞行到灵昌广场入口", 190, 12, 10, 131)
        portal("进入灵昌广场", 190, 97)
        travel("飞行到时雨山脚入口", 111, 97, 3, 85)
        portal("进入时雨山脚", 111, 18)
        walk("沿时雨山脚走到半天坡入口", 18, "1056,1232 992,1200 928,1168 832,1120 800,1104 736,1072 672,1040 608,1008 544,976 448,928 480,880 512,832 544,784 576,704 640,672 672,624 704,576 768,512 832,480 896,448 960,416 1024,384 1088,352 1152,320 1216,288 1280,256 1376,208 1440,176")
        portal("进入半天坡", 112, 19)
        walk("沿半天坡走到清溪云涧入口", 19, "224,1328 288,1264 352,1232 416,1200 480,1168 544,1136 608,1104 736,1104 800,1104 864,1104 960,1088 992,1072 1088,1024 1088,960 1152,928 1120,848 1120,784 1088,736 1056,688 1056,592 992,560 928,528 864,528 736,496 704,480 640,448 576,416 512,384 448,352 352,304 288,272")
        portal("进入清溪云涧", 113, 20)
        walk("清溪云涧前往高山探险者", 20, "1536,1184 1408,1184 1344,1184 1280,1184 1216,1184 1152,1152 1088,1120 992,1104 928,1072 864,1040 800,1040 832,1024")
        quest("勇攀时雨山", 8074, RAIN_MOUNTAIN_TASK_ID, RAIN_MOUNTAIN_TURN_IN_FUNCTION, true)
        walk("走到清溪云涧回城入口", 20, "736,976")
        portal("返回灵昌城", 247, 12)
        travel("飞行到灵昌城皇宫入口", 14, 12, 24, 28)
        portal("返回皇城皇宫并结束", 14, 13)
    }

    private fun MutableList<Action>.travel(description: String, npcId: Int, mapId: Int, x: Int, y: Int) {
        add(Action(Kind.TRAVEL, description, target = TravelTarget(npcId, mapId, x, y)))
    }

    private fun MutableList<Action>.portal(description: String, npcId: Int, expectedMapId: Int) {
        add(Action(Kind.PORTAL, description, npcId = npcId, expectedMapId = expectedMapId))
    }

    private fun MutableList<Action>.npcTeleport(description: String, npcId: Int, functionId: String, expectedMapId: Int) {
        add(Action(Kind.NPC_TELEPORT, description, npcId = npcId, functionId = functionId, expectedMapId = expectedMapId))
    }

    private fun MutableList<Action>.quest(description: String, npcId: Int, taskId: String, functionId: String, turnIn: Boolean) {
        add(Action(Kind.QUEST, description, npcId = npcId, taskId = taskId, functionId = functionId, turnIn = turnIn))
    }

    private fun MutableList<Action>.walk(description: String, mapId: Int, route: String) {
        add(Action(Kind.WALK, description, mapId = mapId, route = parseRoute(route)))
    }

    private fun parseRoute(value: String): List<Point2> =
        value.split(Regex("\\s+")).filter { it.isNotBlank() }.map { part ->
            val pieces = part.split(",")
            require(pieces.size == 2) { "invalid route point $part" }
            Point2(pieces[0].toInt(), pieces[1].toInt())
        }

    companion object {
        private const val MOUNTAIN_SERVER_QUEST_DETAIL = 0x001e
        private const val MOUNTAIN_SERVER_TASK_LIST_ENTRY = 0x0027
        private const val BRAVE_TOWER_TASK_ID = "9382902"
        private const val FIRST_TOWER_TASK_ID = "9382900"
        private const val RAIN_MOUNTAIN_TASK_ID = "9382901"
        private const val BRAVE_TOWER_ACCEPT_FUNCTION = "9382902.1"
        private const val FIRST_TOWER_ACCEPT_FUNCTION = "9382900.1"
        private const val RAIN_MOUNTAIN_ACCEPT_FUNCTION = "9382901.1"
        private const val BRAVE_TOWER_TURN_IN_FUNCTION = "9382902.2"
        private const val FIRST_TOWER_TURN_IN_FUNCTION = "9382900.2"
        private const val RAIN_MOUNTAIN_TURN_IN_FUNCTION = "9382901.2"
        private const val BRAVE_TOWER_DIRECT_TELEPORT_FUNCTION = "9992040"
    }
}
