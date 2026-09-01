# v5 角色选择与进入游戏协议

分析样本：`session-20260831-103808-457选择角色-进入游戏.sqlite`。文档不记录样本中的账号、票据、角色名或角色 ID 原文。

## 结论与时间线

本次样本中的角色选择链路为：

```text
CS_USER_TOKEN2 (0x0006)
  -> SC_ROLE_INFO_LIST (0x000C)
  -> 用户选择角色
  -> CS_SELECT_ROLE (0x000A)
  -> SC_ROLE_INFO (0x0014) 与游戏初始化推送
  -> SC_ROLE_START_POINT (0x0016)
  -> 客户端 enterGame()
```

`0x000C` 在原子操作开始前已经到达；操作区间内唯一的角色选择请求是 `0x000A`。它之后先出现完整角色资料 `0x0014`，再出现物品、技能、任务、地图等初始化推送，最后以 `0x0016` 通知角色地图起点。客户端的 `0x0016` 处理函数读取两个坐标、调用 `enterGame()`，因此它是本流程最可靠的完成确认。

## Base64 数据

本样本中的 Base64 不是角色信息，而是入口服签发的登录票据：

- `SC_CLIENT_TOKEN (0x00C8)` 中的字符串长度为 64 个字符，可正常解码为 48 字节不透明数据。
- 游戏服连接的 `CS_USER_TOKEN2 (0x0006)` 首字符串与上述 64 字节文本逐字节完全相同。
- `0x000C` 角色列表和 `0x000A` 角色选择都采用普通大端二进制与 UTF-8 字符串，没有 Base64 层。

票据内容可用于当前登录链路，不应写入文档或日志。

## SC_ROLE_INFO_LIST (0x000C)

正文先是一个大端 `u16 role_count`，随后是 `role_count` 条记录。客户端角色界面最多展示 5 个槽位。

```text
u16 role_count
repeat role_count times:
    i32 character_id
    string name
    string gender
    string job
    i32 level
    string portrait_image
    string body_image
    string appearance
    string nickname
    i32 hp
    i32 mp
    i32 strength
    i32 intellect
    i32 vitality
    i32 belief
    i32 agility
    i32 physical_attack
    i32 physical_defense
    i32 speed
    i32 spirit
    i32 restore
    string honor_title
    i16 status
```

其中 `string` 仍是 `u16 UTF-8字节数 + UTF-8字节`。`status == 1` 时原客户端会把角色置灰并显示删除/恢复信息；自动登录会拒绝进入该槽位。

## CS_SELECT_ROLE (0x000A)

该帧固定为 8 字节：

```text
u16 frame_length = 8
u16 opcode = 10
i32 character_id
```

样本请求中的 `character_id` 与 `0x000C` 唯一角色记录的 ID 完全一致。程序不会固定保存样本 ID，而是每次从实时角色列表按配置槽位获取 ID，并核对客户端实际发出的 `0x000A`。

## SC_ROLE_START_POINT (0x0016)

该帧同样固定为 8 字节：

```text
u16 frame_length = 8
u16 opcode = 22
i16 map_x
i16 map_y
```

客户端收到后初始化角色坐标、进入游戏场景并继续发送进图请求。自动登录状态机只有在收到此包后才报告完成。

## 自动化策略

自动化使用原客户端界面操作，不伪造或注入角色选择包：

1. 解析实时 `0x000C` 并校验配置槽位存在。
2. 拒绝选择 `status == 1` 的删除/恢复角色。
3. 点击第 1 至第 5 个界面角色槽位，再点击“进入游戏”。
4. 解析 `0x000A`，核对其角色 ID 与配置槽位对应的实时 ID 一致。
5. 等待 `0x0016`；超时或 ID 不一致时停止并记录错误，避免进入错误角色。
