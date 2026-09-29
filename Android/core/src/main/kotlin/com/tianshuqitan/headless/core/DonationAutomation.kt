package com.tianshuqitan.headless.core

internal class DonationAutomation(
    private val maxItems: Int,
    private val npcId: Int,
) : HeadlessAutomation {
    override val owner: String = "AutoDonation"
    override var state: String = "WaitingForInventory"
        private set

    private var functionId: String = ""
    private var sourceSlot = 0
    private var donated = 0
    private var staged = false
    private var confirmed = false

    override fun onStart(ctx: AutomationContext) {
        val item = ctx.world.inventory.values.firstOrNull { it.isTargetFragment }
        if (item == null) {
            ctx.publish(state, "等待背包快照中的上古神器碎片。")
        } else {
            sourceSlot = item.slot
            openNpc(ctx)
        }
    }

    override fun onServerFrame(ctx: AutomationContext, frame: Frame) {
        if (state == "WaitingForInventory") {
            val item = ctx.world.inventory.values.firstOrNull { it.isTargetFragment }
            if (item != null) {
                sourceSlot = item.slot
                openNpc(ctx)
            }
            return
        }

        if (frame.opcode == GameOpcode.SERVER_NPC_DIALOG && state == "WaitingForNpcDialog") {
            val parsed = GameProtocol.parseNpcFunction(frame.bytes, GameProtocol.DONATION_FUNCTION_LABEL) ?: return
            functionId = parsed.second
            state = "WaitingForDonationPanel"
            ctx.publish("SelectingFunction", "选择帮派捐献。")
            ctx.sendPacket("选择帮派捐献") { sequence -> GameProtocol.buildNpcFunction(parsed.first, functionId, sequence) }
            return
        }

        if (frame.opcode == GameOpcode.SERVER_DONATION_PANEL_STATE && state == "WaitingForDonationPanel") {
            val panelState = if (frame.bytes.size >= 6) readUInt16(frame.bytes, 4) else 0
            if (panelState != 3) return ctx.fail("帮派捐献面板状态不可用：$panelState")
            state = "WaitingForStagedItem"
            staged = false
            confirmed = false
            ctx.publish("SendingDonation", "放入 1 个上古神器碎片。")
            ctx.sendPacket("放入捐献物品") { sequence -> GameProtocol.buildDonateItem(sourceSlot, sequence) }
            return
        }

        val update = parseDonationInventoryUpdate(frame.bytes)
        if (update != null && state == "WaitingForStagedItem" && update.bagId == GameProtocol.DONATION_TARGET_CONTAINER) {
            staged = true
            state = "WaitingForResult"
            ctx.publish("ConfirmingDonation", "临时捐献栏已确认，点击捐献。")
            ctx.sendPacket("点击捐献按钮", GameProtocol::buildDonationConfirm)
            return
        }

        if (state == "WaitingForResult") {
            if (GameProtocol.containsText(frame.bytes, "还没有放入物品")) return ctx.fail("服务端提示临时栏为空，捐献停止。")
            if (GameProtocol.containsText(frame.bytes, "感谢你为帮派作出的贡献") || GameProtocol.containsText(frame.bytes, "帮派贡献增加")) {
                confirmed = true
            }
            if (update != null && update.bagId == 1 && update.slot == sourceSlot) {
                ctx.world.inventory[sourceSlot] = update
            }
            if (confirmed && staged) {
                donated += 1
                if (donated >= maxItems) return ctx.complete("已完成 $donated 次帮派捐献。")
                val item = ctx.world.inventory.values.firstOrNull { it.isTargetFragment && it.count > 0 }
                    ?: return ctx.complete("背包中已无上古神器碎片，捐献结束。")
                sourceSlot = item.slot
                openNpc(ctx)
            }
        }

        if (GameProtocol.containsText(frame.bytes, "捐献") &&
            listOf("次数", "上限", "用尽", "不能", "无法", "今日").any { GameProtocol.containsText(frame.bytes, it) }
        ) {
            ctx.complete("服务端提示捐献次数已用尽。")
        }
    }

    private fun openNpc(ctx: AutomationContext) {
        state = "WaitingForNpcDialog"
        ctx.publish("OpeningNpc", "访问帮派捐献官。")
        ctx.sendPacket("访问帮派捐献官") { sequence -> GameProtocol.buildNpcOpen(npcId, sequence) }
    }
}

private fun parseDonationInventoryUpdate(bytes: ByteArray): InventoryItem? {
    if (!hasOpcode(bytes, GameOpcode.SERVER_ITEM_UPDATE) || bytes.size < 10) return null
    val text = bytes.decodeToString()
    val target = text.contains("115000140") || text.contains("上古神器碎片")
    return InventoryItem(
        bagId = readUInt16(bytes, 4),
        slot = readUInt16(bytes, 6),
        count = readUInt16(bytes, 8),
        name = if (target) "上古神器碎片" else "",
        templateId = if (target) "115000140" else "",
        isTargetFragment = target,
    )
}
