using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace TianshuQitanLauncher.Protocol
{
    public enum NpcCatalogHarvesterState
    {
        Inactive = 0,
        WaitingForGameConnection = 1,
        Traversing = 2,
        Completed = 3,
        Stopped = 4,
        Failed = 5
    }

    public sealed class NpcCatalogEntry
    {
        public string Name { get; set; }
        public int NpcId { get; set; }
        public int MapId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }

    public sealed class NpcCatalogDocument
    {
        public int Version { get; set; }
        public IList<NpcCatalogEntry> Entries { get; set; }

        public NpcCatalogDocument()
        {
            Version = 1;
            Entries = new List<NpcCatalogEntry>();
        }
    }

    /// <summary>
    /// One-click traversal over the already-recorded map-teleport catalog. It uses the
    /// two-packet direct teleport API (action 316 then 172), waits for SC_MAP_INFO, then
    /// harvests SC_MAP_ENTITY_LIST / SC_NEARBY_ENTITY_UPDATE into a persistent JSON catalog.
    /// </summary>
    public sealed class NpcCatalogHarvesterCoordinator : IDisposable
    {
        private const string AutomationOwner = "NpcCatalogHarvester";
        private const int SelectToExecuteDelayMs = 750;
        private const int MapArrivalTimeoutMs = 15000;
        private const int EntitySettleDelayMs = 1500;

        private readonly object syncRoot = new object();
        private readonly ProtocolWorkbenchService service;
        private readonly Dictionary<long, ConnectionSession> gameConnections = new Dictionary<long, ConnectionSession>();
        private readonly Dictionary<string, NpcCatalogEntry> entries = new Dictionary<string, NpcCatalogEntry>(StringComparer.Ordinal);
        private readonly string catalogPath;
        private System.Threading.Timer stageTimer;
        private NpcCatalogHarvesterState state;
        private List<MapTeleportDestination> pendingMaps;
        private int currentIndex;
        private MapTeleportDestination currentDestination;
        private RunLoopMapInfo currentMap;
        private long gameConnectionId;
        private uint nextSequence;
        private bool sequenceKnown;
        private bool activatedByAutomation;
        private bool advanceScheduled;
        private int generation;
        private bool disposed;

        public event Action<NpcCatalogHarvesterState, string> StatusChanged;

        public NpcCatalogHarvesterCoordinator(ProtocolWorkbenchService service)
        {
            if (service == null) throw new ArgumentNullException("service");
            this.service = service;
            state = NpcCatalogHarvesterState.Inactive;
            catalogPath = service.Profile.Resolve("data/npc-catalog.json");
            LoadCatalog();
            service.ConnectionChanged += OnConnectionChanged;
            service.FrameCaptured += OnFrameCaptured;
            service.ActiveModeChanged += OnActiveModeChanged;
        }

        public NpcCatalogHarvesterState State { get { lock (syncRoot) return state; } }
        public int TotalMaps
        {
            get { lock (syncRoot) return pendingMaps == null ? 0 : pendingMaps.Count; }
        }
        public int VisitedMaps
        {
            get { lock (syncRoot) return Math.Min(currentIndex, TotalMapsLocked); }
        }
        public int LearnedEntries
        {
            get { lock (syncRoot) return entries.Count; }
        }
        public MapTeleportDestination CurrentDestination
        {
            get { lock (syncRoot) return currentDestination == null ? null : currentDestination.Clone(); }
        }
        public string CatalogPath { get { return catalogPath; } }

        private int TotalMapsLocked
        {
            get { return pendingMaps == null ? 0 : pendingMaps.Count; }
        }

        public void Start()
        {
            string currentOwner;
            if (!service.TryAcquireAutomation(AutomationOwner, out currentOwner))
                throw new InvalidOperationException("当前已有自动化流程正在运行：" + currentOwner + "。请先停止后再采集 NPC 目录。");

            LoadEntriesFromSessions();

            bool enableActive;
            bool canBegin;
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
                pendingMaps = new List<MapTeleportDestination>(TianshuMapTeleportCatalog.All);
                currentIndex = 0;
                currentDestination = null;
                advanceScheduled = false;
                enableActive = !service.ActiveMode;
                activatedByAutomation = enableActive;
                canBegin = gameConnectionId != 0 && sequenceKnown;
                state = canBegin ? NpcCatalogHarvesterState.Traversing : NpcCatalogHarvesterState.WaitingForGameConnection;
            }
            if (enableActive) service.SetActiveMode(true, false);
            Publish(currentGeneration, state, "开始遍历 " + pendingMaps.Count +
                " 张已录制地图并采集 NPC 目录；历史会话已预载 " + entries.Count + " 条。", false);
            if (canBegin) BeginTraversal(currentGeneration);
        }

        public void Stop()
        {
            StopCore(NpcCatalogHarvesterState.Stopped, "NPC 目录采集已停止。", false);
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

        private void OnConnectionChanged(ConnectionSession connection)
        {
            if (connection == null || connection.Kind != ConnectionKind.Game) return;
            bool begin = false;
            int currentGeneration = 0;
            lock (syncRoot)
            {
                if (disposed) return;
                if (connection.ClosedUtc.HasValue || string.Equals(connection.State, "Closed", StringComparison.OrdinalIgnoreCase))
                    gameConnections.Remove(connection.Id);
                else gameConnections[connection.Id] = connection.Clone();
                long selected = SelectLatestGameConnectionLocked();
                if (selected != gameConnectionId)
                {
                    gameConnectionId = selected;
                    sequenceKnown = false;
                    nextSequence = 0;
                    currentMap = null;
                }
                currentGeneration = generation;
                if (state == NpcCatalogHarvesterState.WaitingForGameConnection && gameConnectionId != 0 && sequenceKnown)
                {
                    state = NpcCatalogHarvesterState.Traversing;
                    begin = true;
                }
            }
            if (begin) BeginTraversal(currentGeneration);
        }

        private void OnFrameCaptured(ProtocolFrame frame)
        {
            if (frame == null || !frame.Opcode.HasValue || frame.Bytes == null) return;
            int opcode = (int)frame.Opcode.Value;
            bool begin = false;
            int currentGeneration;
            long selectedConnection;
            lock (syncRoot)
            {
                if (disposed) return;
                if (frame.Direction == TrafficDirection.ClientToServer && IsSequenceOpcode(opcode) &&
                    frame.ConnectionId != gameConnectionId && gameConnections.ContainsKey(frame.ConnectionId))
                {
                    gameConnectionId = frame.ConnectionId;
                    sequenceKnown = false;
                    nextSequence = 0;
                    currentMap = null;
                }
                if (frame.Direction == TrafficDirection.ClientToServer && frame.ConnectionId == gameConnectionId)
                {
                    ObserveSequenceLocked(opcode, frame.Bytes);
                    if (state == NpcCatalogHarvesterState.WaitingForGameConnection && sequenceKnown)
                    {
                        state = NpcCatalogHarvesterState.Traversing;
                        begin = true;
                    }
                }
                currentGeneration = generation;
                selectedConnection = gameConnectionId;
            }
            if (begin)
            {
                BeginTraversal(currentGeneration);
                return;
            }
            if (frame.Direction != TrafficDirection.ServerToClient || frame.ConnectionId != selectedConnection) return;

            if (opcode == TianshuRunLoopProtocol.ServerMapInfo)
            {
                RunLoopMapInfo map;
                if (!TianshuRunLoopProtocol.TryParseMapInfo(frame.Bytes, out map)) return;
                bool arrived;
                lock (syncRoot)
                {
                    if (disposed) return;
                    currentMap = map;
                    arrived = IsCurrentLocked(currentGeneration) && state == NpcCatalogHarvesterState.Traversing &&
                        currentDestination != null && map.MapId == currentDestination.MapId && !advanceScheduled;
                    if (arrived)
                    {
                        advanceScheduled = true;
                        DisposeTimerLocked();
                        ScheduleLocked(currentGeneration, EntitySettleDelayMs, AdvanceToNextMap);
                    }
                }
                if (arrived)
                    Publish(currentGeneration, NpcCatalogHarvesterState.Traversing,
                        "已到 " + map.MapName + "（ID " + map.MapId + "），等待实体表下盘后继续下一张地图。", false);
                return;
            }

            if (opcode == TianshuRunLoopProtocol.ServerEntityList ||
                opcode == TianshuRunLoopProtocol.ServerNearbyEntities)
            {
                LearnEntities(frame.Bytes);
            }
        }

        private void BeginTraversal(int expectedGeneration)
        {
            MapTeleportDestination destination;
            bool alreadyThere;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != NpcCatalogHarvesterState.Traversing ||
                    pendingMaps == null || currentIndex >= pendingMaps.Count) return;
                destination = pendingMaps[currentIndex].Clone();
                currentDestination = destination;
                advanceScheduled = false;
                alreadyThere = currentMap != null && currentMap.MapId == destination.MapId;
                DisposeTimerLocked();
            }
            Publish(expectedGeneration, NpcCatalogHarvesterState.Traversing,
                "目标 " + (currentIndex + 1) + "/" + pendingMaps.Count + "：" + destination.MapName +
                "（ID " + destination.MapId + "）" + (alreadyThere ? "，当前已在此地图，直接采集后继续。" : "。"), false);
            if (alreadyThere)
            {
                lock (syncRoot)
                {
                    if (!IsCurrentLocked(expectedGeneration)) return;
                    advanceScheduled = true;
                    ScheduleLocked(expectedGeneration, EntitySettleDelayMs, AdvanceToNextMap);
                }
                return;
            }
            SendSelectMap(expectedGeneration, destination);
        }

        private void SendSelectMap(int expectedGeneration, MapTeleportDestination destination)
        {
            long connectionId;
            uint sequence;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || gameConnectionId == 0 || !sequenceKnown) return;
                connectionId = gameConnectionId;
                sequence = nextSequence++;
            }
            byte[] packet = TianshuMapTeleportProtocol.BuildSelectMap(destination.MapId, sequence);
            if (!service.Send(connectionId, packet))
            {
                Fail(expectedGeneration, "发送地图选择帧失败；连接可能已关闭或 ACTIVE 已关闭。");
                return;
            }
            Publish(expectedGeneration, NpcCatalogHarvesterState.Traversing,
                "已发送地图选择帧 action=316，mapId=" + destination.MapId + "，seq=" + sequence + "。", false);
            ScheduleLockedThreadSafe(expectedGeneration, SelectToExecuteDelayMs, SendExecuteTeleport);
        }

        private void SendExecuteTeleport(int expectedGeneration)
        {
            MapTeleportDestination destination;
            long connectionId;
            uint sequence;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || gameConnectionId == 0 || !sequenceKnown ||
                    currentDestination == null) return;
                destination = currentDestination.Clone();
                connectionId = gameConnectionId;
                sequence = nextSequence++;
                ScheduleLocked(expectedGeneration, MapArrivalTimeoutMs, OnMapTimeout);
            }
            byte[] packet = TianshuMapTeleportProtocol.BuildTeleportToMap(destination.MapId, sequence);
            if (!service.Send(connectionId, packet))
            {
                Fail(expectedGeneration, "发送地图传送执行帧失败；连接可能已关闭或 ACTIVE 已关闭。");
                return;
            }
            Publish(expectedGeneration, NpcCatalogHarvesterState.Traversing,
                "已发送传送执行帧 action=172，mapId=" + destination.MapId + "，seq=" + sequence + "。", false);
        }

        private void AdvanceToNextMap(int expectedGeneration)
        {
            int next;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != NpcCatalogHarvesterState.Traversing) return;
                advanceScheduled = false;
                DisposeTimerLocked();
                currentIndex++;
                next = currentIndex;
            }
            if (next >= pendingMaps.Count)
            {
                Complete(expectedGeneration, "已遍历 " + pendingMaps.Count + " 张地图，采集完成。");
                return;
            }
            Publish(expectedGeneration, NpcCatalogHarvesterState.Traversing,
                "已完成 " + next + "/" + pendingMaps.Count + " 张地图，当前目录 " + entries.Count + " 条。", false);
            BeginTraversal(expectedGeneration);
        }

        private void LearnEntities(byte[] bytes)
        {
            IList<RunLoopEntity> entities = TianshuRunLoopProtocol.ExtractEntities(bytes);
            if (entities.Count == 0) return;
            int mapId;
            lock (syncRoot)
            {
                mapId = currentMap == null ? 0 : currentMap.MapId;
            }
            if (mapId <= 0) return;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(generation)) return;
                for (int i = 0; i < entities.Count; i++)
                {
                    RunLoopEntity entity = entities[i];
                    AddEntityLocked(entity.Name, entity.Id, mapId, entity.X, entity.Y);
                }
            }
        }

        private void AddEntityLocked(string name, int id, int mapId, int x, int y)
        {
            string normalized = TianshuRunLoopProtocol.NormalizeName(name);
            if (string.IsNullOrWhiteSpace(normalized) || id <= 0 || mapId <= 0) return;
            string key = normalized + "|" + mapId;
            NpcCatalogEntry existing;
            if (!entries.TryGetValue(key, out existing))
            {
                entries[key] = new NpcCatalogEntry
                {
                    Name = normalized,
                    NpcId = id,
                    MapId = mapId,
                    X = x,
                    Y = y
                };
                return;
            }
            existing.NpcId = id;
            existing.X = x;
            existing.Y = y;
        }

        private void LoadEntriesFromSessions()
        {
            try
            {
                string currentPath = Path.GetFullPath(service.DatabasePath);
                DirectoryInfo dataRoot = new DirectoryInfo(Path.GetDirectoryName(currentPath));
                while (dataRoot != null && !string.Equals(dataRoot.Name, "data", StringComparison.OrdinalIgnoreCase))
                    dataRoot = dataRoot.Parent;
                string searchRoot = dataRoot == null ? Path.GetDirectoryName(currentPath) : dataRoot.FullName;
                List<FileInfo> databases = new List<FileInfo>();
                if (Directory.Exists(searchRoot))
                {
                    foreach (string path in Directory.GetFiles(searchRoot, "*.sqlite", SearchOption.AllDirectories))
                        databases.Add(new FileInfo(path));
                }
                databases.Sort(delegate(FileInfo left, FileInfo right)
                {
                    return right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
                });
                int maximum = Math.Min(100, databases.Count);
                for (int index = 0; index < maximum; index++)
                {
                    int mapId = 0;
                    try
                    {
                        using (SQLiteConnection connection = new SQLiteConnection(
                            "Data Source=" + databases[index].FullName + ";Version=3;Read Only=True;Pooling=False;"))
                        {
                            connection.Open();
                            using (SQLiteCommand command = connection.CreateCommand())
                            {
                                command.CommandText =
                                    "SELECT direction,opcode,bytes FROM frames WHERE opcode IN (28,85,106) ORDER BY capture_ordinal;";
                                using (SQLiteDataReader reader = command.ExecuteReader())
                                {
                                    while (reader.Read())
                                    {
                                        int direction = Convert.ToInt32(reader[0]);
                                        int opcode = Convert.ToInt32(reader[1]);
                                        byte[] bytes = (byte[])reader[2];
                                        if (direction != (int)TrafficDirection.ServerToClient) continue;
                                        if (opcode == TianshuRunLoopProtocol.ServerMapInfo)
                                        {
                                            RunLoopMapInfo map;
                                            if (TianshuRunLoopProtocol.TryParseMapInfo(bytes, out map)) mapId = map.MapId;
                                            continue;
                                        }
                                        if (mapId <= 0) continue;
                                        IList<RunLoopEntity> entities = TianshuRunLoopProtocol.ExtractEntities(bytes);
                                        lock (syncRoot)
                                        {
                                            for (int i = 0; i < entities.Count; i++)
                                                AddEntityLocked(entities[i].Name, entities[i].Id, mapId, entities[i].X, entities[i].Y);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot preload NPC catalog from historical sessions", ex);
            }
        }

        private void LoadCatalog()
        {
            try
            {
                if (!File.Exists(catalogPath)) return;
                NpcCatalogDocument document = JsonConvert.DeserializeObject<NpcCatalogDocument>(File.ReadAllText(catalogPath));
                if (document == null || document.Entries == null) return;
                lock (syncRoot)
                {
                    for (int i = 0; i < document.Entries.Count; i++)
                    {
                        NpcCatalogEntry entry = document.Entries[i];
                        if (entry == null) continue;
                        AddEntityLocked(entry.Name, entry.NpcId, entry.MapId, entry.X, entry.Y);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot load NPC catalog " + catalogPath, ex);
            }
        }

        private void SaveCatalog()
        {
            List<NpcCatalogEntry> snapshot;
            lock (syncRoot)
            {
                snapshot = entries.Values.OrderBy(item => item.MapId).ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
            }
            string directory = Path.GetDirectoryName(catalogPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temporary = catalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary,
                    JsonConvert.SerializeObject(new NpcCatalogDocument { Entries = snapshot }, Formatting.Indented),
                    new System.Text.UTF8Encoding(false));
                if (File.Exists(catalogPath)) File.Replace(temporary, catalogPath, catalogPath + ".bak");
                else File.Move(temporary, catalogPath);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
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
            MapTeleportDestination destination = CurrentDestination;
            Fail(expectedGeneration, "等待到图超时：未收到目标地图 " +
                (destination == null ? "未知" : destination.MapName + "（ID " + destination.MapId + "）") +
                " 的 SC_MAP_INFO。已安全停止。");
        }

        private void Complete(int expectedGeneration, string message)
        {
            bool restore;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = NpcCatalogHarvesterState.Completed;
                DisposeTimerLocked();
                restore = activatedByAutomation;
                activatedByAutomation = false;
            }
            SaveCatalog();
            Publish(expectedGeneration, NpcCatalogHarvesterState.Completed,
                message + " 当前目录 " + LearnedEntries + " 条，已写入 " + catalogPath + "。", false);
            service.ReleaseAutomation(AutomationOwner);
            if (restore && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void Fail(int expectedGeneration, string message)
        {
            bool restore;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = NpcCatalogHarvesterState.Failed;
                DisposeTimerLocked();
                restore = activatedByAutomation;
                activatedByAutomation = false;
            }
            SaveCatalog();
            Publish(expectedGeneration, NpcCatalogHarvesterState.Failed, message, true);
            service.ReleaseAutomation(AutomationOwner);
            if (restore && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void StopCore(NpcCatalogHarvesterState finalState, string message, bool error)
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
            SaveCatalog();
            PublishAny(finalState, message, error);
            service.ReleaseAutomation(AutomationOwner);
            if (restore && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void OnActiveModeChanged(bool active)
        {
            if (!active && IsRunning(State))
                StopCore(NpcCatalogHarvesterState.Stopped, "ACTIVE/发包模式已关闭，NPC 目录采集同步停止。", false);
        }

        private void Publish(int expectedGeneration, NpcCatalogHarvesterState publishedState, string message, bool error)
        {
            lock (syncRoot) if (disposed || expectedGeneration != generation) return;
            PublishAny(publishedState, message, error);
        }

        private void PublishAny(NpcCatalogHarvesterState publishedState, string message, bool error)
        {
            Action<NpcCatalogHarvesterState, string> handler = StatusChanged;
            if (handler != null) handler(publishedState, message);
            long connectionId = GetGameConnectionId();
            service.ReportEngineEvent(AutomationOwner, error ? "ERROR" : "INFO", message,
                connectionId == 0 ? (long?)null : connectionId);
        }

        private void ScheduleLockedThreadSafe(int expectedGeneration, int delayMs, Action<int> callback)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                ScheduleLocked(expectedGeneration, delayMs, callback);
            }
        }

        private void ScheduleLocked(int expectedGeneration, int delayMs, Action<int> callback)
        {
            DisposeTimerLocked();
            stageTimer = new System.Threading.Timer(delegate { callback(expectedGeneration); }, null, delayMs, Timeout.Infinite);
        }

        private void DisposeTimerLocked()
        {
            if (stageTimer != null) { stageTimer.Dispose(); stageTimer = null; }
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

        private static bool IsRunning(NpcCatalogHarvesterState value)
        {
            return value != NpcCatalogHarvesterState.Inactive && value != NpcCatalogHarvesterState.Completed &&
                value != NpcCatalogHarvesterState.Stopped && value != NpcCatalogHarvesterState.Failed;
        }

        private static bool IsSequenceOpcode(int opcode)
        {
            return opcode == TianshuBountyProtocol.ClientTravelLink || opcode == TianshuBountyProtocol.ClientNpcOpen ||
                opcode == TianshuBountyProtocol.ClientNpcFunction || opcode == TianshuBountyProtocol.ClientDialogResponse ||
                opcode == TianshuRunLoopProtocol.ClientMovement || opcode == TianshuRunLoopProtocol.ClientUiAction ||
                opcode == TianshuMapTeleportProtocol.ClientUiAction || opcode == 0x0042 || opcode == 0x001B;
        }
    }

    public sealed class NpcCatalogHarvesterControl : UserControl
    {
        private readonly NpcCatalogHarvesterCoordinator coordinator;
        private readonly Button startButton;
        private readonly Button stopButton;
        private readonly Label stateLabel;
        private readonly Label progressLabel;
        private readonly Label catalogLabel;

        public NpcCatalogHarvesterControl(NpcCatalogHarvesterCoordinator coordinator)
        {
            if (coordinator == null) throw new ArgumentNullException("coordinator");
            this.coordinator = coordinator;
            Dock = DockStyle.Fill;

            Label help = new Label
            {
                AutoSize = false,
                Height = 54,
                Dock = DockStyle.Top,
                Text = "遍历目录中的已录制地图，使用两包直传接口逐图到达，等待 SC_MAP_INFO 后自动采集 SC_MAP_ENTITY_LIST / SC_NEARBY_ENTITY_UPDATE，并把 name/id/mapId/x/y 五元组写入 data/npc-catalog.json。运行前请确保游戏角色已登录。"
            };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, AutoSize = false };
            startButton = new Button { Text = "开始采集", AutoSize = true };
            stopButton = new Button { Text = "停止", AutoSize = true, Enabled = false };
            startButton.Click += OnStart;
            stopButton.Click += delegate { coordinator.Stop(); };
            buttons.Controls.Add(startButton);
            buttons.Controls.Add(stopButton);

            stateLabel = new Label { Dock = DockStyle.Top, Height = 24, Text = "状态：Inactive" };
            progressLabel = new Label { Dock = DockStyle.Top, Height = 24, Text = "进度：等待开始" };
            catalogLabel = new Label { Dock = DockStyle.Top, Height = 36, AutoEllipsis = true, Text = "目录文件：" + coordinator.CatalogPath };

            Controls.Add(catalogLabel);
            Controls.Add(progressLabel);
            Controls.Add(stateLabel);
            Controls.Add(buttons);
            Controls.Add(help);

            coordinator.StatusChanged += OnStatusChanged;
            OnStatusChanged(coordinator.State, "尚未开始采集。");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) coordinator.StatusChanged -= OnStatusChanged;
            base.Dispose(disposing);
        }

        private void OnStart(object sender, EventArgs e)
        {
            try { coordinator.Start(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "NPC 目录采集", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void OnStatusChanged(NpcCatalogHarvesterState automationState, string message)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<NpcCatalogHarvesterState, string>(OnStatusChanged), automationState, message); }
                catch (InvalidOperationException) { }
                return;
            }
            bool running = automationState != NpcCatalogHarvesterState.Inactive &&
                automationState != NpcCatalogHarvesterState.Completed &&
                automationState != NpcCatalogHarvesterState.Stopped &&
                automationState != NpcCatalogHarvesterState.Failed;
            startButton.Enabled = !running;
            stopButton.Enabled = running;
            stateLabel.Text = "状态：" + automationState;
            progressLabel.Text = "进度：" + coordinator.VisitedMaps + "/" + coordinator.TotalMaps +
                " 张地图，目录 " + coordinator.LearnedEntries + " 条";
            MapTeleportDestination destination = coordinator.CurrentDestination;
            catalogLabel.Text = destination == null
                ? "目录文件：" + coordinator.CatalogPath
                : "当前地图：" + destination.MapName + "（ID " + destination.MapId + "）｜目录文件：" + coordinator.CatalogPath;
        }
    }
}
