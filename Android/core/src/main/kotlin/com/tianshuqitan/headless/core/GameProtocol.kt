package com.tianshuqitan.headless.core

import java.io.ByteArrayOutputStream
import java.nio.charset.CharacterCodingException
import java.util.Base64
import java.util.zip.Inflater

object GameOpcode {
    const val CLIENT_NPC_OPEN = 0x0016
    const val CLIENT_NPC_FUNCTION = 0x0017
    const val CLIENT_QUEST_ACTION = 0x0018
    const val CLIENT_ITEM_TRANSFER = 0x001d
    const val CLIENT_BATTLE_ADVANCE = 0x001f
    const val CLIENT_UI_ACTION = 0x002e
    const val CLIENT_DIALOG_RESPONSE = 0x004c
    const val CLIENT_TRAVEL_LINK = 0x00b5
    const val CLIENT_MOVEMENT = 0x00c1

    const val SERVER_MAP_START = 0x0016
    const val SERVER_NPC_REMOVED = 0x0019
    const val SERVER_ENTITY_LIST = 0x001c
    const val SERVER_NPC_DIALOG = 0x001d
    const val SERVER_SYSTEM_MESSAGE = 0x0029
    const val SERVER_TASK_STATUS = 0x0027
    const val SERVER_BATTLE_PROMPT = 0x002c
    const val SERVER_TASK_LIST = 0x003d
    const val SERVER_TASK_UPDATE = 0x003e
    const val SERVER_TASK_REMOVED = 0x003f
    const val SERVER_MAP_DATA2 = 0x003c
    const val SERVER_MAP_INFO = 0x0055
    const val SERVER_NEARBY_ENTITIES = 0x006a
    const val SERVER_CONFIRMATION_DIALOG = 0x005c
    const val SERVER_INVENTORY_SNAPSHOT_ZLIB = 0x00b2
    const val SERVER_ITEM_UPDATE = 0x0024
    const val SERVER_ITEM_REMOVE = 0x0025
    const val SERVER_DONATION_PANEL_STATE = 0x03c6
    const val SERVER_FLIGHT_LIST = 0x0122
}

data class TravelTarget(val npcId: Int, val mapId: Int, val x: Int, val y: Int) {
    override fun toString(): String = "NPC $npcId / 地图 $mapId / ($x,$y)"
}

data class ConfirmationDialog(val contextId: Long, val title: String, val content: String, val token: String)
data class MapInfo(val mapId: Int, val mapName: String, val scaledX: Int, val scaledY: Int)
data class EntityInfo(val id: Int, val name: String, val x: Int, val y: Int)
data class Point2(val x: Int, val y: Int)
data class BountyTask(val target: TravelTarget, val giver: TravelTarget?, val number: Int)

enum class RunLoopTaskKind { UNKNOWN, DELIVER_ITEM, HUNT, TALK_TO_NPC }

data class RunLoopTask(
    val ringNumber: Int,
    val kind: RunLoopTaskKind,
    val description: String,
    val itemName: String = "",
    val huntMapName: String = "",
    val objectiveName: String = "",
    val turnInMapName: String = "",
    val turnInNpcName: String = "",
    val turnInMapId: Int = 0,
    val turnInNpcId: Int = 0,
    val turnInX: Int = 0,
    val turnInY: Int = 0,
    val progress: Int = 0,
    val required: Int = 0,
) {
    val isComplete: Boolean get() = required > 0 && progress >= required
}

data class MapGrid(
    val mapId: Int,
    val rpWidth: Int,
    val rpHeight: Int,
    val rows: Int,
    val cols: Int,
    val cells: ByteArray,
) {
    fun isWalkable(col: Int, row: Int): Boolean =
        col in 0 until cols && row in 0 until rows && cells[row * cols + col].toInt() != 0

    fun tryGetScaledCenter(col: Int, row: Int): Point2? {
        if (rpWidth <= 0 || rpHeight <= 0) return null
        val x = col * rpWidth + if ((row and 1) == 1) rpWidth / 2 else 0
        val y = (row - 1) * (rpHeight / 2) + rpHeight / 2
        return Point2(x.coerceIn(0, 0xffff), y.coerceIn(0, 0xffff))
    }

    fun tryGetRp(scaledX: Int, scaledY: Int): Point2? {
        if (rpWidth <= 0 || rpHeight <= 0) return null
        val halfWidth = rpWidth / 2
        val halfHeight = rpHeight / 2
        var row = (scaledY - halfHeight) / halfHeight + 1
        val xOffset = if ((row and 1) == 1) halfWidth else 0
        var col = (scaledX - xOffset) / rpWidth
        if (row < 0) row = 0
        if (col < 0) col = 0
        return if (col in 0 until cols && row in 0 until rows) Point2(col, row) else null
    }
}

class PatrolPath private constructor(private val points: List<Point2>) {
    private var nextIndex = 1
    private var direction = 1

    val route: List<Point2> get() = points

    fun next(): Point2 {
        val result = points[nextIndex]
        if (nextIndex == points.lastIndex) direction = -1 else if (nextIndex == 0) direction = 1
        nextIndex += direction
        return result
    }

