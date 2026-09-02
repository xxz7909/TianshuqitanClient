using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TianshuQitanLauncher.Protocol
{
    public enum DonationAutomationState
    {
        Inactive = 0,
        WaitingForGameConnection = 1,
        WaitingForInventory = 2,
        OpeningNpc = 3,
        WaitingForNpcDialog = 4,
        SelectingFunction = 5,
        WaitingForDonationPanel = 6,
        SendingDonation = 7,
        WaitingForStagedItem = 8,
        ConfirmingDonation = 9,
        WaitingForResult = 10,
        Completed = 11,
        Stopped = 12,
        Failed = 13
    }

    public sealed class DonationInventoryItem
    {
        public ushort BagId { get; set; }
        public ushort Slot { get; set; }
        public ushort Count { get; set; }
        public string TemplateId { get; set; }
        public string Name { get; set; }
        public bool IsTargetFragment { get; set; }

        public DonationInventoryItem Clone()
        {
            return (DonationInventoryItem)MemberwiseClone();
        }

        public override string ToString()
        {
            return (Name ?? TemplateId ?? "未知物品") + " / 背包 " + BagId + " / 格 " + Slot + " / 数量 " + Count;
        }
    }

    public static class TianshuDonationProtocol
    {
        public const int ClientItemTransfer = 0x001D;
        public const int ClientUiAction = 0x002E;
        public const uint DonationConfirmAction = 0x03B9;
        public const int ServerItemUpdate = 0x0024;
        public const int ServerItemRemove = 0x0025;
        public const int ServerInventorySnapshot = 0x00B2;
        public const int ServerDonationPanel = 0x03C6;
        public const ushort SourceBagId = 1;
        public const ushort DonationContainerId = 0x0099;
        public const int DefaultDonationNpcId = 93631;
        public const string AncientArtifactFragmentTemplateId = "115000140";
        public const string DonationFunctionLabel = "帮派捐献";

        private static readonly byte[] TargetTemplateBytes = Encoding.ASCII.GetBytes(AncientArtifactFragmentTemplateId);
        private static readonly byte[] TargetNameBytes = Encoding.UTF8.GetBytes("上古神器碎片(一等)");
        private static readonly byte[] TargetFullWidthNameBytes = Encoding.UTF8.GetBytes("上古神器碎片（一等）");

        public static byte[] BuildDonateItem(ushort sourceSlot, uint sequence)
        {
            byte[] bytes = new byte[18];
            WriteUInt16(bytes, 0, (ushort)bytes.Length);
            WriteUInt16(bytes, 2, ClientItemTransfer);
            WriteUInt16(bytes, 4, 1);
            WriteUInt16(bytes, 6, sourceSlot);
            WriteUInt16(bytes, 8, DonationContainerId);
            WriteUInt32(bytes, 10, 0);
            WriteUInt32(bytes, 14, sequence);
            return bytes;
        }

        public static bool TryParseDonationRequest(byte[] bytes, out ushort sourceSlot, out uint sequence)
        {
            sourceSlot = 0;
            sequence = 0;
            if (!HasOpcode(bytes, ClientItemTransfer) || bytes.Length != 18 || ReadUInt16(bytes, 4) != 1 ||
                ReadUInt16(bytes, 8) != DonationContainerId || ReadUInt32(bytes, 10) != 0) return false;
            sourceSlot = ReadUInt16(bytes, 6);
            sequence = ReadUInt32(bytes, 14);
            return true;
        }

        public static byte[] BuildDonationConfirm(uint sequence)
        {
            byte[] bytes = new byte[24];
            WriteUInt16(bytes, 0, (ushort)bytes.Length);
            WriteUInt16(bytes, 2, ClientUiAction);
            WriteUInt32(bytes, 4, DonationConfirmAction);
            WriteUInt32(bytes, 8, 1);
            WriteUInt32(bytes, 12, 0);
            WriteUInt32(bytes, 16, 0);
            WriteUInt32(bytes, 20, sequence);
            return bytes;
        }

        public static bool TryParseDonationConfirm(byte[] bytes, out uint sequence)
        {
            sequence = 0;
            if (!HasOpcode(bytes, ClientUiAction) || bytes.Length != 24 ||
                ReadUInt32(bytes, 4) != DonationConfirmAction || ReadUInt32(bytes, 8) != 1 ||
                ReadUInt32(bytes, 12) != 0 || ReadUInt32(bytes, 16) != 0) return false;
            sequence = ReadUInt32(bytes, 20);
            return true;
        }

        public static bool TryParseInventoryUpdate(byte[] bytes, out DonationInventoryItem item)
        {
            item = null;
            if (!HasOpcode(bytes, ServerItemUpdate) || bytes.Length < 10) return false;
            bool idMatched = IndexOf(bytes, TargetTemplateBytes, 10) >= 0;
            bool nameMatched = IndexOf(bytes, TargetNameBytes, 10) >= 0;
            bool target = idMatched || nameMatched;
            item = new DonationInventoryItem
            {
                BagId = ReadUInt16(bytes, 4),
                Slot = ReadUInt16(bytes, 6),
                Count = ReadUInt16(bytes, 8),
                TemplateId = target ? AncientArtifactFragmentTemplateId : null,
                Name = target ? "上古神器碎片(一等)" : null,
                IsTargetFragment = target
            };
            return true;
        }

        public static bool TryParseInventoryRemoval(byte[] bytes, out ushort bagId, out ushort slot)
        {
            bagId = 0;
            slot = 0;
            if (!HasOpcode(bytes, ServerItemRemove) || bytes.Length < 8) return false;
            bagId = ReadUInt16(bytes, 4);
            slot = ReadUInt16(bytes, 6);
            return true;
        }

        public static bool TryParseInventorySnapshot(byte[] bytes, out IList<DonationInventoryItem> items)
        {
            items = new List<DonationInventoryItem>();
            if (!HasOpcode(bytes, ServerInventorySnapshot) || bytes.Length < 14) return false;
            int zlibLength = ReadUInt16(bytes, 4);
            if (zlibLength < 8 || zlibLength > bytes.Length - 6 || bytes[6] != 0x78) return false;

            byte[] decompressed;
            try
            {
                // The payload is RFC 1950 zlib: two-byte zlib header, raw DEFLATE data,
                // then a four-byte Adler checksum. DeflateStream consumes the raw middle part.
                int deflateOffset = 8;
                int deflateLength = zlibLength - 6;
                using (MemoryStream input = new MemoryStream(bytes, deflateOffset, deflateLength, false))
                using (DeflateStream inflater = new DeflateStream(input, CompressionMode.Decompress))
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    int total = 0;
                    int read;
                    while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > 16 * 1024 * 1024) return false;
                        output.Write(buffer, 0, read);
                    }
                    decompressed = output.ToArray();
                }
            }
            catch (InvalidDataException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }

            Dictionary<ushort, DonationInventoryItem> found = new Dictionary<ushort, DonationInventoryItem>();
            FindSnapshotItems(decompressed, TargetNameBytes, found);
            FindSnapshotItems(decompressed, TargetFullWidthNameBytes, found);
            foreach (DonationInventoryItem item in found.Values) items.Add(item);
            return true;
        }

        public static bool TryParsePanelReady(byte[] bytes, out ushort panelState)
        {
            panelState = 0;
            if (!HasOpcode(bytes, ServerDonationPanel) || bytes.Length < 6) return false;
            panelState = ReadUInt16(bytes, 4);
            return true;
        }

        public static bool TryParseNpcId(byte[] bytes, out int npcId)
        {
            npcId = 0;
            if (!HasOpcode(bytes, TianshuBountyProtocol.ServerNpcDialog) || bytes.Length < 8) return false;
            npcId = (int)ReadUInt32(bytes, 4);
            return true;
        }

        public static bool IsDonationSuccess(byte[] bytes)
        {
            return HasOpcode(bytes, TianshuBountyProtocol.ServerSystemMessage) &&
                (TianshuBountyProtocol.ContainsText(bytes, "感谢你为帮派作出的贡献") ||
                 TianshuBountyProtocol.ContainsText(bytes, "帮派贡献增加"));
        }

        public static bool IsDonationLimit(byte[] bytes)
        {
            if (!HasOpcode(bytes, TianshuBountyProtocol.ServerSystemMessage) || IsDonationSuccess(bytes) ||
                !TianshuBountyProtocol.ContainsText(bytes, "捐献")) return false;
            string[] markers = { "次数", "上限", "已满", "用完", "用尽", "不能", "无法", "不足", "没有", "今日", "完成" };
            for (int i = 0; i < markers.Length; i++)
                if (TianshuBountyProtocol.ContainsText(bytes, markers[i])) return true;
            return false;
        }

        public static bool IsDonationContainerEmpty(byte[] bytes)
        {
            return HasOpcode(bytes, TianshuBountyProtocol.ServerSystemMessage) &&
                (TianshuBountyProtocol.ContainsText(bytes, "还没有放入物品") ||
                 TianshuBountyProtocol.ContainsText(bytes, "没有放入物品"));
        }

        private static bool HasOpcode(byte[] bytes, int opcode)
        {
            return bytes != null && bytes.Length >= 4 && ReadUInt16(bytes, 2) == opcode;
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

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            if (haystack == null || needle == null || needle.Length == 0) return -1;
            for (int i = Math.Max(0, start); i <= haystack.Length - needle.Length; i++)
            {
                int j;
                for (j = 0; j < needle.Length && haystack[i + j] == needle[j]; j++) { }
                if (j == needle.Length) return i;
            }
            return -1;
        }

        private static void FindSnapshotItems(byte[] bytes, byte[] itemName, IDictionary<ushort, DonationInventoryItem> found)
        {
            int searchOffset = 0;
            while (searchOffset <= bytes.Length - itemName.Length)
            {
                int nameOffset = IndexOf(bytes, itemName, searchOffset);
                if (nameOffset < 0) return;
                searchOffset = nameOffset + itemName.Length;
                // Each decompressed item record starts with the same confirmed prefix as
                // SC_ITEM_UPDATE: bag, slot, stack count, an unknown u16, then name length.
                int recordOffset = nameOffset - 10;
                if (recordOffset < 0 || ReadUInt16(bytes, nameOffset - 2) != itemName.Length) continue;
                ushort bagId = ReadUInt16(bytes, recordOffset);
                ushort slot = ReadUInt16(bytes, recordOffset + 2);
                ushort count = ReadUInt16(bytes, recordOffset + 4);
                if (bagId != SourceBagId || slot > 4096 || count == 0) continue;
                found[slot] = new DonationInventoryItem
                {
                    BagId = bagId,
                    Slot = slot,
                    Count = count,
                    TemplateId = AncientArtifactFragmentTemplateId,
                    Name = "上古神器碎片(一等)",
                    IsTargetFragment = true
                };
            }
        }
    }

    public sealed class DonationAutomationCoordinator : IDisposable
    {
        private const string AutomationOwner = "AutoDonation";
        private readonly object syncRoot = new object();
        private readonly ProtocolWorkbenchService service;
        private readonly Dictionary<long, ConnectionSession> gameConnections = new Dictionary<long, ConnectionSession>();
        private readonly Dictionary<ushort, DonationInventoryItem> sourceItems = new Dictionary<ushort, DonationInventoryItem>();
        private System.Threading.Timer timer;
        private DonationAutomationState state;
        private long gameConnectionId;
        private uint nextSequence;
        private bool sequenceKnown;
        private bool activatedByAutomation;
        private int configuredNpcId;
        private string donationFunctionId;
        private int safetyLimit;
        private int donatedCount;
        private ushort expectedSlot;
        private ushort expectedBeforeCount;
        private bool inventoryConfirmed;
        private bool awaitingPostSuccessInventory;
        private int generation;
        private bool disposed;

        public event Action<DonationAutomationState, string> StatusChanged;

        public DonationAutomationCoordinator(ProtocolWorkbenchService service)
        {
            if (service == null) throw new ArgumentNullException("service");
            this.service = service;
            state = DonationAutomationState.Inactive;
            configuredNpcId = TianshuDonationProtocol.DefaultDonationNpcId;
            service.ConnectionChanged += OnConnectionChanged;
            service.FrameCaptured += OnFrameCaptured;
            service.ActiveModeChanged += OnActiveModeChanged;
        }

        public DonationAutomationState State { get { lock (syncRoot) return state; } }
        public int DonatedCount { get { lock (syncRoot) return donatedCount; } }

        public DonationInventoryItem CurrentItem
        {
            get { lock (syncRoot) { DonationInventoryItem item = SelectItemLocked(); return item == null ? null : item.Clone(); } }
        }

        public void Start(int npcId, int maximumDonations)
        {
            if (npcId <= 0) throw new ArgumentOutOfRangeException("npcId");
            if (maximumDonations < 1 || maximumDonations > 9999) throw new ArgumentOutOfRangeException("maximumDonations");
            string currentOwner;
            if (!service.TryAcquireAutomation(AutomationOwner, out currentOwner))
                throw new InvalidOperationException("当前已有自动化流程正在运行：" + currentOwner + "。请先停止后再启动自动捐献。");

            int currentGeneration;
            bool enableActive;
            bool begin;
            bool hasItem;
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
                configuredNpcId = npcId;
                safetyLimit = maximumDonations;
                donatedCount = 0;
                donationFunctionId = null;
                expectedSlot = 0;
                expectedBeforeCount = 0;
                inventoryConfirmed = false;
                awaitingPostSuccessInventory = false;
                enableActive = !service.ActiveMode;
                activatedByAutomation = enableActive;
                hasItem = SelectItemLocked() != null;
                begin = gameConnectionId != 0 && sequenceKnown && hasItem;
                if (begin)
                {
                    state = DonationAutomationState.OpeningNpc;
                    ScheduleLocked(currentGeneration, 100, OpenNpc);
                }
                else if (gameConnectionId == 0 || !sequenceKnown)
                {
                    state = DonationAutomationState.WaitingForGameConnection;
                    ScheduleLocked(currentGeneration, 15000, OnStageTimeout);
                }
                else
                {
                    state = DonationAutomationState.WaitingForInventory;
                    ScheduleLocked(currentGeneration, 3000, OnInitialInventoryTimeout);
                }
            }
            if (enableActive) service.SetActiveMode(true, false);
            Publish(currentGeneration, state, begin
                ? "自动捐献已启动，已定位碎片，准备访问帮会捐献官。"
                : (!hasItem ? "自动捐献已启动，正在等待背包中的一等上古神器碎片数据。" : "自动捐献已启动，等待游戏连接和实时请求序号。"), false);
        }

        public void Stop()
        {
            StopCore(DonationAutomationState.Stopped, "自动捐献已停止。", false);
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
            }
        }

        private void OnFrameCaptured(ProtocolFrame frame)
        {
            if (frame == null || !frame.Opcode.HasValue || frame.Bytes == null) return;
            int opcode = (int)frame.Opcode.Value;
            int currentGeneration;
            DonationAutomationState currentState;
            bool begin = false;

            lock (syncRoot)
            {
                if (disposed) return;
                if (frame.Direction == TrafficDirection.ClientToServer && IsConfirmedSequenceOpcode(opcode) &&
                    frame.ConnectionId != gameConnectionId && gameConnections.ContainsKey(frame.ConnectionId))
                {
                    gameConnectionId = frame.ConnectionId;
                    sequenceKnown = false;
                    nextSequence = 0;
                }
                if (frame.Direction == TrafficDirection.ClientToServer && frame.ConnectionId == gameConnectionId)
                {
                    ObserveSequenceLocked(opcode, frame.Bytes);
                    if (state == DonationAutomationState.WaitingForGameConnection && sequenceKnown && SelectItemLocked() != null)
                    {
                        state = DonationAutomationState.OpeningNpc;
                        DisposeTimerLocked();
                        ScheduleLocked(generation, 100, OpenNpc);
                        begin = true;
                    }
                }
                currentGeneration = generation;
                currentState = state;
            }

            if (frame.Direction == TrafficDirection.ServerToClient)
            {
                if (opcode == TianshuDonationProtocol.ServerInventorySnapshot)
                    HandleInventorySnapshot(currentGeneration, frame.Bytes);
                else if (opcode == TianshuDonationProtocol.ServerItemUpdate)
                    HandleInventoryUpdate(currentGeneration, frame.Bytes);
                else if (opcode == TianshuDonationProtocol.ServerItemRemove)
                    HandleInventoryRemoval(currentGeneration, frame.Bytes);
            }
            if (begin)
                Publish(currentGeneration, DonationAutomationState.OpeningNpc, "已取得游戏连接和实时序号，准备访问帮会捐献官。", false);

            if (!IsRunning(currentState) || frame.Direction != TrafficDirection.ServerToClient ||
                frame.ConnectionId != GetGameConnectionId()) return;
            if (opcode == TianshuBountyProtocol.ServerNpcDialog)
                HandleNpcDialog(currentGeneration, frame.Bytes);
            else if (opcode == TianshuDonationProtocol.ServerDonationPanel)
                HandleDonationPanel(currentGeneration, frame.Bytes);
            else if (opcode == TianshuBountyProtocol.ServerSystemMessage)
                HandleSystemMessage(currentGeneration, frame.Bytes);
        }

        private void HandleInventorySnapshot(int expectedGeneration, byte[] bytes)
        {
            IList<DonationInventoryItem> items;
            if (!TianshuDonationProtocol.TryParseInventorySnapshot(bytes, out items)) return;
            bool begin = false;
            lock (syncRoot)
            {
                if (disposed) return;
                for (int i = 0; i < items.Count; i++) sourceItems[items[i].Slot] = items[i];
                if (state == DonationAutomationState.WaitingForInventory && !awaitingPostSuccessInventory &&
                    gameConnectionId != 0 && sequenceKnown && SelectItemLocked() != null)
                {
                    state = DonationAutomationState.OpeningNpc;
                    DisposeTimerLocked();
                    ScheduleLocked(generation, 100, OpenNpc);
                    begin = true;
                }
            }
            if (begin)
                Publish(expectedGeneration, DonationAutomationState.OpeningNpc,
                    "已从登录初始背包快照按名称找到一等上古神器碎片，准备访问帮会捐献官。", false);
        }

        private void HandleInventoryUpdate(int expectedGeneration, byte[] bytes)
        {
            DonationInventoryItem item;
            if (!TianshuDonationProtocol.TryParseInventoryUpdate(bytes, out item)) return;
            if (item.BagId == TianshuDonationProtocol.DonationContainerId)
            {
                bool confirm = false;
                lock (syncRoot)
                {
                    if (IsCurrentLocked(expectedGeneration) && state == DonationAutomationState.WaitingForStagedItem &&
                        item.Slot == 0 && item.IsTargetFragment && item.Count > 0)
                    {
                        state = DonationAutomationState.ConfirmingDonation;
                        DisposeTimerLocked();
                        ScheduleLocked(expectedGeneration, 160, ConfirmDonation);
                        confirm = true;
                    }
                }
                if (confirm)
                    Publish(expectedGeneration, DonationAutomationState.ConfirmingDonation,
                        "服务端已确认碎片进入临时捐献栏，准备点击“捐献”按钮。", false);
                return;
            }
            if (item.BagId != TianshuDonationProtocol.SourceBagId) return;
            bool proceed = false;
            bool begin = false;
            lock (syncRoot)
            {
                if (disposed) return;
                if (item.IsTargetFragment && item.Count > 0)
                    sourceItems[item.Slot] = item;
                else
                    sourceItems.Remove(item.Slot);

                if ((state == DonationAutomationState.WaitingForResult || awaitingPostSuccessInventory) &&
                    item.Slot == expectedSlot && (!item.IsTargetFragment || item.Count < expectedBeforeCount))
                {
                    inventoryConfirmed = true;
                    proceed = awaitingPostSuccessInventory;
                }
                if (state == DonationAutomationState.WaitingForInventory && !awaitingPostSuccessInventory &&
                    gameConnectionId != 0 && sequenceKnown && SelectItemLocked() != null)
                {
                    state = DonationAutomationState.OpeningNpc;
                    DisposeTimerLocked();
                    ScheduleLocked(generation, 100, OpenNpc);
                    begin = true;
                }
            }
            if (proceed) ProceedAfterSuccess(expectedGeneration);
            else if (begin) Publish(expectedGeneration, DonationAutomationState.OpeningNpc, "已发现可捐献碎片，准备访问帮会捐献官。", false);
        }

        private void HandleInventoryRemoval(int expectedGeneration, byte[] bytes)
        {
            ushort bagId;
            ushort slot;
            if (!TianshuDonationProtocol.TryParseInventoryRemoval(bytes, out bagId, out slot)) return;
            bool proceed = false;
            lock (syncRoot)
            {
                if (disposed) return;
                if (bagId == TianshuDonationProtocol.SourceBagId)
                {
                    // The server temporarily removes the complete source stack while moving it into
                    // container 0x99. Before the success message this is not evidence of exhaustion.
                    sourceItems.Remove(slot);
                    if (awaitingPostSuccessInventory && slot == expectedSlot)
                    {
                        inventoryConfirmed = true;
                        proceed = true;
                    }
                }
                else if (bagId == TianshuDonationProtocol.DonationContainerId && slot == 0 &&
                    awaitingPostSuccessInventory && SelectItemLocked() == null)
                {
                    // On the last item there is no source-stack update after success. Closing the
                    // temporary donation container while the source remains absent confirms exhaustion.
                    inventoryConfirmed = true;
                    proceed = true;
                }
            }
            if (proceed) ProceedAfterSuccess(expectedGeneration);
        }

        private void OpenNpc(int expectedGeneration)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != DonationAutomationState.OpeningNpc) return;
                state = DonationAutomationState.WaitingForNpcDialog;
                DisposeTimerLocked();
                ScheduleLocked(expectedGeneration, 8000, OnStageTimeout);
            }
            Publish(expectedGeneration, DonationAutomationState.WaitingForNpcDialog,
                "访问帮会捐献官 NPC " + configuredNpcId + "，等待服务端功能列表。", false);
            SendPacket(expectedGeneration, "访问帮会捐献官", delegate(uint sequence)
            {
                return TianshuBountyProtocol.BuildNpcOpen(configuredNpcId, sequence);
            });
        }

        private void HandleNpcDialog(int expectedGeneration, byte[] bytes)
        {
            int npcId;
            string functionId;
            if (TianshuBountyProtocol.TryParseNpcFunction(bytes, TianshuDonationProtocol.DonationFunctionLabel, out npcId, out functionId))
            {
                lock (syncRoot)
                {
                    if (!IsCurrentLocked(expectedGeneration) || state != DonationAutomationState.WaitingForNpcDialog) return;
                    configuredNpcId = npcId;
                    donationFunctionId = functionId;
                    state = DonationAutomationState.SelectingFunction;
                    DisposeTimerLocked();
                    ScheduleLocked(expectedGeneration, 160, SelectDonationFunction);
                }
                Publish(expectedGeneration, DonationAutomationState.SelectingFunction,
                    "已动态解析“帮派捐献”功能 ID=" + functionId + "，准备选择。", false);
                return;
            }
            if (TianshuDonationProtocol.TryParseNpcId(bytes, out npcId) && npcId == configuredNpcId)
                Complete(expectedGeneration, "服务端功能列表中已没有“帮派捐献”，按捐献次数已耗尽处理并停止。", false);
        }

        private void SelectDonationFunction(int expectedGeneration)
        {
            int npcId;
            string functionId;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != DonationAutomationState.SelectingFunction ||
                    string.IsNullOrEmpty(donationFunctionId)) return;
                npcId = configuredNpcId;
                functionId = donationFunctionId;
                state = DonationAutomationState.WaitingForDonationPanel;
                DisposeTimerLocked();
                ScheduleLocked(expectedGeneration, 8000, OnStageTimeout);
            }
            Publish(expectedGeneration, DonationAutomationState.WaitingForDonationPanel, "已选择帮派捐献，等待捐献面板就绪。", false);
            SendPacket(expectedGeneration, "选择帮派捐献", delegate(uint sequence)
            {
                return TianshuBountyProtocol.BuildNpcFunction(npcId, functionId, sequence);
            });
        }

        private void HandleDonationPanel(int expectedGeneration, byte[] bytes)
        {
            ushort panelState;
            if (!TianshuDonationProtocol.TryParsePanelReady(bytes, out panelState)) return;
            if (panelState != 3)
            {
                if (panelState == 2) Fail(expectedGeneration, "服务端返回捐献面板状态 2（已放弃/不可捐献），停止继续发包。");
                return;
            }
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != DonationAutomationState.WaitingForDonationPanel) return;
                state = DonationAutomationState.SendingDonation;
                DisposeTimerLocked();
                ScheduleLocked(expectedGeneration, 1200, SendDonation);
            }
            Publish(expectedGeneration, DonationAutomationState.SendingDonation, "捐献面板已就绪，等待客户端界面稳定后提交 1 个碎片。", false);
        }

        private void SendDonation(int expectedGeneration)
        {
            DonationInventoryItem item;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != DonationAutomationState.SendingDonation) return;
                if (donatedCount >= safetyLimit)
                {
                    item = null;
                }
                else
                {
                    item = SelectItemLocked();
                    if (item != null)
                    {
                        expectedSlot = item.Slot;
                        expectedBeforeCount = item.Count;
                        inventoryConfirmed = false;
                        awaitingPostSuccessInventory = false;
                        state = DonationAutomationState.WaitingForStagedItem;
                        DisposeTimerLocked();
                        ScheduleLocked(expectedGeneration, 8000, OnStageTimeout);
                    }
                }
            }
            if (donatedCount >= safetyLimit)
            {
                Complete(expectedGeneration, "已达到界面设置的安全上限 " + safetyLimit + " 次，自动停止。", false);
                return;
            }
            if (item == null)
            {
                Complete(expectedGeneration, "背包中已没有一等上古神器碎片，自动停止。", false);
                return;
            }
            Publish(expectedGeneration, DonationAutomationState.WaitingForStagedItem,
                "放入第 " + (donatedCount + 1) + " 次待捐碎片：动态背包格 " + item.Slot + "，放入前数量 " + item.Count + "。", false);
            SendPacket(expectedGeneration, "将 1 个上古神器碎片放入临时捐献栏", delegate(uint sequence)
            {
                return TianshuDonationProtocol.BuildDonateItem(item.Slot, sequence);
            });
        }

        private void ConfirmDonation(int expectedGeneration)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != DonationAutomationState.ConfirmingDonation) return;
                state = DonationAutomationState.WaitingForResult;
                DisposeTimerLocked();
                ScheduleLocked(expectedGeneration, 10000, OnStageTimeout);
            }
            Publish(expectedGeneration, DonationAutomationState.WaitingForResult,
                "临时捐献栏已就绪，发送第 " + (donatedCount + 1) + " 次“捐献”按钮请求。", false);
            SendPacket(expectedGeneration, "点击捐献按钮", delegate(uint sequence)
            {
                return TianshuDonationProtocol.BuildDonationConfirm(sequence);
            });
        }

        private void HandleSystemMessage(int expectedGeneration, byte[] bytes)
        {
            if (TianshuDonationProtocol.IsDonationContainerEmpty(bytes))
            {
                Fail(expectedGeneration, "服务端提示临时捐献栏没有物品；为避免空提交，已停止继续发包。");
                return;
            }
            if (TianshuDonationProtocol.IsDonationLimit(bytes))
            {
                Complete(expectedGeneration, "服务端提示捐献次数已耗尽/不可继续，自动停止。", false);
                return;
            }
            if (!TianshuDonationProtocol.IsDonationSuccess(bytes)) return;
            bool proceed;
            int completed;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != DonationAutomationState.WaitingForResult) return;
                donatedCount++;
                completed = donatedCount;
                DisposeTimerLocked();
                proceed = inventoryConfirmed;
                if (!proceed)
                {
                    state = DonationAutomationState.WaitingForInventory;
                    awaitingPostSuccessInventory = true;
                    ScheduleLocked(expectedGeneration, 4000, OnPostSuccessInventoryTimeout);
                }
            }
            Publish(expectedGeneration, proceed ? DonationAutomationState.WaitingForResult : DonationAutomationState.WaitingForInventory,
                "服务端确认第 " + completed + " 次捐献成功" + (proceed ? "，背包扣减也已确认。" : "，等待背包扣减确认。"), false);
            if (proceed) ProceedAfterSuccess(expectedGeneration);
        }

        private void ProceedAfterSuccess(int expectedGeneration)
        {
            bool noItem;
            bool safetyReached;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || !inventoryConfirmed) return;
                awaitingPostSuccessInventory = false;
                noItem = SelectItemLocked() == null;
                safetyReached = donatedCount >= safetyLimit;
                DisposeTimerLocked();
                if (!noItem && !safetyReached)
                {
                    state = DonationAutomationState.OpeningNpc;
                    ScheduleLocked(expectedGeneration, 650, OpenNpc);
                }
            }
            if (noItem)
                Complete(expectedGeneration, "捐献成功，背包中的一等上古神器碎片已经耗尽。", false);
            else if (safetyReached)
                Complete(expectedGeneration, "捐献成功，已达到安全上限 " + safetyLimit + " 次。", false);
            else
                Publish(expectedGeneration, DonationAutomationState.OpeningNpc, "本次成功且背包扣减已确认，继续下一次捐献。", false);
        }

        private void OnInitialInventoryTimeout(int expectedGeneration)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration) || state != DonationAutomationState.WaitingForInventory || awaitingPostSuccessInventory) return;
            }
            Complete(expectedGeneration, "未发现物品 ID " + TianshuDonationProtocol.AncientArtifactFragmentTemplateId +
                "（一等上古神器碎片）；请确认背包中有该物品后再启动。", false);
        }

        private void OnPostSuccessInventoryTimeout(int expectedGeneration)
        {
            Fail(expectedGeneration, "服务端已提示捐献成功，但 4 秒内没有收到源背包扣减包；为避免重复提交，已停止继续发包。");
        }

        private void OnStageTimeout(int expectedGeneration)
        {
            DonationAutomationState timedOutState;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                timedOutState = state;
            }
            Fail(expectedGeneration, "等待阶段超时：" + timedOutState + "。已停止继续发包，请保留会话库用于比对。");
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
                Fail(expectedGeneration, description + "发包失败；连接可能已关闭或 ACTIVE 已被关闭。");
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
                if (!IsConfirmedSequenceOpcode(opcode)) return;
                nextSequence = observed + 1;
                sequenceKnown = true;
                return;
            }
            uint candidate = observed + 1;
            if (candidate >= nextSequence && candidate - nextSequence < 4096) nextSequence = candidate;
        }

        private static bool IsConfirmedSequenceOpcode(int opcode)
        {
            return opcode == TianshuBountyProtocol.ClientNpcOpen || opcode == TianshuBountyProtocol.ClientNpcFunction ||
                opcode == TianshuDonationProtocol.ClientItemTransfer || opcode == TianshuBountyProtocol.ClientDialogResponse ||
                opcode == TianshuBountyProtocol.ClientTravelLink || opcode == TianshuBountyProtocol.ClientBattleAdvance ||
                opcode == TianshuDonationProtocol.ClientUiAction || opcode == 0x0042 || opcode == 0x00C1;
        }

        private void OnActiveModeChanged(bool active)
        {
            if (active) return;
            DonationAutomationState current;
            lock (syncRoot) current = state;
            if (IsRunning(current)) StopCore(DonationAutomationState.Stopped, "ACTIVE/发包模式已关闭，自动捐献同步停止。", false);
        }

        private void Complete(int expectedGeneration, string message, bool error)
        {
            bool restoreActive;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = DonationAutomationState.Completed;
                DisposeTimerLocked();
                awaitingPostSuccessInventory = false;
                restoreActive = activatedByAutomation;
                activatedByAutomation = false;
            }
            PublishAny(DonationAutomationState.Completed, message, error);
            service.ReleaseAutomation(AutomationOwner);
            if (restoreActive && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void Fail(int expectedGeneration, string message)
        {
            bool restoreActive;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration)) return;
                state = DonationAutomationState.Failed;
                generation++;
                DisposeTimerLocked();
                awaitingPostSuccessInventory = false;
                restoreActive = activatedByAutomation;
                activatedByAutomation = false;
            }
            PublishAny(DonationAutomationState.Failed, message, true);
            service.ReleaseAutomation(AutomationOwner);
            if (restoreActive && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private void StopCore(DonationAutomationState finalState, string message, bool error)
        {
            bool restoreActive;
            lock (syncRoot)
            {
                if (disposed || !IsRunning(state)) return;
                state = finalState;
                generation++;
                DisposeTimerLocked();
                awaitingPostSuccessInventory = false;
                restoreActive = activatedByAutomation;
                activatedByAutomation = false;
            }
            PublishAny(finalState, message, error);
            service.ReleaseAutomation(AutomationOwner);
            if (restoreActive && service.ActiveMode) service.SetActiveMode(false, false);
        }

        private DonationInventoryItem SelectItemLocked()
        {
            DonationInventoryItem selected = null;
            foreach (DonationInventoryItem item in sourceItems.Values)
            {
                if (!item.IsTargetFragment || item.Count == 0) continue;
                if (selected == null || item.Slot < selected.Slot) selected = item;
            }
            return selected;
        }

        private long SelectLatestGameConnectionLocked()
        {
            long result = 0;
            foreach (KeyValuePair<long, ConnectionSession> pair in gameConnections)
                if (!pair.Value.ClosedUtc.HasValue && pair.Value.RemotePort != 80 && pair.Value.RemotePort != 443 && pair.Key > result) result = pair.Key;
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

        private static bool IsRunning(DonationAutomationState value)
        {
            return value != DonationAutomationState.Inactive && value != DonationAutomationState.Completed &&
                value != DonationAutomationState.Stopped && value != DonationAutomationState.Failed;
        }

        private void Publish(int expectedGeneration, DonationAutomationState publishedState, string message, bool error)
        {
            lock (syncRoot)
            {
                if (disposed || generation != expectedGeneration) return;
            }
            PublishAny(publishedState, message, error);
        }

        private void PublishAny(DonationAutomationState publishedState, string message, bool error)
        {
            Action<DonationAutomationState, string> handler = StatusChanged;
            if (handler != null) handler(publishedState, message);
            long connectionId = GetGameConnectionId();
            service.ReportEngineEvent(AutomationOwner, error ? "ERROR" : "INFO", message,
                connectionId == 0 ? (long?)null : connectionId);
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

    public sealed class DonationAutomationControl : UserControl
    {
        private readonly DonationAutomationCoordinator coordinator;
        private readonly NumericUpDown npcId;
        private readonly NumericUpDown safetyLimit;
        private readonly Label stateLabel;
        private readonly Label progressLabel;
        private readonly Label itemLabel;
        private readonly Button startButton;
        private readonly Button stopButton;

        public DonationAutomationControl(DonationAutomationCoordinator coordinator)
        {
            if (coordinator == null) throw new ArgumentNullException("coordinator");
            this.coordinator = coordinator;
            Dock = DockStyle.Fill;

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                ColumnCount = 1,
                RowCount = 6
            };
            for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label help = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                Text = "请先站在帮会捐献官附近，然后只需点开始。程序按“上古神器碎片(一等)”名称和物品 ID 115000140 自动查询当前背包格，每次先放入 1 个碎片，等服务端确认临时栏后再自动点击“捐献”；NPC 功能 ID、换格后的背包位置和请求序号均动态解析。服务端次数耗尽、碎片耗尽或确认异常时自动停发。"
            };
            layout.Controls.Add(help, 0, 0);

            FlowLayoutPanel options = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            options.Controls.Add(new Label { Text = "捐献官 NPC", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            npcId = new NumericUpDown { Minimum = 1, Maximum = Int32.MaxValue, Value = TianshuDonationProtocol.DefaultDonationNpcId, Width = 100 };
            options.Controls.Add(npcId);
            options.Controls.Add(new Label { Text = "安全上限", AutoSize = true, Padding = new Padding(12, 6, 0, 0) });
            safetyLimit = new NumericUpDown { Minimum = 1, Maximum = 9999, Value = 999, Width = 75 };
            options.Controls.Add(safetyLimit);
            layout.Controls.Add(options, 0, 1);

            FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true };
            startButton = new Button { Text = "开始自动捐献", AutoSize = true };
            stopButton = new Button { Text = "停止", AutoSize = true, Enabled = false };
            startButton.Click += OnStartClick;
            stopButton.Click += delegate { coordinator.Stop(); };
            buttons.Controls.Add(startButton);
            buttons.Controls.Add(stopButton);
            layout.Controls.Add(buttons, 0, 2);

            stateLabel = new Label { AutoSize = true, Text = "状态：" + coordinator.State };
            progressLabel = new Label { AutoSize = true, Text = "本次已成功捐献：0 次" };
            itemLabel = new Label { AutoSize = true, Text = "当前碎片：尚未捕获背包数据" };
            layout.Controls.Add(stateLabel, 0, 3);
            layout.Controls.Add(progressLabel, 0, 4);
            layout.Controls.Add(itemLabel, 0, 5);

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
                coordinator.Start((int)npcId.Value, (int)safetyLimit.Value);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法启动自动捐献", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnStatusChanged(DonationAutomationState automationState, string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<DonationAutomationState, string>(OnStatusChanged), automationState, message); }
                catch (InvalidOperationException) { }
                return;
            }
            bool running = automationState != DonationAutomationState.Inactive && automationState != DonationAutomationState.Completed &&
                automationState != DonationAutomationState.Stopped && automationState != DonationAutomationState.Failed;
            stateLabel.Text = "状态：" + automationState;
            progressLabel.Text = "本次已成功捐献：" + coordinator.DonatedCount + " 次";
            DonationInventoryItem item = coordinator.CurrentItem;
            itemLabel.Text = "当前碎片：" + (item == null ? "未发现/已耗尽" : item.ToString());
            startButton.Enabled = !running;
            stopButton.Enabled = running;
        }
    }
}
