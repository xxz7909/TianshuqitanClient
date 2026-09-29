package com.tianshuqitan.headless

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.view.View
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

class MainActivity : Activity() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var settingsRepository: SettingsRepository
    private lateinit var passwordStore: SecurePasswordStore
    private lateinit var host: EditText
    private lateinit var ports: EditText
    private lateinit var username: EditText
    private lateinit var password: EditText
    private lateinit var line: Spinner
    private lateinit var roleSlot: Spinner
    private lateinit var operationCom: EditText
    private lateinit var forceLogin: CheckBox
    private lateinit var autoLogin: CheckBox
    private lateinit var resident24h: CheckBox
    private lateinit var bootAutoStart: CheckBox
    private lateinit var autoReconnect: CheckBox
    private lateinit var reloginOnFailure: CheckBox
    private lateinit var runtimeStatus: TextView
    private lateinit var stopTaskButton: Button
    private var statusJob: Job? = null
    private var startJob: Job? = null
    private var startGeneration = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        settingsRepository = SettingsRepository(applicationContext)
        passwordStore = SecurePasswordStore(applicationContext)
        setContentView(buildContent())
        requestNotificationPermission()
        loadSettings()
    }

    override fun onStart() {
        super.onStart()
        statusJob?.cancel()
        statusJob = scope.launch {
            RuntimeStateStore.state.collect { state ->
                runtimeStatus.text = buildString {
                    appendLine("状态：${state.status}${if (state.online) " · 已在线" else ""}")
                    appendLine(state.detail)
                    appendLine("地图：${state.currentMap}")
                    appendLine("跑环：${state.runLoopTask}")
                    append("心跳：${state.lastHeartbeat}")
                }
                stopTaskButton.isEnabled = state.serviceRunning
            }
        }
    }

    override fun onStop() {
        statusJob?.cancel()
        statusJob = null
        super.onStop()
    }

    override fun onDestroy() {
        statusJob?.cancel()
        startJob?.cancel()
        scope.cancel()
        super.onDestroy()
    }

    private fun buildContent(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(32, 32, 32, 32)
        }
        val title = TextView(this).apply {
            val version = packageManager.getPackageInfo(packageName, 0).versionName.orEmpty()
            text = "天书无头客户端 $version"
            textSize = 24f
        }
        root.addView(title)
        root.addView(TextView(this).apply { text = "设置自动登录和 24 小时常驻后，可以直接启动日常全套或单项任务。" })

        runtimeStatus = TextView(this).apply {
            textSize = 16f
            setPadding(0, 24, 0, 16)
            setTextIsSelectable(true)
            root.addView(this)
        }
        stopTaskButton = button("停止当前任务（保持在线）") {
            startServiceAction(AutomationForegroundService.ACTION_STOP_AUTOMATION, null)
        }.apply { isEnabled = false }
        root.addView(stopTaskButton)

        host = input("入口服地址", "124.250.115.168", root)
        ports = input("候选端口", "7800,7801,7802,7803", root)
        username = input("账号", "", root)
        password = input("密码", "", root).apply { inputType = android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_VARIATION_PASSWORD }
        line = spinner("线路", listOf("一线", "二线", "三线", "四线"), root)
        roleSlot = spinner("角色槽位", listOf("第 1 个", "第 2 个", "第 3 个", "第 4 个", "第 5 个"), root)
        operationCom = input("OPERATION_COM", "0", root)
        forceLogin = check("强制登录", false, root)
        autoLogin = check("自动登录", true, root)
        resident24h = check("24 小时常驻", true, root)
        bootAutoStart = check("开机自启", false, root)
        autoReconnect = check("网络断线自动重连", true, root)
        reloginOnFailure = check("失败后自动重新登录", true, root)

        root.addView(button("保存设置") { saveSettings() })
        root.addView(button("允许 24 小时运行") { requestIgnoreBatteryOptimizations() })
        root.addView(button("只登录并常驻") { saveAndStart(null) })
        root.addView(button("启动日常全套（除暴 → 跑环）") { saveAndStart("daily") })
        root.addView(button("自动跑环") { saveAndStart("runloop") })
        root.addView(button("独立自动战斗") { saveAndStart("combat") })
        root.addView(button("自动除暴") { saveAndStart("bounty") })
        root.addView(button("登山爬塔") { saveAndStart("mountain") })
        root.addView(button("自动捐献") { saveAndStart("donation") })
        root.addView(button("停止后台服务") { startServiceAction(AutomationForegroundService.ACTION_STOP, null) })

        return ScrollView(this).apply { addView(root) }
    }

    private fun loadSettings() {
        scope.launch {
            val settings = settingsRepository.read()
            host.setText(settings.loginHost)
            ports.setText(settings.loginPorts)
            username.setText(settings.username)
            line.setSelection((settings.line - 1).coerceIn(0, 3))
            roleSlot.setSelection((settings.roleSlot - 1).coerceIn(0, 4))
            operationCom.setText(settings.operationCom.toString())
            forceLogin.isChecked = settings.forceLogin
            autoLogin.isChecked = settings.autoLogin
            resident24h.isChecked = settings.resident24h
            bootAutoStart.isChecked = settings.bootAutoStart
            autoReconnect.isChecked = settings.autoReconnect
            reloginOnFailure.isChecked = settings.reloginOnFailure
            if (passwordStore.read() != null) password.hint = "已保存密码，留空则沿用"
        }
    }

    private fun saveAndStart(command: String?) {
        startJob?.cancel()
        val generation = ++startGeneration
        startJob = scope.launch {
            saveSettingsInternal()
            if (generation != startGeneration) return@launch
            startServiceAction(AutomationForegroundService.ACTION_START, command)
        }
    }

    private fun saveSettings() {
        scope.launch {
            saveSettingsInternal()
            Toast.makeText(this@MainActivity, "设置已保存", Toast.LENGTH_SHORT).show()
        }
    }

    private suspend fun saveSettingsInternal() {
        settingsRepository.save(readSettings())
        if (password.text.toString().isNotEmpty()) {
            passwordStore.save(password.text.toString())
            password.text.clear()
            password.hint = "已保存密码，留空则沿用"
        }
    }

    private fun readSettings(): AppSettings =
        AppSettings(
            loginHost = host.text.toString(),
            loginPorts = ports.text.toString(),
            username = username.text.toString(),
            line = line.selectedItemPosition + 1,
            roleSlot = roleSlot.selectedItemPosition + 1,
            forceLogin = forceLogin.isChecked,
            operationCom = operationCom.text.toString().toIntOrNull() ?: 0,
            autoLogin = autoLogin.isChecked,
            resident24h = resident24h.isChecked,
            bootAutoStart = bootAutoStart.isChecked,
            autoReconnect = autoReconnect.isChecked,
            reloginOnFailure = reloginOnFailure.isChecked,
        )

    private fun startServiceAction(action: String, command: String?) {
        val stopping = action == AutomationForegroundService.ACTION_STOP ||
            action == AutomationForegroundService.ACTION_STOP_AUTOMATION
        if (stopping) {
            startGeneration++
            startJob?.cancel()
            startJob = null
        }
        val intent = Intent(this, AutomationForegroundService::class.java).setAction(action)
        if (command != null) intent.putExtra(AutomationForegroundService.EXTRA_COMMAND, command)
        if (Build.VERSION.SDK_INT >= 26 && !stopping) startForegroundService(intent) else startService(intent)
    }

    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT >= 33) requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1001)
    }

    private fun requestIgnoreBatteryOptimizations() {
        val power = getSystemService(PowerManager::class.java)
        if (power.isIgnoringBatteryOptimizations(packageName)) {
            Toast.makeText(this, "已经允许忽略电池优化", Toast.LENGTH_SHORT).show()
            return
        }
        val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)
            .setData(Uri.parse("package:$packageName"))
        startActivity(intent)
    }

    private fun input(label: String, defaultValue: String, root: LinearLayout): EditText {
        root.addView(TextView(this).apply { text = label })
        return EditText(this).apply {
            setText(defaultValue)
            setSingleLine(true)
            root.addView(this)
        }
    }

    private fun spinner(label: String, values: List<String>, root: LinearLayout): Spinner {
        root.addView(TextView(this).apply { text = label })
        return Spinner(this).apply {
            adapter = ArrayAdapter(this@MainActivity, android.R.layout.simple_spinner_dropdown_item, values)
            root.addView(this)
        }
    }

    private fun check(label: String, checked: Boolean, root: LinearLayout): CheckBox =
        CheckBox(this).apply {
            text = label
            isChecked = checked
            root.addView(this)
        }

    private fun button(label: String, click: () -> Unit): Button =
        Button(this).apply {
            text = label
            setOnClickListener { click() }
        }
}