    companion object {
        fun create(grid: MapGrid?, x: Int, y: Int, maximumPoints: Int): PatrolPath? {
            require(maximumPoints in 2..16) { "maximumPoints must be 2..16" }
            if (grid == null || grid.rpWidth < 2 || grid.rpHeight < 2 || grid.cells.isEmpty()) return null
            if (grid.rows <= 0 || grid.cols <= 0 || grid.rows.toLong() * grid.cols > grid.cells.size) return null
            val current = grid.tryGetRp(x, y) ?: return null
            var anchor: Point2? = null
            var bestDistance = Long.MAX_VALUE
            for (col in current.x - 1..current.x + 1) {
                for (row in current.y - 1..current.y + 1) {
                    val center = tryCenter(grid, col, row) ?: continue
                    val dx = center.x - x
                    val dy = center.y - y
                    val distance = dx.toLong() * dx + dy.toLong() * dy
                    if (distance < bestDistance) {
                        bestDistance = distance
                        anchor = Point2(col, row)
                    }
                }
            }
            val firstCell = anchor ?: return null
            val cells = mutableListOf(firstCell)
            val centers = mutableListOf(tryCenter(grid, firstCell.x, firstCell.y) ?: return null)
            while (centers.size < maximumPoints) {
                val last = cells.last()
                val parity = last.y and 1
                val dx = intArrayOf(1, 0, parity, parity - 1, -1, 0, parity - 1, parity)
                val dy = intArrayOf(0, 2, 1, 1, 0, -2, -1, -1)
                var found = false
                for (index in dx.indices) {
                    val cell = Point2(last.x + dx[index], last.y + dy[index])
                    val center = tryCenter(grid, cell.x, cell.y) ?: continue
                    if (cells.contains(cell) || centers.contains(center)) continue
                    cells += cell
                    centers += center
                    found = true
                    break
                }
                if (!found) break
            }
            return if (centers.size >= 2) PatrolPath(centers) else null
        }

        private fun tryCenter(grid: MapGrid, col: Int, row: Int): Point2? {
            if (!grid.isWalkable(col, row)) return null
            val rawX = col.toLong() * grid.rpWidth + if ((row and 1) == 1) grid.rpWidth / 2 else 0
            val rawY = row.toLong() * (grid.rpHeight / 2)
            if (rawX !in 0..0xffff || rawY !in 0..0xffff) return null
            return grid.tryGetScaledCenter(col, row)
        }
    }
}

object GameProtocol {
    const val BOUNTY_GIVER_NPC_ID = 8011
    const val BOUNTY_FUNCTION_LABEL = "除暴安良"
    const val BOUNTY_FUNCTION_ID = "374"
    const val RUN_LOOP_TASK_ID = "140000000"
    const val RUN_LOOP_ACCEPT_FUNCTION_ID = "140000001"
    const val RUN_LOOP_TURN_IN_FUNCTION_ID = "140000002"
    const val DONATION_NPC_ID = 93631
    const val DONATION_FUNCTION_LABEL = "帮派捐献"
    const val DONATION_ITEM_NAME = "上古神器碎片"
    const val DONATION_TARGET_CONTAINER = 0x0099
    const val ONE_KEY_RECOVERY_ACTION = 0x0321
    const val MAP_SELECT_ACTION = 316
    const val MAP_EXECUTE_TELEPORT_ACTION = 172
    const val FLIGHT_ACTIVATE_ACTION = 804
    const val FLIGHT_POINT_LIST_ID = 290
    const val FLIGHT_COORDINATE_ACTION = 169

    private val travelLinkRegex = Regex(
        "event\\s*:\\s*x\\s*[:=]\\s*(\\d+)\\s*,\\s*y\\s*[:=]\\s*(\\d+)\\s*,\\s*m\\s*[:=]\\s*(\\d+)\\s*,\\s*n\\s*[:=]\\s*(\\d+)",
        RegexOption.IGNORE_CASE,
    )
    private val base64TokenRegex = Regex("(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{24,}={0,2}(?![A-Za-z0-9+/])")
    private val htmlRegex = Regex("<[^>]*>")
    private val ringRegex = Regex("第\\s*(\\d+)\\s*环")
    private val bountyTaskNumberRegex = Regex("第\\s*(\\d+)\\s*次")
    private val deliveryRegex = Regex("将(?<item>.+?)(?:送给到|送到|交给)(?<map>.+?)的(?<npc>.+?)[（(](?<x>\\d+)\\s*[,，]\\s*(?<y>\\d+)[)）]")
    private val huntMapRegex = Regex("^(?:前往|去)(?<map>.+?)消灭")
    private val talkToNpcRegex = Regex("^与(?<map>.+?)的(?<npc>.+?)[（(](?<x>\\d+)\\s*[,，]\\s*(?<y>\\d+)[)）]对话[，,]?\\s*(?:后\\s*)?领取下一环任务")
    private val turnInRegex = Regex("到(?<map>[^，。]+?)的(?<npc>[^，。]+?)[（(](?<x>\\d+)\\s*[,，]\\s*(?<y>\\d+)[)）]处领取")
    private val progressRegex = Regex("(?<name>[^,，]+?)\\s*[（(]\\s*(?<current>\\d+)\\s*/\\s*(?<required>\\d+)\\s*[）)]")
    private val statusProgressRegex = Regex("^(?<name>.+?)\\s*[:：]\\s*(?<current>\\d+)\\s*/\\s*(?<required>\\d+)\\s*$")

    fun buildTravelRequest(target: TravelTarget, sequence: Long): ByteArray =
        buildFrame(
            GameOpcode.CLIENT_TRAVEL_LINK,
            BinaryWriter()
                .writeUInt32(0xfe)
                .writeString(target.npcId.toString())
                .writeString(target.mapId.toString())
                .writeString("${target.x}=${target.y}")
                .writeUInt32(sequence)
                .toByteArray(),
        )

