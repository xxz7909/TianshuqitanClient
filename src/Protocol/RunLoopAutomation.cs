using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace TianshuQitanLauncher.Protocol
{
    public enum RunLoopTaskKind
    {
        Unknown = 0,
        DeliverItem = 1,
        Hunt = 2
    }

    public enum RunLoopAutomationState
    {
        Inactive = 0,
        WaitingForGameConnection = 1,
        WaitingForTask = 2,
        Traveling = 3,
        ConfirmingTeleport = 4,
        WaitingForMap = 5,
        OpeningNpc = 6,
        WaitingForNpcDialog = 7,
        SelectingFunction = 8,
        WalkingForEncounter = 9,
        WaitingForCombatResult = 10,
        Completed = 11,
        Stopped = 12,
        Failed = 13
    }

    public sealed class RunLoopTask
    {
        public int RingNumber { get; set; }
        public RunLoopTaskKind Kind { get; set; }
        public string Description { get; set; }
        public string ItemName { get; set; }
        public string HuntMapName { get; set; }
        public string ObjectiveName { get; set; }
        public string TurnInMapName { get; set; }
        public string TurnInNpcName { get; set; }
        public int TurnInMapId { get; set; }
        public int TurnInNpcId { get; set; }
        public int TurnInX { get; set; }
        public int TurnInY { get; set; }
        public int Progress { get; set; }
        public int Required { get; set; }

        public bool IsComplete { get { return Required > 0 && Progress >= Required; } }

        public RunLoopTask Clone()
        {
            return (RunLoopTask)MemberwiseClone();
        }

        public override string ToString()
        {
            string kind = Kind == RunLoopTaskKind.DeliverItem ? "提交道具" :
                (Kind == RunLoopTaskKind.Hunt ? "战斗" : "未知");
            string progress = Required > 0 ? " " + Progress + "/" + Required : string.Empty;
            return "第 " + RingNumber + " 环 / " + kind + progress + " / " + (Description ?? string.Empty);
        }
    }

    public sealed class RunLoopMapInfo
    {
        public int MapId { get; set; }
        public string MapName { get; set; }
        public ushort ScaledX { get; set; }
        public ushort ScaledY { get; set; }
    }

    public sealed class RunLoopEntity
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }

    public static class TianshuRunLoopProtocol
    {
        public const int ClientMovement = 0x00C1;
        public const int ClientUiAction = 0x002E;
        public const int OneKeyRecoveryAction = 0x0321;
        public const int ServerTaskUpdate = 0x003E;
        public const int ServerMapInfo = 0x0055;
        public const int ServerEntityList = 0x001C;
        public const int ServerNearbyEntities = 0x006A;
        public const string TaskId = "140000000";
        public const string AcceptFunctionId = "140000001";
        public const string TurnInFunctionId = "140000002";

        private static readonly Regex RingRegex = new Regex(@"第\s*(\d+)\s*环", RegexOptions.CultureInvariant);
        private static readonly Regex DeliveryRegex = new Regex(
            @"将(?<item>.+?)送给到(?<map>.+?)的(?<npc>.+?)\((?<x>\d+)\s*,\s*(?<y>\d+)\)",
            RegexOptions.CultureInvariant);
        private static readonly Regex HuntMapRegex = new Regex(@"^去(?<map>.+?)消灭", RegexOptions.CultureInvariant);
        private static readonly Regex TurnInRegex = new Regex(
            @"到(?<map>[^，。]+?)的(?<npc>[^，。]+?)\((?<x>\d+)\s*,\s*(?<y>\d+)\)处领取",
            RegexOptions.CultureInvariant);
        private static readonly Regex ProgressRegex = new Regex(
            @"(?<name>[^,，]+?)\s*[（(]\s*(?<current>\d+)\s*/\s*(?<required>\d+)\s*[）)]",
            RegexOptions.CultureInvariant);
        private static readonly Regex HtmlRegex = new Regex(@"<[^>]*>", RegexOptions.CultureInvariant);

        public static byte[] BuildMovement(long epochMilliseconds, int mapId, ushort scaledX, ushort scaledY, uint sequence)
        {
            PacketWriter writer = new PacketWriter(ClientMovement);
            writer.WriteUInt64((ulong)epochMilliseconds);
            writer.WriteUInt32((uint)mapId);
            writer.WriteUInt16(scaledX);
            writer.WriteUInt16(scaledY);
            writer.WriteUInt32(sequence);
            return writer.ToArray();
        }

        public static bool TryParseMovement(byte[] bytes, out long epochMilliseconds, out int mapId,
            out ushort scaledX, out ushort scaledY)
        {
            epochMilliseconds = 0;
            mapId = 0;
            scaledX = 0;
            scaledY = 0;
            if (!TianshuBountyProtocol.HasOpcode(bytes, ClientMovement) || bytes.Length != 24) return false;
            epochMilliseconds = (long)ReadUInt64(bytes, 4);
            mapId = (int)ReadUInt32(bytes, 12);
            scaledX = ReadUInt16(bytes, 16);
            scaledY = ReadUInt16(bytes, 18);
            return mapId > 0;
        }

        public static byte[] BuildOneKeyRecovery(uint sequence)
        {
            PacketWriter writer = new PacketWriter(ClientUiAction);
            writer.WriteUInt32(OneKeyRecoveryAction);
            writer.WriteUInt32(1);
            writer.WriteUInt32(3);
            writer.WriteUInt32(0);
            writer.WriteUInt32(sequence);
            return writer.ToArray();
        }

        public static bool TryParseOneKeyRecovery(byte[] bytes)
        {
            return TianshuBountyProtocol.HasOpcode(bytes, ClientUiAction) && bytes.Length == 24 &&
                ReadUInt32(bytes, 4) == OneKeyRecoveryAction && ReadUInt32(bytes, 8) == 1 &&
                ReadUInt32(bytes, 12) == 3;
        }

        public static bool TryParseMapInfo(byte[] bytes, out RunLoopMapInfo map)
        {
            map = null;
            if (!TianshuBountyProtocol.HasOpcode(bytes, ServerMapInfo) || bytes.Length < 12) return false;
            int offset = 4;
            uint mapId;
            string mapName;
            if (!TryReadUInt32(bytes, ref offset, out mapId) || !TryReadString(bytes, ref offset, out mapName)) return false;
            uint x = 0;
            uint y = 0;
            if (offset + 8 <= bytes.Length)
            {
                x = ReadUInt32(bytes, offset);
                y = ReadUInt32(bytes, offset + 4);
            }
            map = new RunLoopMapInfo
            {
                MapId = (int)mapId,
                MapName = NormalizeName(mapName),
                ScaledX = (ushort)Math.Min(UInt16.MaxValue, x),
                ScaledY = (ushort)Math.Min(UInt16.MaxValue, y)
            };
            return map.MapId > 0;
        }

        public static bool TryParseTask(byte[] bytes, out RunLoopTask task)
        {
            task = null;
            if (!TianshuBountyProtocol.HasOpcode(bytes, ServerTaskUpdate)) return false;
            IList<string> strings = ExtractStrings(bytes);
            bool correctTask = false;
            string description = null;
            int ring = 0;
            for (int i = 0; i < strings.Count; i++)
            {
                string value = strings[i];
                string visibleText = NormalizeTaskText(value);
                if (string.Equals(value, TaskId, StringComparison.Ordinal) ||
                    visibleText.IndexOf(TaskId, StringComparison.Ordinal) >= 0) correctTask = true;
                Match ringMatch = RingRegex.Match(visibleText);
                int parsedRing;
                if (ringMatch.Success && Int32.TryParse(ringMatch.Groups[1].Value, out parsedRing) &&
                    parsedRing >= 1 && parsedRing <= 20)
                {
                    ring = parsedRing;
                }
                int deliveryStart = visibleText.IndexOf("将", StringComparison.Ordinal);
                int huntStart = visibleText.IndexOf("去", StringComparison.Ordinal);
                int descriptionStart = deliveryStart < 0 ? huntStart :
                    (huntStart < 0 ? deliveryStart : Math.Min(deliveryStart, huntStart));
                if (descriptionStart >= 0 && visibleText.IndexOf("领取下一环任务", descriptionStart,
                    StringComparison.Ordinal) >= 0)
                {
                    description = visibleText.Substring(descriptionStart);
                }
            }
            if (!correctTask || ring == 0 || string.IsNullOrWhiteSpace(description)) return false;

            RunLoopTask parsed = new RunLoopTask { RingNumber = ring, Description = description };
            Match delivery = DeliveryRegex.Match(description);
            if (delivery.Success)
            {
                parsed.Kind = RunLoopTaskKind.DeliverItem;
                parsed.ItemName = delivery.Groups["item"].Value.Trim();
                ApplyTurnIn(parsed, delivery);
                parsed.Progress = 0;
                parsed.Required = 1;
            }
            else
            {
                Match huntMap = HuntMapRegex.Match(description);
                Match turnIn = TurnInRegex.Match(description);
                if (!huntMap.Success || !turnIn.Success) return false;
                parsed.Kind = RunLoopTaskKind.Hunt;
                parsed.HuntMapName = NormalizeName(huntMap.Groups["map"].Value);
                ApplyTurnIn(parsed, turnIn);
                for (int i = 0; i < strings.Count; i++)
                {
                    Match progress = ProgressRegex.Match(NormalizeTaskText(strings[i]));
                    int current;
                    int required;
                    if (!progress.Success || !Int32.TryParse(progress.Groups["current"].Value, out current) ||
                        !Int32.TryParse(progress.Groups["required"].Value, out required) || required <= 0) continue;
                    parsed.ObjectiveName = progress.Groups["name"].Value.Trim();
                    parsed.Progress = current;
                    parsed.Required = required;
                    break;
                }
                if (parsed.Required <= 0)
                {
                    Match requiredInDescription = Regex.Match(description, @"(?:消灭|得到)\s*(\d+)\s*个",
                        RegexOptions.CultureInvariant);
                    int required;
                    parsed.Required = requiredInDescription.Success && Int32.TryParse(requiredInDescription.Groups[1].Value, out required)
                        ? required : 1;
                }
            }
            ApplyRecordedTravelLink(parsed, strings);
            task = parsed;
            return true;
        }

        public static IList<RunLoopEntity> ExtractEntities(byte[] bytes)
        {
            List<RunLoopEntity> result = new List<RunLoopEntity>();
            if (!TianshuBountyProtocol.HasOpcode(bytes, ServerEntityList) &&
                !TianshuBountyProtocol.HasOpcode(bytes, ServerNearbyEntities)) return result;
            for (int offset = 8; offset + 8 <= bytes.Length; offset++)
            {
                int nameLength = ReadUInt16(bytes, offset);
                if (nameLength < 3 || nameLength > 96 || offset < 4 || offset + 2 + nameLength + 6 > bytes.Length) continue;
                string name;
                try { name = new UTF8Encoding(false, true).GetString(bytes, offset + 2, nameLength); }
                catch (DecoderFallbackException) { continue; }
                name = NormalizeName(name);
                if (!ContainsCjk(name) || name.Length > 40) continue;
                int id = (int)ReadUInt32(bytes, offset - 4);
                int tail = offset + 2 + nameLength;
                int type = ReadUInt16(bytes, tail);
                int x = ReadUInt16(bytes, tail + 2);
                int y = ReadUInt16(bytes, tail + 4);
                if (id <= 0 || type > 32 || x > 2000 || y > 2000) continue;
                bool exists = false;
                for (int i = 0; i < result.Count; i++) if (result[i].Id == id) { exists = true; break; }
                if (!exists) result.Add(new RunLoopEntity { Id = id, Name = name, X = x, Y = y });
            }
            return result;
        }

        public static bool ContainsFunction(byte[] bytes, int expectedNpcId, string expectedFunctionId)
        {
            if (!TianshuBountyProtocol.HasOpcode(bytes, TianshuBountyProtocol.ServerNpcDialog) || bytes.Length < 10 ||
                (int)ReadUInt32(bytes, 4) != expectedNpcId) return false;
            IList<string> strings = ExtractStrings(bytes);
            for (int i = 0; i < strings.Count; i++)
                if (string.Equals(strings[i], expectedFunctionId, StringComparison.Ordinal)) return true;
            return false;
        }

        public static string NormalizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return HtmlRegex.Replace(value, string.Empty).Replace(" ", string.Empty).Replace("\t", string.Empty).Trim();
        }

        private static string NormalizeTaskText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return HtmlRegex.Replace(value, string.Empty)
                .Replace("&nbsp;", " ").Replace("&#160;", " ")
                .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&")
                .Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        }

        private static void ApplyRecordedTravelLink(RunLoopTask task, IList<string> strings)
        {
            if (task == null || strings == null) return;
            BountyTravelTarget selected = null;
            for (int i = 0; i < strings.Count; i++)
            {
                IList<BountyTravelTarget> links = TianshuBountyProtocol.ExtractTravelLinks(strings[i]);
                for (int j = 0; j < links.Count; j++)
                {
                    BountyTravelTarget link = links[j];
                    if (link.X == task.TurnInX && link.Y == task.TurnInY)
                    {
                        selected = link;
                        break;
                    }
                    if (selected == null) selected = link;
                }
                if (selected != null && selected.X == task.TurnInX && selected.Y == task.TurnInY) break;
            }
            if (selected == null) return;
            task.TurnInNpcId = selected.NpcId;
            task.TurnInMapId = selected.MapId;
            task.TurnInX = selected.X;
            task.TurnInY = selected.Y;
        }

        private static void ApplyTurnIn(RunLoopTask task, Match match)
        {
            task.TurnInMapName = NormalizeName(match.Groups["map"].Value);
            task.TurnInNpcName = NormalizeName(match.Groups["npc"].Value);
            task.TurnInX = Int32.Parse(match.Groups["x"].Value);
            task.TurnInY = Int32.Parse(match.Groups["y"].Value);
        }

        private static IList<string> ExtractStrings(byte[] bytes)
        {
            List<string> result = new List<string>();
            for (int offset = 4; offset + 2 <= bytes.Length; offset++)
            {
                string value;
                int ignored;
                if (!TryReadStringAt(bytes, offset, out value, out ignored) || string.IsNullOrWhiteSpace(value)) continue;
                AddTextCandidate(result, value);
            }
            return result;
        }

        private static void AddTextCandidate(IList<string> result, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            AddUnique(result, value);
            string compact = Regex.Replace(value, @"\s+", string.Empty);
            if (compact.Length < 16 || compact.Length % 4 != 0) return;
            try
            {
                byte[] decodedBytes = Convert.FromBase64String(compact);
                string decoded = new UTF8Encoding(false, true).GetString(decodedBytes);
                if (decoded.Length > 0) AddUnique(result, decoded);
            }
            catch (FormatException) { }
            catch (DecoderFallbackException) { }
        }

        private static void AddUnique(IList<string> result, string value)
        {
            for (int i = 0; i < result.Count; i++)
                if (string.Equals(result[i], value, StringComparison.Ordinal)) return;
            result.Add(value);
        }

        private static bool ContainsCjk(string value)
        {
            for (int i = 0; i < value.Length; i++)
                if (value[i] >= '\u3400' && value[i] <= '\u9FFF') return true;
            return false;
        }

        private static bool TryReadString(byte[] bytes, ref int offset, out string value)
        {
            int next;
            if (!TryReadStringAt(bytes, offset, out value, out next)) return false;
            offset = next;
            return true;
        }

        private static bool TryReadStringAt(byte[] bytes, int offset, out string value, out int next)
        {
            value = null;
            next = offset;
            if (bytes == null || offset < 0 || offset + 2 > bytes.Length) return false;
            int length = ReadUInt16(bytes, offset);
            if (length < 0 || offset + 2 + length > bytes.Length) return false;
            try
            {
                value = new UTF8Encoding(false, true).GetString(bytes, offset + 2, length);
                next = offset + 2 + length;
                return true;
            }
            catch (DecoderFallbackException) { return false; }
        }

        private static bool TryReadUInt32(byte[] bytes, ref int offset, out uint value)
        {
            value = 0;
            if (offset + 4 > bytes.Length) return false;
            value = ReadUInt32(bytes, offset);
            offset += 4;
            return true;
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

        private static ulong ReadUInt64(byte[] bytes, int offset)
        {
            return ((ulong)ReadUInt32(bytes, offset) << 32) | ReadUInt32(bytes, offset + 4);
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

            public void WriteUInt64(ulong value)
            {
                WriteUInt32((uint)(value >> 32));
                WriteUInt32((uint)value);
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

    public sealed class RunLoopAutomationCoordinator : IDisposable
    {
        private const string AutomationOwner = "AutoRunLoop";
        private const int StartNpcId = 93290;
        private const int StartMapId = 72;
        private const int RingsPerRound = 20;
        private const int DailyMaximumRounds = 4;

        private enum DestinationPurpose { None, StartNpc, TurnInNpc, Hunt }

        private readonly object syncRoot = new object();
        private readonly ProtocolWorkbenchService service;
        private readonly Dictionary<long, ConnectionSession> gameConnections = new Dictionary<long, ConnectionSession>();
        private readonly Dictionary<string, BountyTravelTarget> npcDirectory = new Dictionary<string, BountyTravelTarget>(StringComparer.Ordinal);
        private readonly ManualResetEvent catalogReady = new ManualResetEvent(false);
        private System.Threading.Timer stageTimer;
        private System.Threading.Timer walkTimer;
        private System.Threading.Timer recoveryTimer;
        private RunLoopAutomationState state;
        private RunLoopTask latestTask;
        private DateTime latestTaskUtc;
        private BountyTravelTarget currentDestination;
        private DestinationPurpose destinationPurpose;
        private List<BountyTravelTarget> route;
        private int routeIndex;
        private int expectedMapAfterPortal;
        private long gameConnectionId;
        private uint nextSequence;
        private bool sequenceKnown;
        private int currentMapId;
        private string currentMapName;
        private ushort currentScaledX;
        private ushort currentScaledY;
        private int plannedRounds;
        private int completedRounds;
        private int completedRings;
        private bool automaticRecovery;
        private bool activatedByAutomation;
        private int walkStep;
        private int generation;
        private bool disposed;

        public event Action<RunLoopAutomationState, string> StatusChanged;

        public RunLoopAutomationCoordinator(ProtocolWorkbenchService service)
        {
            if (service == null) throw new ArgumentNullException("service");
            this.service = service;
            state = RunLoopAutomationState.Inactive;
            SeedNpcDirectory();
            ThreadPool.QueueUserWorkItem(delegate { LoadNpcDirectoryFromSessions(); });
            service.ConnectionChanged += OnConnectionChanged;
            service.FrameCaptured += OnFrameCaptured;
            service.ActiveModeChanged += OnActiveModeChanged;
        }

        public RunLoopAutomationState State { get { lock (syncRoot) return state; } }
        public int CompletedRounds { get { lock (syncRoot) return completedRounds; } }
        public int CompletedRings { get { lock (syncRoot) return completedRings; } }
        public int PlannedRounds { get { lock (syncRoot) return plannedRounds; } }
        public RunLoopTask CurrentTask { get { lock (syncRoot) return latestTask == null ? null : latestTask.Clone(); } }

        public void Start(int roundCount, bool enableAutomaticRecovery)
        {
            if (roundCount < 1 || roundCount > DailyMaximumRounds) throw new ArgumentOutOfRangeException("roundCount");
            string owner;
            if (!service.TryAcquireAutomation(AutomationOwner, out owner))
                throw new InvalidOperationException("当前已有自动化流程正在运行：" + owner + "。请先停止后再启动自动跑环。");
            bool enableActive;
            bool canBegin;
            bool useExistingTask;
            RunLoopTask existingTask = null;
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
                plannedRounds = roundCount;
                completedRounds = 0;
                completedRings = 0;
                automaticRecovery = enableAutomaticRecovery;
                route = null;
                routeIndex = 0;
                destinationPurpose = DestinationPurpose.None;
                expectedMapAfterPortal = 0;
                enableActive = !service.ActiveMode;
                activatedByAutomation = enableActive;
                canBegin = gameConnectionId != 0 && sequenceKnown;
                useExistingTask = canBegin && latestTask != null && DateTime.UtcNow - latestTaskUtc < TimeSpan.FromMinutes(30);
                if (useExistingTask) existingTask = latestTask.Clone();
                else if (latestTask != null && DateTime.UtcNow - latestTaskUtc >= TimeSpan.FromMinutes(30)) latestTask = null;
                state = !canBegin ? RunLoopAutomationState.WaitingForGameConnection :
                    (useExistingTask ? RunLoopAutomationState.WaitingForTask : RunLoopAutomationState.Traveling);
            }
            if (enableActive) service.SetActiveMode(true, false);
            Publish(currentGeneration, state, "自动跑环已启动：每轮 20 环，本次最多 " + roundCount +
                " 轮（每日服务端上限 4 轮）" + (enableAutomaticRecovery ? "，战斗中每 4 秒自动恢复。" : "。"), false);
            if (canBegin)
            {
                if (useExistingTask) DispatchTask(currentGeneration, existingTask);
                else BeginStartNpc(currentGeneration);
            }
        }

        public void Stop()
        {
            StopCore(RunLoopAutomationState.Stopped, "自动跑环已停止。", false);
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

        private void SeedNpcDirectory()
        {
            AddNpc("柳先元", StartNpcId, StartMapId, 23, 132);
            AddNpc("海蚌族蚌岳珊", 94018, 102, 2, 16);
            AddNpc("蚌岳珊", 94018, 102, 2, 16);
            AddNpc("刘兴", 8062, 13, 64, 113);
            AddNpc("远征军先锋将军姜海", 93191, 110, 16, 63);
            AddNpc("先锋护卫姜天", 93192, 110, 13, 71);
            AddNpc("先锋护卫刘承", 93193, 110, 14, 65);
            AddNpc("天空远征军斥候队长夏雨", 93194, 110, 18, 64);
            AddNpc("天空远征军斥候武诚初", 93195, 110, 20, 66);
        }

        private void AddNpc(string name, int id, int mapId, int x, int y)
        {
            npcDirectory[TianshuRunLoopProtocol.NormalizeName(name)] =
                new BountyTravelTarget { NpcId = id, MapId = mapId, X = x, Y = y };
        }

        private void LoadNpcDirectoryFromSessions()
        {
            int learned = 0;
            try
            {
                string currentPath = Path.GetFullPath(service.DatabasePath);
                DirectoryInfo dataRoot = new DirectoryInfo(Path.GetDirectoryName(currentPath));
                while (dataRoot != null && !string.Equals(dataRoot.Name, "data", StringComparison.OrdinalIgnoreCase))
                    dataRoot = dataRoot.Parent;
                string searchRoot = dataRoot == null ? Path.GetDirectoryName(currentPath) : dataRoot.FullName;
                List<FileInfo> databases = new List<FileInfo>();
                foreach (string path in Directory.GetFiles(searchRoot, "*.sqlite", SearchOption.AllDirectories))
                {
                    if (!string.Equals(Path.GetFullPath(path), currentPath, StringComparison.OrdinalIgnoreCase))
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
                            "Data Source=" + databases[index].FullName + ";Version=3;Read Only=True;"))
                        {
                            connection.Open();
                            using (SQLiteCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "SELECT opcode,bytes FROM frames WHERE direction=1 AND opcode IN (28,85,106) ORDER BY capture_ordinal;";
                                using (SQLiteDataReader reader = command.ExecuteReader())
                                {
                                    while (reader.Read())
                                    {
                                        int opcode = Convert.ToInt32(reader[0]);
                                        byte[] bytes = (byte[])reader[1];
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
                                            {
                                                RunLoopEntity entity = entities[i];
                                                string name = TianshuRunLoopProtocol.NormalizeName(entity.Name);
                                                if (npcDirectory.ContainsKey(name)) continue;
                                                npcDirectory[name] = new BountyTravelTarget
                                                {
                                                    NpcId = entity.Id,
                                                    MapId = mapId,
                                                    X = entity.X,
                                                    Y = entity.Y
                                                };
                                                learned++;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot learn run-loop NPC catalog from historical sessions", ex);
            }
            finally
            {
                catalogReady.Set();
                bool canReport;
                lock (syncRoot) canReport = !disposed;
                if (learned > 0 && canReport)
                    service.ReportEngineEvent(AutomationOwner, "INFO", "已从历史会话学习 " + learned + " 个 NPC 名称/ID/地图映射。", null);
            }
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
                else gameConnections[connection.Id] = connection.Clone();
                long selected = SelectLatestGameConnectionLocked();
                if (selected != gameConnectionId)
                {
                    gameConnectionId = selected;
                    sequenceKnown = false;
                    nextSequence = 0;
                }
                if (state == RunLoopAutomationState.WaitingForGameConnection && gameConnectionId != 0 && sequenceKnown)
                {
                    currentGeneration = generation;
                    state = RunLoopAutomationState.Traveling;
                    begin = true;
                }
            }
            if (begin) BeginTaskOrStart(currentGeneration);
        }

        private void OnFrameCaptured(ProtocolFrame frame)
        {
            if (frame == null || !frame.Opcode.HasValue || frame.Bytes == null) return;
            int opcode = (int)frame.Opcode.Value;
            int currentGeneration;
            bool begin = false;
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
                    if (state == RunLoopAutomationState.WaitingForGameConnection && sequenceKnown)
                    {
                        state = RunLoopAutomationState.Traveling;
                        begin = true;
                    }
                }
                currentGeneration = generation;
            }
            if (begin) { BeginTaskOrStart(currentGeneration); return; }
            if (frame.Direction != TrafficDirection.ServerToClient || frame.ConnectionId != GetGameConnectionId()) return;

            if (opcode == TianshuRunLoopProtocol.ServerTaskUpdate)
            {
                RunLoopTask task;
                if (TianshuRunLoopProtocol.TryParseTask(frame.Bytes, out task)) HandleTask(currentGeneration, task);
                return;
            }
            if (opcode == TianshuRunLoopProtocol.ServerMapInfo)
            {
                RunLoopMapInfo map;
                if (TianshuRunLoopProtocol.TryParseMapInfo(frame.Bytes, out map)) HandleMap(currentGeneration, map);
                return;
            }
            if (opcode == TianshuRunLoopProtocol.ServerEntityList || opcode == TianshuRunLoopProtocol.ServerNearbyEntities)
            {
                LearnEntities(frame.Bytes);
                return;
            }
            if (!IsRunning(State)) return;
            if (opcode == TianshuBountyProtocol.ServerSystemMessage &&
                TianshuBountyProtocol.ContainsText(frame.Bytes, "今日") &&
                TianshuBountyProtocol.ContainsText(frame.Bytes, "跑环") &&
                (TianshuBountyProtocol.ContainsText(frame.Bytes, "上限") ||
                TianshuBountyProtocol.ContainsText(frame.Bytes, "不能再")))
            {
                Complete(currentGeneration, "服务端提示今日跑环次数已到上限，自动流程结束。");
                return;
            }
            if (opcode == TianshuBountyProtocol.ServerConfirmationDialog)
            {
                HandleConfirmation(currentGeneration, frame.Bytes);
                return;
            }
            if (opcode == TianshuBountyProtocol.ServerNpcDialog) HandleNpcDialog(currentGeneration, frame.Bytes);
        }

        private void HandleTask(int expectedGeneration, RunLoopTask task)
        {
            RunLoopTask previous;
            bool running;
            bool finish = false;
            bool changed = false;
            bool progressChanged = false;
            lock (syncRoot)
            {
                previous = latestTask;
                latestTask = task.Clone();
                latestTaskUtc = DateTime.UtcNow;
                running = IsCurrentLocked(expectedGeneration);
                if (!running) return;
                if (previous != null && previous.RingNumber != task.RingNumber)
                {
                    completedRings++;
                    changed = true;
                    if (previous.RingNumber == RingsPerRound) completedRounds++;
                    finish = completedRounds >= plannedRounds;
                }
                else if (previous != null && previous.Kind == RunLoopTaskKind.Hunt &&
                    task.Kind == RunLoopTaskKind.Hunt && task.Progress > previous.Progress)
                {
                    progressChanged = true;
                }
            }
            if (finish)
            {
                Complete(expectedGeneration, "已完成 " + CompletedRounds + "/" + PlannedRounds +
                    " 轮（每轮 20 环），达到本次设置上限。");
                return;
            }
            if (changed) Publish(expectedGeneration, RunLoopAutomationState.WaitingForTask,
                "上一环已交付；本次已处理 " + CompletedRings + " 环，完整轮数 " + CompletedRounds + "/" + PlannedRounds + "。", false);
            if (!changed && previous != null && previous.RingNumber == task.RingNumber)
            {
                if (!progressChanged) return;
                lock (syncRoot)
                {
                    if (!IsCurrentLocked(expectedGeneration)) return;
                    DisposeStageTimerLocked();
                    DisposeCombatTimersLocked();
                    state = RunLoopAutomationState.WaitingForTask;
                }
                Publish(expectedGeneration, RunLoopAutomationState.WaitingForTask,
                    "第 " + task.RingNumber + " 环战斗进度 " + task.Progress + "/" + task.Required + "。", false);
                if (!task.IsComplete)
                {
                    int huntMapId;
                    if (TryResolveMapId(task.HuntMapName, out huntMapId) && huntMapId == currentMapId)
                        StartWalking(expectedGeneration);
                    else
                        DispatchTask(expectedGeneration, task);
                    return;
                }
            }
            else
            {
                lock (syncRoot)
                {
                    if (!IsCurrentLocked(expectedGeneration)) return;
                    DisposeStageTimerLocked();
                    DisposeCombatTimersLocked();
                    state = RunLoopAutomationState.WaitingForTask;
                }
            }
            DispatchTask(expectedGeneration, task);
        }

        private void DispatchTask(int expectedGeneration, RunLoopTask task)
        {
            if (task.Kind == RunLoopTaskKind.DeliverItem || task.IsComplete)
            {
                BountyTravelTarget target;
                if (!TryResolveNpc(task, out target))
                {
                    Fail(expectedGeneration, "无法从已确认的 NPC 表解析“" + task.TurnInNpcName + "”（" +
                        task.TurnInMapName + " " + task.TurnInX + "," + task.TurnInY + "）；已停止，未发送猜测 NPC ID。");
                    return;
                }
                BeginRoute(expectedGeneration, new List<BountyTravelTarget> { target }, DestinationPurpose.TurnInNpc,
                    "第 " + task.RingNumber + " 环前往 " + task.TurnInNpcName +
                    (task.Kind == RunLoopTaskKind.DeliverItem ? "提交“" + task.ItemName + "”" : "交付战斗任务"));
                return;
            }
            if (task.Kind != RunLoopTaskKind.Hunt)
            {
                Fail(expectedGeneration, "第 " + task.RingNumber + " 环任务类型尚未识别，未继续发包：" + task.Description);
                return;
            }
            List<BountyTravelTarget> huntRoute;
            if (!TryBuildHuntRoute(task.HuntMapName, out huntRoute))
            {
                Fail(expectedGeneration, "尚无“" + task.HuntMapName + "”的已验证入口链；请录制到达该地图的操作后再补充，未发送猜测报文。");
                return;
            }
            BeginRoute(expectedGeneration, huntRoute, DestinationPurpose.Hunt,
                "第 " + task.RingNumber + " 环前往 " + task.HuntMapName + "，目标 " + task.ObjectiveName);
        }

        private void BeginStartNpc(int expectedGeneration)
        {
            BeginRoute(expectedGeneration, new List<BountyTravelTarget>
            {
                new BountyTravelTarget { NpcId = StartNpcId, MapId = StartMapId, X = 23, Y = 132 }
            }, DestinationPurpose.StartNpc, "前往尚其村柳先元领取跑环任务");
        }

        private void BeginTaskOrStart(int expectedGeneration)
        {
            RunLoopTask task = null;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                if (latestTask != null && DateTime.UtcNow - latestTaskUtc < TimeSpan.FromMinutes(30))
                    task = latestTask.Clone();
            }
            if (task != null) DispatchTask(expectedGeneration, task);
            else BeginStartNpc(expectedGeneration);
        }

        private void BeginRoute(int expectedGeneration, List<BountyTravelTarget> targets, DestinationPurpose purpose, string message)
        {
            if (targets == null || targets.Count == 0) { Fail(expectedGeneration, "目标路线为空。"); return; }
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                route = targets;
                routeIndex = 0;
                destinationPurpose = purpose;
                expectedMapAfterPortal = 0;
                state = RunLoopAutomationState.Traveling;
                DisposeStageTimerLocked();
            }
            Publish(expectedGeneration, RunLoopAutomationState.Traveling, message + "。", false);
            SendCurrentRouteTarget(expectedGeneration);
        }

        private void SendCurrentRouteTarget(int expectedGeneration)
        {
            BountyTravelTarget target;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || route == null || routeIndex >= route.Count) return;
                target = route[routeIndex].Clone();
                currentDestination = target;
                state = RunLoopAutomationState.Traveling;
                ScheduleStageLocked(expectedGeneration, 30000, OnStageTimeout);
            }
            SendPacket(expectedGeneration, "任务链接传送 " + target, delegate(uint sequence)
            {
                return TianshuBountyProtocol.BuildTravelRequest(target, sequence);
            });
        }

        private void HandleConfirmation(int expectedGeneration, byte[] bytes)
        {
            BountyConfirmationDialog dialog;
            if (!TianshuBountyProtocol.TryParseConfirmationDialog(bytes, out dialog)) return;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != RunLoopAutomationState.Traveling ||
                    ((dialog.Title ?? string.Empty).IndexOf("传送", StringComparison.Ordinal) < 0)) return;
                state = RunLoopAutomationState.ConfirmingTeleport;
                DisposeStageTimerLocked();
            }
            Publish(expectedGeneration, RunLoopAutomationState.ConfirmingTeleport,
                "已解析本次服务端随机传送确认串，准备确认“" + dialog.Title + "”。", false);
            ScheduleAction(expectedGeneration, 160, delegate(int value)
            {
                lock (syncRoot)
                {
                    if (!IsCurrentLocked(value) || state != RunLoopAutomationState.ConfirmingTeleport) return;
                    state = RunLoopAutomationState.WaitingForMap;
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
            bool running;
            bool matches;
            bool portalArrival;
            lock (syncRoot)
            {
                currentMapId = map.MapId;
                currentMapName = map.MapName;
                currentScaledX = map.ScaledX;
                currentScaledY = map.ScaledY;
                running = IsCurrentLocked(expectedGeneration);
                matches = running && currentDestination != null && map.MapId == currentDestination.MapId &&
                    (state == RunLoopAutomationState.Traveling || state == RunLoopAutomationState.WaitingForMap ||
                    state == RunLoopAutomationState.ConfirmingTeleport);
                portalArrival = running && expectedMapAfterPortal != 0 && map.MapId == expectedMapAfterPortal;
            }
            if (!running) return;
            if (portalArrival)
            {
                lock (syncRoot) expectedMapAfterPortal = 0;
                StartWalking(expectedGeneration);
                return;
            }
            if (!matches) return;

            bool more;
            DestinationPurpose purpose;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                DisposeStageTimerLocked();
                routeIndex++;
                more = route != null && routeIndex < route.Count;
                purpose = destinationPurpose;
            }
            if (more)
            {
                Publish(expectedGeneration, RunLoopAutomationState.Traveling,
                    "已到路线节点 " + map.MapName + "，继续下一段。", false);
                ScheduleAction(expectedGeneration, 350, SendCurrentRouteTarget);
                return;
            }
            if (purpose == DestinationPurpose.Hunt && map.MapId == 99 &&
                string.Equals(TianshuRunLoopProtocol.NormalizeName(currentMapName), "焚石山", StringComparison.Ordinal))
            {
                lock (syncRoot)
                {
                    state = RunLoopAutomationState.WaitingForMap;
                    expectedMapAfterPortal = 100;
                    ScheduleStageLocked(expectedGeneration, 15000, OnStageTimeout);
                }
                Publish(expectedGeneration, RunLoopAutomationState.WaitingForMap,
                    "已到焚石山，访问入口 233 切换到怒焰祭坛。", false);
                ScheduleAction(expectedGeneration, 350, delegate(int value)
                {
                    SendPacket(value, "访问怒焰祭坛入口", delegate(uint sequence)
                    {
                        return TianshuBountyProtocol.BuildNpcOpen(233, sequence);
                    });
                });
                return;
            }
            if (purpose == DestinationPurpose.Hunt) { StartWalking(expectedGeneration); return; }
            OpenDestinationNpc(expectedGeneration);
        }

        private void OpenDestinationNpc(int expectedGeneration)
        {
            BountyTravelTarget target;
            DestinationPurpose purpose;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || currentDestination == null) return;
                target = currentDestination.Clone();
                purpose = destinationPurpose;
                state = RunLoopAutomationState.OpeningNpc;
                ScheduleStageLocked(expectedGeneration, 12000, OnStageTimeout);
            }
            Publish(expectedGeneration, RunLoopAutomationState.OpeningNpc,
                purpose == DestinationPurpose.StartNpc ? "已到柳先元处，准备领取任务。" : "已到交付 NPC，准备打开任务功能。", false);
            ScheduleAction(expectedGeneration, 450, delegate(int value)
            {
                lock (syncRoot)
                {
                    if (!IsCurrentLocked(value) || state != RunLoopAutomationState.OpeningNpc) return;
                    state = RunLoopAutomationState.WaitingForNpcDialog;
                    ScheduleStageLocked(value, 12000, OnStageTimeout);
                }
                SendPacket(value, "访问 NPC " + target.NpcId, delegate(uint sequence)
                {
                    return TianshuBountyProtocol.BuildNpcOpen(target.NpcId, sequence);
                });
            });
        }

        private void HandleNpcDialog(int expectedGeneration, byte[] bytes)
        {
            BountyTravelTarget target;
            DestinationPurpose purpose;
            string functionId;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != RunLoopAutomationState.WaitingForNpcDialog ||
                    currentDestination == null) return;
                target = currentDestination.Clone();
                purpose = destinationPurpose;
                functionId = purpose == DestinationPurpose.StartNpc ? TianshuRunLoopProtocol.AcceptFunctionId :
                    TianshuRunLoopProtocol.TurnInFunctionId;
            }
            if (!TianshuRunLoopProtocol.ContainsFunction(bytes, target.NpcId, functionId)) return;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = RunLoopAutomationState.SelectingFunction;
                DisposeStageTimerLocked();
            }
            Publish(expectedGeneration, RunLoopAutomationState.SelectingFunction,
                purpose == DestinationPurpose.StartNpc ? "功能列表已确认，选择领取跑环。" : "可交付功能已确认，自动提交并领取下一环。", false);
            ScheduleAction(expectedGeneration, 180, delegate(int value)
            {
                lock (syncRoot)
                {
                    if (!IsCurrentLocked(value) || state != RunLoopAutomationState.SelectingFunction) return;
                    state = RunLoopAutomationState.WaitingForTask;
                    ScheduleStageLocked(value, 15000, OnStageTimeout);
                }
                SendPacket(value, "选择跑环功能 " + functionId, delegate(uint sequence)
                {
                    return TianshuBountyProtocol.BuildNpcFunction(target.NpcId, functionId, sequence);
                });
            });
        }

        private void StartWalking(int expectedGeneration)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = RunLoopAutomationState.WalkingForEncounter;
                walkStep = 0;
                DisposeStageTimerLocked();
                DisposeCombatTimersLocked();
                walkTimer = new System.Threading.Timer(delegate { SendWalkStep(expectedGeneration); }, null, 200, 500);
                if (automaticRecovery)
                    recoveryTimer = new System.Threading.Timer(delegate { SendRecovery(expectedGeneration); }, null, 4000, 4000);
            }
            Publish(expectedGeneration, RunLoopAutomationState.WalkingForEncounter,
                "已到 " + currentMapName + "，开始 5 秒走步遇怪；战斗结果由任务进度包确认。", false);
        }

        private void SendWalkStep(int expectedGeneration)
        {
            int mapId;
            ushort x;
            ushort y;
            int step;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != RunLoopAutomationState.WalkingForEncounter) return;
                step = walkStep++;
                mapId = currentMapId;
                int[] dx = { 16, 32, 16, 0, -16, -32, -16, 0, 16, 0 };
                int[] dy = { 0, 16, 32, 16, 0, -16, -32, -16, 0, 0 };
                x = ClampCoordinate(currentScaledX + dx[step % dx.Length]);
                y = ClampCoordinate(currentScaledY + dy[step % dy.Length]);
                currentScaledX = x;
                currentScaledY = y;
                if (walkStep >= 10)
                {
                    state = RunLoopAutomationState.WaitingForCombatResult;
                    if (walkTimer != null) { walkTimer.Dispose(); walkTimer = null; }
                    ScheduleStageLocked(expectedGeneration, 9000, ResumeWalkingAfterTimeout);
                }
            }
            if (mapId <= 0) { Fail(expectedGeneration, "尚未取得当前地图 ID，无法安全构造走步包。"); return; }
            long epoch = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            SendPacket(expectedGeneration, "走步遇怪 " + (step + 1) + "/10", delegate(uint sequence)
            {
                return TianshuRunLoopProtocol.BuildMovement(epoch, mapId, x, y, sequence);
            });
        }

        private void SendRecovery(int expectedGeneration)
        {
            RunLoopAutomationState current;
            lock (syncRoot) current = state;
            if (current != RunLoopAutomationState.WalkingForEncounter && current != RunLoopAutomationState.WaitingForCombatResult) return;
            SendPacket(expectedGeneration, "战斗一键恢复 HP/MP", TianshuRunLoopProtocol.BuildOneKeyRecovery);
        }

        private void ResumeWalkingAfterTimeout(int expectedGeneration)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != RunLoopAutomationState.WaitingForCombatResult) return;
                state = RunLoopAutomationState.WalkingForEncounter;
                walkStep = 0;
                DisposeStageTimerLocked();
                if (walkTimer != null) walkTimer.Dispose();
                walkTimer = new System.Threading.Timer(delegate { SendWalkStep(expectedGeneration); }, null, 100, 500);
            }
            Publish(expectedGeneration, RunLoopAutomationState.WalkingForEncounter,
                "本轮尚未收到任务完成进度，继续下一次 5 秒走步。", false);
        }

        private bool TryResolveNpc(RunLoopTask task, out BountyTravelTarget target)
        {
            target = null;
            if (task != null && task.TurnInNpcId > 0 && task.TurnInMapId > 0)
            {
                target = new BountyTravelTarget
                {
                    NpcId = task.TurnInNpcId,
                    MapId = task.TurnInMapId,
                    X = task.TurnInX,
                    Y = task.TurnInY
                };
                return true;
            }
            catalogReady.WaitOne(2000);
            string key = TianshuRunLoopProtocol.NormalizeName(task.TurnInNpcName);
            lock (syncRoot)
            {
                BountyTravelTarget found;
                if (npcDirectory.TryGetValue(key, out found))
                {
                    target = found.Clone();
                }
                else
                {
                    foreach (KeyValuePair<string, BountyTravelTarget> pair in npcDirectory)
                    {
                        if (pair.Key.EndsWith(key, StringComparison.Ordinal) || key.EndsWith(pair.Key, StringComparison.Ordinal))
                        {
                            target = pair.Value.Clone();
                            break;
                        }
                    }
                }
            }
            if (target == null) return false;
            target.X = task.TurnInX;
            target.Y = task.TurnInY;
            int mapId;
            if (TryResolveMapId(task.TurnInMapName, out mapId)) target.MapId = mapId;
            return target.MapId > 0;
        }

        private static bool TryBuildHuntRoute(string mapName, out List<BountyTravelTarget> targets)
        {
            targets = null;
            string normalized = TianshuRunLoopProtocol.NormalizeName(mapName);
            MapTeleportDestination recordedDestination;
            if (TianshuMapTeleportCatalog.TryGet(normalized, out recordedDestination))
            {
                targets = new List<BountyTravelTarget>
                {
                    TianshuMapTeleportCatalog.GetTotemTravelTarget(recordedDestination.MapId)
                };
                return true;
            }
            if (string.Equals(normalized, "百鸟树林", StringComparison.Ordinal))
            {
                targets = new List<BountyTravelTarget>
                {
                    new BountyTravelTarget { NpcId = 50100166, MapId = 110, X = 2, Y = 84 },
                    new BountyTravelTarget { NpcId = 608, MapId = 111, X = 6, Y = 84 }
                };
                return true;
            }
            if (string.Equals(normalized, "怒焰祭坛", StringComparison.Ordinal))
            {
                targets = new List<BountyTravelTarget>
                {
                    new BountyTravelTarget { NpcId = 233, MapId = 99, X = 22, Y = 8 }
                };
                return true;
            }
            return false;
        }

        private static bool TryResolveMapId(string mapName, out int mapId)
        {
            mapId = 0;
            string normalized = TianshuRunLoopProtocol.NormalizeName(mapName);
            MapTeleportDestination recordedDestination;
            if (TianshuMapTeleportCatalog.TryGet(normalized, out recordedDestination))
            {
                mapId = recordedDestination.MapId;
                return true;
            }
            Dictionary<string, int> maps = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "灵仙岛", 9 }, { "古道", 10 }, { "灵昌城", 12 }, { "皇城内", 13 },
                { "黄金港口", 15 }, { "明湖水寨", 66 }, { "尚其村", 72 }, { "坠龙城", 76 },
                { "三界关", 86 }, { "灵昌广场", 97 }, { "焚石山", 99 }, { "怒焰祭坛", 100 },
                { "十字路口", 101 }, { "逐浪广场", 102 }, { "主母之林", 109 },
                { "近天回廊", 110 }, { "百鸟树林", 111 }, { "练功房", 213 }, { "帮会大厅", 1058 }
            };
            return maps.TryGetValue(normalized, out mapId);
        }

        private void LearnEntities(byte[] bytes)
        {
            IList<RunLoopEntity> entities = TianshuRunLoopProtocol.ExtractEntities(bytes);
            if (entities.Count == 0) return;
            lock (syncRoot)
            {
                for (int i = 0; i < entities.Count; i++)
                {
                    RunLoopEntity entity = entities[i];
                    npcDirectory[TianshuRunLoopProtocol.NormalizeName(entity.Name)] = new BountyTravelTarget
                    {
                        NpcId = entity.Id,
                        MapId = currentMapId,
                        X = entity.X,
                        Y = entity.Y
                    };
                }
            }
        }

        private void OnActiveModeChanged(bool active)
        {
            if (!active && IsRunning(State))
                StopCore(RunLoopAutomationState.Stopped, "ACTIVE/发包模式已关闭，自动跑环同步停止。", false);
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
                ((packet[2] << 8) | packet[3]).ToString("X4") + "，seq=" + sequence + "，len=" + packet.Length, connectionId);
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
            RunLoopAutomationState current;
            lock (syncRoot) current = state;
            Fail(expectedGeneration, "等待阶段超时：" + current + "。已停止继续发包，请保留会话库用于比对。");
        }

        private void Complete(int expectedGeneration, string message)
        {
            bool restore;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = RunLoopAutomationState.Completed;
                DisposeTimersLocked();
                restore = activatedByAutomation;
                activatedByAutomation = false;
            }
            Publish(expectedGeneration, RunLoopAutomationState.Completed, message, false);
            service.ReleaseAutomation(AutomationOwner);
            if (restore && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void Fail(int expectedGeneration, string message)
        {
            bool restore;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = RunLoopAutomationState.Failed;
                DisposeTimersLocked();
                restore = activatedByAutomation;
                activatedByAutomation = false;
            }
            Publish(expectedGeneration, RunLoopAutomationState.Failed, message, true);
            service.ReleaseAutomation(AutomationOwner);
            if (restore && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void StopCore(RunLoopAutomationState finalState, string message, bool error)
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

        private void Publish(int expectedGeneration, RunLoopAutomationState publishedState, string message, bool error)
        {
            lock (syncRoot) if (disposed || expectedGeneration != generation) return;
            PublishAny(publishedState, message, error);
        }

        private void PublishAny(RunLoopAutomationState publishedState, string message, bool error)
        {
            Action<RunLoopAutomationState, string> handler = StatusChanged;
            if (handler != null) handler(publishedState, message);
            long id = GetGameConnectionId();
            service.ReportEngineEvent(AutomationOwner, error ? "ERROR" : "INFO", message, id == 0 ? (long?)null : id);
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

        private void DisposeCombatTimersLocked()
        {
            if (walkTimer != null) { walkTimer.Dispose(); walkTimer = null; }
            if (recoveryTimer != null) { recoveryTimer.Dispose(); recoveryTimer = null; }
        }

        private void DisposeTimersLocked()
        {
            DisposeStageTimerLocked();
            DisposeCombatTimersLocked();
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

        private bool IsCurrentLocked(int expectedGeneration)
        {
            return !disposed && generation == expectedGeneration && IsRunning(state);
        }

        private static bool IsRunning(RunLoopAutomationState value)
        {
            return value != RunLoopAutomationState.Inactive && value != RunLoopAutomationState.Completed &&
                value != RunLoopAutomationState.Stopped && value != RunLoopAutomationState.Failed;
        }

        private static bool IsSequenceOpcode(int opcode)
        {
            return opcode == TianshuBountyProtocol.ClientTravelLink || opcode == TianshuBountyProtocol.ClientNpcOpen ||
                opcode == TianshuBountyProtocol.ClientNpcFunction || opcode == TianshuBountyProtocol.ClientDialogResponse ||
                opcode == TianshuRunLoopProtocol.ClientMovement || opcode == TianshuRunLoopProtocol.ClientUiAction || opcode == 0x0042;
        }

        private static ushort ClampCoordinate(int value)
        {
            return (ushort)Math.Max(16, Math.Min(UInt16.MaxValue - 16, value));
        }
    }

    public sealed class RunLoopAutomationControl : UserControl
    {
        private readonly RunLoopAutomationCoordinator coordinator;
        private readonly NumericUpDown rounds;
        private readonly CheckBox automaticRecovery;
        private readonly Button startButton;
        private readonly Button stopButton;
        private readonly Label stateLabel;
        private readonly Label progressLabel;
        private readonly Label taskLabel;

        public RunLoopAutomationControl(RunLoopAutomationCoordinator coordinator)
        {
            if (coordinator == null) throw new ArgumentNullException("coordinator");
            this.coordinator = coordinator;
            Dock = DockStyle.Fill;

            Label help = new Label
            {
                AutoSize = false,
                Height = 44,
                Dock = DockStyle.Top,
                Text = "自动解析跑环任务及 Base64 HTML 链接：提交道具时读取 event 中的 NPC/地图/坐标并瞬移访问；战斗地图优先用已录制蟠龙图腾的旧任务链接到图，再走步约 5 秒遇怪。每轮 20 环，每日最多 4 轮。"
            };
            FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, AutoSize = false };
            options.Controls.Add(new Label { Text = "本次轮数", AutoSize = true, Margin = new Padding(3, 9, 3, 3) });
            rounds = new NumericUpDown { Minimum = 1, Maximum = 4, Value = 4, Width = 55 };
            options.Controls.Add(rounds);
            automaticRecovery = new CheckBox { Text = "战斗每 4 秒恢复 HP/MP", Checked = true, AutoSize = true, Margin = new Padding(10, 7, 3, 3) };
            options.Controls.Add(automaticRecovery);
            startButton = new Button { Text = "开始自动跑环", AutoSize = true };
            stopButton = new Button { Text = "停止", AutoSize = true, Enabled = false };
            options.Controls.Add(startButton);
            options.Controls.Add(stopButton);

            stateLabel = new Label { Dock = DockStyle.Top, Height = 23, Text = "状态：Inactive" };
            progressLabel = new Label { Dock = DockStyle.Top, Height = 23, Text = "进度：0/4 轮，0 环" };
            taskLabel = new Label { Dock = DockStyle.Top, Height = 44, AutoEllipsis = true, Text = "当前任务：等待服务端任务包" };
            Controls.Add(taskLabel);
            Controls.Add(progressLabel);
            Controls.Add(stateLabel);
            Controls.Add(options);
            Controls.Add(help);

            startButton.Click += OnStart;
            stopButton.Click += delegate { coordinator.Stop(); };
            coordinator.StatusChanged += OnStatusChanged;
            OnStatusChanged(coordinator.State, "自动跑环尚未启动。");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) coordinator.StatusChanged -= OnStatusChanged;
            base.Dispose(disposing);
        }

        private void OnStart(object sender, EventArgs e)
        {
            try { coordinator.Start((int)rounds.Value, automaticRecovery.Checked); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "自动跑环", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void OnStatusChanged(RunLoopAutomationState automationState, string message)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<RunLoopAutomationState, string>(OnStatusChanged), automationState, message); }
                catch (InvalidOperationException) { }
                return;
            }
            bool running = automationState != RunLoopAutomationState.Inactive && automationState != RunLoopAutomationState.Completed &&
                automationState != RunLoopAutomationState.Stopped && automationState != RunLoopAutomationState.Failed;
            startButton.Enabled = !running;
            stopButton.Enabled = running;
            rounds.Enabled = !running;
            automaticRecovery.Enabled = !running;
            stateLabel.Text = "状态：" + automationState;
            progressLabel.Text = "进度：" + coordinator.CompletedRounds + "/" + coordinator.PlannedRounds +
                " 轮，已处理 " + coordinator.CompletedRings + " 环";
            RunLoopTask task = coordinator.CurrentTask;
            taskLabel.Text = "当前任务：" + (task == null ? "等待服务端任务包" : task.ToString());
        }
    }
}
