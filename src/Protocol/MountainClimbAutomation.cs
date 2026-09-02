using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TianshuQitanLauncher.Protocol
{
    public enum MountainClimbAutomationState
    {
        Inactive = 0,
        WaitingForGameConnection = 1,
        Traveling = 2,
        ConfirmingTeleport = 3,
        WaitingForMap = 4,
        Walking = 5,
        OpeningNpc = 6,
        WaitingForNpcDialog = 7,
        SelectingFunction = 8,
        WaitingForQuestDetail = 9,
        WaitingForTaskUpdate = 10,
        Completed = 11,
        Stopped = 12,
        Failed = 13
    }

    public static class TianshuMountainClimbProtocol
    {
        public const int ClientQuestAction = 0x0018;
        public const int ServerQuestDetail = 0x001E;
        public const int ServerTaskListEntry = 0x0027;
        public const int ServerTaskRemoved = 0x003F;

        public const string BraveTowerTaskId = "9382902";
        public const string FirstTowerTaskId = "9382900";
        public const string RainMountainTaskId = "9382901";

        public const string BraveTowerAcceptFunction = "9382902.1";
        public const string FirstTowerAcceptFunction = "9382900.1";
        public const string RainMountainAcceptFunction = "9382901.1";
        public const string BraveTowerTurnInFunction = "9382902.2";
        public const string FirstTowerTurnInFunction = "9382900.2";
        public const string RainMountainTurnInFunction = "9382901.2";

        public const int TowerTeleporterNpcId = 8054;
        public const string BraveTowerDirectTeleportFunction = "9992040";
        public const int BraveTowerDestinationMapId = 65;

        public static byte[] BuildQuestAction(int npcId, string functionId, bool turnIn, uint sequence)
        {
            if (npcId <= 0) throw new ArgumentOutOfRangeException("npcId");
            if (string.IsNullOrWhiteSpace(functionId)) throw new ArgumentException("Function ID is required.", "functionId");
            PacketWriter writer = new PacketWriter(ClientQuestAction);
            writer.WriteUInt32((uint)npcId);
            writer.WriteString(functionId);
            writer.WriteUInt16((ushort)(turnIn ? 1 : 0));
            writer.WriteUInt32(0);
            writer.WriteUInt32(sequence);
            return writer.ToArray();
        }

        public static bool TryParseQuestAction(byte[] bytes, out int npcId, out string functionId,
            out bool turnIn, out uint sequence)
        {
            npcId = 0;
            functionId = null;
            turnIn = false;
            sequence = 0;
            if (!TianshuBountyProtocol.HasOpcode(bytes, ClientQuestAction) || bytes.Length < 20) return false;
            int offset = 4;
            npcId = (int)ReadUInt32(bytes, offset);
            offset += 4;
            int stringLength = ReadUInt16(bytes, offset);
            offset += 2;
            if (stringLength <= 0 || offset + stringLength + 10 != bytes.Length) return false;
            try { functionId = new UTF8Encoding(false, true).GetString(bytes, offset, stringLength); }
            catch (DecoderFallbackException) { return false; }
            offset += stringLength;
            ushort action = ReadUInt16(bytes, offset);
            offset += 2;
            if (action > 1 || ReadUInt32(bytes, offset) != 0) return false;
            offset += 4;
            sequence = ReadUInt32(bytes, offset);
            turnIn = action == 1;
            return npcId > 0;
        }

        public static bool IsQuestDetail(byte[] bytes, string functionId)
        {
            return TianshuBountyProtocol.HasOpcode(bytes, ServerQuestDetail) &&
                TianshuBountyProtocol.ContainsText(bytes, functionId);
        }

        public static bool IsTaskAccepted(byte[] bytes, string taskId)
        {
            return TianshuBountyProtocol.HasOpcode(bytes, TianshuBountyProtocol.ServerTaskUpdate) &&
                TianshuBountyProtocol.ContainsText(bytes, taskId);
        }

        public static bool IsTaskListEntry(byte[] bytes, string taskId)
        {
            return TianshuBountyProtocol.HasOpcode(bytes, ServerTaskListEntry) &&
                TianshuBountyProtocol.ContainsText(bytes, taskId);
        }

        public static bool IsTaskRemoved(byte[] bytes, string taskId)
        {
            return TianshuBountyProtocol.HasOpcode(bytes, ServerTaskRemoved) &&
                TianshuBountyProtocol.ContainsText(bytes, taskId);
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
                ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private sealed class PacketWriter
        {
            private readonly MemoryStream stream = new MemoryStream();

            public PacketWriter(int opcode)
            {
                stream.WriteByte(0);
                stream.WriteByte(0);
                WriteUInt16((ushort)opcode);
            }

            public void WriteUInt16(ushort value)
            {
                stream.WriteByte((byte)(value >> 8));
                stream.WriteByte((byte)value);
            }

            public void WriteUInt32(uint value)
            {
                stream.WriteByte((byte)(value >> 24));
                stream.WriteByte((byte)(value >> 16));
                stream.WriteByte((byte)(value >> 8));
                stream.WriteByte((byte)value);
            }

            public void WriteString(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                if (bytes.Length > UInt16.MaxValue) throw new ArgumentOutOfRangeException("value");
                WriteUInt16((ushort)bytes.Length);
                stream.Write(bytes, 0, bytes.Length);
            }

            public byte[] ToArray()
            {
                byte[] bytes = stream.ToArray();
                bytes[0] = (byte)(bytes.Length >> 8);
                bytes[1] = (byte)bytes.Length;
                return bytes;
            }
        }
    }

    public sealed class MountainClimbAutomationCoordinator : IDisposable
    {
        private const string AutomationOwner = "AutoMountainClimb";
        private const int TaskGiverNpcId = 93063;
        private const int MovementIntervalMs = 500;

        private enum ActionKind { Travel, Portal, Walk, NpcTeleport, Quest }

        private sealed class RoutePoint
        {
            public ushort X;
            public ushort Y;
        }

        private sealed class MountainAction
        {
            public ActionKind Kind;
            public string Description;
            public BountyTravelTarget Target;
            public int MapId;
            public int ExpectedMapId;
            public int NpcId;
            public string FunctionId;
            public string TaskId;
            public bool TurnIn;
            public IList<RoutePoint> Route;
        }

        private readonly object syncRoot = new object();
        private readonly ProtocolWorkbenchService service;
        private readonly Dictionary<long, ConnectionSession> gameConnections = new Dictionary<long, ConnectionSession>();
        private readonly HashSet<string> knownActiveTasks = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> knownCompletedTasks = new HashSet<string>(StringComparer.Ordinal);
        private List<MountainAction> actions = new List<MountainAction>();
        private System.Threading.Timer stageTimer;
        private System.Threading.Timer walkTimer;
        private MountainClimbAutomationState state;
        private int actionIndex;
        private int walkIndex;
        private long gameConnectionId;
        private uint nextSequence;
        private bool sequenceKnown;
        private int currentMapId;
        private string currentMapName;
        private ushort currentScaledX;
        private ushort currentScaledY;
        private bool activatedByAutomation;
        private int generation;
        private bool disposed;

        public event Action<MountainClimbAutomationState, string> StatusChanged;

        public MountainClimbAutomationCoordinator(ProtocolWorkbenchService service)
        {
            if (service == null) throw new ArgumentNullException("service");
            this.service = service;
            state = MountainClimbAutomationState.Inactive;
            service.ConnectionChanged += OnConnectionChanged;
            service.FrameCaptured += OnFrameCaptured;
            service.ActiveModeChanged += OnActiveModeChanged;
        }

        public MountainClimbAutomationState State { get { lock (syncRoot) return state; } }
        public int CompletedActions { get { lock (syncRoot) return Math.Min(actionIndex, actions.Count); } }
        public int TotalActions { get { lock (syncRoot) return actions.Count; } }
        public string CurrentAction
        {
            get
            {
                lock (syncRoot)
                    return actionIndex >= 0 && actionIndex < actions.Count ? actions[actionIndex].Description : string.Empty;
            }
        }
        public string CurrentMapName { get { lock (syncRoot) return currentMapName ?? string.Empty; } }

        public void Start()
        {
            string currentOwner;
            if (!service.TryAcquireAutomation(AutomationOwner, out currentOwner))
                throw new InvalidOperationException("当前已有自动化流程正在运行：" + currentOwner + "。请先停止后再启动登山爬塔。");

            bool enableActive;
            bool canBegin;
            bool taskStatesKnown;
            bool braveTowerCompleted;
            bool firstTowerCompleted;
            bool rainMountainCompleted;
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
                DisposeTimersLocked();
                taskStatesKnown = HasAllTaskStatesLocked();
                braveTowerCompleted = knownCompletedTasks.Contains(TianshuMountainClimbProtocol.BraveTowerTaskId);
                firstTowerCompleted = knownCompletedTasks.Contains(TianshuMountainClimbProtocol.FirstTowerTaskId);
                rainMountainCompleted = knownCompletedTasks.Contains(TianshuMountainClimbProtocol.RainMountainTaskId);
                actions = BuildActions(taskStatesKnown, braveTowerCompleted, firstTowerCompleted, rainMountainCompleted);
                actionIndex = 0;
                walkIndex = 0;
                enableActive = !service.ActiveMode;
                activatedByAutomation = enableActive;
                canBegin = gameConnectionId != 0 && sequenceKnown;
                state = canBegin ? MountainClimbAutomationState.Traveling : MountainClimbAutomationState.WaitingForGameConnection;
            }
            if (enableActive) service.SetActiveMode(true, false);
            Publish(currentGeneration, state,
                "登山爬塔已启动；" + (taskStatesKnown ? "已确认三项任务的进行/完成状态，继续未完成路线。" :
                "先前往三界关高攀攀，缺少的任务会自动接取。") + " 全程按录制帧执行，结束返回皇城皇宫。", false);
            if (canBegin) ExecuteCurrentAction(currentGeneration);
        }

        public void Stop()
        {
            StopCore(MountainClimbAutomationState.Stopped, "登山爬塔已停止。", false);
        }

        public void Dispose()
        {
            bool restore;
            lock (syncRoot)
            {
                if (disposed) return;
                disposed = true;
                generation++;
                DisposeTimersLocked();
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
                }
                if (state == MountainClimbAutomationState.WaitingForGameConnection && gameConnectionId != 0 && sequenceKnown)
                {
                    state = MountainClimbAutomationState.Traveling;
                    currentGeneration = generation;
                    begin = true;
                }
            }
            if (begin) ExecuteCurrentAction(currentGeneration);
        }

        private void OnFrameCaptured(ProtocolFrame frame)
        {
            if (frame == null || !frame.Opcode.HasValue || frame.Bytes == null) return;
            int opcode = (int)frame.Opcode.Value;
            bool begin = false;
            int currentGeneration;
            lock (syncRoot)
            {
                if (disposed) return;
                if (frame.Direction == TrafficDirection.ClientToServer && IsSequenceOpcode(opcode) &&
                    frame.ConnectionId != gameConnectionId && gameConnections.ContainsKey(frame.ConnectionId))
                {
                    gameConnectionId = frame.ConnectionId;
                    sequenceKnown = false;
                    nextSequence = 0;
                }
                if (frame.Direction == TrafficDirection.ClientToServer && frame.ConnectionId == gameConnectionId)
                {
                    ObserveSequenceLocked(opcode, frame.Bytes);
                    long ignoredTimestamp;
                    int mapId;
                    ushort x;
                    ushort y;
                    if (TianshuRunLoopProtocol.TryParseMovement(frame.Bytes, out ignoredTimestamp, out mapId, out x, out y))
                    {
                        currentMapId = mapId;
                        currentScaledX = x;
                        currentScaledY = y;
                    }
                    if (state == MountainClimbAutomationState.WaitingForGameConnection && sequenceKnown)
                    {
                        state = MountainClimbAutomationState.Traveling;
                        begin = true;
                    }
                }
                currentGeneration = generation;
            }
            if (begin) { ExecuteCurrentAction(currentGeneration); return; }
            if (frame.Direction != TrafficDirection.ServerToClient || frame.ConnectionId != GetGameConnectionId()) return;

            TrackTaskState(opcode, frame.Bytes);
            if (!IsRunning(State)) return;

            if (opcode == TianshuRunLoopProtocol.ServerMapInfo)
            {
                RunLoopMapInfo map;
                if (TianshuRunLoopProtocol.TryParseMapInfo(frame.Bytes, out map)) HandleMap(currentGeneration, map);
                return;
            }
            if (opcode == TianshuBountyProtocol.ServerConfirmationDialog)
            {
                HandleConfirmation(currentGeneration, frame.Bytes);
                return;
            }
            if (opcode == TianshuBountyProtocol.ServerNpcDialog)
            {
                HandleNpcDialog(currentGeneration, frame.Bytes);
                return;
            }
            if (opcode == TianshuMountainClimbProtocol.ServerQuestDetail)
            {
                HandleQuestDetail(currentGeneration, frame.Bytes);
                return;
            }
            if (opcode == TianshuBountyProtocol.ServerTaskUpdate || opcode == TianshuMountainClimbProtocol.ServerTaskRemoved)
            {
                HandleQuestResult(currentGeneration, frame.Bytes);
                return;
            }
            if (opcode == TianshuBountyProtocol.ServerSystemMessage)
            {
                if (TianshuBountyProtocol.ContainsText(frame.Bytes, "背包没有足够") ||
                    TianshuBountyProtocol.ContainsText(frame.Bytes, "背包已满"))
                {
                    Fail(currentGeneration, "交任务失败：背包空间不足。已安全停止，未自动丢弃任何物品；清理背包后可重新启动。");
                }
                else if (TianshuBountyProtocol.ContainsText(frame.Bytes, "尚未开启该地图传送点"))
                {
                    Fail(currentGeneration, "服务端拒绝地图传送。山路动作不会改用未验证的飞行包，流程已停止。");
                }
            }
        }

        private void ExecuteCurrentAction(int expectedGeneration)
        {
            MountainAction action;
            bool skipKnownTask = false;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                if (actionIndex >= actions.Count)
                {
                    action = null;
                }
                else
                {
                    action = actions[actionIndex];
                    skipKnownTask = action.Kind == ActionKind.Quest && !action.TurnIn &&
                        (knownActiveTasks.Contains(action.TaskId) || knownCompletedTasks.Contains(action.TaskId));
                }
            }
            if (action == null)
            {
                Complete(expectedGeneration, "三项登山任务均已交付，并已返回皇城皇宫。");
                return;
            }
            if (skipKnownTask)
            {
                Publish(expectedGeneration, MountainClimbAutomationState.WaitingForTaskUpdate,
                    "服务端任务列表已存在“" + action.Description + "”，跳过重复接取。", false);
                ScheduleAction(expectedGeneration, 80, CompleteCurrentAction);
                return;
            }

            switch (action.Kind)
            {
                case ActionKind.Travel:
                    BeginTravel(expectedGeneration, action);
                    break;
                case ActionKind.Portal:
                    BeginPortal(expectedGeneration, action);
                    break;
                case ActionKind.Walk:
                    BeginWalk(expectedGeneration, action);
                    break;
                case ActionKind.NpcTeleport:
                case ActionKind.Quest:
                    BeginNpcAction(expectedGeneration, action);
                    break;
            }
        }

        private void BeginTravel(int expectedGeneration, MountainAction action)
        {
            lock (syncRoot)
            {
                if (!IsCurrentActionLocked(expectedGeneration, action)) return;
                state = MountainClimbAutomationState.Traveling;
                ScheduleStageLocked(expectedGeneration, 30000, OnStageTimeout);
            }
            Publish(expectedGeneration, MountainClimbAutomationState.Traveling, action.Description + "。", false);
            SendPacket(expectedGeneration, action.Description, delegate(uint sequence)
            {
                return TianshuBountyProtocol.BuildTravelRequest(action.Target, sequence);
            });
        }

        private void BeginPortal(int expectedGeneration, MountainAction action)
        {
            lock (syncRoot)
            {
                if (!IsCurrentActionLocked(expectedGeneration, action)) return;
                state = MountainClimbAutomationState.WaitingForMap;
                ScheduleStageLocked(expectedGeneration, 20000, OnStageTimeout);
            }
            Publish(expectedGeneration, MountainClimbAutomationState.WaitingForMap, action.Description + "。", false);
            SendPacket(expectedGeneration, action.Description, delegate(uint sequence)
            {
                return TianshuBountyProtocol.BuildNpcOpen(action.NpcId, sequence);
            });
        }

        private void BeginWalk(int expectedGeneration, MountainAction action)
        {
            string mismatch = null;
            lock (syncRoot)
            {
                if (!IsCurrentActionLocked(expectedGeneration, action)) return;
                if (currentMapId != action.MapId)
                    mismatch = "走步路线要求地图 " + action.MapId + "，当前服务端地图为 " + currentMapId + "（" + (currentMapName ?? "未知") + "）。";
                else
                {
                    state = MountainClimbAutomationState.Walking;
                    walkIndex = 0;
                    DisposeStageTimerLocked();
                    if (walkTimer != null) walkTimer.Dispose();
                    int timeout = Math.Max(15000, action.Route.Count * MovementIntervalMs + 12000);
                    ScheduleStageLocked(expectedGeneration, timeout, OnStageTimeout);
                    walkTimer = new System.Threading.Timer(delegate { SendWalkPoint(expectedGeneration); }, null, 120, MovementIntervalMs);
                }
            }
            if (mismatch != null) { Fail(expectedGeneration, mismatch + " 已停止，避免跨图发送走步帧。"); return; }
            Publish(expectedGeneration, MountainClimbAutomationState.Walking,
                action.Description + "，按录制轨迹发送 " + action.Route.Count + " 个走步帧。", false);
        }

        private void BeginNpcAction(int expectedGeneration, MountainAction action)
        {
            lock (syncRoot)
            {
                if (!IsCurrentActionLocked(expectedGeneration, action)) return;
                state = MountainClimbAutomationState.WaitingForNpcDialog;
                ScheduleStageLocked(expectedGeneration, 12000, OnStageTimeout);
            }
            Publish(expectedGeneration, MountainClimbAutomationState.OpeningNpc, action.Description + "。", false);
            SendPacket(expectedGeneration, "访问 NPC " + action.NpcId, delegate(uint sequence)
            {
                return TianshuBountyProtocol.BuildNpcOpen(action.NpcId, sequence);
            });
        }

        private void HandleConfirmation(int expectedGeneration, byte[] bytes)
        {
            BountyConfirmationDialog dialog;
            if (!TianshuBountyProtocol.TryParseConfirmationDialog(bytes, out dialog)) return;
            MountainAction action;
            lock (syncRoot)
            {
                action = GetCurrentActionLocked();
                if (!IsCurrentActionLocked(expectedGeneration, action) || action.Kind != ActionKind.Travel ||
                    state != MountainClimbAutomationState.Traveling ||
                    (dialog.Title ?? string.Empty).IndexOf("传送", StringComparison.Ordinal) < 0) return;
                state = MountainClimbAutomationState.ConfirmingTeleport;
                DisposeStageTimerLocked();
            }
            Publish(expectedGeneration, MountainClimbAutomationState.ConfirmingTeleport,
                "已取得本次服务端动态确认串，确认传送。", false);
            ScheduleAction(expectedGeneration, 160, delegate(int value)
            {
                lock (syncRoot)
                {
                    if (!IsCurrentActionLocked(value, action) || state != MountainClimbAutomationState.ConfirmingTeleport) return;
                    state = MountainClimbAutomationState.WaitingForMap;
                    ScheduleStageLocked(value, 30000, OnStageTimeout);
                }
                SendPacket(value, "确认传送", delegate(uint sequence)
                {
                    return TianshuBountyProtocol.BuildDialogResponse(dialog.ContextId, dialog.Token, true, sequence);
                });
            });
        }

        private void HandleMap(int expectedGeneration, RunLoopMapInfo map)
        {
            MountainAction action;
            bool matched = false;
            lock (syncRoot)
            {
                currentMapId = map.MapId;
                currentMapName = map.MapName;
                currentScaledX = map.ScaledX;
                currentScaledY = map.ScaledY;
                action = GetCurrentActionLocked();
                if (!IsCurrentActionLocked(expectedGeneration, action)) return;
                if (action.Kind == ActionKind.Travel)
                    matched = (state == MountainClimbAutomationState.Traveling ||
                        state == MountainClimbAutomationState.ConfirmingTeleport ||
                        state == MountainClimbAutomationState.WaitingForMap) && map.MapId == action.Target.MapId;
                else if (action.Kind == ActionKind.Portal || action.Kind == ActionKind.NpcTeleport)
                    matched = state == MountainClimbAutomationState.WaitingForMap && map.MapId == action.ExpectedMapId;
            }
            if (matched)
            {
                Publish(expectedGeneration, MountainClimbAutomationState.WaitingForMap,
                    "服务端确认到达 " + map.MapName + "（地图 " + map.MapId + "）。", false);
                CompleteCurrentAction(expectedGeneration);
            }
        }

        private void HandleNpcDialog(int expectedGeneration, byte[] bytes)
        {
            MountainAction action;
            lock (syncRoot)
            {
                action = GetCurrentActionLocked();
                if (!IsCurrentActionLocked(expectedGeneration, action) ||
                    (action.Kind != ActionKind.NpcTeleport && action.Kind != ActionKind.Quest) ||
                    state != MountainClimbAutomationState.WaitingForNpcDialog) return;
            }
            if (!TianshuRunLoopProtocol.ContainsFunction(bytes, action.NpcId, action.FunctionId)) return;
            lock (syncRoot)
            {
                if (!IsCurrentActionLocked(expectedGeneration, action) || state != MountainClimbAutomationState.WaitingForNpcDialog) return;
                state = MountainClimbAutomationState.SelectingFunction;
                DisposeStageTimerLocked();
            }
            Publish(expectedGeneration, MountainClimbAutomationState.SelectingFunction,
                "NPC 功能已由服务端确认，选择 " + action.FunctionId + "。", false);
            ScheduleAction(expectedGeneration, 180, delegate(int value)
            {
                lock (syncRoot)
                {
                    if (!IsCurrentActionLocked(value, action) || state != MountainClimbAutomationState.SelectingFunction) return;
                    state = action.Kind == ActionKind.Quest ? MountainClimbAutomationState.WaitingForQuestDetail :
                        MountainClimbAutomationState.WaitingForMap;
                    ScheduleStageLocked(value, action.Kind == ActionKind.Quest ? 12000 : 30000, OnStageTimeout);
                }
                SendPacket(value, "选择 NPC 功能 " + action.FunctionId, delegate(uint sequence)
                {
                    return TianshuBountyProtocol.BuildNpcFunction(action.NpcId, action.FunctionId, sequence);
                });
            });
        }

        private void HandleQuestDetail(int expectedGeneration, byte[] bytes)
        {
            MountainAction action;
            lock (syncRoot)
            {
                action = GetCurrentActionLocked();
                if (!IsCurrentActionLocked(expectedGeneration, action) || action.Kind != ActionKind.Quest ||
                    state != MountainClimbAutomationState.WaitingForQuestDetail) return;
            }
            if (!TianshuMountainClimbProtocol.IsQuestDetail(bytes, action.FunctionId)) return;
            lock (syncRoot)
            {
                if (!IsCurrentActionLocked(expectedGeneration, action) || state != MountainClimbAutomationState.WaitingForQuestDetail) return;
                state = MountainClimbAutomationState.WaitingForTaskUpdate;
                DisposeStageTimerLocked();
            }
            Publish(expectedGeneration, MountainClimbAutomationState.WaitingForTaskUpdate,
                "任务详情已确认，发送 0x0018 " + (action.TurnIn ? "交付" : "接取") + "帧。", false);
            ScheduleAction(expectedGeneration, 180, delegate(int value)
            {
                lock (syncRoot)
                {
                    if (!IsCurrentActionLocked(value, action) || state != MountainClimbAutomationState.WaitingForTaskUpdate) return;
                    ScheduleStageLocked(value, 15000, OnStageTimeout);
                }
                SendPacket(value, (action.TurnIn ? "交付" : "接取") + "任务 " + action.TaskId, delegate(uint sequence)
                {
                    return TianshuMountainClimbProtocol.BuildQuestAction(action.NpcId, action.FunctionId, action.TurnIn, sequence);
                });
            });
        }

        private void HandleQuestResult(int expectedGeneration, byte[] bytes)
        {
            MountainAction action;
            lock (syncRoot)
            {
                action = GetCurrentActionLocked();
                if (!IsCurrentActionLocked(expectedGeneration, action) || action.Kind != ActionKind.Quest ||
                    state != MountainClimbAutomationState.WaitingForTaskUpdate) return;
            }
            bool matched = action.TurnIn
                ? TianshuMountainClimbProtocol.IsTaskRemoved(bytes, action.TaskId)
                : TianshuMountainClimbProtocol.IsTaskAccepted(bytes, action.TaskId);
            if (!matched) return;
            Publish(expectedGeneration, MountainClimbAutomationState.WaitingForTaskUpdate,
                "服务端确认任务“" + action.Description + "”" + (action.TurnIn ? "已交付。" : "已接取。"), false);
            CompleteCurrentAction(expectedGeneration);
        }

        private void SendWalkPoint(int expectedGeneration)
        {
            MountainAction action;
            RoutePoint point;
            bool finalPoint;
            int number;
            lock (syncRoot)
            {
                action = GetCurrentActionLocked();
                if (!IsCurrentActionLocked(expectedGeneration, action) || action.Kind != ActionKind.Walk ||
                    state != MountainClimbAutomationState.Walking || walkIndex >= action.Route.Count) return;
                point = action.Route[walkIndex++];
                number = walkIndex;
                currentScaledX = point.X;
                currentScaledY = point.Y;
                finalPoint = walkIndex >= action.Route.Count;
                if (finalPoint && walkTimer != null)
                {
                    walkTimer.Dispose();
                    walkTimer = null;
                }
            }
            long epoch = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            SendPacket(expectedGeneration, action.Description + " 走步 " + number + "/" + action.Route.Count,
                delegate(uint sequence)
                {
                    return TianshuRunLoopProtocol.BuildMovement(epoch, action.MapId, point.X, point.Y, sequence);
                });
            if (finalPoint) ScheduleAction(expectedGeneration, 420, CompleteCurrentAction);
        }

        private void CompleteCurrentAction(int expectedGeneration)
        {
            MountainAction completed;
            bool finished;
            lock (syncRoot)
            {
                completed = GetCurrentActionLocked();
                if (!IsCurrentActionLocked(expectedGeneration, completed)) return;
                DisposeTimersLocked();
                actionIndex++;
                finished = actionIndex >= actions.Count;
            }
            if (completed != null)
                Publish(expectedGeneration, state, "完成：" + completed.Description + "。", false);
            if (finished)
            {
                Complete(expectedGeneration, "三项登山任务均已交付，并已返回皇城皇宫。");
                return;
            }
            ScheduleAction(expectedGeneration, 320, ExecuteCurrentAction);
        }

        private void TrackTaskState(int opcode, byte[] bytes)
        {
            string[] ids = { TianshuMountainClimbProtocol.BraveTowerTaskId,
                TianshuMountainClimbProtocol.FirstTowerTaskId, TianshuMountainClimbProtocol.RainMountainTaskId };
            lock (syncRoot)
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    if ((opcode == TianshuBountyProtocol.ServerTaskUpdate &&
                        TianshuMountainClimbProtocol.IsTaskAccepted(bytes, ids[i])) ||
                        (opcode == TianshuMountainClimbProtocol.ServerTaskListEntry &&
                        TianshuMountainClimbProtocol.IsTaskListEntry(bytes, ids[i])))
                        knownActiveTasks.Add(ids[i]);
                    else if (opcode == TianshuMountainClimbProtocol.ServerTaskRemoved &&
                        TianshuMountainClimbProtocol.IsTaskRemoved(bytes, ids[i]))
                    {
                        knownActiveTasks.Remove(ids[i]);
                        knownCompletedTasks.Add(ids[i]);
                    }
                }
            }
        }

        private bool HasAllTaskStatesLocked()
        {
            return IsTaskKnownLocked(TianshuMountainClimbProtocol.BraveTowerTaskId) &&
                IsTaskKnownLocked(TianshuMountainClimbProtocol.FirstTowerTaskId) &&
                IsTaskKnownLocked(TianshuMountainClimbProtocol.RainMountainTaskId);
        }

        private bool IsTaskKnownLocked(string taskId)
        {
            return knownActiveTasks.Contains(taskId) || knownCompletedTasks.Contains(taskId);
        }

        private List<MountainAction> BuildActions(bool taskStatesKnown, bool braveTowerCompleted,
            bool firstTowerCompleted, bool rainMountainCompleted)
        {
            List<MountainAction> result = new List<MountainAction>();
            if (!taskStatesKnown)
            {
                AddTravel(result, "飞行到三界关高攀攀", TaskGiverNpcId, 86, 36, 14);
                AddQuest(result, "勇闯通天塔", TaskGiverNpcId, TianshuMountainClimbProtocol.BraveTowerTaskId,
                    TianshuMountainClimbProtocol.BraveTowerAcceptFunction, false);
                AddQuest(result, "初探通天塔", TaskGiverNpcId, TianshuMountainClimbProtocol.FirstTowerTaskId,
                    TianshuMountainClimbProtocol.FirstTowerAcceptFunction, false);
                AddQuest(result, "勇攀时雨山", TaskGiverNpcId, TianshuMountainClimbProtocol.RainMountainTaskId,
                    TianshuMountainClimbProtocol.RainMountainAcceptFunction, false);
            }

            IList<RoutePoint> towerFloorRoute = Route(
                "352,656 416,688 480,720 544,752 608,752 672,720 736,688 800,656 896,608 960,576 " +
                "1024,544 1088,512 1152,480 1088,416 992,400 960,384 896,352 800,304 736,272 " +
                "672,240 608,208 544,208 480,240 416,272 352,304 288,336 160,336");

            if (!braveTowerCompleted)
            {
                AddTravel(result, "飞行到灵昌城皇宫入口", 14, 12, 24, 28);
                AddPortal(result, "进入皇城皇宫", 14, 13);
                AddTravel(result, "飞行到皇宫通天塔入口", 196, 13, 8, 84);
                AddPortal(result, "进入通天塔一层", 196, 25);
                AddNpcTeleport(result, "通过通天塔传送人直达无间境十层",
                    TianshuMountainClimbProtocol.TowerTeleporterNpcId,
                    TianshuMountainClimbProtocol.BraveTowerDirectTeleportFunction,
                    TianshuMountainClimbProtocol.BraveTowerDestinationMapId);
                AddWalk(result, "无间境十层前往酷酷探险者", 65, Route(
                    "416,688 480,720 544,752 640,736 704,704 768,672 800,656 896,608 960,576 1024,544 " +
                    "1088,512 1152,480 1088,416 992,400 960,384 896,352 800,304 768,288 672,240 608,208 " +
                    "544,208 480,240 384,256 288,208"));
                AddQuest(result, "勇闯通天塔", 8075, TianshuMountainClimbProtocol.BraveTowerTaskId,
                    TianshuMountainClimbProtocol.BraveTowerTurnInFunction, true);
                AddWalk(result, "无间境十层返回皇宫出口", 65, Route(
                    "416,208 480,208 544,208 608,208 544,208 416,240 352,272 307,270 320,256"));
                AddPortal(result, "返回皇城皇宫", 214, 13);
            }

            if (!firstTowerCompleted)
            {
                if (braveTowerCompleted)
                {
                    AddTravel(result, "飞行到灵昌城皇宫入口", 14, 12, 24, 28);
                    AddPortal(result, "进入皇城皇宫", 14, 13);
                    AddTravel(result, "飞行到皇宫通天塔入口", 196, 13, 8, 84);
                }
                AddPortal(result, braveTowerCompleted ? "进入通天塔一层" : "再次进入通天塔一层", 196, 25);
                AddWalk(result, "通天塔一层前往元荒境入口", 25, Route(
                    "352,656 416,688 480,720 544,752 608,752 672,720 736,688 800,656 896,608 960,576 " +
                    "1024,544 1088,512 1152,480 1088,416 992,400 928,368 864,336 800,304 736,272 " +
                    "672,240 608,208 544,208 480,240 416,272 320,320 256,352 160,368"));
                AddPortal(result, "进入元荒境一层", 71, 26);
                for (int mapId = 26; mapId <= 29; mapId++)
                {
                    AddWalk(result, "横穿元荒境" + ChineseFloor(mapId - 25) + "层", mapId, towerFloorRoute);
                    AddPortal(result, "进入元荒境" + ChineseFloor(mapId - 24) + "层", mapId + 46, mapId + 1);
                }
                AddWalk(result, "元荒境五层前往高塔探险者", 30, Route(
                    "384,672 448,704 512,736 576,768 672,720 736,688 800,656 864,624 928,592 992,560 " +
                    "1056,528 1152,480 1120,432 1056,400 992,400 928,368 864,336 768,288 736,272 " +
                    "672,240 576,192 512,224 448,256 384,224"));
                AddQuest(result, "初探通天塔", 8073, TianshuMountainClimbProtocol.FirstTowerTaskId,
                    TianshuMountainClimbProtocol.FirstTowerTurnInFunction, true);
                AddWalk(result, "走到元荒境五层回城入口", 30, Route("256,224"));
                AddPortal(result, "返回灵昌城", 246, 12);
            }

            if (!rainMountainCompleted)
            {
                AddTravel(result, "飞行到灵昌城皇宫入口附近", 14, 12, 24, 28);
                AddTravel(result, "飞行到灵昌广场入口", 190, 12, 10, 131);
                AddPortal(result, "进入灵昌广场", 190, 97);
                AddTravel(result, "飞行到时雨山脚入口", 111, 97, 3, 85);
                AddPortal(result, "进入时雨山脚", 111, 18);
                AddWalk(result, "沿时雨山脚走到半天坡入口", 18, Route(
                    "1056,1232 992,1200 928,1168 832,1120 800,1104 736,1072 672,1040 608,1008 544,976 " +
                    "448,928 480,880 512,832 544,784 576,704 640,672 672,624 704,576 768,512 832,480 " +
                    "896,448 960,416 1024,384 1088,352 1152,320 1216,288 1280,256 1376,208 1440,176"));
                AddPortal(result, "进入半天坡", 112, 19);
                AddWalk(result, "沿半天坡走到清溪云涧入口", 19, Route(
                    "224,1328 288,1264 352,1232 416,1200 480,1168 544,1136 608,1104 736,1104 800,1104 " +
                    "864,1104 960,1088 992,1072 1088,1024 1088,960 1152,928 1120,848 1120,784 1088,736 " +
                    "1056,688 1056,592 992,560 928,528 864,528 736,496 704,480 640,448 576,416 512,384 " +
                    "448,352 352,304 288,272"));
                AddPortal(result, "进入清溪云涧", 113, 20);
                AddWalk(result, "清溪云涧前往高山探险者", 20, Route(
                    "1536,1184 1408,1184 1344,1184 1280,1184 1216,1184 1152,1152 1088,1120 992,1104 " +
                    "928,1072 864,1040 800,1040 832,1024"));
                AddQuest(result, "勇攀时雨山", 8074, TianshuMountainClimbProtocol.RainMountainTaskId,
                    TianshuMountainClimbProtocol.RainMountainTurnInFunction, true);
                AddWalk(result, "走到清溪云涧回城入口", 20, Route("736,976"));
                AddPortal(result, "返回灵昌城", 247, 12);
            }
            AddTravel(result, "飞行到灵昌城皇宫入口", 14, 12, 24, 28);
            AddPortal(result, "返回皇城皇宫并结束", 14, 13);
            return result;
        }

        private static void AddTravel(IList<MountainAction> result, string description, int npcId, int mapId, int x, int y)
        {
            result.Add(new MountainAction
            {
                Kind = ActionKind.Travel,
                Description = description,
                Target = new BountyTravelTarget { NpcId = npcId, MapId = mapId, X = x, Y = y }
            });
        }

        private static void AddPortal(IList<MountainAction> result, string description, int npcId, int expectedMapId)
        {
            result.Add(new MountainAction { Kind = ActionKind.Portal, Description = description,
                NpcId = npcId, ExpectedMapId = expectedMapId });
        }

        private static void AddWalk(IList<MountainAction> result, string description, int mapId, IList<RoutePoint> route)
        {
            result.Add(new MountainAction { Kind = ActionKind.Walk, Description = description, MapId = mapId, Route = route });
        }

        private static void AddNpcTeleport(IList<MountainAction> result, string description, int npcId,
            string functionId, int expectedMapId)
        {
            result.Add(new MountainAction { Kind = ActionKind.NpcTeleport, Description = description,
                NpcId = npcId, FunctionId = functionId, ExpectedMapId = expectedMapId });
        }

        private static void AddQuest(IList<MountainAction> result, string description, int npcId,
            string taskId, string functionId, bool turnIn)
        {
            result.Add(new MountainAction { Kind = ActionKind.Quest, Description = description, NpcId = npcId,
                TaskId = taskId, FunctionId = functionId, TurnIn = turnIn });
        }

        private static IList<RoutePoint> Route(string value)
        {
            List<RoutePoint> result = new List<RoutePoint>();
            string[] pairs = (value ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < pairs.Length; i++)
            {
                string[] coordinates = pairs[i].Split(',');
                ushort x;
                ushort y;
                if (coordinates.Length != 2 || !UInt16.TryParse(coordinates[0], out x) || !UInt16.TryParse(coordinates[1], out y))
                    throw new InvalidDataException("Invalid recorded route coordinate: " + pairs[i]);
                result.Add(new RoutePoint { X = x, Y = y });
            }
            return result;
        }

        private static string ChineseFloor(int floor)
        {
            string[] names = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };
            return floor >= 0 && floor < names.Length ? names[floor] : floor.ToString();
        }

        private void SendPacket(int expectedGeneration, string description, Func<uint, byte[]> factory)
        {
            long connectionId;
            uint sequence;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || gameConnectionId == 0 || !sequenceKnown) return;
                connectionId = gameConnectionId;
                sequence = nextSequence++;
            }
            byte[] packet = factory(sequence);
            if (!service.Send(connectionId, packet))
            {
                Fail(expectedGeneration, description + "发包失败；连接可能已关闭或 ACTIVE 已关闭。");
                return;
            }
            service.ReportEngineEvent(AutomationOwner, "INFO", description + "，opcode=0x" +
                ((packet[2] << 8) | packet[3]).ToString("X4") + "，seq=" + sequence + "，len=" + packet.Length,
                connectionId);
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

        private void OnStageTimeout(int expectedGeneration)
        {
            MountainAction action;
            MountainClimbAutomationState current;
            lock (syncRoot) { action = GetCurrentActionLocked(); current = state; }
            Fail(expectedGeneration, "等待阶段超时：" + current + " / " +
                (action == null ? "未知动作" : action.Description) + "。已停止继续发包，请保留会话库用于比对。");
        }

        private void Complete(int expectedGeneration, string message)
        {
            bool restore;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = MountainClimbAutomationState.Completed;
                DisposeTimersLocked();
                restore = activatedByAutomation;
                activatedByAutomation = false;
            }
            Publish(expectedGeneration, MountainClimbAutomationState.Completed, message, false);
            service.ReleaseAutomation(AutomationOwner);
            if (restore && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void Fail(int expectedGeneration, string message)
        {
            bool restore;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = MountainClimbAutomationState.Failed;
                DisposeTimersLocked();
                restore = activatedByAutomation;
                activatedByAutomation = false;
            }
            Publish(expectedGeneration, MountainClimbAutomationState.Failed, message, true);
            service.ReleaseAutomation(AutomationOwner);
            if (restore && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void StopCore(MountainClimbAutomationState finalState, string message, bool error)
        {
            bool restore;
            lock (syncRoot)
            {
                if (disposed || !IsRunning(state)) return;
                state = finalState;
                generation++;
                DisposeTimersLocked();
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
                StopCore(MountainClimbAutomationState.Stopped, "ACTIVE/发包模式已关闭，登山爬塔同步停止。", false);
        }

        private void Publish(int expectedGeneration, MountainClimbAutomationState publishedState, string message, bool error)
        {
            lock (syncRoot) if (disposed || expectedGeneration != generation) return;
            PublishAny(publishedState, message, error);
        }

        private void PublishAny(MountainClimbAutomationState publishedState, string message, bool error)
        {
            Action<MountainClimbAutomationState, string> handler = StatusChanged;
            if (handler != null) handler(publishedState, message);
            long connectionId = GetGameConnectionId();
            service.ReportEngineEvent(AutomationOwner, error ? "ERROR" : "INFO", message,
                connectionId == 0 ? (long?)null : connectionId);
        }

        private void ScheduleAction(int expectedGeneration, int delayMs, Action<int> callback)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                ScheduleStageLocked(expectedGeneration, delayMs, callback);
            }
        }

        private void ScheduleStageLocked(int expectedGeneration, int delayMs, Action<int> callback)
        {
            DisposeStageTimerLocked();
            stageTimer = new System.Threading.Timer(delegate { callback(expectedGeneration); }, null, delayMs, Timeout.Infinite);
        }

        private void DisposeStageTimerLocked()
        {
            if (stageTimer != null) { stageTimer.Dispose(); stageTimer = null; }
        }

        private void DisposeTimersLocked()
        {
            DisposeStageTimerLocked();
            if (walkTimer != null) { walkTimer.Dispose(); walkTimer = null; }
        }

        private MountainAction GetCurrentActionLocked()
        {
            return actionIndex >= 0 && actionIndex < actions.Count ? actions[actionIndex] : null;
        }

        private bool IsCurrentActionLocked(int expectedGeneration, MountainAction action)
        {
            return action != null && IsCurrentLocked(expectedGeneration) && object.ReferenceEquals(action, GetCurrentActionLocked());
        }

        private bool IsCurrentLocked(int expectedGeneration)
        {
            return !disposed && generation == expectedGeneration && IsRunning(state);
        }

        private static bool IsRunning(MountainClimbAutomationState value)
        {
            return value != MountainClimbAutomationState.Inactive && value != MountainClimbAutomationState.Completed &&
                value != MountainClimbAutomationState.Stopped && value != MountainClimbAutomationState.Failed;
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

        private long GetGameConnectionId() { lock (syncRoot) return gameConnectionId; }

        private static bool IsSequenceOpcode(int opcode)
        {
            return opcode == TianshuBountyProtocol.ClientTravelLink || opcode == TianshuBountyProtocol.ClientNpcOpen ||
                opcode == TianshuBountyProtocol.ClientNpcFunction || opcode == TianshuMountainClimbProtocol.ClientQuestAction ||
                opcode == TianshuBountyProtocol.ClientDialogResponse || opcode == TianshuRunLoopProtocol.ClientMovement ||
                opcode == TianshuRunLoopProtocol.ClientUiAction || opcode == 0x0042 || opcode == 0x001B;
        }
    }

    public sealed class MountainClimbAutomationControl : UserControl
    {
        private readonly MountainClimbAutomationCoordinator coordinator;
        private readonly Button startButton;
        private readonly Button stopButton;
        private readonly Label stateLabel;
        private readonly Label progressLabel;
        private readonly Label actionLabel;

        public MountainClimbAutomationControl(MountainClimbAutomationCoordinator coordinator)
        {
            if (coordinator == null) throw new ArgumentNullException("coordinator");
            this.coordinator = coordinator;
            Dock = DockStyle.Fill;

            Label help = new Label
            {
                AutoSize = false,
                Height = 64,
                Dock = DockStyle.Top,
                Text = "按“登山爬塔”录制执行：三界关接三项任务；通天塔传送人直达无间境十层，之后到元荒境五层、清溪云涧依次交付；最后返回皇城皇宫。飞行确认串取自实时服务端，塔层/山路使用已验证走步帧和进图帧。"
            };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, AutoSize = false };
            startButton = new Button { Text = "开始登山爬塔", AutoSize = true };
            stopButton = new Button { Text = "停止", AutoSize = true, Enabled = false };
            buttons.Controls.Add(startButton);
            buttons.Controls.Add(stopButton);
            buttons.Controls.Add(new Label { Text = "背包不足时会安全停止，不会自动丢弃物品。", AutoSize = true,
                Margin = new Padding(12, 9, 3, 3), ForeColor = Color.DarkRed });

            stateLabel = new Label { Dock = DockStyle.Top, Height = 23, Text = "状态：Inactive" };
            progressLabel = new Label { Dock = DockStyle.Top, Height = 23, Text = "动作：0/0" };
            actionLabel = new Label { Dock = DockStyle.Top, Height = 42, AutoEllipsis = true, Text = "当前动作：等待启动" };
            Controls.Add(actionLabel);
            Controls.Add(progressLabel);
            Controls.Add(stateLabel);
            Controls.Add(buttons);
            Controls.Add(help);

            startButton.Click += OnStart;
            stopButton.Click += delegate { coordinator.Stop(); };
            coordinator.StatusChanged += OnStatusChanged;
            OnStatusChanged(coordinator.State, "登山爬塔尚未启动。");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) coordinator.StatusChanged -= OnStatusChanged;
            base.Dispose(disposing);
        }

        private void OnStart(object sender, EventArgs e)
        {
            try { coordinator.Start(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "登山爬塔", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void OnStatusChanged(MountainClimbAutomationState automationState, string message)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<MountainClimbAutomationState, string>(OnStatusChanged), automationState, message); }
                catch (InvalidOperationException) { }
                return;
            }
            bool running = automationState != MountainClimbAutomationState.Inactive &&
                automationState != MountainClimbAutomationState.Completed &&
                automationState != MountainClimbAutomationState.Stopped &&
                automationState != MountainClimbAutomationState.Failed;
            startButton.Enabled = !running;
            stopButton.Enabled = running;
            stateLabel.Text = "状态：" + automationState + (string.IsNullOrEmpty(coordinator.CurrentMapName) ? string.Empty :
                " / 地图：" + coordinator.CurrentMapName);
            progressLabel.Text = "动作：" + coordinator.CompletedActions + "/" + coordinator.TotalActions;
            actionLabel.Text = "当前动作：" + (string.IsNullOrEmpty(coordinator.CurrentAction) ? "无" : coordinator.CurrentAction);
        }
    }
}