    fun buildNpcOpen(npcId: Int, sequence: Long): ByteArray =
        buildFrame(GameOpcode.CLIENT_NPC_OPEN, BinaryWriter().writeUInt32(npcId.toLong()).writeUInt32(sequence).toByteArray())

    fun buildNpcFunction(npcId: Int, functionId: String, sequence: Long): ByteArray =
        buildFrame(
            GameOpcode.CLIENT_NPC_FUNCTION,
            BinaryWriter().writeUInt32(npcId.toLong()).writeString(functionId).writeUInt32(sequence).toByteArray(),
        )

    fun buildDialogResponse(contextId: Long, token: String, accepted: Boolean, sequence: Long): ByteArray =
        buildFrame(
            GameOpcode.CLIENT_DIALOG_RESPONSE,
            BinaryWriter()
                .writeUInt32(contextId)
                .writeString(token)
                .writeString(if (accepted) "ok" else "cancel")
                .writeUInt16(0)
                .writeUInt32(sequence)
                .toByteArray(),
        )

    fun buildBattleAdvance(sequence: Long): ByteArray =
        buildFrame(GameOpcode.CLIENT_BATTLE_ADVANCE, BinaryWriter().writeUInt32(sequence).toByteArray())

    fun buildMovement(epochMilliseconds: Long, mapId: Int, scaledX: Int, scaledY: Int, sequence: Long): ByteArray =
        buildFrame(
            GameOpcode.CLIENT_MOVEMENT,
            BinaryWriter()
                .writeUInt64(epochMilliseconds)
                .writeUInt32(mapId.toLong())
                .writeUInt16(scaledX)
                .writeUInt16(scaledY)
                .writeUInt32(sequence)
                .toByteArray(),
        )

    fun buildUiAction(action: Int, value: Int, arg1: Int, arg2: Int, sequence: Long): ByteArray =
        buildFrame(
            GameOpcode.CLIENT_UI_ACTION,
            BinaryWriter()
                .writeUInt32(action.toLong())
                .writeUInt32(value.toLong())
                .writeUInt32(arg1.toLong())
                .writeUInt32(arg2.toLong())
                .writeUInt32(sequence)
                .toByteArray(),
        )

    fun buildOneKeyRecovery(sequence: Long): ByteArray = buildUiAction(ONE_KEY_RECOVERY_ACTION, 1, 3, 0, sequence)
    fun buildSelectMap(mapId: Int, sequence: Long): ByteArray = buildUiAction(MAP_SELECT_ACTION, mapId, 0, 0, sequence)
    fun buildTeleportToMap(mapId: Int, sequence: Long): ByteArray = buildUiAction(MAP_EXECUTE_TELEPORT_ACTION, mapId, 0, 0, sequence)
    fun buildActivateFlight(sequence: Long): ByteArray = buildUiAction(FLIGHT_ACTIVATE_ACTION, FLIGHT_POINT_LIST_ID, 0, 0, sequence)
    fun buildFlyToCoordinate(x: Int, y: Int, sequence: Long): ByteArray = buildUiAction(FLIGHT_COORDINATE_ACTION, 0, x, y, sequence)

    fun buildQuestAction(npcId: Int, functionId: String, turnIn: Boolean, sequence: Long): ByteArray =
        buildFrame(
            GameOpcode.CLIENT_QUEST_ACTION,
            BinaryWriter()
                .writeUInt32(npcId.toLong())
                .writeString(functionId)
                .writeUInt16(if (turnIn) 1 else 0)
                .writeUInt32(0)
                .writeUInt32(sequence)
                .toByteArray(),
        )

    fun buildDonateItem(sourceSlot: Int, sequence: Long): ByteArray =
        buildFrame(
            GameOpcode.CLIENT_ITEM_TRANSFER,
            BinaryWriter()
                .writeUInt16(1)
                .writeUInt16(sourceSlot)
                .writeUInt16(DONATION_TARGET_CONTAINER)
                .writeUInt32(0)
                .writeUInt32(sequence)
                .toByteArray(),
        )

    fun buildDonationConfirm(sequence: Long): ByteArray = buildUiAction(0x03b9, 1, 0, 0, sequence)

    fun parseConfirmationDialog(bytes: ByteArray): ConfirmationDialog? {
        if (!hasOpcode(bytes, GameOpcode.SERVER_CONFIRMATION_DIALOG) || bytes.size < 12) return null
        var offset = 4
        if (offset + 4 > bytes.size) return null
        val contextId = readUInt32(bytes, offset)
        offset += 4
        val title = readStringAt(bytes, offset) ?: return null
        offset = title.nextOffset
        val content = readStringAt(bytes, offset) ?: return null
        offset = content.nextOffset
        val token = readStringAt(bytes, offset) ?: return null
        return if (token.value.isNotEmpty()) ConfirmationDialog(contextId, title.value, content.value, token.value) else null
    }

    fun parseNpcFunction(bytes: ByteArray, label: String): Pair<Int, String>? {
        if (!hasOpcode(bytes, GameOpcode.SERVER_NPC_DIALOG) || bytes.size < 10) return null
        val npcId = readUInt32(bytes, 4).toInt()
        for (offset in 8..bytes.size - 2) {
            val value = readStringAt(bytes, offset) ?: continue
            if (value.value != label) continue
            val action = readStringAt(bytes, value.nextOffset) ?: continue
            if (action.value.isNotBlank()) return npcId to action.value
        }
        return null
    }

