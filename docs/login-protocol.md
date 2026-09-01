# v5 登录协议与自动登录

分析样本：`session-20260831-095654-523-登录.sqlite`。样本没有原子操作步骤，但连接 8（入口服）和连接 9（游戏服）的时间线足以完整关联登录链路。文档不记录样本中的账号、密码或票据原文。

## 帧格式

所有确认的 TCP 消息均使用大端序：

```text
u16 frame_length   // 包含 4 字节头
u16 opcode
byte body[frame_length - 4]
```

字符串格式为 `u16 byte_length + UTF-8 bytes`。

## 登录链路

1. 客户端连接入口服 `124.250.115.168:7800..7803`。
2. `CS_USER_PASS (0x0000)`：`string username + string password`。样本确认密码在原始线协议中是明文 UTF-8。
3. `SC_LOGIN_RESULT (0x0001)`：入口服确认登录，并返回登录随机值、时间、外网地址和结果字段。
4. `SC_CLIENT_TOKEN (0x00C8)`：`string token`。字符串是 Base64；样本解码为 48 字节不透明票据。客户端不解释其中语义。
5. `SC_GAMESERVER_LIST (0x00C9)`：`u32 count`，随后是 `count` 个线路记录：

   ```text
   string server_id
   string display_name
   string ip
   string comma_separated_ports
   u32 state
   ```

   样本包含一个“推荐线路”记录和一线、二线、三线、四线四个物理线路记录。不能假设推荐线路固定对应某一线，自动登录按显示名寻找目标物理线路。

6. 客户端关闭入口连接，连接选中线路返回的 IP/端口。
7. `CS_USER_TOKEN2 (0x0006)`：首个字符串与 `SC_CLIENT_TOKEN` 中的 Base64 文本逐字节相同；后面包含强制登录/运营标识和客户端校验字符串。
8. `SC_ROLE_INFO_LIST (0x000C)` 返回角色列表；随后 `CS_SELECT_ROLE (0x000A)` 选择角色。
9. `SC_ROLE_INFO (0x0014)` 返回所选角色的完整资料并开始批量初始化；`SC_ROLE_START_POINT (0x0016)` 返回两个 `i16` 地图坐标，客户端在该处理函数中正式调用 `enterGame()`。

## 自动登录实现

- “自动登录”页可配置开关、账号、密码、一线至四线以及第 1 至第 5 个角色槽位。
- 页面加载完成后等待 Flash 登录界面，填写两个输入框并提交。
- 抓包状态机观察到 `0x0000` 后等待 `0x00C9`，解析实际线路顺序，再点击目标线路；不使用固定端口或固定列表索引。
- 观察到目标游戏连接发出 `0x0006` 后继续等待并解析 `0x000C`，点击配置的角色槽位和“进入游戏”。
- 通过 `0x000A` 中的角色 ID 校验界面实际选择是否正确，以 `0x0016` 作为自动登录完成标志。
- 密码使用 Windows DPAPI `CurrentUser` 范围加密，保存在运行目录的 `data/secrets/auto-login.json`；界面和日志不回显密码。
- `CS_USER_PASS`、`SC_CLIENT_TOKEN` 和 `CS_USER_TOKEN2` 在网络收发完成后、进入 SQLite/UI/PCAPNG 捕获副本前，对账号、密码和票据字节进行等长 `*` 脱敏。服务端协议本身仍是明文，无法由客户端单方面升级为加密协议。

## 样本线路映射

样本确认一线有多个候选端口，二线也有多个候选端口，三线和四线各有一个端口。具体端口以每次 `SC_GAMESERVER_LIST` 为准，代码不会固化样本端口。

角色选择与进入游戏的详细字段见 [角色选择协议](role-selection-protocol.md)。
