# 数据包 SQLite 保存路径说明

## 保存位置

当前版本中，每次启动客户端都会创建一个独立实例。该实例捕获的数据包保存在：

```text
<程序目录>\data\instances\<instance-id>\sessions\session-<时间戳>[-<会话名称>].sqlite
```

Release 版本的常见完整路径是：

```text
C:\Users\xzw65\Desktop\tianshuqitan\bin\Release\data\instances\<instance-id>\sessions\<会话文件>.sqlite
```

例如：

```text
C:\Users\xzw65\Desktop\tianshuqitan\bin\Release\data\instances\b396719520874a3e90cf320335234940\sessions\session-20260901-211851-207-登山爬塔.sqlite
```

其中：

- `<程序目录>` 是 `TianshuQitanLauncher.exe` 所在目录。移动或发布到其他目录后，数据目录也会随程序目录改变。
- `<instance-id>` 是本次客户端启动生成的 32 位实例 ID。直接双击启动和多开管理启动都会生成独立实例目录。
- 时间戳格式为 `yyyyMMdd-HHmmss-fff`，使用本机当前时间并精确到毫秒。
- `[-<会话名称>]` 是可选的语义名称。使用工作台的“会话命名”功能后，会保留原时间戳并追加名称。

目录结构示例：

```text
bin\Release\data\
└─ instances\
   └─ b396719520874a3e90cf320335234940\
      ├─ instance.json
      ├─ sessions\
      │  └─ session-20260901-211851-207-登山爬塔.sqlite
      ├─ logs\
      ├─ configuration\
      └─ audio-cache\
```

`sessions` 目录和本次会话的 SQLite 文件由程序自动创建，无需手工建立。SQLite 中保存捕获的数据块、解析后的数据帧、连接与状态变化、原子操作记录等本次会话数据。

## 查看当前会话的准确路径

推荐直接在协议工作台顶部查看当前数据库文件名，然后点击“复制路径”。复制到剪贴板的是当前正在写入的 SQLite 完整路径。

在“原子操作”页选择会话记录时：

- `Ctrl+C`：复制文件名；
- `Ctrl+Shift+C`：复制完整路径；
- 也可以使用会话行的右键菜单复制文件名或完整路径。

如果要从项目目录查找最近生成的抓包文件，可以在 PowerShell 中运行：

```powershell
Get-ChildItem -Path .\bin\Release\data\instances -Filter 'session-*.sqlite' -Recurse |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 20 FullName, Length, LastWriteTime
```

## 多开和多账户

不同客户端实例使用不同的 `<instance-id>` 目录，即使登录同一个账户也不会共用抓包 SQLite。这样可以避免多进程同时写入同一数据库，也便于按照客户端启动批次定位数据。

实例与账户不是同一个概念：账户配置保存在 `data\accounts\<account-id>`，抓包数据库保存在 `data\instances\<instance-id>\sessions`。

## 显示缓存与数据库的关系

工作台的“清空显示缓存”只清除内存中的数据包列表、解析帧列表和状态变化列表，用于降低长时间运行后的界面占用和卡顿：

- 不会删除或清空 SQLite 文件；
- 不会停止抓包；
- 清理之后收到的新数据包仍会继续写入同一个 SQLite 文件；
- 已经写入数据库的历史数据仍可用于后续分析。

## 命名、复制和清理注意事项

- 正在抓包时需要改名，使用工作台内置的“会话命名”功能。程序会安全关闭并重新打开数据库写入器，不需要停止本次捕获。
- “复制路径”只复制路径文本，不会复制或移动数据库文件。
- 不要在客户端仍使用当前数据库时，通过资源管理器强行移动、重命名或删除该文件，否则可能遇到占用错误或得到不完整副本。
- 需要整理旧文件时，先确认它不是当前工作台显示的数据库；重要会话建议先备份再删除。
- “导出分析包”生成的 `*.tsqop.sqlite` 保存到导出时选择的位置，不属于上述自动抓包目录。

## 关于 `data\sessions` 默认配置

基础 profile 中仍保留相对配置 `data/sessions`，但正常启动程序时会把它转换成当前实例的隔离目录：

```text
data\instances\<instance-id>\sessions
```

因此，在当前版本中查找正常客户端的抓包文件时，应优先检查 `data\instances`，不要只查看旧版或测试场景可能使用的 `data\sessions`。