    fun containsFunction(bytes: ByteArray, expectedNpcId: Int, expectedFunctionId: String): Boolean {
        if (!hasOpcode(bytes, GameOpcode.SERVER_NPC_DIALOG) || bytes.size < 10) return false
        if (readUInt32(bytes, 4).toInt() != expectedNpcId) return false
        return extractStrings(bytes).any { it == expectedFunctionId }
    }

    fun parseBountyTask(bytes: ByteArray): Pair<TravelTarget, TravelTarget?>? {
        if (!hasOpcode(bytes, GameOpcode.SERVER_TASK_UPDATE)) return null
        var enemy: TravelTarget? = null
        var giver: TravelTarget? = null
        for (candidate in extractTextCandidates(bytes)) {
            for (link in extractTravelLinks(candidate)) {
                if (link.npcId == BOUNTY_GIVER_NPC_ID) giver = link else if (enemy == null) enemy = link
            }
        }
        return enemy?.let { it to giver }
    }

    fun parseBountyTaskNumber(bytes: ByteArray): Int? {
        if (!hasOpcode(bytes, GameOpcode.SERVER_TASK_UPDATE)) return null
        return extractTextCandidates(bytes).firstNotNullOfOrNull { candidate ->
            bountyTaskNumberRegex.find(candidate)?.groupValues?.get(1)?.toIntOrNull()?.takeIf { it in 1..10 }
        }
    }

    fun parseBountySnapshot(bytes: ByteArray): BountyTask? {
        val task = when {
            hasOpcode(bytes, GameOpcode.SERVER_TASK_UPDATE) && readStringAt(bytes, 4)?.value == "1984" -> bytes
            hasOpcode(bytes, GameOpcode.SERVER_TASK_LIST) ->
                taskRecordFromList(bytes, "1984")?.let { buildFrame(GameOpcode.SERVER_TASK_UPDATE, it) }
            else -> null
        } ?: return null
        if (!containsText(task, BOUNTY_FUNCTION_LABEL)) return null
        val targets = parseBountyTask(task) ?: return null
        return BountyTask(targets.first, targets.second, parseBountyTaskNumber(task) ?: return null)
    }

    fun parseNpcRemoved(bytes: ByteArray): Int? =
        if (hasOpcode(bytes, GameOpcode.SERVER_NPC_REMOVED) && bytes.size >= 8) readUInt32(bytes, 4).toInt() else null

    fun containsText(bytes: ByteArray, expected: String): Boolean =
        expected.isNotEmpty() && extractTextCandidates(bytes).any { it.contains(expected, ignoreCase = true) }

    fun parseMapInfo(bytes: ByteArray): MapInfo? {
        if (!hasOpcode(bytes, GameOpcode.SERVER_MAP_INFO) || bytes.size < 12) return null
        var offset = 4
        val mapId = readUInt32(bytes, offset).toInt()
        offset += 4
        val name = readStringAt(bytes, offset) ?: return null
        offset = name.nextOffset
        val x = if (offset + 8 <= bytes.size) readUInt32(bytes, offset).coerceAtMost(0xffff).toInt() else 0
        val y = if (offset + 8 <= bytes.size) readUInt32(bytes, offset + 4).coerceAtMost(0xffff).toInt() else 0
        return MapInfo(mapId, normalizeName(name.value), x, y).takeIf { it.mapId > 0 }
    }

    fun parseMovement(bytes: ByteArray): Point2? {
        if (!hasOpcode(bytes, GameOpcode.CLIENT_MOVEMENT) || bytes.size != 24) return null
        return Point2(readUInt16(bytes, 16), readUInt16(bytes, 18))
    }

    fun parseMapData(bytes: ByteArray): MapGrid? {
        if (!hasOpcode(bytes, GameOpcode.SERVER_MAP_DATA2) || bytes.size < 24) return null
        var offset = 4
        val mapId = readUInt16(bytes, offset)
        offset += 2
        val ignoredName = readStringAt(bytes, offset) ?: return null
        offset = ignoredName.nextOffset
        if (offset + 18 > bytes.size) return null
        val values = IntArray(9)
        for (index in values.indices) {
            values[index] = readUInt16(bytes, offset)
            offset += 2
        }
        offset += 6
        if (offset + 4 > bytes.size) return null
        val compressedLength = readUInt32(bytes, offset).toInt()
        offset += 4
        if (compressedLength <= 2 || offset + compressedLength > bytes.size) return null
        val rows = values[7]
        val cols = values[8]
        val cellCount = rows.toLong() * cols
        if (rows <= 0 || cols <= 0 || cellCount !in 1..MAX_MAP_CELLS.toLong()) return null
        val inflated = inflate(bytes, offset, compressedLength, MAX_MAP_CELLS) ?: return null
        if (cellCount > inflated.size) return null
        return MapGrid(mapId, values[5], values[6], rows, cols, inflated.copyOfRange(0, cellCount.toInt()))
    }

    fun parseRunLoopTask(bytes: ByteArray): RunLoopTask? {
        if (!hasOpcode(bytes, GameOpcode.SERVER_TASK_UPDATE) || readStringAt(bytes, 4)?.value != RUN_LOOP_TASK_ID) return null
        return parseRunLoopTaskRecord(bytes.copyOfRange(4, bytes.size))
    }

