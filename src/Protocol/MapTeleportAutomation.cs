using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TianshuQitanLauncher.Protocol
{
    public enum MapTeleportAutomationState
    {
        Inactive = 0,
        WaitingForGameConnection = 1,
        SelectingMap = 2,
        RequestingTeleport = 3,
        WaitingForMap = 4,
        Completed = 5,
        Stopped = 6,
        Failed = 7,
        TravelingToTotem = 8,
        ConfirmingTotemTravel = 9,
        OpeningTotem = 10,
        ActivatingFlight = 11,
        FlyingToCoordinate = 12
    }

    public enum MapTravelMode
    {
        DirectTeleport = 0,
        LegacyTaskLink = 1,
        FlyToCoordinate = 2
    }

    public sealed class MapTeleportDestination
    {
        public int MapId { get; set; }
        public string MapName { get; set; }
        public ushort SpawnX { get; set; }
        public ushort SpawnY { get; set; }
        public int TotemNpcId { get; set; }
        public ushort TotemX { get; set; }
        public ushort TotemY { get; set; }

        public MapTeleportDestination Clone()
        {
            return (MapTeleportDestination)MemberwiseClone();
        }

        public override string ToString()
        {
            return (MapName ?? string.Empty) + "（ID " + MapId + "）";
        }
    }

    /// <summary>
    /// Confirmed two-packet map-teleport protocol from the 2026-09-02 recording.
    /// Callers that already own a larger automation state machine can use these
    /// builders directly and supply their own live request sequence.
    /// </summary>
    public static class TianshuMapTeleportProtocol
    {
        public const int ClientUiAction = 0x002E;
        public const int SelectMapAction = 316;
        public const int ExecuteTeleportAction = 172;
        public const int FlyToCoordinateAction = 169;
        public const int ActivateFlightAction = 804;
        public const int FlightPointListId = 290;
        public const int ServerMapInfo = 0x0055;
        public const int ServerFlightPointList = 0x0122;

        public static byte[] BuildSelectMap(int mapId, uint sequence)
        {
            return BuildMapAction(SelectMapAction, mapId, sequence);
        }

        public static byte[] BuildTeleportToMap(int mapId, uint sequence)
        {
            return BuildMapAction(ExecuteTeleportAction, mapId, sequence);
        }

        public static byte[] BuildMapAction(int actionId, int mapId, uint sequence)
        {
            if (actionId != SelectMapAction && actionId != ExecuteTeleportAction)
                throw new ArgumentOutOfRangeException("actionId", "Only the recorded map-select and map-teleport actions are supported.");
            if (mapId <= 0) throw new ArgumentOutOfRangeException("mapId");
            MapTeleportDestination ignored;
            if (!TianshuMapTeleportCatalog.TryGet(mapId, out ignored))
                throw new ArgumentOutOfRangeException("mapId", "Only destinations confirmed by the supplied recording are supported.");

            byte[] bytes = new byte[24];
            WriteUInt16(bytes, 0, (ushort)bytes.Length);
            WriteUInt16(bytes, 2, ClientUiAction);
            WriteUInt32(bytes, 4, (uint)actionId);
            WriteUInt32(bytes, 8, (uint)mapId);
            WriteUInt32(bytes, 12, 0);
            WriteUInt32(bytes, 16, 0);
            WriteUInt32(bytes, 20, sequence);
            return bytes;
        }

        public static byte[] BuildActivateFlight(uint sequence)
        {
            return BuildUiAction(ActivateFlightAction, FlightPointListId, 0, 0, sequence);
        }

        public static byte[] BuildFlyToCoordinate(int x, int y, uint sequence)
        {
            ValidateCoordinate(x, "x");
            ValidateCoordinate(y, "y");
            return BuildUiAction(FlyToCoordinateAction, 0, x, y, sequence);
        }

        public static bool TryParseFlightAction(byte[] bytes, out int actionId, out int x, out int y,
            out uint sequence)
        {
            actionId = 0;
            x = 0;
            y = 0;
            sequence = 0;
            if (bytes == null || bytes.Length != 24 || ReadUInt16(bytes, 0) != bytes.Length ||
                ReadUInt16(bytes, 2) != ClientUiAction) return false;
            actionId = (int)ReadUInt32(bytes, 4);
            sequence = ReadUInt32(bytes, 20);
            if (actionId == ActivateFlightAction)
                return ReadUInt32(bytes, 8) == FlightPointListId && ReadUInt32(bytes, 12) == 0 &&
                    ReadUInt32(bytes, 16) == 0;
            if (actionId != FlyToCoordinateAction || ReadUInt32(bytes, 8) != 0) return false;
            x = (int)ReadUInt32(bytes, 12);
            y = (int)ReadUInt32(bytes, 16);
            return x <= UInt16.MaxValue && y <= UInt16.MaxValue;
        }

        public static bool TryParseMapAction(byte[] bytes, out int actionId, out int mapId, out uint sequence)
        {
            actionId = 0;
            mapId = 0;
            sequence = 0;
            if (bytes == null || bytes.Length != 24 || ReadUInt16(bytes, 0) != bytes.Length ||
                ReadUInt16(bytes, 2) != ClientUiAction) return false;
            actionId = (int)ReadUInt32(bytes, 4);
            mapId = (int)ReadUInt32(bytes, 8);
            sequence = ReadUInt32(bytes, 20);
            return (actionId == SelectMapAction || actionId == ExecuteTeleportAction) && mapId > 0 &&
                ReadUInt32(bytes, 12) == 0 && ReadUInt32(bytes, 16) == 0;
        }

        private static void WriteUInt16(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)(value >> 8);
            bytes[offset + 1] = (byte)value;
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        private static int ReadUInt16(byte[] bytes, int offset)
        {
            return (bytes[offset] << 8) | bytes[offset + 1];
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
                ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private static byte[] BuildUiAction(int actionId, int value, int argument1, int argument2, uint sequence)
        {
            byte[] bytes = new byte[24];
            WriteUInt16(bytes, 0, (ushort)bytes.Length);
            WriteUInt16(bytes, 2, ClientUiAction);
            WriteUInt32(bytes, 4, (uint)actionId);
            WriteUInt32(bytes, 8, (uint)value);
            WriteUInt32(bytes, 12, (uint)argument1);
            WriteUInt32(bytes, 16, (uint)argument2);
            WriteUInt32(bytes, 20, sequence);
            return bytes;
        }

        private static void ValidateCoordinate(int value, string parameterName)
        {
            if (value < 0 || value > UInt16.MaxValue)
                throw new ArgumentOutOfRangeException(parameterName, "Coordinate must be between 0 and 65535.");
        }
    }

    /// <summary>
    /// Only destinations actually observed in the supplied recording are exposed.
    /// This avoids guessing unpublished or instance-only map identifiers.
    /// </summary>
    public static class TianshuMapTeleportCatalog
    {
        private static readonly IList<MapTeleportDestination> destinations = new List<MapTeleportDestination>
        {
            Create(86, "三界关", 2112, 1024, 575, 33, 65),
            Create(12, "灵昌城", 3424, 1328, 544, 40, 81),
            Create(76, "坠龙城", 896, 3360, 560, 14, 211),
            Create(77, "广寒城", 4384, 1136, 568, 67, 70),
            Create(89, "天山瑶池", 2080, 944, 578, 34, 60),
            Create(156, "仙境府邸", 1792, 320, 592, 29, 18),
            Create(9, "灵仙岛", 928, 2160, 543, 12, 137),
            Create(70, "天涯海角", 992, 1392, 562, 15, 90),
            Create(102, "逐浪广场", 928, 1008, 589, 13, 61),
            Create(72, "尚其村", 1984, 1984, 564, 31, 125),
            Create(75, "幽谷清泉", 1152, 960, 567, 18, 61),
            Create(101, "十字路口", 1248, 560, 587, 18, 35),
            Create(92, "北岭天关", 864, 304, 581, 13, 16),
            Create(91, "天外天", 192, 736, 580, 2, 44),
            Create(90, "九霄台", 960, 416, 579, 14, 25),
            Create(84, "落雁峰", 800, 592, 570, 12, 33),
            Create(83, "咆哮谷", 800, 976, 569, 12, 57),
            Create(88, "万重山雪顶", 1440, 1904, 577, 21, 117),
            Create(87, "万重山脚", 896, 1088, 576, 13, 66),
            Create(81, "千针雪林", 256, 960, 571, 3, 62),
            Create(78, "碧波水域", 480, 144, 574, 6, 8),
            Create(80, "伏魔山", 832, 1152, 572, 14, 73),
            Create(100, "怒焰祭坛", 1600, 1248, 586, 26, 76),
            Create(99, "焚石山", 160, 880, 585, 1, 53),
            Create(95, "祭牙台地", 1408, 704, 584, 21, 42),
            Create(93, "迷途沙洲", 928, 528, 582, 15, 31),
            Create(94, "困顿之林", 576, 672, 583, 8, 40),
            Create(73, "密霞谷", 928, 176, 563, 15, 9),
            Create(67, "回音谷", 1728, 640, 559, 28, 38),
            Create(79, "埋骨之地", 768, 992, 573, 11, 63),
            Create(74, "不归幽林", 1408, 320, 557, 21, 18),
            Create(68, "西川沼泽", 288, 336, 565, 3, 19),
            Create(66, "明湖水寨", 1152, 800, 561, 17, 52),
            Create(16, "芷水湖", 384, 608, 541, 7, 36),
            Create(69, "召凤台", 352, 368, 558, 5, 24),
            Create(71, "迷雾海", 1376, 848, 566, 20, 51),
            Create(15, "黄金港口", 672, 1008, 538, 9, 61),
            Create(10, "古道", 384, 1088, 534, 6, 62),
            Create(8, "迷梦泽", 1856, 832, 535, 29, 48),
            Create(7, "黑色水域", 1824, 816, 536, 27, 53),
            Create(5, "北影月森林", 1088, 608, 532, 16, 36),
            Create(4, "东影月森林", 704, 416, 533, 10, 24)
        }.AsReadOnly();

        public static IList<MapTeleportDestination> All
        {
            get
            {
                List<MapTeleportDestination> result = new List<MapTeleportDestination>(destinations.Count);
                for (int i = 0; i < destinations.Count; i++) result.Add(destinations[i].Clone());
                return result.AsReadOnly();
            }
        }

        public static bool TryGet(int mapId, out MapTeleportDestination destination)
        {
            for (int i = 0; i < destinations.Count; i++)
            {
                if (destinations[i].MapId != mapId) continue;
                destination = destinations[i].Clone();
                return true;
            }
            destination = null;
            return false;
        }

        public static bool TryGet(string mapNameOrId, out MapTeleportDestination destination)
        {
            destination = null;
            if (string.IsNullOrWhiteSpace(mapNameOrId)) return false;
            string expected = NormalizeName(mapNameOrId);
            int mapId;
            if (Int32.TryParse(expected, out mapId)) return TryGet(mapId, out destination);
            for (int i = 0; i < destinations.Count; i++)
            {
                if (!string.Equals(NormalizeName(destinations[i].MapName), expected, StringComparison.OrdinalIgnoreCase)) continue;
                destination = destinations[i].Clone();
                return true;
            }
            return false;
        }

        public static MapTeleportDestination GetRequired(int mapId)
        {
            MapTeleportDestination destination;
            if (!TryGet(mapId, out destination))
                throw new ArgumentOutOfRangeException("mapId", "地图 ID " + mapId + " 不在已录制的传送目录中。");
            return destination;
        }

        public static MapTeleportDestination GetRequired(string mapNameOrId)
        {
            MapTeleportDestination destination;
            if (!TryGet(mapNameOrId, out destination))
                throw new ArgumentException("地图名称或 ID 不在已录制的传送目录中：" + mapNameOrId, "mapNameOrId");
            return destination;
        }

        public static BountyTravelTarget GetTotemTravelTarget(int mapId)
        {
            MapTeleportDestination destination = GetRequired(mapId);
            return new BountyTravelTarget
            {
                NpcId = destination.TotemNpcId,
                MapId = destination.MapId,
                X = destination.TotemX,
                Y = destination.TotemY
            };
        }

        private static MapTeleportDestination Create(int mapId, string mapName, int x, int y,
            int totemNpcId, int totemX, int totemY)
        {
            return new MapTeleportDestination
            {
                MapId = mapId,
                MapName = mapName,
                SpawnX = (ushort)x,
                SpawnY = (ushort)y,
                TotemNpcId = totemNpcId,
                TotemX = (ushort)totemX,
                TotemY = (ushort)totemY
            };
        }

        private static string NormalizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.Trim().Replace(" ", string.Empty).Replace("　", string.Empty);
        }
    }

    /// <summary>
    /// Standalone reusable map-teleport state machine. The public TeleportTo overloads
    /// are the integration entry points for UI and other features that do not already
    /// own the workbench automation lock.
    /// </summary>
    public sealed class MapTeleportAutomationCoordinator : IDisposable
    {
        private const string AutomationOwner = "MapTeleport";
        private const int SelectToExecuteDelayMs = 750;
        private const int DialogConfirmDelayMs = 160;
        private const int TotemOpenDelayMs = 350;
        private const int MapArrivalTimeoutMs = 15000;

        private readonly object syncRoot = new object();
        private readonly ProtocolWorkbenchService service;
        private readonly Dictionary<long, ConnectionSession> gameConnections = new Dictionary<long, ConnectionSession>();
        private System.Threading.Timer stageTimer;
        private MapTeleportAutomationState state;
        private MapTravelMode mode;
        private MapTeleportDestination target;
        private int flightX;
        private int flightY;
        private RunLoopMapInfo currentMap;
        private long gameConnectionId;
        private uint nextSequence;
        private bool sequenceKnown;
        private bool activatedByAutomation;
        private int generation;
        private bool disposed;

        public event Action<MapTeleportAutomationState, string> StatusChanged;
        public event Action<MapTeleportDestination, RunLoopMapInfo> TeleportCompleted;
        public event Action<MapTeleportDestination, int, int> FlightCompleted;

        public MapTeleportAutomationCoordinator(ProtocolWorkbenchService service)
        {
            if (service == null) throw new ArgumentNullException("service");
            this.service = service;
            state = MapTeleportAutomationState.Inactive;
            service.ConnectionChanged += OnConnectionChanged;
            service.FrameCaptured += OnFrameCaptured;
            service.ActiveModeChanged += OnActiveModeChanged;
        }

        public MapTeleportAutomationState State { get { lock (syncRoot) return state; } }
        public MapTravelMode Mode { get { lock (syncRoot) return mode; } }
        public MapTeleportDestination Target { get { lock (syncRoot) return target == null ? null : target.Clone(); } }
        public RunLoopMapInfo CurrentMap
        {
            get
            {
                lock (syncRoot)
                    return currentMap == null ? null : new RunLoopMapInfo
                    {
                        MapId = currentMap.MapId,
                        MapName = currentMap.MapName,
                        ScaledX = currentMap.ScaledX,
                        ScaledY = currentMap.ScaledY
                    };
            }
        }

        public void TeleportTo(int mapId)
        {
            Start(TianshuMapTeleportCatalog.GetRequired(mapId), MapTravelMode.DirectTeleport, 0, 0);
        }

        public void TeleportTo(string mapNameOrId)
        {
            Start(TianshuMapTeleportCatalog.GetRequired(mapNameOrId), MapTravelMode.DirectTeleport, 0, 0);
        }

        public void TeleportTo(MapTeleportDestination destination)
        {
            if (destination == null) throw new ArgumentNullException("destination");
            Start(TianshuMapTeleportCatalog.GetRequired(destination.MapId), MapTravelMode.DirectTeleport, 0, 0);
        }

        public void TeleportViaTotem(int mapId)
        {
            Start(TianshuMapTeleportCatalog.GetRequired(mapId), MapTravelMode.LegacyTaskLink, 0, 0);
        }

        public void TeleportViaTotem(string mapNameOrId)
        {
            Start(TianshuMapTeleportCatalog.GetRequired(mapNameOrId), MapTravelMode.LegacyTaskLink, 0, 0);
        }

        public void FlyTo(int mapId, int x, int y)
        {
            ValidateFlightCoordinate(x, "x");
            ValidateFlightCoordinate(y, "y");
            Start(TianshuMapTeleportCatalog.GetRequired(mapId), MapTravelMode.FlyToCoordinate, x, y);
        }

        public void FlyTo(string mapNameOrId, int x, int y)
        {
            ValidateFlightCoordinate(x, "x");
            ValidateFlightCoordinate(y, "y");
            Start(TianshuMapTeleportCatalog.GetRequired(mapNameOrId), MapTravelMode.FlyToCoordinate, x, y);
        }

        public void FlyTo(int x, int y)
        {
            RunLoopMapInfo map = CurrentMap;
            if (map == null || map.MapId <= 0)
                throw new InvalidOperationException("尚未收到当前地图信息，请指定 mapId 或等待 SC_MAP_INFO 后再飞行。");
            FlyTo(map.MapId, x, y);
        }

        public void Stop()
        {
            StopCore(MapTeleportAutomationState.Stopped, "地图传送已停止。", false);
        }

        public void Dispose()
        {
            bool restore;
            lock (syncRoot)
            {
                if (disposed) return;
                disposed = true;
                generation++;
                DisposeTimerLocked();
                restore = activatedByAutomation;
                activatedByAutomation = false;
            }
            service.ConnectionChanged -= OnConnectionChanged;
            service.FrameCaptured -= OnFrameCaptured;
            service.ActiveModeChanged -= OnActiveModeChanged;
            service.ReleaseAutomation(AutomationOwner);
            if (restore && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void Start(MapTeleportDestination destination, MapTravelMode requestedMode, int requestedX, int requestedY)
        {
            string currentOwner;
            if (!service.TryAcquireAutomation(AutomationOwner, out currentOwner))
                throw new InvalidOperationException("当前已有自动化流程正在运行：" + currentOwner + "。请先停止后再传送地图。");

            bool enableActive;
            bool canBegin;
            bool alreadyThere;
            int currentGeneration;
            lock (syncRoot)
            {
                if (disposed)
                {
                    service.ReleaseAutomation(AutomationOwner);
                    throw new ObjectDisposedException(GetType().Name);
                }
                generation++;
                currentGeneration = generation;
                DisposeTimerLocked();
                target = destination.Clone();
                mode = requestedMode;
                flightX = requestedX;
                flightY = requestedY;
                enableActive = !service.ActiveMode;
                activatedByAutomation = enableActive;
                canBegin = gameConnectionId != 0 && sequenceKnown;
                alreadyThere = requestedMode == MapTravelMode.DirectTeleport &&
                    currentMap != null && currentMap.MapId == target.MapId;
                state = canBegin ? GetInitialState(requestedMode) : MapTeleportAutomationState.WaitingForGameConnection;
            }
            if (enableActive) service.SetActiveMode(true, false);
            string operation = requestedMode == MapTravelMode.DirectTeleport ? "传送点直传" :
                (requestedMode == MapTravelMode.LegacyTaskLink ? "旧任务链接瞬移" :
                "飞行到坐标 " + requestedX + "," + requestedY);
            Publish(currentGeneration, state, operation + "目标：" + destination.MapName + "（ID " + destination.MapId + "）。", false);
            if (alreadyThere)
                Complete(currentGeneration, "当前已经位于 " + destination.MapName + "，无需重复发包。");
            else if (canBegin)
                BeginSelectedOperation(currentGeneration);
        }

        private void OnConnectionChanged(ConnectionSession connection)
        {
            if (connection == null || connection.Kind != ConnectionKind.Game) return;
            bool begin = false;
            bool connectionChangedWhileRunning = false;
            int currentGeneration = 0;
            lock (syncRoot)
            {
                if (disposed) return;
                if (connection.ClosedUtc.HasValue || string.Equals(connection.State, "Closed", StringComparison.OrdinalIgnoreCase))
                    gameConnections.Remove(connection.Id);
                else
                    gameConnections[connection.Id] = connection.Clone();
                long selected = SelectLatestGameConnectionLocked();
                if (selected != gameConnectionId)
                {
                    connectionChangedWhileRunning = IsRunning(state) &&
                        state != MapTeleportAutomationState.WaitingForGameConnection && gameConnectionId != 0;
                    gameConnectionId = selected;
                    sequenceKnown = false;
                    nextSequence = 0;
                    currentMap = null;
                }
                currentGeneration = generation;
                if (state == MapTeleportAutomationState.WaitingForGameConnection && gameConnectionId != 0 && sequenceKnown)
                {
                    state = GetInitialState(mode);
                    begin = true;
                }
            }
            if (connectionChangedWhileRunning)
            {
                Fail(currentGeneration, "地图传送期间游戏连接发生切换。为避免在错误连接上继续使用序号，流程已安全停止。");
                return;
            }
            if (begin) BeginSelectedOperation(currentGeneration);
        }

        private void OnFrameCaptured(ProtocolFrame frame)
        {
            if (frame == null || !frame.Opcode.HasValue || frame.Bytes == null) return;
            int opcode = (int)frame.Opcode.Value;
            bool begin = false;
            bool connectionChangedWhileRunning = false;
            int currentGeneration;
            long selectedConnection;
            lock (syncRoot)
            {
                if (disposed) return;
                if (frame.Direction == TrafficDirection.ClientToServer && IsSequenceOpcode(opcode) &&
                    frame.ConnectionId != gameConnectionId && gameConnections.ContainsKey(frame.ConnectionId))
                {
                    connectionChangedWhileRunning = IsRunning(state) &&
                        state != MapTeleportAutomationState.WaitingForGameConnection && gameConnectionId != 0;
                    gameConnectionId = frame.ConnectionId;
                    sequenceKnown = false;
                    nextSequence = 0;
                    currentMap = null;
                }
                if (frame.Direction == TrafficDirection.ClientToServer && frame.ConnectionId == gameConnectionId)
                {
                    ObserveSequenceLocked(opcode, frame.Bytes);
                    if (state == MapTeleportAutomationState.WaitingForGameConnection && sequenceKnown)
                    {
                        state = GetInitialState(mode);
                        begin = true;
                    }
                }
                currentGeneration = generation;
                selectedConnection = gameConnectionId;
            }
            if (connectionChangedWhileRunning)
            {
                Fail(currentGeneration, "地图传送期间检测到另一条游戏连接。为避免序号串线，流程已安全停止。");
                return;
            }
            if (begin)
            {
                BeginSelectedOperation(currentGeneration);
                return;
            }
            if (frame.Direction != TrafficDirection.ServerToClient || frame.ConnectionId != selectedConnection) return;

            if (opcode == TianshuMapTeleportProtocol.ServerMapInfo)
            {
                RunLoopMapInfo map;
                if (!TianshuRunLoopProtocol.TryParseMapInfo(frame.Bytes, out map)) return;
                bool directArrived;
                bool legacyArrived;
                bool readyForFlight;
                bool flightResponse;
                lock (syncRoot)
                {
                    currentMap = map;
                    bool targetMap = IsCurrentLocked(currentGeneration) && target != null && map.MapId == target.MapId;
                    directArrived = targetMap && mode == MapTravelMode.DirectTeleport &&
                        state == MapTeleportAutomationState.WaitingForMap;
                    legacyArrived = targetMap && mode == MapTravelMode.LegacyTaskLink &&
                        state == MapTeleportAutomationState.WaitingForMap;
                    readyForFlight = targetMap && mode == MapTravelMode.FlyToCoordinate &&
                        state == MapTeleportAutomationState.WaitingForMap;
                    flightResponse = targetMap && mode == MapTravelMode.FlyToCoordinate &&
                        state == MapTeleportAutomationState.FlyingToCoordinate;
                }
                if (directArrived || legacyArrived)
                {
                    Complete(currentGeneration, "已到达 " + map.MapName + "（ID " + map.MapId +
                        "），服务端地图帧校验通过。" + (legacyArrived ? "落点为已录制的蟠龙图腾。" : string.Empty));
                }
                else if (readyForFlight)
                {
                    Publish(currentGeneration, MapTeleportAutomationState.OpeningTotem,
                        "旧任务链接已到达蟠龙图腾，准备访问 NPC " + target.TotemNpcId + "。", false);
                    lock (syncRoot)
                    {
                        if (!IsCurrentLocked(currentGeneration)) return;
                        state = MapTeleportAutomationState.OpeningTotem;
                        ScheduleLocked(currentGeneration, TotemOpenDelayMs, SendOpenTotem);
                    }
                }
                else if (flightResponse)
                {
                    Complete(currentGeneration, "飞行请求已由服务端确认：" + map.MapName + "（ID " + map.MapId +
                        "）目标坐标 " + flightX + "," + flightY + "。");
                }
                return;
            }

            if (opcode == TianshuBountyProtocol.ServerConfirmationDialog)
            {
                HandleTotemTravelConfirmation(currentGeneration, frame.Bytes);
                return;
            }

            if (opcode == TianshuMapTeleportProtocol.ServerFlightPointList)
            {
                bool sendFlight;
                lock (syncRoot)
                    sendFlight = IsCurrentLocked(currentGeneration) && mode == MapTravelMode.FlyToCoordinate &&
                        state == MapTeleportAutomationState.ActivatingFlight;
                if (sendFlight) SendFlight(currentGeneration);
                return;
            }

            if (opcode == TianshuBountyProtocol.ServerSystemMessage && IsRunning(State))
            {
                if (TianshuBountyProtocol.ContainsText(frame.Bytes, "尚未开启该地图传送点"))
                    Fail(currentGeneration, "服务端拒绝传送：尚未开启该地图传送点。未继续发送其他报文。");
                else if (TianshuBountyProtocol.ContainsText(frame.Bytes, "不能传送") ||
                    TianshuBountyProtocol.ContainsText(frame.Bytes, "无法传送"))
                    Fail(currentGeneration, "服务端拒绝地图传送。未继续发送其他报文。");
            }
        }

        private void BeginSelectedOperation(int expectedGeneration)
        {
            MapTravelMode selectedMode;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                selectedMode = mode;
            }
            if (selectedMode == MapTravelMode.DirectTeleport) SendSelectMap(expectedGeneration);
            else SendTravelToTotem(expectedGeneration);
        }

        private void SendTravelToTotem(int expectedGeneration)
        {
            MapTeleportDestination destination;
            long connectionId;
            uint sequence;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || gameConnectionId == 0 || !sequenceKnown) return;
                destination = target.Clone();
                connectionId = gameConnectionId;
                sequence = nextSequence++;
                state = MapTeleportAutomationState.TravelingToTotem;
                ScheduleLocked(expectedGeneration, MapArrivalTimeoutMs, OnMapTimeout);
            }
            BountyTravelTarget travelTarget = TianshuMapTeleportCatalog.GetTotemTravelTarget(destination.MapId);
            byte[] packet = TianshuBountyProtocol.BuildTravelRequest(travelTarget, sequence);
            if (!service.Send(connectionId, packet))
            {
                Fail(expectedGeneration, "旧任务链接 0x00B5 发送失败；连接可能已关闭或 ACTIVE 已关闭。");
                return;
            }
            Publish(expectedGeneration, MapTeleportAutomationState.TravelingToTotem,
                "已发送旧任务链接 0x00B5：蟠龙图腾 NPC " + travelTarget.NpcId + " / mapId=" +
                travelTarget.MapId + " / (" + travelTarget.X + "," + travelTarget.Y + ") / seq=" + sequence + "。", false);
        }

        private void HandleTotemTravelConfirmation(int expectedGeneration, byte[] bytes)
        {
            BountyConfirmationDialog dialog;
            if (!TianshuBountyProtocol.TryParseConfirmationDialog(bytes, out dialog)) return;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != MapTeleportAutomationState.TravelingToTotem ||
                    (dialog.Title ?? string.Empty).IndexOf("传送", StringComparison.Ordinal) < 0) return;
                state = MapTeleportAutomationState.ConfirmingTotemTravel;
                DisposeTimerLocked();
            }
            Publish(expectedGeneration, MapTeleportAutomationState.ConfirmingTotemTravel,
                "已取得本次服务端动态确认串，准备确认蟠龙图腾任务链接传送。", false);
            ScheduleLockedThreadSafe(expectedGeneration, DialogConfirmDelayMs, delegate(int value)
            {
                long connectionId;
                uint sequence;
                lock (syncRoot)
                {
                    if (!IsCurrentLocked(value) || state != MapTeleportAutomationState.ConfirmingTotemTravel ||
                        gameConnectionId == 0 || !sequenceKnown) return;
                    connectionId = gameConnectionId;
                    sequence = nextSequence++;
                    state = MapTeleportAutomationState.WaitingForMap;
                    ScheduleLocked(value, MapArrivalTimeoutMs, OnMapTimeout);
                }
                byte[] packet = TianshuBountyProtocol.BuildDialogResponse(dialog.ContextId, dialog.Token, true, sequence);
                if (!service.Send(connectionId, packet))
                {
                    Fail(value, "确认蟠龙图腾任务链接传送失败；连接可能已关闭或 ACTIVE 已关闭。");
                    return;
                }
                Publish(value, MapTeleportAutomationState.WaitingForMap,
                    "已发送 0x004C 动态确认，等待目标地图 SC_MAP_INFO。", false);
            });
        }

        private void SendOpenTotem(int expectedGeneration)
        {
            MapTeleportDestination destination;
            long connectionId;
            uint sequence;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || mode != MapTravelMode.FlyToCoordinate ||
                    state != MapTeleportAutomationState.OpeningTotem || gameConnectionId == 0 || !sequenceKnown) return;
                destination = target.Clone();
                connectionId = gameConnectionId;
                sequence = nextSequence++;
            }
            if (!service.Send(connectionId, TianshuBountyProtocol.BuildNpcOpen(destination.TotemNpcId, sequence)))
            {
                Fail(expectedGeneration, "访问蟠龙图腾失败；连接可能已关闭或 ACTIVE 已关闭。");
                return;
            }
            Publish(expectedGeneration, MapTeleportAutomationState.OpeningTotem,
                "已访问蟠龙图腾 NPC " + destination.TotemNpcId + "，准备开启飞行点列表。", false);
            ScheduleLockedThreadSafe(expectedGeneration, TotemOpenDelayMs, SendActivateFlight);
        }

        private void SendActivateFlight(int expectedGeneration)
        {
            long connectionId;
            uint sequence;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != MapTeleportAutomationState.OpeningTotem ||
                    gameConnectionId == 0 || !sequenceKnown) return;
                connectionId = gameConnectionId;
                sequence = nextSequence++;
                state = MapTeleportAutomationState.ActivatingFlight;
                ScheduleLocked(expectedGeneration, MapArrivalTimeoutMs, OnMapTimeout);
            }
            if (!service.Send(connectionId, TianshuMapTeleportProtocol.BuildActivateFlight(sequence)))
            {
                Fail(expectedGeneration, "开启飞行点列表失败；连接可能已关闭或 ACTIVE 已关闭。");
                return;
            }
            Publish(expectedGeneration, MapTeleportAutomationState.ActivatingFlight,
                "已发送 action=804/value=290，等待服务端 0x0122 飞行点列表。", false);
        }

        private void SendFlight(int expectedGeneration)
        {
            long connectionId;
            uint sequence;
            int x;
            int y;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != MapTeleportAutomationState.ActivatingFlight ||
                    gameConnectionId == 0 || !sequenceKnown) return;
                connectionId = gameConnectionId;
                sequence = nextSequence++;
                x = flightX;
                y = flightY;
                state = MapTeleportAutomationState.FlyingToCoordinate;
                ScheduleLocked(expectedGeneration, MapArrivalTimeoutMs, OnMapTimeout);
            }
            if (!service.Send(connectionId, TianshuMapTeleportProtocol.BuildFlyToCoordinate(x, y, sequence)))
            {
                Fail(expectedGeneration, "坐标飞行请求发送失败；连接可能已关闭或 ACTIVE 已关闭。");
                return;
            }
            Publish(expectedGeneration, MapTeleportAutomationState.FlyingToCoordinate,
                "已发送 action=169：目标 (" + x + "," + y + ") / seq=" + sequence +
                "；等待服务端 SC_MAP_INFO 确认。", false);
        }

        private void SendSelectMap(int expectedGeneration)
        {
            MapTeleportDestination destination;
            long connectionId;
            uint sequence;
            bool ready;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                ready = gameConnectionId != 0 && sequenceKnown;
                if (!ready)
                {
                    destination = null;
                    connectionId = 0;
                    sequence = 0;
                }
                else
                {
                    destination = target.Clone();
                    connectionId = gameConnectionId;
                    sequence = nextSequence++;
                    state = MapTeleportAutomationState.SelectingMap;
                }
            }
            if (!ready)
            {
                Fail(expectedGeneration, "发送地图选择帧前游戏连接或实时序号失效，流程已安全停止。");
                return;
            }
            byte[] packet = TianshuMapTeleportProtocol.BuildSelectMap(destination.MapId, sequence);
            if (!service.Send(connectionId, packet))
            {
                Fail(expectedGeneration, "地图选择帧发送失败；连接可能已关闭或 ACTIVE 已关闭。");
                return;
            }
            Publish(expectedGeneration, MapTeleportAutomationState.SelectingMap,
                "已发送地图选择帧 action=316，mapId=" + destination.MapId + "，seq=" + sequence + "。", false);
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = MapTeleportAutomationState.RequestingTeleport;
                ScheduleLocked(expectedGeneration, SelectToExecuteDelayMs, SendExecuteTeleport);
            }
        }

        private void SendExecuteTeleport(int expectedGeneration)
        {
            MapTeleportDestination destination;
            long connectionId;
            uint sequence;
            bool ready;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                ready = gameConnectionId != 0 && sequenceKnown;
                if (!ready)
                {
                    destination = null;
                    connectionId = 0;
                    sequence = 0;
                }
                else
                {
                    destination = target.Clone();
                    connectionId = gameConnectionId;
                    sequence = nextSequence++;
                    state = MapTeleportAutomationState.WaitingForMap;
                    DisposeTimerLocked();
                }
            }
            if (!ready)
            {
                Fail(expectedGeneration, "发送地图传送帧前游戏连接或实时序号失效，流程已安全停止。");
                return;
            }
            byte[] packet = TianshuMapTeleportProtocol.BuildTeleportToMap(destination.MapId, sequence);
            if (!service.Send(connectionId, packet))
            {
                Fail(expectedGeneration, "地图传送执行帧发送失败；连接可能已关闭或 ACTIVE 已关闭。");
                return;
            }
            Publish(expectedGeneration, MapTeleportAutomationState.WaitingForMap,
                "已发送传送执行帧 action=172，mapId=" + destination.MapId + "，seq=" + sequence +
                "；等待服务端 SC_MAP_INFO(0x0055) 到图确认。", false);
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                ScheduleLocked(expectedGeneration, MapArrivalTimeoutMs, OnMapTimeout);
            }
        }

        private void ObserveSequenceLocked(int opcode, byte[] bytes)
        {
            uint observed;
            if (!TianshuBountyProtocol.TryReadClientSequence(bytes, out observed)) return;
            if (!sequenceKnown)
            {
                if (!IsSequenceOpcode(opcode)) return;
                nextSequence = observed + 1;
                sequenceKnown = true;
                return;
            }
            uint candidate = observed + 1;
            if (candidate >= nextSequence && candidate - nextSequence < 4096) nextSequence = candidate;
        }

        private void OnMapTimeout(int expectedGeneration)
        {
            MapTeleportDestination destination = Target;
            MapTravelMode currentMode = Mode;
            Fail(expectedGeneration, (currentMode == MapTravelMode.FlyToCoordinate ? "等待飞行响应超时：" : "等待到图超时：") +
                "未收到目标地图 " +
                (destination == null ? "未知" : destination.MapName + "（ID " + destination.MapId + "）") +
                " 的 SC_MAP_INFO。已安全停止。");
        }

        private void Complete(int expectedGeneration, string message)
        {
            bool restore;
            MapTeleportDestination completedTarget;
            RunLoopMapInfo completedMap;
            MapTravelMode completedMode;
            int completedX;
            int completedY;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = MapTeleportAutomationState.Completed;
                DisposeTimerLocked();
                restore = activatedByAutomation;
                activatedByAutomation = false;
                completedTarget = target == null ? null : target.Clone();
                completedMode = mode;
                completedX = flightX;
                completedY = flightY;
                completedMap = currentMap == null ? null : new RunLoopMapInfo
                {
                    MapId = currentMap.MapId,
                    MapName = currentMap.MapName,
                    ScaledX = currentMap.ScaledX,
                    ScaledY = currentMap.ScaledY
                };
            }
            Publish(expectedGeneration, MapTeleportAutomationState.Completed, message, false);
            service.ReleaseAutomation(AutomationOwner);
            if (restore && service.ActiveMode) service.SetActiveMode(false, false);
            Action<MapTeleportDestination, RunLoopMapInfo> handler = TeleportCompleted;
            if (handler != null) handler(completedTarget, completedMap);
            Action<MapTeleportDestination, int, int> flightHandler = FlightCompleted;
            if (completedMode == MapTravelMode.FlyToCoordinate && flightHandler != null)
                flightHandler(completedTarget, completedX, completedY);
        }

        private void Fail(int expectedGeneration, string message)
        {
            bool restore;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = MapTeleportAutomationState.Failed;
                DisposeTimerLocked();
                restore = activatedByAutomation;
                activatedByAutomation = false;
            }
            Publish(expectedGeneration, MapTeleportAutomationState.Failed, message, true);
            service.ReleaseAutomation(AutomationOwner);
            if (restore && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void StopCore(MapTeleportAutomationState finalState, string message, bool error)
        {
            bool restore;
            lock (syncRoot)
            {
                if (disposed || !IsRunning(state)) return;
                state = finalState;
                generation++;
                DisposeTimerLocked();
                restore = activatedByAutomation;
                activatedByAutomation = false;
            }
            PublishAny(finalState, message, error);
            service.ReleaseAutomation(AutomationOwner);
            if (restore && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void OnActiveModeChanged(bool active)
        {
            if (!active && IsRunning(State))
                StopCore(MapTeleportAutomationState.Stopped, "ACTIVE/发包模式已关闭，地图传送同步停止。", false);
        }

        private void Publish(int expectedGeneration, MapTeleportAutomationState publishedState, string message, bool error)
        {
            lock (syncRoot) if (disposed || expectedGeneration != generation) return;
            PublishAny(publishedState, message, error);
        }

        private void PublishAny(MapTeleportAutomationState publishedState, string message, bool error)
        {
            Action<MapTeleportAutomationState, string> handler = StatusChanged;
            if (handler != null) handler(publishedState, message);
            long connectionId = GetGameConnectionId();
            service.ReportEngineEvent(AutomationOwner, error ? "ERROR" : "INFO", message,
                connectionId == 0 ? (long?)null : connectionId);
        }

        private void ScheduleLocked(int expectedGeneration, int delayMs, Action<int> callback)
        {
            DisposeTimerLocked();
            stageTimer = new System.Threading.Timer(delegate { callback(expectedGeneration); }, null, delayMs, Timeout.Infinite);
        }

        private void ScheduleLockedThreadSafe(int expectedGeneration, int delayMs, Action<int> callback)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                ScheduleLocked(expectedGeneration, delayMs, callback);
            }
        }

        private void DisposeTimerLocked()
        {
            if (stageTimer == null) return;
            stageTimer.Dispose();
            stageTimer = null;
        }

        private long SelectLatestGameConnectionLocked()
        {
            long result = 0;
            foreach (KeyValuePair<long, ConnectionSession> pair in gameConnections)
                if (!pair.Value.ClosedUtc.HasValue && pair.Value.RemotePort != 80 && pair.Value.RemotePort != 443 && pair.Key > result)
                    result = pair.Key;
            if (result != 0) return result;
            foreach (KeyValuePair<long, ConnectionSession> pair in gameConnections)
                if (!pair.Value.ClosedUtc.HasValue && pair.Key > result) result = pair.Key;
            return result;
        }

        private long GetGameConnectionId()
        {
            lock (syncRoot) return gameConnectionId;
        }

        private bool IsCurrentLocked(int expectedGeneration)
        {
            return !disposed && generation == expectedGeneration && IsRunning(state);
        }

        private static bool IsRunning(MapTeleportAutomationState value)
        {
            return value != MapTeleportAutomationState.Inactive && value != MapTeleportAutomationState.Completed &&
                value != MapTeleportAutomationState.Stopped && value != MapTeleportAutomationState.Failed;
        }

        private static bool IsSequenceOpcode(int opcode)
        {
            return opcode == TianshuBountyProtocol.ClientTravelLink || opcode == TianshuBountyProtocol.ClientNpcOpen ||
                opcode == TianshuBountyProtocol.ClientNpcFunction || opcode == TianshuMountainClimbProtocol.ClientQuestAction ||
                opcode == TianshuBountyProtocol.ClientDialogResponse || opcode == TianshuRunLoopProtocol.ClientMovement ||
                opcode == TianshuMapTeleportProtocol.ClientUiAction || opcode == 0x0042 || opcode == 0x001B;
        }

        private static MapTeleportAutomationState GetInitialState(MapTravelMode selectedMode)
        {
            return selectedMode == MapTravelMode.DirectTeleport ? MapTeleportAutomationState.SelectingMap :
                MapTeleportAutomationState.TravelingToTotem;
        }

        private static void ValidateFlightCoordinate(int value, string parameterName)
        {
            if (value < 0 || value > UInt16.MaxValue)
                throw new ArgumentOutOfRangeException(parameterName, "坐标必须位于 0 到 65535 之间。");
        }
    }

    public sealed class MapTeleportAutomationControl : UserControl
    {
        private readonly MapTeleportAutomationCoordinator coordinator;
        private readonly ComboBox destinationBox;
        private readonly Button teleportButton;
        private readonly Button legacyTeleportButton;
        private readonly Button flyButton;
        private readonly NumericUpDown flightXBox;
        private readonly NumericUpDown flightYBox;
        private readonly Button stopButton;
        private readonly Label stateLabel;
        private readonly Label currentMapLabel;
        private readonly Label destinationDetailLabel;

        public MapTeleportAutomationControl(MapTeleportAutomationCoordinator coordinator)
        {
            if (coordinator == null) throw new ArgumentNullException("coordinator");
            this.coordinator = coordinator;
            Dock = DockStyle.Fill;

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                ColumnCount = 1,
                RowCount = 7
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label help = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                Text = "地图页提供三种可组合能力：传送点直传；用旧任务链接 0x00B5 瞬移到目标地图蟠龙图腾；先到图腾并开启飞行列表，再按输入坐标飞行。全部使用实时连接、实时序号和服务端动态确认串。"
            };
            layout.Controls.Add(help, 0, 0);

            FlowLayoutPanel selector = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            selector.Controls.Add(new Label { Text = "目标地图", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
            destinationBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
            IList<MapTeleportDestination> destinations = TianshuMapTeleportCatalog.All;
            for (int i = 0; i < destinations.Count; i++) destinationBox.Items.Add(destinations[i]);
            if (destinationBox.Items.Count > 0) destinationBox.SelectedIndex = 0;
            destinationBox.SelectedIndexChanged += delegate { UpdateDestinationDetail(); };
            selector.Controls.Add(destinationBox);
            teleportButton = new Button { Text = "传送点直传", AutoSize = true };
            legacyTeleportButton = new Button { Text = "旧链接瞬移到图腾", AutoSize = true };
            stopButton = new Button { Text = "停止", AutoSize = true, Enabled = false };
            teleportButton.Click += OnTeleport;
            legacyTeleportButton.Click += OnLegacyTeleport;
            stopButton.Click += delegate { coordinator.Stop(); };
            selector.Controls.Add(teleportButton);
            selector.Controls.Add(legacyTeleportButton);
            selector.Controls.Add(stopButton);
            layout.Controls.Add(selector, 0, 1);

            FlowLayoutPanel flightPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            flightPanel.Controls.Add(new Label { Text = "飞行坐标 X", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
            flightXBox = new NumericUpDown { Minimum = 0, Maximum = UInt16.MaxValue, Width = 80 };
            flightPanel.Controls.Add(flightXBox);
            flightPanel.Controls.Add(new Label { Text = "Y", AutoSize = true, Padding = new Padding(4, 7, 0, 0) });
            flightYBox = new NumericUpDown { Minimum = 0, Maximum = UInt16.MaxValue, Width = 80 };
            flightPanel.Controls.Add(flightYBox);
            flyButton = new Button { Text = "飞行到坐标", AutoSize = true };
            flyButton.Click += OnFly;
            flightPanel.Controls.Add(flyButton);
            layout.Controls.Add(flightPanel, 0, 2);

            destinationDetailLabel = new Label { AutoSize = true };
            stateLabel = new Label { AutoSize = true, Text = "状态：" + coordinator.State };
            currentMapLabel = new Label { AutoSize = true, Text = "当前地图：等待服务端地图帧" };
            layout.Controls.Add(destinationDetailLabel, 0, 3);
            layout.Controls.Add(stateLabel, 0, 4);
            layout.Controls.Add(currentMapLabel, 0, 5);
            layout.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = Color.DarkRed,
                Text = "提示：目录只包含录制确认的 42 张地图和对应蟠龙图腾；飞行目标必须是游戏地图允许到达的坐标。服务端拒绝或阶段超时会自动停止。"
            }, 0, 6);
            Controls.Add(layout);

            coordinator.StatusChanged += OnStatusChanged;
            UpdateDestinationDetail();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) coordinator.StatusChanged -= OnStatusChanged;
            base.Dispose(disposing);
        }

        private void OnTeleport(object sender, EventArgs e)
        {
            MapTeleportDestination destination = destinationBox.SelectedItem as MapTeleportDestination;
            if (destination == null) return;
            try { coordinator.TeleportTo(destination.MapId); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "地图传送", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void OnLegacyTeleport(object sender, EventArgs e)
        {
            MapTeleportDestination destination = destinationBox.SelectedItem as MapTeleportDestination;
            if (destination == null) return;
            try { coordinator.TeleportViaTotem(destination.MapId); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "旧任务链接瞬移", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void OnFly(object sender, EventArgs e)
        {
            MapTeleportDestination destination = destinationBox.SelectedItem as MapTeleportDestination;
            if (destination == null) return;
            try { coordinator.FlyTo(destination.MapId, Decimal.ToInt32(flightXBox.Value), Decimal.ToInt32(flightYBox.Value)); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "坐标飞行", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void UpdateDestinationDetail()
        {
            MapTeleportDestination destination = destinationBox.SelectedItem as MapTeleportDestination;
            destinationDetailLabel.Text = destination == null ? "目标详情：未选择" :
                "目标详情：" + destination.MapName + " / mapId=" + destination.MapId +
                " / 蟠龙图腾 NPC=" + destination.TotemNpcId + " (" + destination.TotemX + "," +
                destination.TotemY + ") / 直传落点=" + destination.SpawnX + "," + destination.SpawnY;
        }

        private void OnStatusChanged(MapTeleportAutomationState automationState, string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<MapTeleportAutomationState, string>(OnStatusChanged), automationState, message); }
                catch (InvalidOperationException) { }
                return;
            }
            bool running = automationState != MapTeleportAutomationState.Inactive &&
                automationState != MapTeleportAutomationState.Completed &&
                automationState != MapTeleportAutomationState.Stopped &&
                automationState != MapTeleportAutomationState.Failed;
            teleportButton.Enabled = !running;
            legacyTeleportButton.Enabled = !running;
            flyButton.Enabled = !running;
            flightXBox.Enabled = !running;
            flightYBox.Enabled = !running;
            destinationBox.Enabled = !running;
            stopButton.Enabled = running;
            stateLabel.Text = "状态：" + automationState;
            RunLoopMapInfo map = coordinator.CurrentMap;
            currentMapLabel.Text = map == null ? "当前地图：等待服务端地图帧" :
                "当前地图：" + map.MapName + "（ID " + map.MapId + "，坐标 " + map.ScaledX + "," + map.ScaledY + "）";
        }
    }
}
