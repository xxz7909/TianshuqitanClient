package com.tianshuqitan.headless

import com.tianshuqitan.headless.core.LoginRequest

data class AppSettings(
    val loginHost: String = "124.250.115.168",
    val loginPorts: String = "7800,7801,7802,7803",
    val username: String = "",
    val line: Int = 4,
    val roleSlot: Int = 1,
    val forceLogin: Boolean = false,
    val operationCom: Int = 0,
    val autoLogin: Boolean = true,
    val resident24h: Boolean = true,
    val bootAutoStart: Boolean = false,
    val autoReconnect: Boolean = true,
    val reloginOnFailure: Boolean = true,
) {
    fun toLoginRequest(password: String): LoginRequest =
        LoginRequest(
            loginHost = loginHost.trim(),
            loginPorts = loginPorts.split(",").mapNotNull { it.trim().toIntOrNull() }.ifEmpty { listOf(7800, 7801, 7802, 7803) },
            username = username.trim(),
            password = password,
            line = line,
            roleSlot = roleSlot,
            forceLogin = forceLogin,
            operationCom = operationCom,
        )
}
