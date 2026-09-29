package com.tianshuqitan.headless.core

/** Routes and seed directory from the Windows RunLoopAutomationCoordinator. */
internal object RunLoopDestinations {
    val npcs: Map<String, TravelTarget> by lazy {
        val result = linkedMapOf<String, TravelTarget>()
        fun add(name: String, id: Int, map: Int, x: Int, y: Int) {
            result["${GameProtocol.normalizeName(name)}@$map"] = TravelTarget(id, map, x, y)
        }
        add("柳先元", 93290, 72, 23, 132)
        add("乔烈", 8021, 12, 39, 37)
        add("舞修罗", 8011, 13, 30, 100)
        add("冯奇", 10102, 101, 17, 73)
        add("周猎户", 8032, 12, 31, 130)
        add("海蚌族蚌岳珊", 94018, 102, 2, 16)
        add("蚌岳珊", 94018, 102, 2, 16)
        add("刘兴", 8062, 13, 64, 113)
        add("远征军先锋将军姜海", 93191, 110, 16, 63)
        add("先锋护卫姜天", 93192, 110, 13, 71)
        add("先锋护卫刘承", 93193, 110, 14, 65)
        add("天空远征军斥候队长夏雨", 93194, 110, 18, 64)
        add("天空远征军斥候武诚初", 93195, 110, 20, 66)
        RunLoopDestinations::class.java.getResourceAsStream("/run-loop-npcs.tsv")?.bufferedReader()?.useLines { lines ->
            lines.filter { it.isNotBlank() && !it.startsWith("#") }.forEach { line ->
                val fields = line.split('\t')
                if (fields.size != 5 || fields[0].any(Char::isISOControl)) return@forEach
                val id = fields[1].toIntOrNull() ?: return@forEach
                val map = fields[2].toIntOrNull() ?: return@forEach
                val x = fields[3].toIntOrNull() ?: return@forEach
                val y = fields[4].toIntOrNull() ?: return@forEach
                if (id > 0 && map > 0) add(fields[0], id, map, x, y)
            }
        }
        result
    }

    private val extraMaps = mapOf(
        "皇城内" to 13, "灵昌广场" to 97, "主母之林" to 109, "近天回廊" to 110,
        "百鸟树林" to 111, "练功房" to 213, "帮会大厅" to 1058, "新月村城镇" to 3,
    )

    fun mapId(name: String): Int? {
        val normalized = GameProtocol.normalizeName(name)
        return MapTeleportCatalog.all.firstOrNull { it.mapName == normalized }?.mapId ?: extraMaps[normalized]
    }

    fun huntRoute(name: String): List<TravelTarget>? {
        val normalized = GameProtocol.normalizeName(name)
        // Windows checks the recorded catalog before its special route fallbacks.
        MapTeleportCatalog.all.firstOrNull { it.mapName == normalized }?.let { return listOf(it.totemTarget()) }
        return when (normalized) {
            "百鸟树林" -> listOf(TravelTarget(50100166, 110, 2, 84), TravelTarget(608, 111, 6, 84))
            "怒焰祭坛" -> listOf(TravelTarget(233, 99, 22, 8))
            else -> null
        }
    }
}
