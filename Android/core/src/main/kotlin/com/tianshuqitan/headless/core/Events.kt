package com.tianshuqitan.headless.core

sealed interface ClientEvent {
    data class State(val state: String, val detail: String) : ClientEvent
    data class Servers(val servers: List<GameServer>, val selectedId: String) : ClientEvent
    data class Roles(val roles: List<RoleSummary>, val selectedSlot: Int) : ClientEvent
    data class Entered(val x: Int, val y: Int) : ClientEvent
    data class Heartbeat(val counter: Int, val systemTime: String, val latencyMs: Double, val authCodeHex: String) : ClientEvent
    data class Packet(val direction: PacketDirection, val opcode: Int, val length: Int, val name: String) : ClientEvent
    data class Automation(val owner: String, val state: String, val detail: String, val error: Boolean = false) : ClientEvent
    data class SessionRefreshRequired(val command: AutomationCommand, val reason: String) : ClientEvent
    data class Warning(val message: String) : ClientEvent
    data class Error(val message: String) : ClientEvent
    data class Disconnected(val reason: String) : ClientEvent
}

data class SessionSnapshot(
    val online: Boolean = false,
    val currentMap: MapInfo? = null,
    val lastHeartbeatAtMillis: Long = 0,
    val currentAutomation: String? = null,
    val automationState: String = "Idle",
    val runLoopTask: RunLoopTask? = null,
    val mapReady: Boolean = false,
    val taskListReady: Boolean = false,
)
