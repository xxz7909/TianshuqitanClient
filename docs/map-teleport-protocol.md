# 地图传送基础功能

本功能根据以下原子操作录制实现：

```text
C:\Users\xzw65\Desktop\tianshuqitan\bin\Release\data\instances\4dd01405b8bd45e3b66cec27df5f85ae\sessions\session-20260902-092240-999-传送大部分地图.sqlite
C:\Users\xzw65\Desktop\tianshuqitan\bin\Release\data\instances\a421ad6c42e6455ea91104f521d29b36\sessions\session-20260902-210122-016-飞行.sqlite
```

第一份录制给出 42 张地图的传送过程和各地图蟠龙图腾实体；第二份录制确认了图腾飞行列表与指定坐标飞行。程序不保存或重放录制中的请求序号、时间戳和传送确认 token。

## 界面使用

重新启动客户端后，打开工作台的“地图传送”页：

1. 在下拉框选择目标地图。
2. “传送点直传”沿用 action 316/172；“旧链接瞬移到图腾”使用 `0x00B5` 和该地图已录制的蟠龙图腾；两者都以目标 `SC_MAP_INFO (0x0055)` 为成功条件。
3. 输入 X/Y 后点击“飞行到坐标”，程序先用旧任务链接到目标地图图腾，再访问图腾、开启飞行列表，最后发送坐标飞行请求。
4. 任一方式都会等待当前游戏连接和实时请求序号；连接切换、服务端拒绝或阶段超时会安全停止。

如果服务端提示“尚未开启该地图传送点”、连接发生切换、ACTIVE 被关闭或 15 秒内没有到图，流程会停止，不会继续猜测或重发其他报文。

## 代码接口

高层入口位于 `MapTeleportAutomationCoordinator`，可按地图 ID、地图名称或目录对象调用：

```csharp
mapTeleportAutomation.TeleportTo(86);          // 三界关
mapTeleportAutomation.TeleportTo("灵昌城");   // ID 12
mapTeleportAutomation.TeleportTo("102");      // 逐浪广场，也接受数字字符串

mapTeleportAutomation.TeleportViaTotem(86);    // 0x00B5 到三界关蟠龙图腾
mapTeleportAutomation.TeleportViaTotem("灵昌城");

mapTeleportAutomation.FlyTo(69, 21, 35);       // 到召凤台图腾后飞到 (21,35)
mapTeleportAutomation.FlyTo("召凤台", 23, 74);
mapTeleportAutomation.FlyTo(21, 35);           // 已知当前地图时可省略 mapId
```

成功时可订阅事件，将后续业务接在服务端到图确认之后：

```csharp
mapTeleportAutomation.TeleportCompleted += delegate(
    MapTeleportDestination target, RunLoopMapInfo arrivedMap)
{
    // arrivedMap.MapId 已经与 target.MapId 校验相等，可执行后续步骤。
};

mapTeleportAutomation.FlightCompleted += delegate(
    MapTeleportDestination target, int x, int y)
{
    // 服务器已返回目标地图的 SC_MAP_INFO，可继续访问 NPC 或执行跑环步骤。
};
```

高层协调器会独占工作台的发包自动化锁，适合独立功能或界面调用。一个已经持有自动化锁的较大状态机不应嵌套调用高层协调器；它应复用底层函数，并使用自己的实时序号和到图状态：

```csharp
byte[] select = TianshuMapTeleportProtocol.BuildSelectMap(mapId, nextSequence++);
byte[] execute = TianshuMapTeleportProtocol.BuildTeleportToMap(mapId, nextSequence++);
byte[] legacy = TianshuBountyProtocol.BuildTravelRequest(
    TianshuMapTeleportCatalog.GetTotemTravelTarget(mapId), nextSequence++);
byte[] activate = TianshuMapTeleportProtocol.BuildActivateFlight(nextSequence++);
byte[] fly = TianshuMapTeleportProtocol.BuildFlyToCoordinate(x, y, nextSequence++);
```

地图目录接口：

```csharp
IList<MapTeleportDestination> maps = TianshuMapTeleportCatalog.All;
MapTeleportDestination target = TianshuMapTeleportCatalog.GetRequired(86);
bool found = TianshuMapTeleportCatalog.TryGet("三界关", out target);
BountyTravelTarget totem = TianshuMapTeleportCatalog.GetTotemTravelTarget(86);
```

