package com.tianshuqitan.headless

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Only user-facing session summaries belong here, never credentials or frames. */
data class RuntimeUiState(
    val serviceRunning: Boolean = false,
    val online: Boolean = false,
    val status: String = "未启动",
    val detail: String = "保存设置后，点击登录或选择任务。",
    val lastHeartbeat: String = "尚未收到心跳",
    val currentMap: String = "尚未进图",
    val runLoopTask: String = "尚未收到跑环任务",
)

object RuntimeStateStore {
    private val mutableState = MutableStateFlow(RuntimeUiState())
    val state: StateFlow<RuntimeUiState> = mutableState.asStateFlow()

    internal fun publish(value: RuntimeUiState) {
        mutableState.value = value
    }
}