    fun parseRunLoopTaskList(bytes: ByteArray): RunLoopTask? {
        return taskRecordFromList(bytes, RUN_LOOP_TASK_ID)?.let(::parseRunLoopTaskRecord)
    }

    private fun taskRecordFromList(bytes: ByteArray, taskId: String): ByteArray? {
        val inflated = decodeTaskList(bytes) ?: return null
        val recordStart = (2 until inflated.size - 2).firstOrNull { offset ->
            readUInt16(inflated, offset) == taskId.length && taskRecordIdAt(inflated, offset) == taskId
        } ?: return null
        // Recorded 0x003D lists use: string id, u16 type, string category,
        // string title, followed by the task fields and rewards. Stop at the
        // next record header, not an arbitrary 2048 bytes: the live second-ring
        // record is only 281 bytes, so the old slice included unrelated tasks.
        val recordEnd = (recordStart + 2 until inflated.size - 2).firstOrNull { offset ->
            taskRecordIdAt(inflated, offset) != null
        } ?: inflated.size
        val record = inflated.copyOfRange(recordStart, recordEnd)
        return record
    }

    fun isValidTaskList(bytes: ByteArray): Boolean = decodeTaskList(bytes) != null

    private fun decodeTaskList(bytes: ByteArray): ByteArray? {
        if (!hasOpcode(bytes, GameOpcode.SERVER_TASK_LIST) || bytes.size < 8) return null
        val length = readUInt16(bytes, 4)
        if (length <= 2 || 6 + length != bytes.size) return null
        val data = inflate(bytes, 6, length, MAX_TASK_LIST_BYTES) ?: return null
        if (data.size < 2) return null
        val count = readUInt16(data, 0)
        if (count == 0 && data.size != 2 || count > 0 && count.toLong() * 6 > data.size - 2) return null
        return data
    }

    private fun parseRunLoopTaskRecord(bytes: ByteArray): RunLoopTask? {
        val id = readStringAt(bytes, 0) ?: return null
        if (id.value != RUN_LOOP_TASK_ID || id.nextOffset + 2 > bytes.size) return null
        val textOffset = if (readUInt16(bytes, id.nextOffset) in 0..1) id.nextOffset + 2 else id.nextOffset
        val category = readStringAt(bytes, textOffset) ?: return null
        val title = readStringAt(bytes, category.nextOffset) ?: return null
        val description = readStringAt(bytes, title.nextOffset) ?: return null
        // Ring number and destination must come from this record's actual
        // title/description fields. Reward names and tracker text may also
        // mention a ring or task; scanning those must not replace the task.
        val strings = mutableListOf<String>()
        addTextCandidate(strings, description.value)
        strings += extractStrings(bytes, description.nextOffset)
        val decodedDescription = tryDecodeBase64Text(Regex("\\s+").replace(description.value, "")) ?: description.value
        return parseRunLoopTaskText(title.value, decodedDescription, strings)
    }

    private fun taskRecordIdAt(bytes: ByteArray, offset: Int): String? {
        if (offset < 0 || offset + 2 > bytes.size || readUInt16(bytes, offset) !in 1..12) return null
        val id = readStringAt(bytes, offset) ?: return null
        if (id.value.isEmpty() || !id.value.all { it in '0'..'9' }) return null
        if (id.nextOffset + 2 > bytes.size) return null
        // The original Windows synthetic fixtures omit the type word; both
        // forms have the same two text fields after it. Real recordings use 0/1.
        val textOffset = if (readUInt16(bytes, id.nextOffset) in 0..1) id.nextOffset + 2 else id.nextOffset
        val category = readStringAt(bytes, textOffset) ?: return null
        val title = readStringAt(bytes, category.nextOffset) ?: return null
        if (category.value.isBlank() || title.value.isBlank() ||
            category.value.any(Char::isISOControl) || title.value.any(Char::isISOControl)) return null
        // With no type word, only accept the known legacy run-loop fixture.
        if (textOffset == id.nextOffset && (id.value != RUN_LOOP_TASK_ID || normalizeName(category.value) != "跑环任务")) return null
        return id.value
    }

    fun isRunLoopTaskRemoved(bytes: ByteArray): Boolean {
        if (!hasOpcode(bytes, GameOpcode.SERVER_TASK_REMOVED)) return false
        val id = readStringAt(bytes, 4) ?: return false
        return id.nextOffset == bytes.size && id.value == RUN_LOOP_TASK_ID
    }

    fun isTaskRemoved(bytes: ByteArray, taskId: String): Boolean {
        if (!hasOpcode(bytes, GameOpcode.SERVER_TASK_REMOVED)) return false
        val id = readStringAt(bytes, 4) ?: return false
        return id.value == taskId
    }

