package com.tianshuqitan.headless

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import android.util.Log
import com.tianshuqitan.headless.core.AutomationCommand
import com.tianshuqitan.headless.core.ClientEvent
import com.tianshuqitan.headless.core.GameSession
import com.tianshuqitan.headless.core.LoginRequest
import com.tianshuqitan.headless.core.DefaultNetworkState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

class AutomationForegroundService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val handler = Handler(Looper.getMainLooper())
    private lateinit var settingsRepository: SettingsRepository
    private lateinit var passwordStore: SecurePasswordStore
    private lateinit var session: GameSession
    private lateinit var runtimeEventLog: RuntimeEventLog
    private lateinit var connectivity: ConnectivityManager
    private val networks = DefaultNetworkState<Network>()
    @Volatile private var socketNetwork: Network? = null
    private var waitingForNetwork = false
    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) { networkChanged(networks.available(network)) }
        override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
            networkChanged(networks.capabilities(network, caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) &&
                caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)))
        }
        override fun onLost(network: Network) { networkChanged(networks.lost(network)) }
    }
    private var wakeLock: PowerManager.WakeLock? = null
    private var settings: AppSettings = AppSettings()
    private var lastLoginRequest: LoginRequest? = null
    private var pendingAutomation: AutomationCommand? = null
    private var resumeAutomation: AutomationCommand? = null
    private var loginInProgress = false
    private var reloginCallback: Runnable? = null
    private var automationStartCallback: Runnable? = null
    private var taskRefreshCallback: Runnable? = null
    private var settingsLoadJob: Job? = null
    private var settingsLoadGeneration = 0
    @Volatile private var automationGeneration = 0
    @Volatile private var loginAttempt = 0
    private var loginFailed = false
    private var lastSessionError = ""
    private var stopping = false
    private var foregroundStarted = false
    private var startingAutomationOwner: String? = null
    private var runLoopTaskRefreshAttempts = 0
    private var statusLine = "等待启动"
    private var detailLine = "后台服务已准备。"
    private var lastHeartbeatLine = ""
    private val watchdog = object : Runnable {
        override fun run() {
            checkHeartbeat()
            publishRuntimeState()
            handler.postDelayed(this, 15_000)
        }
    }

    override fun onCreate() {
        super.onCreate()
        settingsRepository = SettingsRepository(applicationContext)
        passwordStore = SecurePasswordStore(applicationContext)
        runtimeEventLog = RuntimeEventLog(applicationContext)
        connectivity = getSystemService(ConnectivityManager::class.java)
        connectivity.activeNetwork?.let { network ->
            networks.available(network)
            val caps = connectivity.getNetworkCapabilities(network)
            networks.capabilities(network, caps?.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) == true &&
                caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED))
        }
        socketNetwork = networks.current.takeIf { networks.usable }
        session = newSession(loginAttempt)
        connectivity.registerDefaultNetworkCallback(networkCallback, handler)
        createNotificationChannel()
        handler.post(watchdog)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopEverything("用户停止")
                return START_NOT_STICKY
            }
            ACTION_STOP_AUTOMATION -> {
                // This action can arrive just after the service was reclaimed.
                // Honour startForegroundService's contract before stopping it.
                if (!foregroundStarted) startForegroundNow()
                stopCurrentAutomation()
            }
            ACTION_RECONNECT -> {
                stopping = false
                startForegroundNow()
                loadSettingsAndLogin(null, forceReconnect = true)
            }
            else -> {
                stopping = false
                startForegroundNow()
                val command = intent?.getStringExtra(EXTRA_COMMAND)?.let(::parseCommand)
                if (command != null) runLoopTaskRefreshAttempts = 0
                loadSettingsAndLogin(command, forceReconnect = false)
            }
        }
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        connectivity.unregisterNetworkCallback(networkCallback)
        handler.removeCallbacks(watchdog)
        scope.cancel()
        stopEverything("服务销毁")
        session.close()
        runtimeEventLog.close()
        super.onDestroy()
    }

    private fun loadSettingsAndLogin(command: AutomationCommand?, forceReconnect: Boolean) {
        settingsLoadJob?.cancel()
        val loadGeneration = ++settingsLoadGeneration
        settingsLoadJob = scope.launch {
            val loadedSettings = settingsRepository.read()
            val password = withContext(Dispatchers.IO) { passwordStore.read() }
            if (stopping || loadGeneration != settingsLoadGeneration) return@launch
            settings = loadedSettings
            if (!settings.resident24h) releaseWakeLock()
            val snapshot = session.getSnapshot()
            if (!forceReconnect && snapshot.online) {
                if (settings.resident24h) acquireWakeLock()
                pendingAutomation = null
                if (command == null) {
                    statusLine = "在线"
                    detailLine = "会话已在线。"
                    updateNotification()
                } else {
                    startAutomationSafely(command)
                }
                return@launch
            }
            if (!forceReconnect && loginInProgress) {
                if (command != null) pendingAutomation = command
                statusLine = "登录中"
                detailLine = if (command == null) "正在登录，请稍候。" else "正在登录，已排队启动 ${ownerFor(command)}。"
                updateNotification()
                return@launch
            }
            if (!settings.autoLogin && command == null && !forceReconnect) {
                statusLine = "常驻待命"
                detailLine = "自动登录未开启。"
                updateNotification()
                return@launch
            }
            if (password.isNullOrEmpty()) {
                statusLine = "缺少密码"
                detailLine = "请回设置页输入并保存密码。"
                updateNotification()
                return@launch
            }
            val request = settings.toLoginRequest(password)
            lastLoginRequest = request
            pendingAutomation = command
            statusLine = "自动登录"
            detailLine = "正在连接入口服。"
            updateNotification()
            beginLogin(request)
        }
    }

    private fun newSession(attempt: Int): GameSession {
        val network = socketNetwork
        return GameSession(
            socketFactory = { requireNotNull(network) { "等待可用网络" }.socketFactory.createSocket() },
            emit = { onClientEvent(it, attempt) },
        )
    }

    private fun networkChanged(change: DefaultNetworkState.Change) {
        socketNetwork = networks.current.takeIf { networks.usable }
        if (stopping) return
        if (change.invalidateConnection && lastLoginRequest != null) {
            // The socket can fail before onLost reaches the main thread. Keep
            // the user's session intent even if its online flag is already clear.
            waitingForNetwork = waitingForNetwork || settings.autoReconnect
            loginAttempt++ // Events from the retired session retain their original attempt id.
            cancelRelogin()
            cancelAutomationStart()
            taskRefreshCallback?.let(handler::removeCallbacks)
            taskRefreshCallback = null
            startingAutomationOwner = null
            loginInProgress = false
            session.disconnect("网络切换，重建会话", notify = false)
            lastHeartbeatLine = ""
            statusLine = "网络已变化"
            detailLine = if (waitingForNetwork) "等待新网络可用后重新同步地图和任务。" else "自动重连未开启，可手动重连。"
            releaseWakeLock()
            updateNotification()
        }
        if (change.becameUsable && waitingForNetwork) {
            scheduleRelogin(delayMillis = 500)
        }
    }

    private fun onClientEvent(event: ClientEvent, eventLoginAttempt: Int) {
        Log.i(TAG, event.safeLogText())
        runtimeEventLog.append(event)
        // Session events also originate on the socket, map-ready and automation
        // threads. Keep retry flags and UI state on the service main thread.
        val eventAutomationGeneration = automationGeneration
        handler.post {
            if (stopping || eventLoginAttempt != loginAttempt) return@post
            if ((event is ClientEvent.Automation || event is ClientEvent.SessionRefreshRequired) &&
                eventAutomationGeneration != automationGeneration) return@post
            handleClientEvent(event)
        }
    }

    private fun handleClientEvent(event: ClientEvent) {
        when (event) {
            is ClientEvent.State -> {
                statusLine = event.state
                detailLine = event.detail
            }
            is ClientEvent.Automation -> {
                if (event.state == "Packet") {
                    publishRuntimeState()
                    return
                }
                if (resumeAutomation == null && pendingAutomation == null && startingAutomationOwner == null &&
                    session.getSnapshot().currentAutomation == null && event.state != "Stopped") return
                // DailyAll has already finished Bounty when its RunLoop stage
                // starts. Resume that stage after a network failure instead of
                // repeating the beginning of the daily queue.
                if (resumeAutomation is AutomationCommand.DailyAll &&
                    event.owner == "AutoRunLoop" && event.state == "Starting") {
                    resumeAutomation = AutomationCommand.RunLoop()
                }
                if (event.state == "Failed" ||
                    (event.state == "Completed" && resumeAutomation?.let(::ownerFor) == event.owner)) {
                    resumeAutomation = null
                }
                if (startingAutomationOwner == event.owner ||
                    (startingAutomationOwner == "DailyAll" && event.owner in setOf("AutoBounty", "AutoRunLoop"))) {
                    startingAutomationOwner = null
                }
                statusLine = event.owner
                detailLine = "${event.state} · ${event.detail}"
            }
            is ClientEvent.Heartbeat -> {
                val time = DateTimeFormatter.ofPattern("HH:mm:ss")
                    .withZone(ZoneId.systemDefault()).format(Instant.now())
                lastHeartbeatLine = "$time · 第 ${event.counter} 次"
            }
            is ClientEvent.Entered -> {
                loginInProgress = false
                loginFailed = false
                cancelRelogin()
                statusLine = "在线"
                detailLine = "角色起点 (${event.x},${event.y})"
                pendingAutomation?.let {
                    pendingAutomation = null
                    resumeAutomation = it
                    // Entered is now a data barrier, not a fixed-delay guess.
                    val attempt = loginAttempt
                    val taskGeneration = automationGeneration
                    automationStartCallback = Runnable {
                        automationStartCallback = null
                        if (!stopping && attempt == loginAttempt && taskGeneration == automationGeneration && session.getSnapshot().online) {
                            startAutomationSafely(it)
                        }
                    }.also { callback -> handler.post(callback) }
                }
            }
            is ClientEvent.SessionRefreshRequired -> {
                // A tick already inside core's lock may emit its refresh just
                // as Stop is requested. No task intent means it is obsolete.
                if (resumeAutomation == null && pendingAutomation == null && startingAutomationOwner == null) return
                startingAutomationOwner = null
                val request = lastLoginRequest
                if (runLoopTaskRefreshAttempts >= 1 || request == null) {
                    pendingAutomation = null
                    resumeAutomation = null
                    session.stopAutomation()
                    statusLine = "任务同步失败"
                    detailLine = if (request == null) "没有可用的登录配置。" else "重新登录后仍未收到当前跑环任务，请保留日志继续比对。"
                } else {
                    runLoopTaskRefreshAttempts++
                    pendingAutomation = event.command
                    statusLine = "同步跑环任务"
                    detailLine = event.reason
                    val taskGeneration = automationGeneration
                    val attempt = loginAttempt
                    taskRefreshCallback?.let(handler::removeCallbacks)
                    taskRefreshCallback = Runnable {
                        taskRefreshCallback = null
                        if (!stopping && taskGeneration == automationGeneration && attempt == loginAttempt) {
                            beginLogin(request)
                        }
                    }.also { callback -> handler.post(callback) }
                }
            }
            is ClientEvent.Error -> {
                lastSessionError = event.message
                loginFailed = loginInProgress
                loginInProgress = false
                cancelAutomationStart()
                startingAutomationOwner = null
                statusLine = "错误"
                detailLine = event.message
                releaseWakeLock()
            }
            is ClientEvent.Warning -> {
                if (event.message.startsWith("启动自动化失败") && session.getSnapshot().currentAutomation == null) {
                    resumeAutomation = null
                }
                startingAutomationOwner = null
                statusLine = "提示"
                detailLine = event.message
            }
            is ClientEvent.Disconnected -> {
                loginInProgress = false
                cancelAutomationStart()
                startingAutomationOwner = null
                statusLine = "已断开"
                detailLine = lastSessionError.ifBlank { event.reason }
                releaseWakeLock()
                val shouldRetry = if (loginFailed) settings.reloginOnFailure else settings.autoReconnect
                loginFailed = false
                if (shouldRetry) scheduleRelogin()
            }
            is ClientEvent.Packet -> {
                // Packets may refresh map/task snapshots but must not hide an
                // automation failure or cause 200 ms notification updates.
                publishRuntimeState()
                return
            }
            else -> Unit
        }
        updateNotification()
    }

    private fun scheduleRelogin(delayMillis: Long = 5_000) {
        if (stopping || reloginCallback != null || loginInProgress) return
        val request = lastLoginRequest ?: return
        if (!networks.usable) {
            waitingForNetwork = true
            statusLine = "等待网络"
            detailLine = "网络恢复后会重新登录，并同步地图和任务。"
            updateNotification()
            return
        }
        val attempt = loginAttempt
        reloginCallback = Runnable {
            reloginCallback = null
            if (!stopping && attempt == loginAttempt && !loginInProgress && !session.getSnapshot().online) {
                statusLine = "自动重连"
                detailLine = "正在恢复会话。"
                updateNotification()
                beginLogin(request)
            }
        }.also { handler.postDelayed(it, delayMillis) }
    }

    private fun cancelRelogin() {
        reloginCallback?.let(handler::removeCallbacks)
        reloginCallback = null
    }

    private fun cancelAutomationStart() {
        automationStartCallback?.let(handler::removeCallbacks)
        automationStartCallback = null
    }

    private fun beginLogin(request: LoginRequest) {
        cancelRelogin()
        cancelAutomationStart()
        taskRefreshCallback?.let(handler::removeCallbacks)
        taskRefreshCallback = null
        if (pendingAutomation == null) pendingAutomation = resumeAutomation
        if (!networks.usable) {
            waitingForNetwork = true
            statusLine = "等待网络"
            detailLine = "收到可用网络后再登录，不复用旧连接。"
            updateNotification()
            return
        }
        waitingForNetwork = false
        loginAttempt++
        loginFailed = false
        lastSessionError = ""
        loginInProgress = true
        lastHeartbeatLine = ""
        if (settings.resident24h) acquireWakeLock()
        try {
            // Retire the entire old transport and callbacks, not just its online flag.
            session.close()
            session = newSession(loginAttempt)
            session.login(request)
        } catch (error: Throwable) {
            loginInProgress = false
            releaseWakeLock()
            statusLine = "登录失败"
            detailLine = error.message ?: error.toString()
            updateNotification()
        }
    }

    private fun checkHeartbeat() {
        if (settings.resident24h && !stopping && (loginInProgress || session.getSnapshot().online)) acquireWakeLock()
        val request = lastLoginRequest ?: return
        if (!settings.autoReconnect) return
        val snapshot = session.getSnapshot()
        val last = snapshot.lastHeartbeatAtMillis
        if (snapshot.online && last > 0 && System.currentTimeMillis() - last > 65_000) {
            statusLine = "心跳超时"
            detailLine = "65 秒未收到服务器心跳，正在重连。"
            updateNotification()
            beginLogin(request)
        }
    }

    private fun startForegroundNow() {
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(NOTIFICATION_ID, buildNotification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(NOTIFICATION_ID, buildNotification())
        }
        foregroundStarted = true
        publishRuntimeState()
    }

    private fun updateNotification() {
        publishRuntimeState()
        if (!foregroundStarted) return
        val manager = getSystemService(NotificationManager::class.java)
        manager.notify(NOTIFICATION_ID, buildNotification())
    }

    private fun publishRuntimeState() {
        val snapshot = session.getSnapshot()
        val map = snapshot.currentMap
        val task = snapshot.runLoopTask
        RuntimeStateStore.publish(RuntimeUiState(
            serviceRunning = foregroundStarted && !stopping,
            online = snapshot.online && !stopping,
            status = statusLine,
            detail = detailLine,
            lastHeartbeat = lastHeartbeatLine.ifBlank { "尚未收到心跳" },
            currentMap = map?.let { "${it.mapName} (${it.mapId})${if (snapshot.mapReady) "" else " · 等待数据同步"}" } ?: "尚未进图",
            runLoopTask = task?.let {
                buildString {
                    append("第 ${it.ringNumber} 环")
                    if (it.required > 0) append(" · ${it.progress}/${it.required}")
                    append("\n${it.description}")
                }
            } ?: if (snapshot.taskListReady) "当前没有跑环任务" else "尚未同步任务列表",
        ))
    }

    private fun buildNotification(): Notification {
        val stop = PendingIntent.getService(
            this,
            1,
            Intent(this, AutomationForegroundService::class.java).setAction(ACTION_STOP),
            pendingIntentFlags(),
        )
        val reconnect = PendingIntent.getService(
            this,
            2,
            Intent(this, AutomationForegroundService::class.java).setAction(ACTION_RECONNECT),
            pendingIntentFlags(),
        )
        val open = PendingIntent.getActivity(
            this,
            3,
            Intent(this, MainActivity::class.java),
            pendingIntentFlags(),
        )
        val notificationDetail = if (lastHeartbeatLine.isBlank() || detailLine.contains(lastHeartbeatLine)) {
            detailLine
        } else {
            "$detailLine · 心跳 $lastHeartbeatLine"
        }
        return Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_notify_sync)
            .setContentTitle("天书无头：$statusLine")
            .setContentText(notificationDetail)
            .setContentIntent(open)
            .setOngoing(true)
            .addAction(android.R.drawable.ic_media_pause, "停止", stop)
            .addAction(android.R.drawable.stat_notify_sync, "重连", reconnect)
            .build()
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT < 26) return
        val channel = NotificationChannel(CHANNEL_ID, "天书无头常驻", NotificationManager.IMPORTANCE_LOW)
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    private fun acquireWakeLock() {
        if (wakeLock?.isHeld == true) return
        val power = getSystemService(PowerManager::class.java)
        wakeLock = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "TianshuHeadless:Automation").apply {
            setReferenceCounted(false)
            acquire(WAKE_LOCK_TIMEOUT_MS)
        }
    }

    private fun releaseWakeLock() {
        wakeLock?.takeIf { it.isHeld }?.release()
        wakeLock = null
    }

    private fun stopEverything(reason: String) {
        stopping = true
        waitingForNetwork = false
        loginAttempt++
        cancelRelogin()
        clearAutomationRequests()
        loginInProgress = false
        session.disconnect(reason)
        statusLine = "已停止"
        detailLine = reason
        releaseWakeLock()
        stopForeground(STOP_FOREGROUND_REMOVE)
        foregroundStarted = false
        publishRuntimeState()
        stopSelf()
    }

    private fun clearAutomationRequests() {
        automationGeneration++
        settingsLoadGeneration++
        settingsLoadJob?.cancel()
        settingsLoadJob = null
        cancelAutomationStart()
        taskRefreshCallback?.let(handler::removeCallbacks)
        taskRefreshCallback = null
        pendingAutomation = null
        resumeAutomation = null
        startingAutomationOwner = null
        runLoopTaskRefreshAttempts = 0
    }

    private fun stopCurrentAutomation() {
        clearAutomationRequests()
        session.stopAutomation()
        statusLine = if (session.getSnapshot().online) "在线待命" else if (loginInProgress) "登录中" else "常驻待命"
        detailLine = "已停止当前任务并清空待启动任务，保留当前连接。"
        updateNotification()
    }

    private fun parseCommand(value: String): AutomationCommand =
        when (value) {
            "daily" -> AutomationCommand.DailyAll
            "runloop" -> AutomationCommand.RunLoop()
            "combat" -> AutomationCommand.StandaloneCombat()
            "bounty" -> AutomationCommand.Bounty()
            "mountain" -> AutomationCommand.MountainClimb
            "donation" -> AutomationCommand.Donation()
            else -> AutomationCommand.DailyAll
        }

    private fun startAutomationSafely(command: AutomationCommand) {
        val snapshot = session.getSnapshot()
        val owner = ownerFor(command)
        Log.i(TAG, "startAutomation command=$command snapshot=$snapshot")
        startingAutomationOwner?.let { starting ->
            statusLine = starting
            detailLine = if (starting == owner) "该功能正在启动，已忽略重复点击。" else "$starting 正在启动，请稍候。"
            updateNotification()
            return
        }
        if (snapshot.currentAutomation == owner) {
            statusLine = owner
            detailLine = "该功能已在运行，未重新登录。"
            updateNotification()
            return
        }
        val takeOverCombat = command is AutomationCommand.RunLoop && snapshot.currentAutomation == "AutoCombat"
        if (snapshot.currentAutomation != null && !takeOverCombat) {
            statusLine = "已有自动化"
            detailLine = "${snapshot.currentAutomation} 正在运行，请先点击“停止当前任务”。"
            updateNotification()
            return
        }
        try {
            resumeAutomation = command
            startingAutomationOwner = owner
            statusLine = owner
            detailLine = "正在启动，准备发送首个协议包。"
            updateNotification()
            session.startAutomation(command)
        } catch (error: Throwable) {
            resumeAutomation = null
            startingAutomationOwner = null
            statusLine = "启动失败"
            detailLine = error.message ?: error.toString()
            updateNotification()
        }
    }

    private fun ownerFor(command: AutomationCommand): String =
        when (command) {
            is AutomationCommand.RunLoop -> "AutoRunLoop"
            is AutomationCommand.StandaloneCombat -> "AutoCombat"
            is AutomationCommand.Bounty -> "AutoBounty"
            AutomationCommand.MountainClimb -> "AutoMountainClimb"
            is AutomationCommand.MapTeleport -> "AutoMapTeleport"
            is AutomationCommand.Donation -> "AutoDonation"
            AutomationCommand.DailyAll -> "DailyAll"
        }

    private fun pendingIntentFlags(): Int =
        if (Build.VERSION.SDK_INT >= 23) PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        else PendingIntent.FLAG_UPDATE_CURRENT

    companion object {
        const val ACTION_START = "com.tianshuqitan.headless.START"
        const val ACTION_STOP = "com.tianshuqitan.headless.STOP"
        const val ACTION_STOP_AUTOMATION = "com.tianshuqitan.headless.STOP_AUTOMATION"
        const val ACTION_RECONNECT = "com.tianshuqitan.headless.RECONNECT"
        const val EXTRA_COMMAND = "command"
        private const val CHANNEL_ID = "tianshu_headless_resident"
        private const val NOTIFICATION_ID = 100
        private const val WAKE_LOCK_TIMEOUT_MS = 10 * 60_000L
        private const val TAG = "TianshuHeadless"
    }
}