目录仅包含录制确认的地图。传入未录制 ID 或名称会直接拒绝，不会构造猜测请求。

## 已确认协议

两帧都是 24 字节的 `CS_UI_ACTION (0x002E)`，采用大端整数：

| 偏移 | 长度 | 字段 | 第一帧 | 第二帧 |
|---:|---:|---|---:|---:|
| 0 | 2 | 帧长 | 24 | 24 |
| 2 | 2 | opcode | `0x002E` | `0x002E` |
| 4 | 4 | action_id | `316` | `172` |
| 8 | 4 | action_value | 目标 mapId | 同一目标 mapId |
| 12 | 4 | arg1 | 0 | 0 |
| 16 | 4 | arg2 | 0 | 0 |
| 20 | 4 | sequence | 实时序号 N | 实时序号 N+1 |

三界关（mapId 86）的录制样例：

```text
选择：00 18 00 2E 00 00 01 3C 00 00 00 56 00 00 00 00 00 00 00 00 00 00 00 20
执行：00 18 00 2E 00 00 00 AC 00 00 00 56 00 00 00 00 00 00 00 00 00 00 00 21
```

录制中两帧之间的人工操作间隔中位数约为 793 ms；自动化使用 750 ms。第二帧发出后，服务端到图帧约在 3.0–3.1 秒后出现。`0x0042` 是约 30 秒一次的周期帧，与传送动作无关，状态机不会把它当作传送步骤。

## 旧任务链接到蟠龙图腾

`TeleportViaTotem` 使用已经在除暴、跑环中验证过的旧任务链接 `CS 0x00B5`：

```text
frame_len:u16 | 00 B5 | mode:u32=FE
| npc_id:utf8 | map_id:utf8 | "x=y":utf8 | sequence:u32
```

例如召凤台蟠龙图腾是 `NPC 558 / map 69 / (5,24)`：

```text
00 1B 00 B5 00 00 00 FE 00 03 35 35 38 00 02 36 39 00 04 35 3D 32 34 00 00 00 05
```

服务端随后返回 `SC 0x005C`。实现解析本次响应中的 `contextId` 和随机 token，再以实时序号构造 `CS 0x004C` 确认；不能复制历史 token。目标地图的 `SC 0x0055` 到达后才完成。

## 指定坐标飞行

飞行录制中的 `0x00C1` 是走近并访问蟠龙图腾前的一次普通走步，不是最终飞行包。真正的飞行过程为：

1. `CS 0x0016` 访问当前地图蟠龙图腾。
2. `CS 0x002E / action=804 / value=290` 开启飞行点列表。
3. 等待服务端 `0x0122` 列表响应。
4. `CS 0x002E / action=169 / value=0 / arg1=X / arg2=Y` 飞到指定逻辑坐标。
5. 等待同一目标地图的 `SC 0x0055` 作为服务端确认。

录制样例：

```text
开启列表：00 18 00 2E 00 00 03 24 00 00 01 22 00 00 00 00 00 00 00 00 00 00 00 07
飞到 21,35：00 18 00 2E 00 00 00 A9 00 00 00 00 00 00 00 15 00 00 00 23 00 00 00 09
```

这里 X/Y 是飞行 action 的逻辑坐标，不需要乘 16；只有 `0x00C1` 走步帧使用 16 倍坐标。坐标范围在 API 层限制为 `0..65535`，具体地图是否接受该落点仍由服务端决定。

## 已录制地图目录

