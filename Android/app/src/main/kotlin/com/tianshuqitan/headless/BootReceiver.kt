package com.tianshuqitan.headless

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        if (intent?.action != Intent.ACTION_BOOT_COMPLETED && intent?.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        val pending = goAsync()
        CoroutineScope(Dispatchers.IO).launch {
            try {
                val settings = SettingsRepository(context.applicationContext).read()
                if (settings.bootAutoStart && settings.autoLogin) {
                    val service = Intent(context, AutomationForegroundService::class.java)
                        .setAction(AutomationForegroundService.ACTION_START)
                        .putExtra(AutomationForegroundService.EXTRA_COMMAND, "daily")
                    if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(service) else context.startService(service)
                }
            } finally {
                pending.finish()
            }
        }
    }
}