    /** Flash receiveTaskStatus: task id, u32 count, then (string status, u32 finish). */
    fun applyRunLoopTaskStatus(bytes: ByteArray, current: RunLoopTask?): RunLoopTask? {
        if (current == null || current.kind != RunLoopTaskKind.HUNT || current.objectiveName.isBlank() ||
            !hasOpcode(bytes, GameOpcode.SERVER_TASK_STATUS)) return null
        val id = readStringAt(bytes, 4) ?: return null
        if (id.value != RUN_LOOP_TASK_ID || id.nextOffset + 4 > bytes.size) return null
        val count = readUInt32(bytes, id.nextOffset)
        var offset = id.nextOffset + 4
        // Every entry needs at least a string length and a finish word. Bound
        // both the count and the byte budget before examining any task data.
        if (count !in 1L..32L || count * 6 > bytes.size - offset) return null
        var matching: TaskProgress? = null
        repeat(count.toInt()) {
            val status = readStringAt(bytes, offset) ?: return null
            offset = status.nextOffset
            if (offset + 4 > bytes.size) return null
            val finish = readUInt32(bytes, offset)
            offset += 4
            if (finish !in 0L..2L) return null
            val progress = parseTaskProgress(status.value, statusProgressRegex)
            if (progress != null && normalizeName(progress.name) == normalizeName(current.objectiveName)) {
                // SC_TASK_STATUS has no ring number. Require the live task's
                // exact objective and denominator so unrelated/old status
                // packets cannot change the random task's destination.
                if (matching != null || progress.required != current.required ||
                    progress.current < current.progress || finish == 2L ||
                    (progress.current >= progress.required) != (finish == 1L)) return null
                matching = progress
            }
        }
        if (offset != bytes.size) return null
        val progress = matching ?: return null
        return current.copy(progress = progress.current)
    }

    fun extractEntities(bytes: ByteArray): List<EntityInfo> {
        if (!hasOpcode(bytes, GameOpcode.SERVER_ENTITY_LIST) && !hasOpcode(bytes, GameOpcode.SERVER_NEARBY_ENTITIES)) return emptyList()
        val result = mutableListOf<EntityInfo>()
        for (offset in 8..bytes.size - 8) {
            val nameLength = readUInt16(bytes, offset)
            if (nameLength !in 3..96 || offset < 4 || offset + 2 + nameLength + 6 > bytes.size) continue
            val name = normalizeName(bytes.decodeToString(offset + 2, offset + 2 + nameLength))
            if (!name.any { it in '\u3400'..'\u9fff' } || name.length > 40) continue
            val id = readUInt32(bytes, offset - 4).toInt()
            val tail = offset + 2 + nameLength
            val type = readUInt16(bytes, tail)
            val x = readUInt16(bytes, tail + 2)
            val y = readUInt16(bytes, tail + 4)
            if (id <= 0 || type > 32 || x > 2000 || y > 2000) continue
            if (result.none { it.id == id }) result += EntityInfo(id, name, x, y)
        }
        return result
    }

    fun extractTravelLinks(text: String): List<TravelTarget> {
        if (text.isBlank()) return emptyList()
        val result = linkedSetOf<TravelTarget>()
        fun add(source: String) {
            travelLinkRegex.findAll(source).forEach { match ->
                result += TravelTarget(
                    npcId = match.groupValues[4].toIntOrNull()?.takeIf { it > 0 } ?: return@forEach,
                    mapId = match.groupValues[3].toIntOrNull()?.takeIf { it > 0 } ?: return@forEach,
                    x = match.groupValues[1].toIntOrNull() ?: return@forEach,
                    y = match.groupValues[2].toIntOrNull() ?: return@forEach,
                )
            }
        }
        add(text)
        tryDecodeBase64Text(text)?.let(::add)
        base64TokenRegex.findAll(text).forEach { token -> tryDecodeBase64Text(token.value)?.let(::add) }
        return result.toList()
    }

    fun extractStrings(bytes: ByteArray, startOffset: Int = 4): List<String> {
        val result = mutableListOf<String>()
        for (offset in startOffset.coerceAtLeast(0)..bytes.size - 2) {
            val value = readStringAt(bytes, offset) ?: continue
            if (value.value.isNotBlank()) addTextCandidate(result, value.value)
        }
        return result
    }

    fun extractTextCandidates(bytes: ByteArray): List<String> {
        val result = mutableListOf<String>()
        addUnique(result, bytes.decodeToString())
        for (offset in 4..bytes.size - 2) {
            val value = readStringAt(bytes, offset) ?: continue
            if (value.value.length >= 4) {
                addUnique(result, value.value)
                tryDecodeBase64Text(value.value)?.let { addUnique(result, it) }
            }
        }
        return result
    }

    fun normalizeName(value: String): String =
        htmlRegex.replace(value, "").replace(" ", "").replace("\t", "").trim()