| mapId | 地图 | 录制到图坐标 | mapId | 地图 | 录制到图坐标 |
|---:|---|---:|---:|---|---:|
| 86 | 三界关 | 2112,1024 | 12 | 灵昌城 | 3424,1328 |
| 76 | 坠龙城 | 896,3360 | 77 | 广寒城 | 4384,1136 |
| 89 | 天山瑶池 | 2080,944 | 156 | 仙境府邸 | 1792,320 |
| 9 | 灵仙岛 | 928,2160 | 70 | 天涯海角 | 992,1392 |
| 102 | 逐浪广场 | 928,1008 | 72 | 尚其村 | 1984,1984 |
| 75 | 幽谷清泉 | 1152,960 | 101 | 十字路口 | 1248,560 |
| 92 | 北岭天关 | 864,304 | 91 | 天外天 | 192,736 |
| 90 | 九霄台 | 960,416 | 84 | 落雁峰 | 800,592 |
| 83 | 咆哮谷 | 800,976 | 88 | 万重山雪顶 | 1440,1904 |
| 87 | 万重山脚 | 896,1088 | 81 | 千针雪林 | 256,960 |
| 78 | 碧波水域 | 480,144 | 80 | 伏魔山 | 832,1152 |
| 100 | 怒焰祭坛 | 1600,1248 | 99 | 焚石山 | 160,880 |
| 95 | 祭牙台地 | 1408,704 | 93 | 迷途沙洲 | 928,528 |
| 94 | 困顿之林 | 576,672 | 73 | 密霞谷 | 928,176 |
| 67 | 回音谷 | 1728,640 | 79 | 埋骨之地 | 768,992 |
| 74 | 不归幽林 | 1408,320 | 68 | 西川沼泽 | 288,336 |
| 66 | 明湖水寨 | 1152,800 | 16 | 芷水湖 | 384,608 |
| 69 | 召凤台 | 352,368 | 71 | 迷雾海 | 1376,848 |
| 15 | 黄金港口 | 672,1008 | 10 | 古道 | 384,1088 |
| 8 | 迷梦泽 | 1856,832 | 7 | 黑色水域 | 1824,816 |
| 5 | 北影月森林 | 1088,608 | 4 | 东影月森林 | 704,416 |

坐标来自每次传送后的服务端地图帧，仅用于诊断和校验录制目录；请求本身只需要 mapId，不能把这些坐标当作跨地图走步包使用。

对应蟠龙图腾目录如下；坐标是 `0x00B5` 任务链接使用的逻辑坐标：

| mapId | 地图 | 图腾 NPC | 图腾坐标 | mapId | 地图 | 图腾 NPC | 图腾坐标 |
|---:|---|---:|---:|---:|---|---:|---:|
| 86 | 三界关 | 575 | 33,65 | 12 | 灵昌城 | 544 | 40,81 |
| 76 | 坠龙城 | 560 | 14,211 | 77 | 广寒城 | 568 | 67,70 |
| 89 | 天山瑶池 | 578 | 34,60 | 156 | 仙境府邸 | 592 | 29,18 |
| 9 | 灵仙岛 | 543 | 12,137 | 70 | 天涯海角 | 562 | 15,90 |
| 102 | 逐浪广场 | 589 | 13,61 | 72 | 尚其村 | 564 | 31,125 |
| 75 | 幽谷清泉 | 567 | 18,61 | 101 | 十字路口 | 587 | 18,35 |
| 92 | 北岭天关 | 581 | 13,16 | 91 | 天外天 | 580 | 2,44 |
| 90 | 九霄台 | 579 | 14,25 | 84 | 落雁峰 | 570 | 12,33 |
| 83 | 咆哮谷 | 569 | 12,57 | 88 | 万重山雪顶 | 577 | 21,117 |
| 87 | 万重山脚 | 576 | 13,66 | 81 | 千针雪林 | 571 | 3,62 |
| 78 | 碧波水域 | 574 | 6,8 | 80 | 伏魔山 | 572 | 14,73 |
| 100 | 怒焰祭坛 | 586 | 26,76 | 99 | 焚石山 | 585 | 1,53 |
| 95 | 祭牙台地 | 584 | 21,42 | 93 | 迷途沙洲 | 582 | 15,31 |
| 94 | 困顿之林 | 583 | 8,40 | 73 | 密霞谷 | 563 | 15,9 |
| 67 | 回音谷 | 559 | 28,38 | 79 | 埋骨之地 | 573 | 11,63 |
| 74 | 不归幽林 | 557 | 21,18 | 68 | 西川沼泽 | 565 | 3,19 |
| 66 | 明湖水寨 | 561 | 17,52 | 16 | 芷水湖 | 541 | 7,36 |
| 69 | 召凤台 | 558 | 5,24 | 71 | 迷雾海 | 566 | 20,51 |
| 15 | 黄金港口 | 538 | 9,61 | 10 | 古道 | 534 | 6,62 |
| 8 | 迷梦泽 | 535 | 29,48 | 7 | 黑色水域 | 536 | 27,53 |
| 5 | 北影月森林 | 532 | 16,36 | 4 | 东影月森林 | 533 | 10,24 |
