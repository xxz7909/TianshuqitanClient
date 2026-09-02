using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace TianshuQitanLauncher.Protocol
{
    public enum BountyAutomationState
    {
        Inactive = 0,
        WaitingForGameConnection = 1,
        TravelingToPalace = 2,
        TravelingToGiver = 3,
        ConfirmingTeleport = 4,
        WaitingForMap = 5,
        OpeningGiver = 6,
        WaitingForGiverDialog = 7,
        SelectingBounty = 8,
        WaitingForBountyConfirmation = 9,
        ConfirmingBounty = 10,
        WaitingForTask = 11,
        TravelingToTarget = 12,
        OpeningTarget = 13,
        WaitingForBattle = 14,
        InBattle = 15,
        ReturningToGiver = 16,
        WaitingForRoundReward = 17,
        Completed = 18,
        Stopped = 19,
        Failed = 20
    }

    public sealed class BountyTravelTarget
    {
        public int NpcId { get; set; }
        public int MapId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }

        public BountyTravelTarget Clone()
        {
            return new BountyTravelTarget { NpcId = NpcId, MapId = MapId, X = X, Y = Y };
        }

        public override string ToString()
        {
            return "NPC " + NpcId + " / 地图 " + MapId + " / (" + X + "," + Y + ")";
        }
    }

    public sealed class BountyConfirmationDialog
    {
        public uint ContextId { get; set; }
        public string Title { get; set; }
        public string Content { get; set; }
        public string Token { get; set; }
    }

    public static class TianshuBountyProtocol
    {
        public const int ClientNpcOpen = 0x0016;
        public const int ClientNpcFunction = 0x0017;
        public const int ClientBattleAdvance = 0x001F;
        public const int ClientDialogResponse = 0x004C;
        public const int ClientTravelLink = 0x00B5;

        public const int ServerMapStart = 0x0016;
        public const int ServerNpcRemoved = 0x0019;
        public const int ServerNpcDialog = 0x001D;
        public const int ServerSystemMessage = 0x0029;
        public const int ServerBattlePrompt = 0x002C;
        public const int ServerTaskUpdate = 0x003E;
        public const int ServerConfirmationDialog = 0x005C;

        public const int BountyGiverNpcId = 8011;
        public const string BountyFunctionId = "374";

        private static readonly Regex TravelLinkRegex = new Regex(
            @"event\s*:\s*x\s*[:=]\s*(\d+)\s*,\s*y\s*[:=]\s*(\d+)\s*,\s*m\s*[:=]\s*(\d+)\s*,\s*n\s*[:=]\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Base64TokenRegex = new Regex(
            @"(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{24,}={0,2}(?![A-Za-z0-9+/])",
            RegexOptions.CultureInvariant);
        private static readonly Regex TaskNumberRegex = new Regex(
            @"第\s*(\d+)\s*次",
            RegexOptions.CultureInvariant);

        public static byte[] BuildTravelRequest(BountyTravelTarget target, uint sequence)
        {
            if (target == null) throw new ArgumentNullException("target");
            PacketWriter writer = new PacketWriter(ClientTravelLink);
            writer.WriteUInt32(0xFE);
            writer.WriteString(target.NpcId.ToString());
            writer.WriteString(target.MapId.ToString());
            writer.WriteString(target.X + "=" + target.Y);
            writer.WriteUInt32(sequence);
            return writer.ToArray();
        }

        public static byte[] BuildNpcOpen(int npcId, uint sequence)
        {
            PacketWriter writer = new PacketWriter(ClientNpcOpen);
            writer.WriteUInt32((uint)npcId);
            writer.WriteUInt32(sequence);
            return writer.ToArray();
        }

        public static byte[] BuildNpcFunction(int npcId, string functionId, uint sequence)
        {
            PacketWriter writer = new PacketWriter(ClientNpcFunction);
            writer.WriteUInt32((uint)npcId);
            writer.WriteString(functionId);
            writer.WriteUInt32(sequence);
            return writer.ToArray();
        }

        public static byte[] BuildDialogResponse(uint contextId, string token, bool accepted, uint sequence)
        {
            PacketWriter writer = new PacketWriter(ClientDialogResponse);
            writer.WriteUInt32(contextId);
            writer.WriteString(token);
            writer.WriteString(accepted ? "ok" : "cancel");
            // The dialog response has a confirmed u16 zero field before the request sequence.
            writer.WriteUInt16(0);
            writer.WriteUInt32(sequence);
            return writer.ToArray();
        }

        public static byte[] BuildBattleAdvance(uint sequence)
        {
            PacketWriter writer = new PacketWriter(ClientBattleAdvance);
            writer.WriteUInt32(sequence);
            return writer.ToArray();
        }

        public static bool TryParseConfirmationDialog(byte[] bytes, out BountyConfirmationDialog dialog)
        {
            dialog = null;
            if (!HasOpcode(bytes, ServerConfirmationDialog) || bytes.Length < 12) return false;
            int offset = 4;
            uint contextId;
            string title;
            string content;
            string token;
            if (!TryReadUInt32(bytes, ref offset, out contextId) ||
                !TryReadString(bytes, ref offset, out title) ||
                !TryReadString(bytes, ref offset, out content) ||
                !TryReadString(bytes, ref offset, out token)) return false;
            dialog = new BountyConfirmationDialog
            {
                ContextId = contextId,
                Title = title,
                Content = content,
                Token = token
            };
            return !string.IsNullOrEmpty(token);
        }

        public static bool TryParseNpcFunction(byte[] bytes, string label, out int npcId, out string functionId)
        {
            npcId = 0;
            functionId = null;
            if (!HasOpcode(bytes, ServerNpcDialog) || bytes.Length < 10) return false;
            npcId = (int)ReadUInt32(bytes, 4);
            for (int offset = 8; offset <= bytes.Length - 2; offset++)
            {
                string value;
                int nextOffset;
                if (!TryReadStringAt(bytes, offset, out value, out nextOffset) ||
                    !string.Equals(value, label, StringComparison.Ordinal)) continue;
                string action;
                int ignored;
                if (TryReadStringAt(bytes, nextOffset, out action, out ignored) && !string.IsNullOrWhiteSpace(action))
                {
                    functionId = action;
                    return true;
                }
            }
            return false;
        }

        public static bool TryParseTaskUpdate(byte[] bytes, out BountyTravelTarget enemy, out BountyTravelTarget giver)
        {
            enemy = null;
            giver = null;
            if (!HasOpcode(bytes, ServerTaskUpdate)) return false;
            IList<string> textCandidates = ExtractTextCandidates(bytes);
            for (int i = 0; i < textCandidates.Count; i++)
            {
                IList<BountyTravelTarget> links = ExtractTravelLinks(textCandidates[i]);
                for (int j = 0; j < links.Count; j++)
                {
                    BountyTravelTarget target = links[j];
                    if (target.NpcId == BountyGiverNpcId)
                    {
                        giver = target;
                    }
                    else if (enemy == null)
                    {
                        enemy = target;
                    }
                }
            }
            return enemy != null;
        }

        public static bool TryParseTaskNumber(byte[] bytes, out int taskNumber)
        {
            taskNumber = 0;
            if (!HasOpcode(bytes, ServerTaskUpdate)) return false;
            IList<string> candidates = ExtractTextCandidates(bytes);
            for (int i = 0; i < candidates.Count; i++)
            {
                Match match = TaskNumberRegex.Match(candidates[i]);
                int parsed;
                if (match.Success && Int32.TryParse(match.Groups[1].Value, out parsed) && parsed >= 1 && parsed <= 10)
                {
                    taskNumber = parsed;
                    return true;
                }
            }
            return false;
        }

        public static bool ContainsText(byte[] bytes, string expected)
        {
            if (bytes == null || string.IsNullOrEmpty(expected)) return false;
            IList<string> candidates = ExtractTextCandidates(bytes);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        public static IList<BountyTravelTarget> ExtractTravelLinks(string text)
        {
            List<BountyTravelTarget> result = new List<BountyTravelTarget>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            AddTravelLinks(text, result);
            string decoded;
            if (TryDecodeBase64Text(text, out decoded)) AddTravelLinks(decoded, result);
            MatchCollection base64Matches = Base64TokenRegex.Matches(text);
            for (int i = 0; i < base64Matches.Count; i++)
            {
                if (TryDecodeBase64Text(base64Matches[i].Value, out decoded)) AddTravelLinks(decoded, result);
            }
            return result;
        }

        public static bool TryParseNpcRemoved(byte[] bytes, out int npcId)
        {
            npcId = 0;
            if (!HasOpcode(bytes, ServerNpcRemoved) || bytes.Length < 8) return false;
            npcId = (int)ReadUInt32(bytes, 4);
            return true;
        }

        public static bool TryReadClientSequence(byte[] bytes, out uint sequence)
        {
            sequence = 0;
            if (bytes == null || bytes.Length < 8 || ReadUInt16(bytes, 0) != bytes.Length) return false;
            sequence = ReadUInt32(bytes, bytes.Length - 4);
            return true;
        }

        public static bool HasOpcode(byte[] bytes, int opcode)
        {
            return bytes != null && bytes.Length >= 4 && ReadUInt16(bytes, 0) == bytes.Length &&
                ReadUInt16(bytes, 2) == opcode;
        }

        private static IList<string> ExtractTextCandidates(byte[] bytes)
        {
            List<string> result = new List<string>();
            AddUnique(result, Encoding.UTF8.GetString(bytes));
            for (int offset = 4; offset <= bytes.Length - 2; offset++)
            {
                string value;
                int ignored;
                if (TryReadStringAt(bytes, offset, out value, out ignored) && value.Length >= 4)
                {
                    AddUnique(result, value);
                    string decoded;
                    if (TryDecodeBase64Text(value, out decoded)) AddUnique(result, decoded);
                }
            }
            return result;
        }

        private static void AddTravelLinks(string text, IList<BountyTravelTarget> targets)
        {
            MatchCollection matches = TravelLinkRegex.Matches(text ?? string.Empty);
            for (int i = 0; i < matches.Count; i++)
            {
                BountyTravelTarget target = new BountyTravelTarget
                {
                    X = Int32.Parse(matches[i].Groups[1].Value),
                    Y = Int32.Parse(matches[i].Groups[2].Value),
                    MapId = Int32.Parse(matches[i].Groups[3].Value),
                    NpcId = Int32.Parse(matches[i].Groups[4].Value)
                };
                bool exists = false;
                for (int j = 0; j < targets.Count; j++)
                {
                    BountyTravelTarget current = targets[j];
                    if (current.NpcId == target.NpcId && current.MapId == target.MapId &&
                        current.X == target.X && current.Y == target.Y)
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists) targets.Add(target);
            }
        }

        private static bool TryDecodeBase64Text(string value, out string decoded)
        {
            decoded = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            StringBuilder compact = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (!Char.IsWhiteSpace(value[i])) compact.Append(value[i]);
            }
            if (compact.Length < 16 || compact.Length % 4 != 0) return false;
            try
            {
                byte[] decodedBytes = Convert.FromBase64String(compact.ToString());
                decoded = Encoding.UTF8.GetString(decodedBytes);
                return decoded.IndexOf("event", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    decoded.IndexOf('\uFFFD') < 0;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static void AddUnique(IList<string> values, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            for (int i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], value, StringComparison.Ordinal)) return;
            }
            values.Add(value);
        }

        private static bool TryReadString(byte[] bytes, ref int offset, out string value)
        {
            int nextOffset;
            if (!TryReadStringAt(bytes, offset, out value, out nextOffset)) return false;
            offset = nextOffset;
            return true;
        }

        private static bool TryReadStringAt(byte[] bytes, int offset, out string value, out int nextOffset)
        {
            value = null;
            nextOffset = offset;
            if (bytes == null || offset < 0 || offset + 2 > bytes.Length) return false;
            int length = ReadUInt16(bytes, offset);
            if (length < 0 || offset + 2 + length > bytes.Length) return false;
            try
            {
                UTF8Encoding strictUtf8 = new UTF8Encoding(false, true);
                value = strictUtf8.GetString(bytes, offset + 2, length);
                nextOffset = offset + 2 + length;
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static bool TryReadUInt32(byte[] bytes, ref int offset, out uint value)
        {
            value = 0;
            if (bytes == null || offset < 0 || offset + 4 > bytes.Length) return false;
            value = ReadUInt32(bytes, offset);
            offset += 4;
            return true;
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

    public sealed class BountyAutomationCoordinator : IDisposable
    {
        private const string AutomationOwner = "AutoBounty";

        private enum TravelDestination
        {
            None = 0,
            Palace = 1,
            Giver = 2,
            Target = 3
        }

        private static readonly BountyTravelTarget PalaceTarget = new BountyTravelTarget
        {
            NpcId = 14,
            MapId = 12,
            X = 24,
            Y = 28
        };

        private static readonly BountyTravelTarget DefaultGiverTarget = new BountyTravelTarget
        {
            NpcId = TianshuBountyProtocol.BountyGiverNpcId,
            MapId = 13,
            X = 30,
            Y = 100
        };

        private readonly object syncRoot = new object();
        private readonly ProtocolWorkbenchService service;
        private readonly Dictionary<long, ConnectionSession> gameConnections = new Dictionary<long, ConnectionSession>();
        private System.Threading.Timer timer;
        private BountyAutomationState state;
        private BountyTravelTarget giverTarget;
        private BountyTravelTarget currentTarget;
        private TravelDestination pendingDestination;
        private long gameConnectionId;
        private uint nextSequence;
        private bool sequenceKnown;
        private bool viaPalace;
        private bool palaceVisited;
        private bool claimRoundReward;
        private bool activatedByAutomation;
        private bool allowWantedPoster;
        private int plannedRounds;
        private int completedRounds;
        private int completedTasks;
        private int completedTasksInRound;
        private int currentTaskNumber;
        private int generation;
        private bool disposed;

        public event Action<BountyAutomationState, string> StatusChanged;

        public BountyAutomationCoordinator(ProtocolWorkbenchService service)
        {
            if (service == null) throw new ArgumentNullException("service");
            this.service = service;
            state = BountyAutomationState.Inactive;
            giverTarget = DefaultGiverTarget.Clone();
            service.ConnectionChanged += OnConnectionChanged;
            service.FrameCaptured += OnFrameCaptured;
            service.ActiveModeChanged += OnActiveModeChanged;
        }

        public BountyAutomationState State
        {
            get { lock (syncRoot) return state; }
        }

        public int CompletedTasks
        {
            get { lock (syncRoot) return completedTasks; }
        }

        public int MaximumTasks
        {
            get { lock (syncRoot) return plannedRounds * 10; }
        }

        public int PlannedRounds
        {
            get { lock (syncRoot) return plannedRounds; }
        }

        public int CompletedRounds
        {
            get { lock (syncRoot) return completedRounds; }
        }

        public int CompletedTasksInRound
        {
            get { lock (syncRoot) return completedTasksInRound; }
        }

        public BountyTravelTarget CurrentTarget
        {
            get { lock (syncRoot) return currentTarget == null ? null : currentTarget.Clone(); }
        }

        public void Start(int roundCount, bool travelViaPalace)
        {
            Start(roundCount, false, travelViaPalace);
        }

        public void Start(int roundCount, bool useWantedPoster, bool travelViaPalace)
        {
            if (roundCount < 1 || roundCount > 2) throw new ArgumentOutOfRangeException("roundCount");
            string currentOwner;
            if (!service.TryAcquireAutomation(AutomationOwner, out currentOwner))
                throw new InvalidOperationException("当前已有自动化流程正在运行：" + currentOwner + "。请先停止后再启动自动除暴。");
            int currentGeneration;
            bool enableActive;
            bool canBegin;
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
                plannedRounds = roundCount + (useWantedPoster ? 1 : 0);
                completedRounds = 0;
                completedTasks = 0;
                completedTasksInRound = 0;
                currentTaskNumber = 0;
                allowWantedPoster = useWantedPoster;
                viaPalace = travelViaPalace;
                palaceVisited = false;
                claimRoundReward = false;
                currentTarget = null;
                pendingDestination = TravelDestination.None;
                enableActive = !service.ActiveMode;
                activatedByAutomation = enableActive;
                canBegin = gameConnectionId != 0 && sequenceKnown;
                state = canBegin
                    ? (travelViaPalace ? BountyAutomationState.TravelingToPalace : BountyAutomationState.TravelingToGiver)
                    : BountyAutomationState.WaitingForGameConnection;
            }
            if (enableActive) service.SetActiveMode(true, false);
            Publish(currentGeneration, canBegin
                    ? (travelViaPalace ? BountyAutomationState.TravelingToPalace : BountyAutomationState.TravelingToGiver)
                    : BountyAutomationState.WaitingForGameConnection,
                canBegin ? "自动除暴已启动，准备发送首个传送请求。" : "自动除暴已启动，等待游戏连接和请求序号。", false);
            if (canBegin) BeginRoute(currentGeneration);
        }

        public void Stop()
        {
            StopCore(BountyAutomationState.Stopped, "自动除暴已停止。", false);
        }

        public void Dispose()
        {
            bool restoreActive;
            lock (syncRoot)
            {
                if (disposed) return;
                disposed = true;
                generation++;
                DisposeTimerLocked();
                restoreActive = activatedByAutomation;
                activatedByAutomation = false;
            }
            service.ConnectionChanged -= OnConnectionChanged;
            service.FrameCaptured -= OnFrameCaptured;
            service.ActiveModeChanged -= OnActiveModeChanged;
            service.ReleaseAutomation(AutomationOwner);
            if (restoreActive && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void OnConnectionChanged(ConnectionSession connection)
        {
            if (connection == null || connection.Kind != ConnectionKind.Game) return;
            int currentGeneration = 0;
            bool begin = false;
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
                    gameConnectionId = selected;
                    sequenceKnown = false;
                    nextSequence = 0;
                }
                if (state == BountyAutomationState.WaitingForGameConnection && gameConnectionId != 0 && sequenceKnown)
                {
                    currentGeneration = generation;
                    state = viaPalace ? BountyAutomationState.TravelingToPalace : BountyAutomationState.TravelingToGiver;
                    begin = true;
                }
            }
            if (begin) BeginRoute(currentGeneration);
        }

        private void OnFrameCaptured(ProtocolFrame frame)
        {
            if (frame == null || !frame.Opcode.HasValue || frame.Bytes == null) return;
            int opcode = (int)frame.Opcode.Value;
            int currentGeneration;
            BountyAutomationState currentState;
            bool begin = false;
            lock (syncRoot)
            {
                if (disposed) return;
                if (frame.Direction == TrafficDirection.ClientToServer && IsConfirmedSequenceOpcode(opcode) &&
                    frame.ConnectionId != gameConnectionId && gameConnections.ContainsKey(frame.ConnectionId))
                {
                    // A confirmed gameplay request is stronger evidence than connection order;
                    // HTTP sockets may briefly be classified as Game before their first GET.
                    gameConnectionId = frame.ConnectionId;
                    sequenceKnown = false;
                    nextSequence = 0;
                }
                if (frame.Direction == TrafficDirection.ClientToServer && frame.ConnectionId == gameConnectionId)
                {
                    ObserveSequenceLocked(opcode, frame.Bytes);
                    if (state == BountyAutomationState.WaitingForGameConnection && sequenceKnown)
                    {
                        state = viaPalace ? BountyAutomationState.TravelingToPalace : BountyAutomationState.TravelingToGiver;
                        begin = true;
                    }
                }
                currentGeneration = generation;
                currentState = state;
            }
            if (begin)
            {
                BeginRoute(currentGeneration);
                return;
            }
            if (!IsRunning(currentState) || frame.Direction != TrafficDirection.ServerToClient ||
                frame.ConnectionId != GetGameConnectionId()) return;

            if (opcode == TianshuBountyProtocol.ServerConfirmationDialog)
            {
                HandleConfirmationDialog(currentGeneration, frame.Bytes);
                return;
            }
            if (opcode == TianshuBountyProtocol.ServerMapStart)
            {
                HandleMapArrival(currentGeneration);
                return;
            }
            if (opcode == TianshuBountyProtocol.ServerNpcDialog)
            {
                HandleNpcDialog(currentGeneration, frame.Bytes);
                return;
            }
            if (opcode == TianshuBountyProtocol.ServerTaskUpdate)
            {
                HandleTaskUpdate(currentGeneration, frame.Bytes);
                return;
            }
            if (opcode == TianshuBountyProtocol.ServerSystemMessage)
            {
                HandleSystemMessage(currentGeneration, frame.Bytes);
                return;
            }
            if (opcode == TianshuBountyProtocol.ServerBattlePrompt)
            {
                HandleBattlePrompt(currentGeneration);
                return;
            }
            if (opcode == TianshuBountyProtocol.ServerNpcRemoved)
            {
                HandleNpcRemoved(currentGeneration, frame.Bytes);
            }
        }

        private void OnActiveModeChanged(bool active)
        {
            if (active) return;
            BountyAutomationState current;
            lock (syncRoot) current = state;
            if (IsRunning(current)) StopCore(BountyAutomationState.Stopped, "ACTIVE/发包模式已关闭，自动除暴同步停止。", false);
        }

        private void BeginRoute(int expectedGeneration)
        {
            bool usePalace;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                usePalace = viaPalace && !palaceVisited;
            }
            if (usePalace)
                BeginTravel(expectedGeneration, PalaceTarget, TravelDestination.Palace, BountyAutomationState.TravelingToPalace, "皇城皇宫");
            else
                BeginTravel(expectedGeneration, GetGiverTarget(), TravelDestination.Giver, BountyAutomationState.TravelingToGiver, "舞修罗");
        }

        private void BeginTravel(int expectedGeneration, BountyTravelTarget target, TravelDestination destination,
            BountyAutomationState travelState, string label)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = travelState;
                pendingDestination = destination;
                DisposeTimerLocked();
                ScheduleLocked(expectedGeneration, 25000, OnStageTimeout);
            }
            Publish(expectedGeneration, travelState, "发送任务链接，前往" + label + "：" + target, false);
            SendPacket(expectedGeneration, "传送到" + label, delegate(uint sequence)
            {
                return TianshuBountyProtocol.BuildTravelRequest(target, sequence);
            });
        }

        private void HandleConfirmationDialog(int expectedGeneration, byte[] bytes)
        {
            BountyConfirmationDialog dialog;
            if (!TianshuBountyProtocol.TryParseConfirmationDialog(bytes, out dialog)) return;
            BountyAutomationState nextState;
            bool teleport;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                teleport = IsTravelState(state) && dialog.Title.IndexOf("传送", StringComparison.Ordinal) >= 0;
                string dialogText = (dialog.Title ?? string.Empty) + " " + (dialog.Content ?? string.Empty);
                bool bounty = state == BountyAutomationState.WaitingForBountyConfirmation &&
                    (dialogText.IndexOf("除暴安良", StringComparison.Ordinal) >= 0 ||
                    (allowWantedPoster && dialogText.IndexOf("通缉令", StringComparison.Ordinal) >= 0));
                if (!teleport && !bounty) return;
                nextState = teleport ? BountyAutomationState.ConfirmingTeleport : BountyAutomationState.ConfirmingBounty;
                state = nextState;
                DisposeTimerLocked();
                ScheduleLocked(expectedGeneration, 180, delegate(int generationValue)
                {
                    ConfirmDialog(generationValue, dialog, teleport);
                });
            }
            Publish(expectedGeneration, nextState,
                "已解析服务端随机确认串，自动确认“" + dialog.Title + "”（不会重放录制值）。", false);
        }

        private void ConfirmDialog(int expectedGeneration, BountyConfirmationDialog dialog, bool teleport)
        {
            BountyAutomationState waitingState;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) ||
                    (state != BountyAutomationState.ConfirmingTeleport && state != BountyAutomationState.ConfirmingBounty)) return;
                waitingState = teleport ? BountyAutomationState.WaitingForMap : BountyAutomationState.WaitingForTask;
                state = waitingState;
                DisposeTimerLocked();
                ScheduleLocked(expectedGeneration, teleport ? 30000 : 15000, OnStageTimeout);
            }
            Publish(expectedGeneration, waitingState, teleport ? "已确认传送，等待地图起点包。" : "已接受除暴任务，等待任务 HTML。", false);
            SendPacket(expectedGeneration, "确认“" + dialog.Title + "”", delegate(uint sequence)
            {
                return TianshuBountyProtocol.BuildDialogResponse(dialog.ContextId, dialog.Token, true, sequence);
            });
        }

        private void HandleMapArrival(int expectedGeneration)
        {
            TravelDestination destination;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) ||
                    (!IsTravelState(state) && state != BountyAutomationState.WaitingForMap)) return;
                destination = pendingDestination;
                pendingDestination = TravelDestination.None;
                DisposeTimerLocked();
                if (destination == TravelDestination.Palace)
                {
                    palaceVisited = true;
                    state = BountyAutomationState.TravelingToGiver;
                    ScheduleLocked(expectedGeneration, 350, delegate(int value) { BeginRoute(value); });
                }
                else if (destination == TravelDestination.Giver)
                {
                    state = BountyAutomationState.OpeningGiver;
                    ScheduleLocked(expectedGeneration, 400, OpenGiver);
                }
                else if (destination == TravelDestination.Target)
                {
                    state = BountyAutomationState.OpeningTarget;
                    ScheduleLocked(expectedGeneration, 500, OpenTarget);
                }
                else
                {
                    return;
                }
            }
            if (destination == TravelDestination.Palace)
                Publish(expectedGeneration, BountyAutomationState.TravelingToGiver, "已到皇城皇宫，继续前往舞修罗。", false);
            else if (destination == TravelDestination.Giver)
                Publish(expectedGeneration, BountyAutomationState.OpeningGiver, "已到舞修罗所在地图，准备访问 NPC。", false);
            else if (destination == TravelDestination.Target)
                Publish(expectedGeneration, BountyAutomationState.OpeningTarget, "已到任务坐标，准备点击目标 NPC。", false);
        }

        private void OpenGiver(int expectedGeneration)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != BountyAutomationState.OpeningGiver) return;
                state = BountyAutomationState.WaitingForGiverDialog;
                DisposeTimerLocked();
                ScheduleLocked(expectedGeneration, 12000, OnStageTimeout);
            }
            Publish(expectedGeneration, BountyAutomationState.WaitingForGiverDialog, "已访问舞修罗，等待功能列表。", false);
            SendPacket(expectedGeneration, "访问舞修罗", delegate(uint sequence)
            {
                return TianshuBountyProtocol.BuildNpcOpen(TianshuBountyProtocol.BountyGiverNpcId, sequence);
            });
        }

        private void HandleNpcDialog(int expectedGeneration, byte[] bytes)
        {
            BountyAutomationState current;
            lock (syncRoot) current = state;
            if (current != BountyAutomationState.WaitingForGiverDialog) return;
            int npcId;
            string functionId;
            if (!TianshuBountyProtocol.TryParseNpcFunction(bytes, "除暴安良", out npcId, out functionId) ||
                npcId != TianshuBountyProtocol.BountyGiverNpcId)
            {
                Fail(expectedGeneration, "舞修罗功能列表中未找到“除暴安良”，没有发送猜测报文。");
                return;
            }
            bool claimingReward;
            BountyAutomationState waitingState;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != BountyAutomationState.WaitingForGiverDialog) return;
                claimingReward = claimRoundReward;
                waitingState = claimingReward
                    ? BountyAutomationState.WaitingForRoundReward
                    : BountyAutomationState.WaitingForBountyConfirmation;
                state = waitingState;
                DisposeTimerLocked();
                ScheduleLocked(expectedGeneration, claimingReward ? 15000 : 12000, OnStageTimeout);
            }
            Publish(expectedGeneration, waitingState,
                "从 NPC 功能列表解析到除暴安良功能号 " + functionId +
                (claimingReward ? "，正在领取本轮第 10 环奖励。" : "，正在选择。"), false);
            SendPacket(expectedGeneration, "选择除暴安良", delegate(uint sequence)
            {
                return TianshuBountyProtocol.BuildNpcFunction(npcId, functionId, sequence);
            });
        }

        private void HandleTaskUpdate(int expectedGeneration, byte[] bytes)
        {
            BountyTravelTarget enemy;
            BountyTravelTarget giver;
            if (!TianshuBountyProtocol.TryParseTaskUpdate(bytes, out enemy, out giver)) return;
            int parsedTaskNumber;
            TianshuBountyProtocol.TryParseTaskNumber(bytes, out parsedTaskNumber);
            bool startTravel;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                currentTarget = enemy.Clone();
                if (parsedTaskNumber > 0) currentTaskNumber = parsedTaskNumber;
                if (giver != null) giverTarget = giver.Clone();
                startTravel = state == BountyAutomationState.WaitingForTask ||
                    state == BountyAutomationState.WaitingForBountyConfirmation ||
                    state == BountyAutomationState.ConfirmingBounty;
            }
            Publish(expectedGeneration, State,
                "已从任务 HTML" + (giver == null ? string.Empty : "（含返回链接）") +
                (parsedTaskNumber > 0 ? "解析到第 " + parsedTaskNumber + " 环，" : "解析到") + "目标：" + enemy, false);
            if (startTravel)
            {
                BeginTravel(expectedGeneration, enemy, TravelDestination.Target,
                    BountyAutomationState.TravelingToTarget, "除暴目标");
            }
        }

        private void OpenTarget(int expectedGeneration)
        {
            BountyTravelTarget target;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != BountyAutomationState.OpeningTarget || currentTarget == null) return;
                target = currentTarget.Clone();
                state = BountyAutomationState.WaitingForBattle;
                DisposeTimerLocked();
                ScheduleLocked(expectedGeneration, 20000, OnStageTimeout);
            }
            Publish(expectedGeneration, BountyAutomationState.WaitingForBattle, "点击任务 NPC " + target.NpcId + "，等待战斗轮次。", false);
            SendPacket(expectedGeneration, "点击除暴目标", delegate(uint sequence)
            {
                return TianshuBountyProtocol.BuildNpcOpen(target.NpcId, sequence);
            });
        }

        private void HandleBattlePrompt(int expectedGeneration)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) ||
                    (state != BountyAutomationState.WaitingForBattle && state != BountyAutomationState.InBattle)) return;
                state = BountyAutomationState.InBattle;
                DisposeTimerLocked();
                ScheduleLocked(expectedGeneration, 650, AdvanceBattle);
            }
            Publish(expectedGeneration, BountyAutomationState.InBattle, "收到服务端战斗行动提示 0x002C，准备推进本回合。", false);
        }

        private void AdvanceBattle(int expectedGeneration)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != BountyAutomationState.InBattle) return;
                DisposeTimerLocked();
                ScheduleLocked(expectedGeneration, 60000, OnStageTimeout);
            }
            SendPacket(expectedGeneration, "推进战斗回合", delegate(uint sequence)
            {
                return TianshuBountyProtocol.BuildBattleAdvance(sequence);
            });
        }

        private void HandleNpcRemoved(int expectedGeneration, byte[] bytes)
        {
            int removedNpcId;
            if (!TianshuBountyProtocol.TryParseNpcRemoved(bytes, out removedNpcId)) return;
            BountyTravelTarget target;
            int completed;
            int completedInRound;
            bool tenthTask;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) ||
                    (state != BountyAutomationState.InBattle && state != BountyAutomationState.WaitingForBattle) ||
                    currentTarget == null || currentTarget.NpcId != removedNpcId) return;
                target = currentTarget.Clone();
                completedTasks++;
                completed = completedTasks;
                if (currentTaskNumber > 0)
                    completedTasksInRound = Math.Max(completedTasksInRound, currentTaskNumber);
                else
                    completedTasksInRound++;
                completedInRound = completedTasksInRound;
                tenthTask = completedTasksInRound >= 10;
                claimRoundReward = tenthTask;
                state = BountyAutomationState.ReturningToGiver;
                DisposeTimerLocked();
                ScheduleLocked(expectedGeneration, 500, ReturnToGiver);
            }
            Publish(expectedGeneration, BountyAutomationState.ReturningToGiver,
                "目标 NPC " + target.NpcId + " 已由服务端移除，本轮第 " + completedInRound +
                " 环战斗完成（本次自动化累计 " + completed + " 环）；返回舞修罗" +
                (tenthTask ? "领取轮次奖励。" : "。"), false);
        }

        private void HandleSystemMessage(int expectedGeneration, byte[] bytes)
        {
            BountyAutomationState current;
            lock (syncRoot) current = state;
            if (current == BountyAutomationState.WaitingForRoundReward &&
                TianshuBountyProtocol.ContainsText(bytes, "任务奖励"))
            {
                lock (syncRoot)
                {
                    if (!IsCurrentLocked(expectedGeneration) || state != BountyAutomationState.WaitingForRoundReward) return;
                    DisposeTimerLocked();
                    // The activity reward and inventory updates follow the skill-exp message.
                    ScheduleLocked(expectedGeneration, 1800, FinishRoundReward);
                }
                Publish(expectedGeneration, BountyAutomationState.WaitingForRoundReward,
                    "已收到第 10 环轮次奖励，等待同批活跃度/背包更新完成。", false);
            }
        }

        private void FinishRoundReward(int expectedGeneration)
        {
            bool allDone;
            bool posterRound;
            int finishedRounds;
            int targetRounds;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != BountyAutomationState.WaitingForRoundReward) return;
                completedRounds++;
                finishedRounds = completedRounds;
                targetRounds = plannedRounds;
                completedTasksInRound = 0;
                currentTaskNumber = 0;
                claimRoundReward = false;
                allDone = completedRounds >= plannedRounds;
                posterRound = finishedRounds >= 2 && allowWantedPoster;
                DisposeTimerLocked();
                if (allDone)
                {
                    state = BountyAutomationState.Completed;
                }
                else
                {
                    state = BountyAutomationState.OpeningGiver;
                    ScheduleLocked(expectedGeneration, 500, OpenGiver);
                }
            }
            if (allDone)
            {
                Complete(expectedGeneration);
            }
            else
            {
                Publish(expectedGeneration, BountyAutomationState.OpeningGiver,
                    "第 " + finishedRounds + "/" + targetRounds + " 轮奖励已领取；再次访问舞修罗开始下一轮" +
                    (posterRound ? "（允许按服务端提示使用通缉令）" : string.Empty) + "。", false);
            }
        }

        private void ReturnToGiver(int expectedGeneration)
        {
            BeginTravel(expectedGeneration, GetGiverTarget(), TravelDestination.Giver,
                BountyAutomationState.ReturningToGiver, "舞修罗");
        }

        private void Complete(int expectedGeneration)
        {
            bool restoreActive;
            int completed;
            int rounds;
            lock (syncRoot)
            {
                if (disposed || generation != expectedGeneration || state != BountyAutomationState.Completed) return;
                DisposeTimerLocked();
                restoreActive = activatedByAutomation;
                activatedByAutomation = false;
                completed = completedTasks;
                rounds = completedRounds;
            }
            Publish(expectedGeneration, BountyAutomationState.Completed,
                "已完成并领取 " + rounds + " 轮除暴奖励（本次自动化处理 " + completed + " 环），自动流程结束。", false);
            service.ReleaseAutomation(AutomationOwner);
            if (restoreActive && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void SendPacket(int expectedGeneration, string description, Func<uint, byte[]> factory)
        {
            long connectionId;
            uint sequence;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || gameConnectionId == 0 || !sequenceKnown) return;
                connectionId = gameConnectionId;
                sequence = nextSequence;
                nextSequence++;
            }
            byte[] packet = factory(sequence);
            if (!service.Send(connectionId, packet))
            {
                Fail(expectedGeneration, description + "发包失败；连接可能已关闭或 ACTIVE 已被关闭。");
                return;
            }
            service.ReportEngineEvent("AutoBounty", "INFO",
                description + "，opcode=0x" + ((packet[2] << 8) | packet[3]).ToString("X4") +
                "，seq=" + sequence + "，len=" + packet.Length, connectionId);
        }

        private void ObserveSequenceLocked(int opcode, byte[] bytes)
        {
            uint observed;
            if (!TianshuBountyProtocol.TryReadClientSequence(bytes, out observed)) return;
            bool confirmedSequencePacket = IsConfirmedSequenceOpcode(opcode);
            if (!sequenceKnown)
            {
                if (!confirmedSequencePacket) return;
                nextSequence = observed + 1;
                sequenceKnown = true;
                return;
            }
            uint candidate = observed + 1;
            if (candidate >= nextSequence && candidate - nextSequence < 4096) nextSequence = candidate;
        }

        private void OnStageTimeout(int expectedGeneration)
        {
            BountyAutomationState timedOutState;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                timedOutState = state;
            }
            Fail(expectedGeneration, "等待阶段超时：" + timedOutState + "。已停止继续发包。请保留本次会话库用于比对。");
        }

        private void Fail(int expectedGeneration, string message)
        {
            bool restoreActive;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = BountyAutomationState.Failed;
                generation++;
                DisposeTimerLocked();
                restoreActive = activatedByAutomation;
                activatedByAutomation = false;
            }
            PublishAny(BountyAutomationState.Failed, message, true);
            service.ReleaseAutomation(AutomationOwner);
            if (restoreActive && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void StopCore(BountyAutomationState finalState, string message, bool error)
        {
            bool restoreActive;
            lock (syncRoot)
            {
                if (disposed || !IsRunning(state)) return;
                state = finalState;
                generation++;
                DisposeTimerLocked();
                pendingDestination = TravelDestination.None;
                restoreActive = activatedByAutomation;
                activatedByAutomation = false;
            }
            PublishAny(finalState, message, error);
            service.ReleaseAutomation(AutomationOwner);
            if (restoreActive && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void Publish(int expectedGeneration, BountyAutomationState publishedState, string message, bool error)
        {
            lock (syncRoot)
            {
                if (disposed || generation != expectedGeneration) return;
            }
            PublishAny(publishedState, message, error);
        }

        private void PublishAny(BountyAutomationState publishedState, string message, bool error)
        {
            Action<BountyAutomationState, string> handler = StatusChanged;
            if (handler != null) handler(publishedState, message);
            long connectionId = GetGameConnectionId();
            service.ReportEngineEvent("AutoBounty", error ? "ERROR" : "INFO", message,
                connectionId == 0 ? (long?)null : connectionId);
        }

        private long SelectLatestGameConnectionLocked()
        {
            long result = 0;
            foreach (KeyValuePair<long, ConnectionSession> pair in gameConnections)
            {
                if (!pair.Value.ClosedUtc.HasValue && pair.Value.RemotePort != 80 && pair.Value.RemotePort != 443 &&
                    pair.Key > result) result = pair.Key;
            }
            if (result != 0) return result;
            foreach (KeyValuePair<long, ConnectionSession> pair in gameConnections)
            {
                if (!pair.Value.ClosedUtc.HasValue && pair.Key > result) result = pair.Key;
            }
            return result;
        }

        private static bool IsConfirmedSequenceOpcode(int opcode)
        {
            return opcode == TianshuBountyProtocol.ClientTravelLink ||
                opcode == TianshuBountyProtocol.ClientNpcOpen ||
                opcode == TianshuBountyProtocol.ClientNpcFunction ||
                opcode == TianshuBountyProtocol.ClientDialogResponse ||
                opcode == TianshuBountyProtocol.ClientBattleAdvance ||
                opcode == 0x002E || opcode == 0x0042 || opcode == 0x00C1;
        }

        private long GetGameConnectionId()
        {
            lock (syncRoot) return gameConnectionId;
        }

        private BountyTravelTarget GetGiverTarget()
        {
            lock (syncRoot) return giverTarget.Clone();
        }

        private bool IsCurrentLocked(int expectedGeneration)
        {
            return !disposed && generation == expectedGeneration && IsRunning(state);
        }

        private static bool IsTravelState(BountyAutomationState value)
        {
            return value == BountyAutomationState.TravelingToPalace ||
                value == BountyAutomationState.TravelingToGiver ||
                value == BountyAutomationState.TravelingToTarget ||
                value == BountyAutomationState.ReturningToGiver;
        }

        private static bool IsRunning(BountyAutomationState value)
        {
            return value != BountyAutomationState.Inactive && value != BountyAutomationState.Completed &&
                value != BountyAutomationState.Stopped && value != BountyAutomationState.Failed;
        }

        private void ScheduleLocked(int expectedGeneration, int delayMs, Action<int> callback)
        {
            DisposeTimerLocked();
            timer = new System.Threading.Timer(delegate { callback(expectedGeneration); }, null, delayMs, Timeout.Infinite);
        }

        private void DisposeTimerLocked()
        {
            if (timer == null) return;
            timer.Dispose();
            timer = null;
        }
    }

    public sealed class BountyAutomationControl : UserControl
    {
        private readonly BountyAutomationCoordinator coordinator;
        private readonly NumericUpDown taskCount;
        private readonly CheckBox useWantedPoster;
        private readonly CheckBox viaPalace;
        private readonly Label stateLabel;
        private readonly Label progressLabel;
        private readonly Label targetLabel;
        private readonly Button startButton;
        private readonly Button stopButton;

        public BountyAutomationControl(BountyAutomationCoordinator coordinator)
        {
            if (coordinator == null) throw new ArgumentNullException("coordinator");
            this.coordinator = coordinator;
            Dock = DockStyle.Fill;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(8);
            layout.ColumnCount = 1;
            layout.RowCount = 6;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label help = new Label();
            help.AutoSize = true;
            help.MaximumSize = new Size(700, 0);
            help.Text = "每轮10环，默认直接前往舞修罗；第10环会再次访问除暴功能并等待轮奖。动态解析 token、序号和任务坐标；仅勾选后才允许通缉令第3轮。Ctrl+Shift+F12 可紧急旁路。";
            layout.Controls.Add(help, 0, 0);

            FlowLayoutPanel options = new FlowLayoutPanel();
            options.AutoSize = true;
            options.WrapContents = false;
            options.Controls.Add(new Label { Text = "轮数（每轮10环）", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            taskCount = new NumericUpDown { Minimum = 1, Maximum = 2, Value = 2, Width = 60 };
            options.Controls.Add(taskCount);
            useWantedPoster = new CheckBox { Text = "有通缉令时追加第3轮", Checked = false, AutoSize = true, Padding = new Padding(8, 4, 0, 0) };
            options.Controls.Add(useWantedPoster);
            viaPalace = new CheckBox { Text = "启动时先经皇城皇宫（兼容）", Checked = false, AutoSize = true, Padding = new Padding(8, 4, 0, 0) };
            options.Controls.Add(viaPalace);
            layout.Controls.Add(options, 0, 1);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            startButton = new Button { Text = "开始自动除暴", AutoSize = true };
            stopButton = new Button { Text = "停止", AutoSize = true, Enabled = false };
            startButton.Click += OnStartClick;
            stopButton.Click += delegate { coordinator.Stop(); };
            buttons.Controls.Add(startButton);
            buttons.Controls.Add(stopButton);
            layout.Controls.Add(buttons, 0, 2);

            stateLabel = new Label { AutoSize = true, Text = "状态：" + coordinator.State };
            progressLabel = new Label { AutoSize = true, Text = "进度：第 0/0 轮，本轮 0/10 环" };
            targetLabel = new Label { AutoSize = true, Text = "当前目标：尚未解析" };
            layout.Controls.Add(stateLabel, 0, 3);
            layout.Controls.Add(progressLabel, 0, 4);
            layout.Controls.Add(targetLabel, 0, 5);

            Controls.Add(layout);
            coordinator.StatusChanged += OnStatusChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) coordinator.StatusChanged -= OnStatusChanged;
            base.Dispose(disposing);
        }

        private void OnStartClick(object sender, EventArgs e)
        {
            try
            {
                coordinator.Start((int)taskCount.Value, useWantedPoster.Checked, viaPalace.Checked);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法启动自动除暴", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnStatusChanged(BountyAutomationState automationState, string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<BountyAutomationState, string>(OnStatusChanged), automationState, message); }
                catch (InvalidOperationException) { }
                return;
            }
            bool running = automationState != BountyAutomationState.Inactive &&
                automationState != BountyAutomationState.Completed &&
                automationState != BountyAutomationState.Stopped &&
                automationState != BountyAutomationState.Failed;
            stateLabel.Text = "状态：" + automationState;
            progressLabel.Text = "进度：已领奖 " + coordinator.CompletedRounds + "/" + coordinator.PlannedRounds +
                " 轮，本轮 " + coordinator.CompletedTasksInRound + "/10 环（本次累计 " + coordinator.CompletedTasks + " 环）";
            BountyTravelTarget target = coordinator.CurrentTarget;
            targetLabel.Text = "当前目标：" + (target == null ? "尚未解析" : target.ToString());
            startButton.Enabled = !running;
            stopButton.Enabled = running;
        }
    }
}