    private fun parseRunLoopTaskText(title: String, description: String, strings: List<String>): RunLoopTask? {
        val ring = ringRegex.find(normalizeTaskText(title))?.groupValues?.get(1)?.toIntOrNull()
            ?.takeIf { it in 1..20 } ?: return null
        val visibleText = normalizeTaskText(description)
        val start = listOf(visibleText.indexOf("将"), visibleText.indexOf("去"), visibleText.indexOf("前往"), visibleText.indexOf("与"))
            .filter { it >= 0 }.minOrNull() ?: return null
        if (visibleText.indexOf("领取下一环任务", start) < 0) return null
        val desc = visibleText.substring(start)

        val delivery = deliveryRegex.find(desc)
        if (delivery != null) {
            return applyRecordedTravelLink(
                RunLoopTask(
                    ringNumber = ring,
                    kind = RunLoopTaskKind.DELIVER_ITEM,
                    description = desc,
                    itemName = delivery.groups["item"]!!.value.trim(),
                    turnInMapName = normalizeName(delivery.groups["map"]!!.value),
                    turnInNpcName = normalizeName(delivery.groups["npc"]!!.value),
                    turnInX = delivery.groups["x"]!!.value.toIntOrNull() ?: return null,
                    turnInY = delivery.groups["y"]!!.value.toIntOrNull() ?: return null,
                    required = 1,
                ),
                strings,
            )
        }

        val talk = talkToNpcRegex.find(desc)
        if (talk != null) {
            return applyRecordedTravelLink(
                RunLoopTask(
                    ringNumber = ring,
                    kind = RunLoopTaskKind.TALK_TO_NPC,
                    description = desc,
                    turnInMapName = normalizeName(talk.groups["map"]!!.value),
                    turnInNpcName = normalizeName(talk.groups["npc"]!!.value),
                    turnInX = talk.groups["x"]!!.value.toIntOrNull() ?: return null,
                    turnInY = talk.groups["y"]!!.value.toIntOrNull() ?: return null,
                    required = 1,
                ),
                strings,
            )
        }

        val huntMap = huntMapRegex.find(desc) ?: return null
        val turnIn = turnInRegex.find(desc) ?: return null
        var objectiveName = ""
        var progressValue = 0
        var requiredValue = 0
        for (text in strings.map(::normalizeTaskText)) {
            val progress = parseTaskProgress(text, progressRegex) ?: continue
            objectiveName = progress.name
            progressValue = progress.current
            requiredValue = progress.required
            break
        }
        if (requiredValue <= 0) {
            requiredValue = Regex("(?:消灭|得到)\\s*(\\d+)\\s*个").find(desc)?.groupValues?.get(1)?.toIntOrNull() ?: 1
        }
        return applyRecordedTravelLink(
            RunLoopTask(
                ringNumber = ring,
                kind = RunLoopTaskKind.HUNT,
                description = desc,
                huntMapName = normalizeName(huntMap.groups["map"]!!.value),
                objectiveName = objectiveName,
                turnInMapName = normalizeName(turnIn.groups["map"]!!.value),
                turnInNpcName = normalizeName(turnIn.groups["npc"]!!.value),
                turnInX = turnIn.groups["x"]!!.value.toIntOrNull() ?: return null,
                turnInY = turnIn.groups["y"]!!.value.toIntOrNull() ?: return null,
                progress = progressValue,
                required = requiredValue,
            ),
            strings,
        )
    }

    private data class TaskProgress(val name: String, val current: Int, val required: Int)

    private fun parseTaskProgress(text: String, pattern: Regex): TaskProgress? {
        val match = pattern.find(normalizeTaskText(text)) ?: return null
        val name = match.groups["name"]!!.value.trim().takeIf { it.isNotEmpty() } ?: return null
        val current = match.groups["current"]!!.value.toIntOrNull() ?: return null
        val required = match.groups["required"]!!.value.toIntOrNull()?.takeIf { it > 0 } ?: return null
        return TaskProgress(name, current, required)
    }

    private fun applyRecordedTravelLink(task: RunLoopTask, strings: List<String>): RunLoopTask {
        for (text in strings) {
            for (link in extractTravelLinks(text)) {
                if (link.x == task.turnInX && link.y == task.turnInY) {
                    return task.copy(turnInNpcId = link.npcId, turnInMapId = link.mapId)
                }
            }
        }
        return task
    }

    private fun normalizeTaskText(value: String): String =
        htmlRegex.replace(value, "")
            .replace("&nbsp;", " ")
            .replace("&#160;", " ")
            .replace("&lt;", "<")
            .replace("&gt;", ">")
            .replace("&amp;", "&")
            .replace("\r", "")
            .replace("\n", "")
            .trim()

    private fun addTextCandidate(result: MutableList<String>, value: String) {
        addUnique(result, value)
        val compact = Regex("\\s+").replace(value, "")
        if (compact.length < 16 || compact.length % 4 != 0) return
        tryDecodeBase64Text(compact)?.let { addUnique(result, it) }
    }

    private fun addUnique(result: MutableList<String>, value: String) {
        if (value !in result) result += value
    }

    private fun readStringAt(bytes: ByteArray, offset: Int): StringRead? {
        if (offset < 0 || offset + 2 > bytes.size) return null
        val length = readUInt16(bytes, offset)
        if (offset + 2 + length > bytes.size) return null
        return try {
            StringRead(bytes.decodeToString(offset + 2, offset + 2 + length, throwOnInvalidSequence = true), offset + 2 + length)
        } catch (_: CharacterCodingException) {
            null
        }
    }

    private data class StringRead(val value: String, val nextOffset: Int)

    private fun inflate(bytes: ByteArray, offset: Int, length: Int, maximumOutputBytes: Int): ByteArray? {
        if (offset < 0 || length <= 2 || offset.toLong() + length > bytes.size) return null
        // Windows InflateZlib skips the two-byte header and reads raw DEFLATE.
        // Recorded task/map frames may omit the Adler32 trailer; the default Java
        // zlib decoder rejects those and silently loses the existing task list.
        val inflater = Inflater(true)
        return try {
            inflater.setInput(bytes, offset + 2, length - 2)
            val output = ByteArrayOutputStream(minOf(maximumOutputBytes, 8192))
            val buffer = ByteArray(4096)
            while (!inflater.finished()) {
                val read = inflater.inflate(buffer)
                if (read > 0) {
                    if (output.size().toLong() + read > maximumOutputBytes) return null
                    output.write(buffer, 0, read)
                } else if (inflater.needsInput() || inflater.needsDictionary()) {
                    return null
                } else {
                    return null
                }
            }
            output.toByteArray()
        } catch (_: Exception) {
            null
        } finally {
            inflater.end()
        }
    }

