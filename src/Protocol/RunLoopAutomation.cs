using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace TianshuQitanLauncher.Protocol
{
    public enum RunLoopTaskKind
    {
        Unknown = 0,
        DeliverItem = 1,
        Hunt = 2,
        TalkToNpc = 3
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
                (Kind == RunLoopTaskKind.Hunt ? "战斗" :
                (Kind == RunLoopTaskKind.TalkToNpc ? "NPC 对话" : "未知"));
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

    public sealed class RunLoopMapGrid
    {
        public int MapId { get; set; }
        public int RpWidth { get; set; }
        public int RpHeight { get; set; }
        public int Rows { get; set; }
        public int Cols { get; set; }
        public byte[] Cells { get; set; }

        public bool IsWalkable(int col, int row)
        {
            if (Cells == null || col < 0 || col >= Cols || row < 0 || row >= Rows) return false;
            return Cells[row * Cols + col] != 0;
        }

        public bool TryGetScaledCenter(int col, int row, out ushort scaledX, out ushort scaledY)
        {
            scaledX = 0;
            scaledY = 0;
            if (RpWidth <= 0 || RpHeight <= 0) return false;
            int halfWidth = RpWidth / 2;
            int halfHeight = RpHeight / 2;
            int x = col * RpWidth + ((row & 1) == 1 ? halfWidth : 0);
            int y = (row - 1) * halfHeight + halfHeight;
            scaledX = (ushort)Math.Max(0, Math.Min(UInt16.MaxValue, x));
            scaledY = (ushort)Math.Max(0, Math.Min(UInt16.MaxValue, y));
            return true;
        }

        public bool TryGetRp(int scaledX, int scaledY, out int col, out int row)
        {
            col = 0;
            row = 0;
            if (RpWidth <= 0 || RpHeight <= 0) return false;
            int halfWidth = RpWidth / 2;
            int halfHeight = RpHeight / 2;
            int r = (scaledY - halfHeight) / halfHeight + 1;
            int xOffset = (r & 1) == 1 ? halfWidth : 0;
            int c = (scaledX - xOffset) / RpWidth;
            if (r < 0) r = 0;
            if (c < 0) c = 0;
            col = c;
            row = r;
            return c >= 0 && c < Cols && r >= 0 && r < Rows;
        }
    }

    public sealed class RunLoopWalkOptions
    {
        // Code configuration: change these defaults and rebuild, or pass options to the coordinator.
        public RunLoopWalkOptions()
        {
            StepIntervalMs = 200;
            InitialDelayMs = 200;
            PatrolPointCount = 4;
            MapDataWaitTimeoutMs = 10000;
            NoProgressTimeoutMs = 75000;
            RecoveryIntervalMs = 4000;
            MaximumHuntRetries = 3;
        }

        public int StepIntervalMs { get; set; }
        public int InitialDelayMs { get; set; }
        public int PatrolPointCount { get; set; }
        public int MapDataWaitTimeoutMs { get; set; }
        public int NoProgressTimeoutMs { get; set; }
        public int RecoveryIntervalMs { get; set; }
        public int MaximumHuntRetries { get; set; }

        internal RunLoopWalkOptions ValidatedCopy()
        {
            if (StepIntervalMs < 1 || InitialDelayMs < 0 || PatrolPointCount < 2 || PatrolPointCount > 16 ||
                MapDataWaitTimeoutMs < 1 || NoProgressTimeoutMs < 1 || RecoveryIntervalMs < 1 || MaximumHuntRetries < 0)
                throw new ArgumentOutOfRangeException("walkOptions", "走步间隔必须为正数，巡逻点数为 2–16，延迟和重试次数不能为负数。");
            return (RunLoopWalkOptions)MemberwiseClone();
        }
    }

    // A short connected path, traversed A→B→C→D→C→B→A. Never grows with each tick.
    public sealed class RunLoopPatrolPath
    {
        private readonly IList<Point> points;
        private int nextIndex = 1;
        private int direction = 1;

        private RunLoopPatrolPath(List<Point> points) { this.points = points.AsReadOnly(); }
        public IList<Point> Points { get { return points; } }

        public Point Next()
        {
            Point result = points[nextIndex];
            if (nextIndex == points.Count - 1) direction = -1;
            else if (nextIndex == 0) direction = 1;
            nextIndex += direction;
            return result;
        }

        public static bool TryCreate(RunLoopMapGrid grid, ushort x, ushort y, int maximumPoints, out RunLoopPatrolPath path)
        {
            path = null;
            if (maximumPoints < 2 || maximumPoints > 16) throw new ArgumentOutOfRangeException("maximumPoints");
            if (grid == null || grid.RpWidth < 2 || grid.RpHeight < 2 || grid.Cells == null ||
                grid.Rows <= 0 || grid.Cols <= 0 || (long)grid.Rows * grid.Cols > grid.Cells.Length) return false;
            int col;
            int row;
            if (!grid.TryGetRp(x, y, out col, out row)) return false;

            // Select the closest walkable anchor in the current cell's immediate neighbourhood.
            Point anchor = Point.Empty;
            long bestDistance = long.MaxValue;
            for (int c = col - 1; c <= col + 1; c++)
                for (int r = row - 1; r <= row + 1; r++)
                {
                    Point center;
                    if (!TryCenter(grid, c, r, out center)) continue;
                    long distance = (long)(center.X - x) * (center.X - x) + (long)(center.Y - y) * (center.Y - y);
                    if (distance < bestDistance) { bestDistance = distance; anchor = new Point(c, r); }
                }
            if (bestDistance == long.MaxValue) return false;

            List<Point> cells = new List<Point> { anchor };
            Point first;
            TryCenter(grid, anchor.X, anchor.Y, out first);
            List<Point> centers = new List<Point> { first };
            while (centers.Count < maximumPoints)
            {
                Point last = cells[cells.Count - 1];
                int parity = last.Y & 1;
                int[] dx = { 1, 0, parity, parity - 1, -1, 0, parity - 1, parity };
                int[] dy = { 0, 2, 1, 1, 0, -2, -1, -1 };
                bool found = false;
                for (int i = 0; i < dx.Length; i++)
                {
                    Point cell = new Point(last.X + dx[i], last.Y + dy[i]);
                    Point center;
                    if (cells.Contains(cell) || !TryCenter(grid, cell.X, cell.Y, out center) || centers.Contains(center)) continue;
                    cells.Add(cell);
                    centers.Add(center);
                    found = true;
                    break;
                }
                if (!found) break;
            }
            if (centers.Count < 2) return false;
            path = new RunLoopPatrolPath(centers);
            return true;
        }

        private static bool TryCenter(RunLoopMapGrid grid, int col, int row, out Point center)
        {
            center = Point.Empty;
            if (!grid.IsWalkable(col, row)) return false;
            long rawX = (long)col * grid.RpWidth + ((row & 1) == 1 ? grid.RpWidth / 2 : 0);
            long rawY = (long)row * (grid.RpHeight / 2);
            if (rawX < 0 || rawX > UInt16.MaxValue || rawY < 0 || rawY > UInt16.MaxValue) return false;
            ushort x;
            ushort y;
            if (!grid.TryGetScaledCenter(col, row, out x, out y)) return false;
            center = new Point(x, y);
            return true;
        }
    }

    public static class TianshuRunLoopProtocol
    {
        public const int ClientMovement = 0x00C1;
        public const int ClientUiAction = 0x002E;
        public const int OneKeyRecoveryAction = 0x0321;
        public const int ServerTaskList = 0x003D;
        public const int ServerTaskUpdate = 0x003E;
        public const int ServerTaskRemoved = 0x003F;
        public const int ServerMapData2 = 0x003C;
        public const int ServerMapInfo = 0x0055;
        public const int ServerEntityList = 0x001C;
        public const int ServerNearbyEntities = 0x006A;
        public const string TaskId = "140000000";
        public const string AcceptFunctionId = "140000001";
        public const string TurnInFunctionId = "140000002";

        private static readonly Regex RingRegex = new Regex(@"第\s*(\d+)\s*环", RegexOptions.CultureInvariant);
        private static readonly Regex DeliveryRegex = new Regex(
            @"将(?<item>.+?)(?:送给到|送到|交给)(?<map>.+?)的(?<npc>.+?)[（(](?<x>\d+)\s*[,，]\s*(?<y>\d+)[)）]",
            RegexOptions.CultureInvariant);
        private static readonly Regex HuntMapRegex = new Regex(@"^(?:前往|去)(?<map>.+?)消灭", RegexOptions.CultureInvariant);
        private static readonly Regex TalkToNpcRegex = new Regex(
            @"^与(?<map>.+?)的(?<npc>.+?)[（(](?<x>\d+)\s*[,，]\s*(?<y>\d+)[)）]对话[，,]?\s*(?:后\s*)?领取下一环任务",
            RegexOptions.CultureInvariant);
        private static readonly Regex TurnInRegex = new Regex(
            @"到(?<map>[^，。]+?)的(?<npc>[^，。]+?)[（(](?<x>\d+)\s*[,，]\s*(?<y>\d+)[)）]处领取",
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

        public static bool TryParseMapData(byte[] bytes, out RunLoopMapGrid grid)
        {
            grid = null;
            if (!TianshuBountyProtocol.HasOpcode(bytes, ServerMapData2) || bytes.Length < 24) return false;
            int offset = 4;
            int mapId = ReadUInt16(bytes, offset);
            offset += 2;
            string ignoredName;
            if (!TryReadString(bytes, ref offset, out ignoredName)) return false;

            int[] values = new int[9];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = ReadUInt16(bytes, offset);
                offset += 2;
            }
            offset += 4; // mapMark (int)
            offset += 2; // unknown short
            int compressedLength = (int)ReadUInt32(bytes, offset);
            offset += 4;
            if (compressedLength <= 2 || offset + compressedLength > bytes.Length) return false;

            byte[] inflated = InflateZlib(bytes, offset, compressedLength);
            if (inflated == null || inflated.Length == 0) return false;

            int rows = values[7];
            int cols = values[8];
            if (rows <= 0 || cols <= 0 || rows * cols > inflated.Length) return false;

            grid = new RunLoopMapGrid
            {
                MapId = mapId,
                RpWidth = values[5],
                RpHeight = values[6],
                Rows = rows,
                Cols = cols,
                Cells = new byte[rows * cols]
            };
            Array.Copy(inflated, grid.Cells, rows * cols);
            return true;
        }

        public static bool TryParseTask(byte[] bytes, out RunLoopTask task)
        {
            task = null;
            if (!TianshuBountyProtocol.HasOpcode(bytes, ServerTaskUpdate)) return false;
            return TryParseTaskText(ExtractStrings(bytes), out task);
        }

        public static bool IsRunLoopTaskRemoved(byte[] bytes)
        {
            if (!TianshuBountyProtocol.HasOpcode(bytes, ServerTaskRemoved)) return false;
            int offset = 4;
            string id;
            return TryReadString(bytes, ref offset, out id) && offset == bytes.Length &&
                string.Equals(id, TaskId, StringComparison.Ordinal);
        }

        public static bool TryParseTaskList(byte[] bytes, out RunLoopTask task)
        {
            task = null;
            if (!TianshuBountyProtocol.HasOpcode(bytes, ServerTaskList) || bytes.Length < 8) return false;
            int compressedLength = ReadUInt16(bytes, 4);
            if (compressedLength <= 2 || 6 + compressedLength > bytes.Length) return false;
            byte[] inflated = InflateZlib(bytes, 6, compressedLength);
            if (inflated == null || inflated.Length == 0) return false;

            byte[] taskIdBytes = Encoding.UTF8.GetBytes(TaskId);
            int recordOffset = IndexOfBytes(inflated, taskIdBytes);
            if (recordOffset < 2 || ReadUInt16(inflated, recordOffset - 2) != taskIdBytes.Length) return false;
            int recordStart = recordOffset - 2;
            int recordLength = Math.Min(inflated.Length - recordStart, 2048);
            byte[] record = new byte[recordLength];
            Array.Copy(inflated, recordStart, record, 0, recordLength);
            return TryParseTaskText(ExtractStrings(record, 0), out task);
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0 || haystack.Length < needle.Length) return -1;
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool matched = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { matched = false; break; }
                }
                if (matched) return i;
            }
            return -1;
        }

        private static bool TryParseTaskText(IList<string> strings, out RunLoopTask task)
        {
            task = null;
            if (strings == null) return false;
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
                int talkStart = visibleText.IndexOf("与", StringComparison.Ordinal);
                int descriptionStart = FirstNonNegative(deliveryStart, huntStart, talkStart);
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
                Match talkToNpc = TalkToNpcRegex.Match(description);
                if (talkToNpc.Success)
                {
                    parsed.Kind = RunLoopTaskKind.TalkToNpc;
                    ApplyTurnIn(parsed, talkToNpc);
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
            }
            ApplyRecordedTravelLink(parsed, strings);
            task = parsed;
            return true;
        }

        private static byte[] InflateZlib(byte[] data, int offset, int length)
        {
            if (data == null || offset < 0 || length < 0 || offset + length > data.Length || length <= 2) return null;
            try
            {
                using (MemoryStream input = new MemoryStream(data, offset + 2, length - 2))
                using (DeflateStream deflate = new DeflateStream(input, CompressionMode.Decompress))
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[4096];
                    int read;
                    while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                        output.Write(buffer, 0, read);
                    return output.ToArray();
                }
            }
            catch (InvalidDataException)
            {
                return null;
            }
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
            for (int i = 0; i < strings.Count; i++)
            {
                IList<BountyTravelTarget> links = TianshuBountyProtocol.ExtractTravelLinks(strings[i]);
                for (int j = 0; j < links.Count; j++)
                {
                    BountyTravelTarget link = links[j];
                    if (link.X == task.TurnInX && link.Y == task.TurnInY)
                    {
                        task.TurnInNpcId = link.NpcId;
                        task.TurnInMapId = link.MapId;
                        task.TurnInX = link.X;
                        task.TurnInY = link.Y;
                        return;
                    }
                }
            }
        }

        private static void ApplyTurnIn(RunLoopTask task, Match match)
        {
            task.TurnInMapName = NormalizeName(match.Groups["map"].Value);
            task.TurnInNpcName = NormalizeName(match.Groups["npc"].Value);
            task.TurnInX = Int32.Parse(match.Groups["x"].Value);
            task.TurnInY = Int32.Parse(match.Groups["y"].Value);
        }

        private static int FirstNonNegative(params int[] values)
        {
            int result = -1;
            for (int i = 0; i < values.Length; i++)
                if (values[i] >= 0 && (result < 0 || values[i] < result)) result = values[i];
            return result;
        }

        private static IList<string> ExtractStrings(byte[] bytes)
        {
            return ExtractStrings(bytes, 4);
        }

        private static IList<string> ExtractStrings(byte[] bytes, int startOffset)
        {
            List<string> result = new List<string>();
            if (bytes == null) return result;
            if (startOffset < 0) startOffset = 0;
            for (int offset = startOffset; offset + 2 <= bytes.Length; offset++)
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
        public const int MaximumPlannedRounds = 6;

        private enum DestinationPurpose { None, StartNpc, TurnInNpc, Hunt }

        private readonly object syncRoot = new object();
        private readonly ProtocolWorkbenchService service;
        private readonly RunLoopWalkOptions walkOptions;
        private RunLoopPatrolPath patrolPath;
        private readonly Stopwatch huntProgressWatch = new Stopwatch();
        private readonly Stopwatch mapDataWaitWatch = new Stopwatch();
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
        private RunLoopMapGrid currentMapGrid;
        private int plannedRounds;
        private int completedRounds;
        private int completedRings;
        // A task update immediately preceding removal can still describe the submitted ring.
        // Count only the correlated removal, and gate snapshots until the expected next ring arrives.
        private int pendingTurnInRing;
        private int expectedNextRing;
        private bool automaticRecovery;
        private bool activatedByAutomation;
        private int walkStep;
        private int huntRetryCount;
        private int generation;
        private bool disposed;

        public event Action<RunLoopAutomationState, string> StatusChanged;

        public RunLoopAutomationCoordinator(ProtocolWorkbenchService service)
            : this(service, new RunLoopWalkOptions())
        {
        }

        public RunLoopAutomationCoordinator(ProtocolWorkbenchService service, RunLoopWalkOptions walkOptions)
        {
            if (service == null) throw new ArgumentNullException("service");
            if (walkOptions == null) throw new ArgumentNullException("walkOptions");
            this.service = service;
            this.walkOptions = walkOptions.ValidatedCopy();
            state = RunLoopAutomationState.Inactive;
            SeedNpcDirectory();
            LoadPersistedNpcCatalog();
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
            Begin(roundCount, enableAutomaticRecovery, false);
        }

        public void Resume(int roundCount, bool enableAutomaticRecovery)
        {
            Begin(roundCount, enableAutomaticRecovery, true);
        }

        public string DescribeCurrentTaskAction()
        {
            RunLoopTask task = CurrentTask;
            if (task == null) return "等待服务端任务包";

            string action;
            if (task.IsComplete)
            {
                action = "已可交付 → 交任务";
            }
            else if (task.Kind == RunLoopTaskKind.Hunt)
            {
                bool collect = task.Description != null && task.Description.IndexOf("得到", StringComparison.Ordinal) >= 0;
                action = "战斗 " + task.Progress + "/" + task.Required +
                    (collect ? "，获取物品：" + (task.ObjectiveName ?? "任务掉落") : string.Empty) + " → 继续战斗";
            }
            else if (task.Kind == RunLoopTaskKind.DeliverItem)
            {
                action = "提交道具“" + task.ItemName + "” → 交任务";
            }
            else if (task.Kind == RunLoopTaskKind.TalkToNpc)
            {
                action = "找 " + task.TurnInNpcName + " 对话 → 交任务";
            }
            else
            {
                action = "任务类型尚未识别，将安全停止";
            }

            return "第 " + task.RingNumber + " 环：" + action;
        }

        private void Begin(int roundCount, bool enableAutomaticRecovery, bool resumeFromCurrent)
        {
            if (roundCount < 1 || roundCount > MaximumPlannedRounds) throw new ArgumentOutOfRangeException("roundCount");
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
                pendingTurnInRing = 0;
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
                if (resumeFromCurrent && existingTask != null)
                {
                    completedRings = Math.Max(0, existingTask.RingNumber - 1);
                    completedRounds = completedRings / RingsPerRound;
                }
                else
                {
                    completedRounds = 0;
                    completedRings = 0;
                }
                state = !canBegin ? RunLoopAutomationState.WaitingForGameConnection :
                    (useExistingTask ? RunLoopAutomationState.WaitingForTask : RunLoopAutomationState.Traveling);
            }
            if (enableActive) service.SetActiveMode(true, false);
            string resumeHint = resumeFromCurrent
                ? (existingTask == null
                    ? "；未读取到最近任务，将前往柳先元获取当前环。"
                    : "；从第 " + existingTask.RingNumber + " 环继续。")
                : string.Empty;
            Publish(currentGeneration, state, (resumeFromCurrent ? "自动跑环已从当前进度继续" : "自动跑环已启动") +
                "：每轮 20 环，本次最多 " + roundCount + " 轮 / " + (roundCount * RingsPerRound) + " 环，次数以服务端许可为准" +
                (enableAutomaticRecovery ? "，战斗中每 " + walkOptions.RecoveryIntervalMs + "ms 自动恢复。" : "。") + resumeHint, false);
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
            AddNpc("乔烈", 8021, 12, 39, 37);
            AddNpc("舞修罗", 8011, 13, 30, 100);
            AddNpc("冯奇", 10102, 101, 17, 73);
            AddNpc("周猎户", 8032, 12, 31, 130);
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
            npcDirectory[BuildNpcKey(name, mapId)] =
                new BountyTravelTarget { NpcId = id, MapId = mapId, X = x, Y = y };
        }

        private static string BuildNpcKey(string name, int mapId)
        {
            return TianshuRunLoopProtocol.NormalizeName(name) + "|" + mapId;
        }

        private void LoadPersistedNpcCatalog()
        {
            try
            {
                string catalogPath = service.Profile.Resolve("data/npc-catalog.json");
                if (!File.Exists(catalogPath)) return;
                NpcCatalogDocument document = JsonConvert.DeserializeObject<NpcCatalogDocument>(File.ReadAllText(catalogPath));
                if (document == null || document.Entries == null) return;
                int loaded = 0;
                lock (syncRoot)
                {
                    for (int i = 0; i < document.Entries.Count; i++)
                    {
                        NpcCatalogEntry entry = document.Entries[i];
                        if (entry == null || string.IsNullOrWhiteSpace(entry.Name) ||
                            entry.NpcId <= 0 || entry.MapId <= 0) continue;
                        AddNpc(entry.Name, entry.NpcId, entry.MapId, entry.X, entry.Y);
                        loaded++;
                    }
                }
                if (loaded > 0)
                    service.ReportEngineEvent(AutomationOwner, "INFO",
                        "已加载持久化 NPC 目录 " + loaded + " 条：" + catalogPath, null);
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot load persisted run-loop NPC catalog", ex);
            }
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
                                                string key = BuildNpcKey(entity.Name, mapId);
                                                if (npcDirectory.ContainsKey(key)) continue;
                                                npcDirectory[key] = new BountyTravelTarget
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
            if (opcode == TianshuRunLoopProtocol.ServerTaskRemoved)
            {
                if (TianshuRunLoopProtocol.IsRunLoopTaskRemoved(frame.Bytes)) HandleTaskRemoved(currentGeneration);
                return;
            }
            if (opcode == TianshuRunLoopProtocol.ServerTaskList)
            {
                RunLoopTask task;
                if (TianshuRunLoopProtocol.TryParseTaskList(frame.Bytes, out task)) HandleTask(currentGeneration, task);
                return;
            }
            if (opcode == TianshuRunLoopProtocol.ServerMapInfo)
            {
                RunLoopMapInfo map;
                if (TianshuRunLoopProtocol.TryParseMapInfo(frame.Bytes, out map)) HandleMap(currentGeneration, map);
                return;
            }
            if (opcode == TianshuRunLoopProtocol.ServerMapData2)
            {
                HandleMapData(frame.Bytes);
                return;
            }
            if (opcode == TianshuRunLoopProtocol.ServerEntityList || opcode == TianshuRunLoopProtocol.ServerNearbyEntities)
            {
                LearnEntities(frame.Bytes);
                return;
            }
            if (!IsRunning(State)) return;
            if (opcode == TianshuBountyProtocol.ServerSystemMessage &&
                TianshuBountyProtocol.ContainsText(frame.Bytes, "跑环") &&
                (TianshuBountyProtocol.ContainsText(frame.Bytes, "上限") ||
                TianshuBountyProtocol.ContainsText(frame.Bytes, "不能再") ||
                TianshuBountyProtocol.ContainsText(frame.Bytes, "无法再") ||
                TianshuBountyProtocol.ContainsText(frame.Bytes, "已满")))
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
            bool changed = false;
            bool progressChanged = false;
            lock (syncRoot)
            {
                if (expectedNextRing != 0 && task.RingNumber != expectedNextRing) return;
                // While a turn-in is outstanding, old task refreshes are not a new assignment.
                if (pendingTurnInRing != 0) return;
                previous = latestTask;
                if (previous != null && task.RingNumber != previous.RingNumber) return;
                if (previous != null && task.Progress < previous.Progress) return;
                expectedNextRing = 0;
                latestTask = task.Clone();
                latestTaskUtc = DateTime.UtcNow;
                running = IsCurrentLocked(expectedGeneration);
                if (!running) return;
                changed = previous == null;
                if (previous != null && previous.Kind == RunLoopTaskKind.Hunt &&
                    task.Kind == RunLoopTaskKind.Hunt && task.Progress > previous.Progress)
                {
                    progressChanged = true;
                    huntProgressWatch.Restart();
                    huntRetryCount = 0;
                }
            }
            if (changed) Publish(expectedGeneration, RunLoopAutomationState.WaitingForTask,
                "收到第 " + task.RingNumber + " 环任务；本次已交付 " + CompletedRings + " 环，完成轮数 " + CompletedRounds + "/" + PlannedRounds + "。", false);
            if (!changed && previous != null && previous.RingNumber == task.RingNumber)
            {
                if (!progressChanged) return;
                bool keepWalking;
                lock (syncRoot)
                {
                    if (!IsCurrentLocked(expectedGeneration)) return;
                    keepWalking = state == RunLoopAutomationState.WalkingForEncounter ||
                        state == RunLoopAutomationState.WaitingForCombatResult;
                    if (!keepWalking || task.IsComplete)
                    {
                        DisposeStageTimerLocked();
                        DisposeCombatTimersLocked();
                        state = RunLoopAutomationState.WaitingForTask;
                    }
                }
                Publish(expectedGeneration, keepWalking ? RunLoopAutomationState.WalkingForEncounter :
                    RunLoopAutomationState.WaitingForTask,
                    "第 " + task.RingNumber + " 环战斗进度 " + task.Progress + "/" + task.Required +
                    (keepWalking ? "，继续持续走步。" : "。"), false);
                if (!task.IsComplete)
                {
                    if (keepWalking) return;
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

        private void HandleTaskRemoved(int expectedGeneration)
        {
            bool running;
            bool roundFinished;
            bool finish;
            int removedRing;
            lock (syncRoot)
            {
                if (disposed) return;
                removedRing = pendingTurnInRing;
                running = IsCurrentLocked(expectedGeneration);
                if (running && removedRing == 0) return;
                // Duplicate removals and unrelated task removals must not count twice.
                if (removedRing == 0 && latestTask == null) return;
                int cachedRing = latestTask == null ? 0 : latestTask.RingNumber;
                latestTask = null;
                latestTaskUtc = DateTime.MinValue;
                pendingTurnInRing = 0;
                expectedNextRing = removedRing == 0 ? 0 : (removedRing % RingsPerRound) + 1;
                if (!running)
                {
                    // A manually completed twentieth ring must not reappear from an old snapshot.
                    if (removedRing == 0 && cachedRing == RingsPerRound) expectedNextRing = 1;
                    return;
                }
                completedRings++;
                roundFinished = removedRing == RingsPerRound;
                if (roundFinished) completedRounds++;
                finish = roundFinished && completedRounds >= plannedRounds;
                DisposeTimersLocked();
                state = RunLoopAutomationState.WaitingForTask;
                if (!finish)
                    ScheduleStageLocked(expectedGeneration, roundFinished ? 250 : 15000,
                        roundFinished ? (Action<int>)BeginStartNpc : OnStageTimeout);
            }
            if (finish)
                Complete(expectedGeneration, "已交付第 20 环，完成 " + CompletedRounds + "/" + PlannedRounds +
                    " 轮，本次共交付 " + CompletedRings + " 环。");
            else
                Publish(expectedGeneration, RunLoopAutomationState.WaitingForTask,
                    "第 " + removedRing + " 环交付已由服务端确认，旧任务已清除。" +
                    (roundFinished ? "返回尚其村柳先元重新领取下一轮。" : "等待第 " + (removedRing + 1) + " 环任务。"), false);
        }

        private void DispatchTask(int expectedGeneration, RunLoopTask task)
        {
            if (task.Kind == RunLoopTaskKind.DeliverItem || task.Kind == RunLoopTaskKind.TalkToNpc || task.IsComplete)
            {
                BountyTravelTarget target;
                if (!TryResolveNpc(task, out target))
                {
                    Fail(expectedGeneration, "无法从已确认的 NPC 表解析“" + task.TurnInNpcName + "”（" +
                        task.TurnInMapName + " " + task.TurnInX + "," + task.TurnInY +
                        "）；已停止，未发送猜测 NPC ID。请先在“NPC 目录采集”中遍历包含该 NPC 的地图，" +
                        "或录制一次进入该地图并靠近该 NPC 的会话。");
                    return;
                }
                BeginRoute(expectedGeneration, new List<BountyTravelTarget> { target }, DestinationPurpose.TurnInNpc,
                    "第 " + task.RingNumber + " 环前往 " + task.TurnInNpcName +
                    (task.Kind == RunLoopTaskKind.DeliverItem ? "提交“" + task.ItemName + "”" :
                    (task.Kind == RunLoopTaskKind.TalkToNpc ? "完成 NPC 对话" : "交付战斗任务")));
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
                currentMapGrid = null;
                patrolPath = null;
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
                    if (purpose == DestinationPurpose.TurnInNpc && latestTask != null)
                        pendingTurnInRing = latestTask.RingNumber;
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
                patrolPath = null;
                huntProgressWatch.Restart();
                mapDataWaitWatch.Restart();
                DisposeStageTimerLocked();
                DisposeCombatTimersLocked();
                walkTimer = new System.Threading.Timer(delegate { SendWalkStep(expectedGeneration); }, null,
                    walkOptions.InitialDelayMs, walkOptions.StepIntervalMs);
                if (automaticRecovery)
                    recoveryTimer = new System.Threading.Timer(delegate { SendRecovery(expectedGeneration); }, null,
                        walkOptions.RecoveryIntervalMs, walkOptions.RecoveryIntervalMs);
            }
            Publish(expectedGeneration, RunLoopAutomationState.WalkingForEncounter,
                "已到 " + currentMapName + "，每 " + walkOptions.StepIntervalMs + "ms 在最多 " +
                walkOptions.PatrolPointCount + " 个有效点之间往返走步；战斗结果由任务进度包确认。", false);
        }

        private void SendWalkStep(int expectedGeneration)
        {
            int mapId;
            bool stuck;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != RunLoopAutomationState.WalkingForEncounter) return;
                mapId = currentMapId;
                stuck = huntProgressWatch.ElapsedMilliseconds >= walkOptions.NoProgressTimeoutMs;
                if (!stuck)
                {
                    if (patrolPath == null)
                    {
                        RunLoopMapGrid grid = currentMapGrid != null && currentMapGrid.MapId == mapId ? currentMapGrid : null;
                        if (!RunLoopPatrolPath.TryCreate(grid, currentScaledX, currentScaledY,
                            walkOptions.PatrolPointCount, out patrolPath))
                        {
                            if (mapDataWaitWatch.ElapsedMilliseconds >= walkOptions.MapDataWaitTimeoutMs)
                                Fail(expectedGeneration, "未能从当前地图数据找到至少两个相连的可行走点，已停止自动遇怪。");
                            return;
                        }
                        Publish(expectedGeneration, RunLoopAutomationState.WalkingForEncounter,
                            "已固定 " + patrolPath.Points.Count + " 个有效走步点，开始循环往返。", false);
                    }
                    if (mapId <= 0) { Fail(expectedGeneration, "尚未取得当前地图 ID，无法构造走步包。"); return; }
                    Point point = patrolPath.Next();
                    currentScaledX = (ushort)point.X;
                    currentScaledY = (ushort)point.Y;
                    int step = ++walkStep;
                    long epoch = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                    // Serialize the timer callbacks through packet submission so route order cannot overlap.
                    SendPacket(expectedGeneration, "循环走步遇怪 " + step, delegate(uint sequence)
                    {
                        return TianshuRunLoopProtocol.BuildMovement(epoch, mapId, (ushort)point.X, (ushort)point.Y, sequence);
                    });
                }
            }
            if (stuck) RecoverFromStuckHunt(expectedGeneration);
        }

        private void SendRecovery(int expectedGeneration)
        {
            RunLoopAutomationState current;
            lock (syncRoot) current = state;
            if (current != RunLoopAutomationState.WalkingForEncounter && current != RunLoopAutomationState.WaitingForCombatResult) return;
            SendPacket(expectedGeneration, "战斗一键恢复 HP/MP", TianshuRunLoopProtocol.BuildOneKeyRecovery);
        }

        private void RecoverFromStuckHunt(int expectedGeneration)
        {
            RunLoopTask task;
            int retries;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                task = latestTask == null ? null : latestTask.Clone();
                retries = huntRetryCount;
                huntProgressWatch.Restart();
                DisposeCombatTimersLocked();
                state = RunLoopAutomationState.WaitingForTask;
            }
            if (task == null || task.Kind != RunLoopTaskKind.Hunt)
            {
                Fail(expectedGeneration, "战斗长时间未遇怪，且没有可继续的战斗任务，自动停止。");
                return;
            }
            if (retries >= walkOptions.MaximumHuntRetries)
            {
                Fail(expectedGeneration, "第 " + task.RingNumber + " 环战斗长时间未遇怪，已重新到图 " +
                    walkOptions.MaximumHuntRetries + " 次仍未触发，自动停止。");
                return;
            }
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                huntRetryCount = retries + 1;
            }
            Publish(expectedGeneration, RunLoopAutomationState.WaitingForTask,
                "第 " + task.RingNumber + " 环战斗长时间未遇怪，重新到图重试 " + (retries + 1) + "/" + walkOptions.MaximumHuntRetries + "。", false);
            DispatchTask(expectedGeneration, task);
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
            string name = TianshuRunLoopProtocol.NormalizeName(task.TurnInNpcName);
            int mapId;
            bool hasMapId = TryResolveMapId(task.TurnInMapName, out mapId);
            lock (syncRoot) TryFindNpcLocked(name, hasMapId ? mapId : 0, out target);
            if (target == null)
            {
                catalogReady.WaitOne(2000);
                lock (syncRoot) TryFindNpcLocked(name, hasMapId ? mapId : 0, out target);
            }
            if (target == null) return false;
            target.X = task.TurnInX;
            target.Y = task.TurnInY;
            if (hasMapId) target.MapId = mapId;
            return target.MapId > 0;
        }

        private bool TryFindNpcLocked(string name, int mapId, out BountyTravelTarget target)
        {
            target = null;
            if (mapId > 0)
            {
                BountyTravelTarget exact;
                if (npcDirectory.TryGetValue(BuildNpcKey(name, mapId), out exact))
                {
                    target = exact.Clone();
                    return true;
                }
                foreach (KeyValuePair<string, BountyTravelTarget> pair in npcDirectory)
                {
                    if (pair.Value.MapId != mapId) continue;
                    string keyName = GetNameFromNpcKey(pair.Key);
                    if (string.Equals(keyName, name, StringComparison.Ordinal) ||
                        keyName.EndsWith(name, StringComparison.Ordinal) || name.EndsWith(keyName, StringComparison.Ordinal))
                    {
                        target = pair.Value.Clone();
                        return true;
                    }
                }
            }
            foreach (KeyValuePair<string, BountyTravelTarget> pair in npcDirectory)
            {
                string keyName = GetNameFromNpcKey(pair.Key);
                if (string.Equals(keyName, name, StringComparison.Ordinal) ||
                    keyName.EndsWith(name, StringComparison.Ordinal) || name.EndsWith(keyName, StringComparison.Ordinal))
                {
                    target = pair.Value.Clone();
                    return true;
                }
            }
            return false;
        }

        private static string GetNameFromNpcKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            int separator = key.LastIndexOf('|');
            return separator < 0 ? key : key.Substring(0, separator);
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
                { "近天回廊", 110 }, { "百鸟树林", 111 }, { "练功房", 213 }, { "帮会大厅", 1058 },
                { "新月村", 3 }, { "新月村城镇", 3 }
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
                    npcDirectory[BuildNpcKey(entity.Name, currentMapId)] = new BountyTravelTarget
                    {
                        NpcId = entity.Id,
                        MapId = currentMapId,
                        X = entity.X,
                        Y = entity.Y
                    };
                }
            }
        }

        private void HandleMapData(byte[] bytes)
        {
            RunLoopMapGrid grid;
            if (!TianshuRunLoopProtocol.TryParseMapData(bytes, out grid)) return;
            lock (syncRoot)
            {
                if (disposed) return;
                if (grid.MapId != currentMapId) return;
                currentMapGrid = grid;
                patrolPath = null;
                mapDataWaitWatch.Restart();
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

    }

    public sealed class RunLoopAutomationControl : UserControl
    {
        private readonly RunLoopAutomationCoordinator coordinator;
        private readonly NumericUpDown rounds;
        private readonly CheckBox automaticRecovery;
        private readonly Button startButton;
        private readonly Button resumeButton;
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
                Text = "支持提交道具、NPC 对话和战斗。每轮 20 环，交付第 20 环后自动返回柳先元重新接取；本次最多 6 轮 / 120 环，服务端次数用尽时停止。"
            };
            FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, AutoSize = false };
            options.Controls.Add(new Label { Text = "本次轮数", AutoSize = true, Margin = new Padding(3, 9, 3, 3) });
            rounds = new NumericUpDown { Minimum = 1, Maximum = RunLoopAutomationCoordinator.MaximumPlannedRounds, Value = 6, Width = 55 };
            options.Controls.Add(rounds);
            automaticRecovery = new CheckBox { Text = "战斗自动恢复 HP/MP", Checked = true, AutoSize = true, Margin = new Padding(10, 7, 3, 3) };
            options.Controls.Add(automaticRecovery);
            startButton = new Button { Text = "开始自动跑环", AutoSize = true };
            resumeButton = new Button { Text = "从当前环继续", AutoSize = true };
            stopButton = new Button { Text = "停止", AutoSize = true, Enabled = false };
            options.Controls.Add(startButton);
            options.Controls.Add(resumeButton);
            options.Controls.Add(stopButton);

            stateLabel = new Label { Dock = DockStyle.Top, Height = 23, Text = "状态：Inactive" };
            progressLabel = new Label { Dock = DockStyle.Top, Height = 23, Text = "进度：0/6 轮，0 环" };
            taskLabel = new Label { Dock = DockStyle.Top, Height = 44, AutoEllipsis = true, Text = "当前任务：等待服务端任务包" };
            Controls.Add(taskLabel);
            Controls.Add(progressLabel);
            Controls.Add(stateLabel);
            Controls.Add(options);
            Controls.Add(help);

            startButton.Click += OnStart;
            resumeButton.Click += OnResume;
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

        private void OnResume(object sender, EventArgs e)
        {
            try { coordinator.Resume((int)rounds.Value, automaticRecovery.Checked); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "继续跑环", MessageBoxButtons.OK, MessageBoxIcon.Error); }
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
            resumeButton.Enabled = !running;
            stopButton.Enabled = running;
            rounds.Enabled = !running;
            automaticRecovery.Enabled = !running;
            stateLabel.Text = "状态：" + automationState;
            progressLabel.Text = "进度：" + coordinator.CompletedRounds + "/" + coordinator.PlannedRounds +
                " 轮，已处理 " + coordinator.CompletedRings + " 环";
            taskLabel.Text = "当前任务：" + coordinator.DescribeCurrentTaskAction();
        }
    }
}
