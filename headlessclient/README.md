# 天书奇谈 TypeScript 无头网页客户端

这是一个本机运行的协议客户端原型，目录与仓库的 `src` 平级。它可以执行：

```text
入口服账号密码认证
  → 实时解析线路列表并选择一线至四线
  → 票据登录游戏服
  → 实时解析角色列表并选择第 1–5 个槽位
  → 确认角色起点并发送进图请求
  → 自动响应 30 秒服务器心跳
```

它是“无头协议客户端”，没有复刻 Flash 的地图绘制、战斗界面和全部游戏业务。登录、角色进入和保持会话在线已经实现；后续自动化可以直接接在 `TianshuClient` 的在线状态和统一发包器上。

## 为什么需要本机网关

普通网页不能直接建立任意 TCP Socket，而游戏服不是 WebSocket 服务。因此：

- 浏览器页面负责登录表单、状态和帧元数据显示；
- Node.js 网关监听 `127.0.0.1:3210`，负责原生 TCP、协议拆帧和心跳；
- 页面与网关之间使用只允许本机来源的 WebSocket。

## 安装和运行

需要 Node.js `20.19+`，或 `22.12+`。

```powershell
cd C:\Users\xzw65\Desktop\tianshuqitan\headlessclient
npm install
npm run dev
```

开发页面：

```text
http://127.0.0.1:5173
```

生产构建和运行：

```powershell
npm run build
npm start
```

生产页面：

```text
http://127.0.0.1:3210
```

验证命令：

```powershell
npm test
npm run check
```

## 登录参数

- 入口服默认使用实录中的 `124.250.115.168` 与候选端口 `7800,7801,7802,7803`。
- 游戏服地址和端口不固化，始终从本次 `SC_GAMESERVER_LIST (0x00C9)` 读取。
- 线路按显示名中的“一线、二线、三线、四线”匹配，推荐线路别名不会替代明确选择。
- `OPERATION_COM` 默认是本次客户端配置使用的 0。客户端校验字符串按原版公式实时计算：`md5(operationCom + serverId + md5("tspk")) + " "`。
- 强制登录默认关闭；服务器明确认为账号仍在线时才需要启用。

## 凭据处理

- 页面不使用 localStorage、sessionStorage、Cookie 或 URL 参数保存账号密码。
- 表单关闭浏览器自动填充；浏览器自身是否仍建议保存密码由用户的密码管理器策略决定。
- 提交后立即清空密码输入框。
- 网关只绑定 `127.0.0.1`，并校验 WebSocket 来源；不接受局域网访问。
- 凭据帧和票据帧发送完成后会尽力清零对应 Buffer；日志只展示 opcode 和长度。
- 天书 v5 原协议的 `CS_USER_PASS` 本身是明文 TCP。上述措施只能避免本机应用额外持久化，不能把旧服务器协议升级成 TLS。不要在不可信网络运行。

## 已知边界

- 验证码、实名/防沉迷弹窗、封号提示等分支暂时只会作为服务端错误停止，不会自动绕过。
- 本次没有使用真实账号执行自动化冒烟测试；单元测试使用脱敏的协议向量，生产构建只验证代码和静态页面。
- 若服务器今后改变登录字段、运营商编号或心跳密钥，客户端会明确失败，不会猜测报文。

心跳算法、完整字段表、21 组连续验证值和时间统计见 [`../docs/heartbeat-protocol.md`](../docs/heartbeat-protocol.md)。登录与角色字段见 [`../docs/login-protocol.md`](../docs/login-protocol.md) 和 [`../docs/role-selection-protocol.md`](../docs/role-selection-protocol.md)。