    private const val MAX_MAP_CELLS = 8 * 1024 * 1024
    private const val MAX_TASK_LIST_BYTES = 2 * 1024 * 1024

    private fun tryDecodeBase64Text(value: String): String? =
        try {
            Base64.getDecoder().decode(value).decodeToString(throwOnInvalidSequence = true)
        } catch (_: Exception) {
            null
        }

    private fun ByteArray.indexOf(needle: ByteArray): Int {
        if (needle.isEmpty() || size < needle.size) return -1
        for (i in 0..size - needle.size) {
            var matched = true
            for (j in needle.indices) {
                if (this[i + j] != needle[j]) {
                    matched = false
                    break
                }
            }
            if (matched) return i
        }
        return -1
    }
}

data class MapTeleportDestination(
    val mapId: Int,
    val mapName: String,
    val arrivalX: Int,
    val arrivalY: Int,
    val totemNpcId: Int,
    val totemX: Int,
    val totemY: Int,
) {
    fun totemTarget(): TravelTarget = TravelTarget(totemNpcId, mapId, totemX, totemY)
    override fun toString(): String = "$mapName ($mapId)"
}

object MapTeleportCatalog {
    val all: List<MapTeleportDestination> = listOf(
        entry(3, "新月村", 2240, 384, 542, 36, 22),
        entry(86, "三界关", 2112, 1024, 575, 33, 65),
        entry(12, "灵昌城", 3424, 1328, 544, 40, 81),
        entry(76, "坠龙城", 896, 3360, 560, 14, 211),
        entry(77, "广寒城", 4384, 1136, 568, 67, 70),
        entry(89, "天山瑶池", 2080, 944, 578, 34, 60),
        entry(156, "仙境府邸", 1792, 320, 592, 29, 18),
        entry(9, "灵仙岛", 928, 2160, 543, 12, 137),
        entry(70, "天涯海角", 992, 1392, 562, 15, 90),
        entry(102, "逐浪广场", 928, 1008, 589, 13, 61),
        entry(72, "尚其村", 1984, 1984, 564, 31, 125),
        entry(75, "幽谷清泉", 1152, 960, 567, 18, 61),
        entry(101, "十字路口", 1248, 560, 587, 18, 35),
        entry(92, "北岭天关", 864, 304, 581, 13, 16),
        entry(91, "天外天", 192, 736, 580, 2, 44),
        entry(90, "九霄台", 960, 416, 579, 14, 25),
        entry(84, "落雁峰", 800, 592, 570, 12, 33),
        entry(83, "咆哮谷", 800, 976, 569, 12, 57),
        entry(88, "万重山雪顶", 1440, 1904, 577, 21, 117),
        entry(87, "万重山脚", 896, 1088, 576, 13, 66),
        entry(81, "千针雪林", 256, 960, 571, 3, 62),
        entry(78, "碧波水域", 480, 144, 574, 6, 8),
        entry(80, "伏魔山", 832, 1152, 572, 14, 73),
        entry(100, "怒焰祭坛", 1600, 1248, 586, 26, 76),
        entry(99, "焚石山", 160, 880, 585, 1, 53),
        entry(95, "祭牙台地", 1408, 704, 584, 21, 42),
        entry(93, "迷途沙洲", 928, 528, 582, 15, 31),
        entry(94, "困顿之林", 576, 672, 583, 8, 40),
        entry(73, "密霞谷", 928, 176, 563, 15, 9),
        entry(67, "回音谷", 1728, 640, 559, 28, 38),
        entry(79, "埋骨之地", 768, 992, 573, 11, 63),
        entry(74, "不归幽林", 1408, 320, 557, 21, 18),
        entry(68, "西川沼泽", 288, 336, 565, 3, 19),
        entry(66, "明湖水寨", 1152, 800, 561, 17, 52),
        entry(16, "芷水湖", 384, 608, 541, 7, 36),
        entry(69, "召凤台", 352, 368, 558, 5, 24),
        entry(71, "迷雾海", 1376, 848, 566, 20, 51),
        entry(15, "黄金港口", 672, 1008, 538, 9, 61),
        entry(10, "古道", 384, 1088, 534, 6, 62),
        entry(8, "迷梦泽", 1856, 832, 535, 29, 48),
        entry(7, "黑色水域", 1824, 816, 536, 27, 53),
        entry(5, "北影月森林", 1088, 608, 532, 16, 36),
        entry(4, "东影月森林", 704, 416, 533, 10, 24),
    )

    fun find(mapId: Int): MapTeleportDestination? = all.firstOrNull { it.mapId == mapId }
    fun find(value: String): MapTeleportDestination? =
        value.toIntOrNull()?.let(::find) ?: all.firstOrNull { it.mapName == value || it.mapName.contains(value) }

    fun required(mapId: Int): MapTeleportDestination = find(mapId) ?: error("未录制地图 $mapId 的传送点")
    fun required(value: String): MapTeleportDestination = find(value) ?: error("未录制地图 $value 的传送点")

    private fun entry(
        mapId: Int,
        mapName: String,
        arrivalX: Int,
        arrivalY: Int,
        totemNpcId: Int,
        totemX: Int,
        totemY: Int,
    ) = MapTeleportDestination(mapId, mapName, arrivalX, arrivalY, totemNpcId, totemX, totemY)
}
