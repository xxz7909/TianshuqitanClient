package com.tianshuqitan.headless.core

/** One login's synchronization barrier. TCP connection alone never implies game readiness. */
internal class SessionReadiness {
    var epoch = 0
        private set
    var map: MapInfo? = null
        private set
    var gridMapId: Int? = null
        private set
    var point: RoleStartPoint? = null
        private set
    var taskListReceived = false
        private set
    private var sawMapInfo = false
    private var handshakeComplete = false
    var published = false
        private set

    val ready: Boolean get() = map != null && gridMapId == map?.mapId && point != null &&
        handshakeComplete && taskListReceived

    fun mapInfo(value: MapInfo) {
        if (published || sawMapInfo) beginMap(keepMap = false)
        map = value
        sawMapInfo = true
    }

    fun mapData(value: MapGrid) { gridMapId = value.mapId }

    fun startPoint(value: RoleStartPoint): Int {
        // A position-only re-entry can reuse this connection's verified map.
        // A new SC55 has already cleared it and requires a new grid.
        if (published || point != null) beginMap(keepMap = true)
        point = value
        return epoch
    }

    fun tasksReceived() { taskListReceived = true }
    fun acknowledge(expectedEpoch: Int) { if (expectedEpoch == epoch) handshakeComplete = true }
    fun publish() { check(ready); published = true }

    fun missing(): String = buildList {
        if (map == null) add("地图信息")
        if (map == null || gridMapId != map?.mapId) add("地图可走格")
        if (point == null || !handshakeComplete) add("进图确认")
        if (!taskListReceived) add("任务列表")
    }.joinToString("、")

    private fun beginMap(keepMap: Boolean) {
        epoch++
        if (!keepMap) { map = null; gridMapId = null }
        point = null
        sawMapInfo = false
        handshakeComplete = false
        published = false
    }
}
