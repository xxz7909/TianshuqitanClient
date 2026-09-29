package com.tianshuqitan.headless

import android.content.Context
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

private val Context.settingsDataStore by preferencesDataStore("tianshu_settings")

class SettingsRepository(private val context: Context) {
    suspend fun read(): AppSettings =
        context.settingsDataStore.data.map { values ->
            AppSettings(
                loginHost = values[Keys.loginHost] ?: "124.250.115.168",
                loginPorts = values[Keys.loginPorts] ?: "7800,7801,7802,7803",
                username = values[Keys.username] ?: "",
                line = values[Keys.line] ?: 4,
                roleSlot = values[Keys.roleSlot] ?: 1,
                forceLogin = values[Keys.forceLogin] ?: false,
                operationCom = values[Keys.operationCom] ?: 0,
                autoLogin = values[Keys.autoLogin] ?: true,
                resident24h = values[Keys.resident24h] ?: true,
                bootAutoStart = values[Keys.bootAutoStart] ?: false,
                autoReconnect = values[Keys.autoReconnect] ?: true,
                reloginOnFailure = values[Keys.reloginOnFailure] ?: true,
            )
        }.first()

    suspend fun save(settings: AppSettings) {
        context.settingsDataStore.edit { values ->
            values[Keys.loginHost] = settings.loginHost
            values[Keys.loginPorts] = settings.loginPorts
            values[Keys.username] = settings.username
            values[Keys.line] = settings.line
            values[Keys.roleSlot] = settings.roleSlot
            values[Keys.forceLogin] = settings.forceLogin
            values[Keys.operationCom] = settings.operationCom
            values[Keys.autoLogin] = settings.autoLogin
            values[Keys.resident24h] = settings.resident24h
            values[Keys.bootAutoStart] = settings.bootAutoStart
            values[Keys.autoReconnect] = settings.autoReconnect
            values[Keys.reloginOnFailure] = settings.reloginOnFailure
        }
    }

    private object Keys {
        val loginHost = stringPreferencesKey("login_host")
        val loginPorts = stringPreferencesKey("login_ports")
        val username = stringPreferencesKey("username")
        val line = intPreferencesKey("line")
        val roleSlot = intPreferencesKey("role_slot")
        val forceLogin = booleanPreferencesKey("force_login")
        val operationCom = intPreferencesKey("operation_com")
        val autoLogin = booleanPreferencesKey("auto_login")
        val resident24h = booleanPreferencesKey("resident_24h")
        val bootAutoStart = booleanPreferencesKey("boot_auto_start")
        val autoReconnect = booleanPreferencesKey("auto_reconnect")
        val reloginOnFailure = booleanPreferencesKey("relogin_on_failure")
    }
}
