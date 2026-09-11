using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using TianshuQitanLauncher;
using TianshuQitanLauncher.Protocol;

namespace TianshuQitanLauncher.Tests
{
    internal static class Program
    {
        private static int failures;

        [STAThread]
        private static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "TianshuQitanTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Run("Hex codec", TestHexCodec);
                Run("Length-prefix framing", TestFrameDecoder);
                Run("Visual rule actions", delegate { TestRuleActions(root); });
                Run("Lua sandbox and scenario", delegate { TestLua(root); });
                Run("State tracker", TestStateTracker);
                Run("Login protocol parsing and redaction", TestLoginProtocol);
                Run("Bounty protocol parsing and packet builders", TestBountyProtocol);
                Run("Donation protocol parsing and packet builders", TestDonationProtocol);
                Run("Run-loop protocol parsing and packet builders", TestRunLoopProtocol);
                Run("Run-loop fixed walkable patrol and 250ms packet cadence", delegate { TestRunLoopPatrol(root); });
                Run("Run-loop 120-ring reaccept and stale-cache regression", delegate { TestRunLoopRounds(root); });
                Run("Mountain-climb protocol parsing and packet builders", TestMountainClimbProtocol);
                Run("Map-teleport protocol and recorded destination catalog", TestMapTeleportProtocol);
                Run("Unified client logging and fixed GUI", delegate { TestClientLogging(root); });
                Run("Packet-grid multi-select and context actions", delegate { TestPacketGridInteractions(root); });
                string bountyRecording = Environment.GetEnvironmentVariable("TIANSHU_BOUNTY_SAMPLE");
                if (!string.IsNullOrWhiteSpace(bountyRecording))
                    Run("Recorded bounty SQLite replay", delegate { TestRecordedBounty(bountyRecording); });
                string donationRecording = Environment.GetEnvironmentVariable("TIANSHU_DONATION_SAMPLE");
                if (!string.IsNullOrWhiteSpace(donationRecording))
                    Run("Recorded donation SQLite replay", delegate { TestRecordedDonation(donationRecording); });
                string donationConfirmRecording = Environment.GetEnvironmentVariable("TIANSHU_DONATION_CONFIRM_SAMPLE");
                if (!string.IsNullOrWhiteSpace(donationConfirmRecording))
                    Run("Recorded donation-confirm SQLite replay", delegate { TestRecordedDonationConfirmation(donationConfirmRecording); });
                string inventorySnapshotRecording = Environment.GetEnvironmentVariable("TIANSHU_INVENTORY_SNAPSHOT_SAMPLE");
                if (!string.IsNullOrWhiteSpace(inventorySnapshotRecording))
                    Run("Recorded initial inventory snapshot replay", delegate { TestRecordedInventorySnapshot(inventorySnapshotRecording); });
                string runLoopRecording = Environment.GetEnvironmentVariable("TIANSHU_RUN_LOOP_SAMPLE");
                if (!string.IsNullOrWhiteSpace(runLoopRecording))
                    Run("Recorded run-loop SQLite replay", delegate { TestRecordedRunLoop(runLoopRecording); });
                string runLoopDemonstrationRecording = Environment.GetEnvironmentVariable("TIANSHU_RUN_LOOP_DEMO_SAMPLE");
                if (!string.IsNullOrWhiteSpace(runLoopDemonstrationRecording))
                    Run("Recorded run-loop demonstration SQLite replay", delegate { TestRecordedRunLoopDemonstration(runLoopDemonstrationRecording); });
                string mountainClimbRecording = Environment.GetEnvironmentVariable("TIANSHU_MOUNTAIN_CLIMB_SAMPLE");
                if (!string.IsNullOrWhiteSpace(mountainClimbRecording))
                    Run("Recorded mountain-climb SQLite replay", delegate { TestRecordedMountainClimb(mountainClimbRecording); });
                string mountainShortcutRecording = Environment.GetEnvironmentVariable("TIANSHU_MOUNTAIN_SHORTCUT_SAMPLE");
                if (!string.IsNullOrWhiteSpace(mountainShortcutRecording))
                    Run("Recorded mountain-climb shortcut SQLite replay", delegate { TestRecordedMountainShortcut(mountainShortcutRecording); });
                string mapTeleportRecording = Environment.GetEnvironmentVariable("TIANSHU_MAP_TELEPORT_SAMPLE");
                if (!string.IsNullOrWhiteSpace(mapTeleportRecording))
                    Run("Recorded map-teleport SQLite replay", delegate { TestRecordedMapTeleport(mapTeleportRecording); });
                Run("DPAPI login credential store", delegate { TestLoginCredentialStore(root); });
                Run("Multi-account process isolation", delegate { TestMultiAccountIsolation(root); });
                Run("SQLite and PCAPNG", delegate { TestStoreAndPcapng(root); });
                Run("SQLite v1 to v2 migration", delegate { TestOperationSchemaMigration(root); });
                Run("Persistent operation template catalog", delegate { TestPersistentOperationTemplates(root); });
                Run("Atomic operation recording and analysis bundle", delegate { TestAtomicOperationWorkflow(root); });
                Run("EasyHook loopback replacement", delegate { TestLoopbackCapture(root); });
                Run("Realtime FFmpeg BGM proxy and cache", delegate { TestAudioFilterProxy(root); });
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }

            Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : failures + " TEST(S) FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("PASS  " + name);
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("FAIL  " + name + ": " + ex);
            }
        }

        private static void TestHexCodec()
        {
            byte[] bytes = HexCodec.Parse("00 aa-FF");
            AssertEqual(3, bytes.Length, "hex length");
            AssertEqual("00 AA FF", HexCodec.Format(bytes), "hex format");
            AssertEqual("...", HexCodec.FormatAscii(bytes), "ascii format");
        }

        private static void TestFrameDecoder()
        {
            ProtocolDefinition definition = CreateLengthPrefixDefinition();
            GenericFrameDecoder decoder = new GenericFrameDecoder(definition);
            FrameStream stream = new FrameStream();
            stream.Append(new byte[] { 0, 6, 0, 0xC8 });
            ProtocolFrame frame;
            Assert(!stream.TryRead(decoder, TrafficDirection.ServerToClient, out frame), "partial frame must wait");
            stream.Append(new byte[] { 0xAA, 0xBB, 0, 4, 0, 1 });
            Assert(stream.TryRead(decoder, TrafficDirection.ServerToClient, out frame), "first frame");
            AssertEqual(200L, frame.Opcode.Value, "first opcode");
            AssertEqual(6, frame.Bytes.Length, "first length");
            Assert(stream.TryRead(decoder, TrafficDirection.ServerToClient, out frame), "second frame");
            AssertEqual(1L, frame.Opcode.Value, "second opcode");
        }

        private static void TestBountyProtocol()
        {
            BountyTravelTarget enemy = new BountyTravelTarget { NpcId = 90005, MapId = 15, X = 12, Y = 57 };
            AssertEqual("00 1E 00 B5 00 00 00 FE 00 05 39 30 30 30 35 00 02 31 35 00 05 31 32 3D 35 37 00 00 00 A1",
                HexCodec.Format(TianshuBountyProtocol.BuildTravelRequest(enemy, 0xA1)), "captured enemy travel request");
            AssertEqual("00 0C 00 16 00 01 5F 95 00 00 00 A5",
                HexCodec.Format(TianshuBountyProtocol.BuildNpcOpen(90005, 0xA5)), "captured enemy open request");
            AssertEqual("00 11 00 17 00 00 1F 4B 00 03 33 37 34 00 00 00 9F",
                HexCodec.Format(TianshuBountyProtocol.BuildNpcFunction(8011, "374", 0x9F)), "captured bounty function request");
            AssertEqual("00 26 00 4C 00 00 1F 4B 00 12 30 2E 39 39 34 34 38 30 37 36 37 35 36 37 32 35 34 36 00 02 6F 6B 00 00 00 00 00 A0",
                HexCodec.Format(TianshuBountyProtocol.BuildDialogResponse(8011, "0.9944807675672546", true, 0xA0)),
                "captured dynamic task confirmation");
            AssertEqual("00 08 00 1F 00 00 00 A6",
                HexCodec.Format(TianshuBountyProtocol.BuildBattleAdvance(0xA6)), "captured battle advance");

            string html = "第10次，一群暴徒出现在<a href='event:x:12,y:57,m:15,n:90005'><font color='#126D00'>黄金港口</font></a>，" +
                "返回<a href='event:x:30,y:100,m:13,n:8011'>舞修罗</a>";
            byte[] taskFrame = BuildServerStringFrame(TianshuBountyProtocol.ServerTaskUpdate, html);
            BountyTravelTarget parsedEnemy;
            BountyTravelTarget parsedGiver;
            Assert(TianshuBountyProtocol.TryParseTaskUpdate(taskFrame, out parsedEnemy, out parsedGiver), "direct HTML task link");
            AssertEqual(90005, parsedEnemy.NpcId, "dynamic enemy npc");
            AssertEqual(15, parsedEnemy.MapId, "dynamic enemy map");
            AssertEqual(8011, parsedGiver.NpcId, "dynamic giver link");
            int taskNumber;
            Assert(TianshuBountyProtocol.TryParseTaskNumber(taskFrame, out taskNumber), "task number parser");
            AssertEqual(10, taskNumber, "tenth task detection");

            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
            byte[] base64Frame = BuildServerStringFrame(TianshuBountyProtocol.ServerTaskUpdate, base64);
            Assert(TianshuBountyProtocol.TryParseTaskUpdate(base64Frame, out parsedEnemy, out parsedGiver), "base64 HTML task link");
            AssertEqual(57, parsedEnemy.Y, "base64 enemy coordinate");

            byte[] rewardFrame = BuildServerStringFrame(TianshuBountyProtocol.ServerSystemMessage, "任务奖励：技能经验1236");
            Assert(TianshuBountyProtocol.ContainsText(rewardFrame, "任务奖励"), "round reward detection");

            byte[] confirm = BuildConfirmationFrame(0, "是否传送", "会员用户单人传送", "0.13218770596945928");
            BountyConfirmationDialog dialog;
            Assert(TianshuBountyProtocol.TryParseConfirmationDialog(confirm, out dialog), "dynamic confirmation parser");
            AssertEqual("0.13218770596945928", dialog.Token, "dynamic token");
            AssertEqual(0U, dialog.ContextId, "teleport context");

            byte[] npcDialog = BuildNpcDialogFrame();
            int npcId;
            string functionId;
            Assert(TianshuBountyProtocol.TryParseNpcFunction(npcDialog, "除暴安良", out npcId, out functionId),
                "npc function parser");
            AssertEqual(8011, npcId, "giver npc id");
            AssertEqual("374", functionId, "bounty function id");
        }

        private static byte[] BuildServerStringFrame(int opcode, string value)
        {
            byte[] text = Encoding.UTF8.GetBytes(value);
            byte[] frame = new byte[6 + text.Length];
            frame[0] = (byte)(frame.Length >> 8);
            frame[1] = (byte)frame.Length;
            frame[2] = (byte)(opcode >> 8);
            frame[3] = (byte)opcode;
            frame[4] = (byte)(text.Length >> 8);
            frame[5] = (byte)text.Length;
            Buffer.BlockCopy(text, 0, frame, 6, text.Length);
            return frame;
        }

        private static void TestDonationProtocol()
        {
            byte[] request = TianshuDonationProtocol.BuildDonateItem(0x12, 0x112);
            AssertEqual("00 12 00 1D 00 01 00 12 00 99 00 00 00 00 00 00 01 12",
                HexCodec.Format(request), "captured donation request");
            ushort parsedSlot;
            uint parsedSequence;
            Assert(TianshuDonationProtocol.TryParseDonationRequest(request, out parsedSlot, out parsedSequence),
                "donation request parser");
            AssertEqual((ushort)0x12, parsedSlot, "dynamic source slot");
            AssertEqual(0x112U, parsedSequence, "dynamic donation sequence");

            byte[] confirm = TianshuDonationProtocol.BuildDonationConfirm(0x30);
            AssertEqual("00 18 00 2E 00 00 03 B9 00 00 00 01 00 00 00 00 00 00 00 00 00 00 00 30",
                HexCodec.Format(confirm), "captured donation button request");
            Assert(TianshuDonationProtocol.TryParseDonationConfirm(confirm, out parsedSequence),
                "donation button request parser");
            AssertEqual(0x30U, parsedSequence, "dynamic donation button sequence");

            byte[] itemUpdate = BuildInventoryUpdateFrame(1, 0x12, 42, "上古神器碎片(一等)", "115000140");
            DonationInventoryItem item;
            Assert(TianshuDonationProtocol.TryParseInventoryUpdate(itemUpdate, out item), "inventory update parser");
            Assert(item.IsTargetFragment, "target item identified by name/id");
            AssertEqual((ushort)42, item.Count, "target stack count");
            AssertEqual((ushort)0x12, item.Slot, "target stack slot");

            IList<DonationInventoryItem> snapshotItems;
            Assert(TianshuDonationProtocol.TryParseInventorySnapshot(BuildInventorySnapshotFrame(1, 13, 730), out snapshotItems),
                "zlib initial inventory snapshot parser");
            AssertEqual(1, snapshotItems.Count, "target item count in initial snapshot");
            AssertEqual((ushort)13, snapshotItems[0].Slot, "initial snapshot source slot");
            AssertEqual((ushort)730, snapshotItems[0].Count, "initial snapshot stack count");

            byte[] removal = new byte[] { 0, 8, 0, 0x25, 0, 1, 0, 0x12 };
            ushort bag;
            ushort removedSlot;
            Assert(TianshuDonationProtocol.TryParseInventoryRemoval(removal, out bag, out removedSlot), "inventory removal parser");
            AssertEqual((ushort)1, bag, "removed source bag");
            AssertEqual((ushort)0x12, removedSlot, "removed source slot");

            ushort panelState;
            Assert(TianshuDonationProtocol.TryParsePanelReady(new byte[] { 0, 6, 3, 0xC6, 0, 3 }, out panelState),
                "donation panel parser");
            AssertEqual((ushort)3, panelState, "donation panel ready state");

            byte[] dialog = BuildNpcDialogFrame(TianshuDonationProtocol.DefaultDonationNpcId,
                "帮会捐献官", "帮派捐献", "9991523");
            int npcId;
            string functionId;
            Assert(TianshuBountyProtocol.TryParseNpcFunction(dialog, TianshuDonationProtocol.DonationFunctionLabel,
                out npcId, out functionId), "dynamic donation function parser");
            AssertEqual(TianshuDonationProtocol.DefaultDonationNpcId, npcId, "donation npc id");
            AssertEqual("9991523", functionId, "dynamic donation function id");

            Assert(TianshuDonationProtocol.IsDonationSuccess(BuildServerStringFrame(
                TianshuBountyProtocol.ServerSystemMessage,
                "感谢你为帮派作出的贡献！帮派贡献增加650点，奖励你3250点元魄值!")), "donation success message");
            Assert(TianshuDonationProtocol.IsDonationLimit(BuildServerStringFrame(
                TianshuBountyProtocol.ServerSystemMessage, "今日捐献次数已达到上限")), "donation limit message");
            Assert(TianshuDonationProtocol.IsDonationContainerEmpty(BuildServerStringFrame(
                TianshuBountyProtocol.ServerSystemMessage, "你还没有放入物品呢！")), "empty donation container message");
        }

        private sealed class RunLoopTestTransport : ITransportCaptureEngine
        {
            public readonly BlockingCollection<byte[]> Sent = new BlockingCollection<byte[]>();
            public bool IsRunning { get; private set; }
            public void Start() { IsRunning = true; }
            public void Stop() { IsRunning = false; }
            public bool Send(long connectionId, byte[] bytes) { Sent.Add(bytes); return true; }
            public bool InjectReceive(long connectionId, byte[] bytes) { return false; }
            public void Dispose() { Stop(); Sent.Dispose(); }

            public byte[] Take(int opcode)
            {
                byte[] bytes;
                Assert(Sent.TryTake(out bytes, 5000), "automation must send opcode " + opcode.ToString("X4"));
                Assert(TianshuBountyProtocol.HasOpcode(bytes, opcode), "unexpected automation packet: " + HexCodec.Format(bytes));
                return bytes;
            }
        }

        private static void FeedRunLoopFrame(ProtocolWorkbenchService service, byte[] bytes, TrafficDirection direction)
        {
            service.RecordChunk(new TransportChunk
            {
                ConnectionId = 1, TimestampUtc = DateTime.UtcNow, Direction = direction,
                Operation = direction == TrafficDirection.ServerToClient ? TransportOperation.Receive : TransportOperation.Send,
                OriginalBytes = bytes, EffectiveBytes = bytes, NativeResult = bytes.Length
            });
        }

        private static void CompleteRunLoopNpcVisit(ProtocolWorkbenchService service, RunLoopTestTransport transport,
            int mapId, int npcId, string functionId)
        {
            byte[] travel = transport.Take(TianshuBountyProtocol.ClientTravelLink);
            Assert(Encoding.UTF8.GetString(travel).Contains(npcId.ToString()), "travel targets the expected NPC");
            byte[] map;
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(new byte[] { 0, 0, 0, 0x55 }, 0, 4);
                WriteUInt32BigEndian(stream, (uint)mapId);
                WriteUtf8String(stream, mapId == 72 ? "尚其村" : "皇城内");
                WriteUInt32BigEndian(stream, 32);
                WriteUInt32BigEndian(stream, 32);
                map = stream.ToArray();
                map[0] = (byte)(map.Length >> 8);
                map[1] = (byte)map.Length;
            }
            FeedRunLoopFrame(service, map, TrafficDirection.ServerToClient);
            byte[] open = transport.Take(TianshuBountyProtocol.ClientNpcOpen);
            uint sequence;
            TianshuBountyProtocol.TryReadClientSequence(open, out sequence);
            AssertEqual(HexCodec.Format(TianshuBountyProtocol.BuildNpcOpen(npcId, sequence)), HexCodec.Format(open),
                "open expected NPC");
            FeedRunLoopFrame(service, BuildNpcDialogFrame(npcId, "任务 NPC", "跑环任务", functionId), TrafficDirection.ServerToClient);
            byte[] select = transport.Take(TianshuBountyProtocol.ClientNpcFunction);
            TianshuBountyProtocol.TryReadClientSequence(select, out sequence);
            AssertEqual(HexCodec.Format(TianshuBountyProtocol.BuildNpcFunction(npcId, functionId, sequence)),
                HexCodec.Format(select), "select expected accept/turn-in function");
        }

        private static ProtocolWorkbenchService CreateRunLoopTestService(string directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "protocol.json"), JsonConvert.SerializeObject(CreateLengthPrefixDefinition()));
            File.WriteAllText(Path.Combine(directory, "rules.json"), JsonConvert.SerializeObject(new RuleSetDocument()));
            File.WriteAllText(Path.Combine(directory, "packet.lua"), "function on_frame(ctx) return nil end\n");
            string profile = Path.Combine(directory, "profile.json");
            File.WriteAllText(profile, JsonConvert.SerializeObject(new
            {
                name = "run-loop-rounds", gameUrl = "about:blank", activeMode = false,
                captureDirectory = "sessions", protocolPath = "protocol.json", rulesPath = "rules.json",
                packetScriptPath = "packet.lua", operationsPath = "operations.json",
                captureQueueCapacity = 8192, proxyPorts = new int[0], policyPorts = new int[0]
            }));
            return new ProtocolWorkbenchService(WorkbenchProfile.Load(profile, directory));
        }

        private static void TestRunLoopPatrol(string root)
        {
            byte[] cells = new byte[25];
            for (int col = 0; col < 4; col++) cells[2 * 5 + col] = 1;
            RunLoopMapGrid grid = new RunLoopMapGrid
            {
                MapId = 95, RpWidth = 64, RpHeight = 32, Cols = 5, Rows = 5, Cells = cells
            };
            RunLoopPatrolPath path;
            Assert(RunLoopPatrolPath.TryCreate(grid, 0, 32, 4, out path), "four connected walkable points found");
            AssertEqual(4, path.Points.Count, "bounded patrol point count");
            int[] expectedX = { 64, 128, 192, 128, 64, 0, 64, 128, 192, 128, 64, 0 };
            foreach (int x in expectedX)
                AssertEqual(new System.Drawing.Point(x, 32), path.Next(), "patrol reverses at endpoints without drift");
            Assert(RunLoopPatrolPath.TryCreate(grid, 0, 32, 2, out path), "configurable two-point patrol");
            for (int i = 0; i < 10; i++)
                AssertEqual(i % 2 == 0 ? 64 : 0, path.Next().X, "two-point patrol alternates");
            cells[2 * 5 + 1] = 0;
            Assert(!RunLoopPatrolPath.TryCreate(grid, 0, 32, 4, out path), "isolated anchor never jumps across blocked cells");
            Assert(!RunLoopPatrolPath.TryCreate(null, 0, 32, 4, out path), "missing map data produces no guessed path");
            cells[2 * 5 + 1] = 1;

            using (ProtocolWorkbenchService service = CreateRunLoopTestService(Path.Combine(root, "run-loop-patrol")))
            {
                RunLoopTestTransport transport = new RunLoopTestTransport();
                service.AttachCaptureEngine(transport);
                using (RunLoopAutomationCoordinator coordinator = new RunLoopAutomationCoordinator(service))
                {
                    service.RecordConnection(new ConnectionSession
                    {
                        Id = 1, Kind = ConnectionKind.Game, State = "Connected", OpenedUtc = DateTime.UtcNow,
                        RemotePort = 12345, RemoteEndPoint = "127.0.0.1:12345"
                    }, "Connected");
                    FeedRunLoopFrame(service, TianshuBountyProtocol.BuildNpcOpen(93290, 1), TrafficDirection.ClientToServer);
                    coordinator.Start(1, false);
                    CompleteRunLoopNpcVisit(service, transport, 72, 93290, TianshuRunLoopProtocol.AcceptFunctionId);
                    FeedRunLoopFrame(service, BuildRunLoopTaskFrame(1,
                        "去祭牙台地消灭5个金翅雏鸟后，到近天回廊的天空远征军斥候武诚初(20,66)处领取下一环任务。",
                        "金翅雏鸟 (0/5),"), TrafficDirection.ServerToClient);
                    transport.Take(TianshuBountyProtocol.ClientTravelLink);
                    using (MemoryStream stream = new MemoryStream())
                    {
                        stream.Write(new byte[] { 0, 0, 0, 0x55 }, 0, 4);
                        WriteUInt32BigEndian(stream, 95);
                        WriteUtf8String(stream, "祭牙台地");
                        WriteUInt32BigEndian(stream, 0);
                        WriteUInt32BigEndian(stream, 32);
                        byte[] map = stream.ToArray();
                        map[0] = (byte)(map.Length >> 8);
                        map[1] = (byte)map.Length;
                        FeedRunLoopFrame(service, map, TrafficDirection.ServerToClient);
                    }
                    byte[] unexpected;
                    Assert(!transport.Sent.TryTake(out unexpected, 350), "no movement before a verified map grid arrives");
                    FeedRunLoopFrame(service, BuildRunLoopMapDataFrame(95, 64, 32, 5, 5, cells), TrafficDirection.ServerToClient);
                    long firstTime = 0;
                    long lastTime = 0;
                    uint lastSequence = 0;
                    for (int i = 0; i < expectedX.Length; i++)
                    {
                        byte[] packet = transport.Take(TianshuRunLoopProtocol.ClientMovement);
                        long timestamp;
                        int mapId;
                        ushort x;
                        ushort y;
                        uint sequence;
                        Assert(TianshuRunLoopProtocol.TryParseMovement(packet, out timestamp, out mapId, out x, out y), "real timer emits movement packets");
                        AssertEqual(95, mapId, "movement stays on current map");
                        AssertEqual((ushort)expectedX[i], x, "sent positions follow fixed patrol");
                        AssertEqual((ushort)32, y, "patrol remains within valid corridor");
                        TianshuBountyProtocol.TryReadClientSequence(packet, out sequence);
                        if (i == 0) firstTime = timestamp;
                        else Assert(sequence > lastSequence, "live movement sequence advances");
                        lastSequence = sequence;
                        lastTime = timestamp;
                    }
                    double averageInterval = (lastTime - firstTime) / (double)(expectedX.Length - 1);
                    Assert(averageInterval >= 180 && averageInterval < 400, "actual packet cadence is approximately 250ms: " + averageInterval);
                    coordinator.Stop();
                    while (transport.Sent.TryTake(out unexpected)) { }
                    Assert(!transport.Sent.TryTake(out unexpected, 350), "stop cancels patrol packets");
                }
            }
        }

        private static void TestRunLoopRounds(string root)
        {
            using (ProtocolWorkbenchService service = CreateRunLoopTestService(Path.Combine(root, "run-loop-rounds")))
            {
                RunLoopTestTransport transport = new RunLoopTestTransport();
                service.AttachCaptureEngine(transport);
                using (RunLoopAutomationCoordinator coordinator = new RunLoopAutomationCoordinator(service))
                {
                    service.RecordConnection(new ConnectionSession
                    {
                        Id = 1, Kind = ConnectionKind.Game, State = "Connected", OpenedUtc = DateTime.UtcNow,
                        RemotePort = 12345, RemoteEndPoint = "127.0.0.1:12345"
                    }, "Connected");
                    FeedRunLoopFrame(service, TianshuBountyProtocol.BuildNpcOpen(93290, 1), TrafficDirection.ClientToServer);
                    byte[] removed = BuildServerStringFrame(TianshuRunLoopProtocol.ServerTaskRemoved, TianshuRunLoopProtocol.TaskId);
                    byte[] unrelated = BuildServerStringFrame(TianshuRunLoopProtocol.ServerTaskRemoved, "another-task");
                    const string description = "与皇城内的刘兴(64,113)对话，领取下一环任务。";

                    // Manual completion while inactive must invalidate a remembered twentieth ring too.
                    FeedRunLoopFrame(service, BuildRunLoopTaskFrame(20, description, ""), TrafficDirection.ServerToClient);
                    FeedRunLoopFrame(service, removed, TrafficDirection.ServerToClient);
                    FeedRunLoopFrame(service, BuildRunLoopTaskListFrame(20, description, "", "刘兴(64,113)"), TrafficDirection.ServerToClient);
                    Assert(coordinator.CurrentTask == null, "removed twentieth ring cannot return from task-list cache");
                    coordinator.Start(6, false);
                    for (int round = 1; round <= 6; round++)
                    {
                        CompleteRunLoopNpcVisit(service, transport, 72, 93290, TianshuRunLoopProtocol.AcceptFunctionId);
                        for (int ring = 1; ring <= 20; ring++)
                        {
                            byte[] current = BuildRunLoopTaskFrame(ring, description, "");
                            FeedRunLoopFrame(service, ring == 1 && round % 2 == 0
                                ? BuildRunLoopTaskListFrame(ring, description, "", "刘兴(64,113)") : current,
                                TrafficDirection.ServerToClient);
                            CompleteRunLoopNpcVisit(service, transport, 13, 8062, TianshuRunLoopProtocol.TurnInFunctionId);
                            FeedRunLoopFrame(service, current, TrafficDirection.ServerToClient); // captured order: stale 3E, then 3F
                            FeedRunLoopFrame(service, unrelated, TrafficDirection.ServerToClient);
                            AssertEqual((round - 1) * 20 + ring - 1, coordinator.CompletedRings,
                                "updates and other task removals do not confirm turn-in");
                            FeedRunLoopFrame(service, removed, TrafficDirection.ServerToClient);
                            FeedRunLoopFrame(service, removed, TrafficDirection.ServerToClient); // duplicate must be idempotent
                            FeedRunLoopFrame(service, current, TrafficDirection.ServerToClient);
                            FeedRunLoopFrame(service, BuildRunLoopTaskListFrame(ring, description, "", "刘兴(64,113)"), TrafficDirection.ServerToClient);
                            Assert(coordinator.CurrentTask == null, "late task refresh cannot resurrect submitted ring");
                            AssertEqual((round - 1) * 20 + ring, coordinator.CompletedRings, "confirmed ring count");
                            AssertEqual(ring == 20 ? round : round - 1, coordinator.CompletedRounds, "round counted at twentieth removal");
                        }
                        Console.WriteLine("      Simulated run-loop round " + round + "/6 completed");
                    }
                    AssertEqual(RunLoopAutomationState.Completed, coordinator.State, "120 rings terminate without requiring round seven");
                    AssertEqual(120, coordinator.CompletedRings, "all 120 confirmed turn-ins counted");
                    AssertEqual(0, transport.Sent.Count, "no seventh accept or stale twentieth turn-in sent");
                    Assert(!service.ActiveMode, "completion restores ACTIVE mode");
                }
            }
        }

        private static void TestRunLoopProtocol()
        {
            Assert(TianshuRunLoopProtocol.IsRunLoopTaskRemoved(HexCodec.Parse("00 0F 00 3F 00 09 31 34 30 30 30 30 30 30 30")),
                "captured twentieth-ring removal parser");
            Assert(!TianshuRunLoopProtocol.IsRunLoopTaskRemoved(BuildServerStringFrame(0x3F, "140000001")),
                "other task IDs do not remove run-loop cache");
            Assert(!TianshuRunLoopProtocol.IsRunLoopTaskRemoved(HexCodec.Parse("00 06 00 3F 00 09")),
                "truncated task removal rejected");
            byte[] movement = HexCodec.Parse("00 18 00 C1 00 00 01 A0 5C 98 8C 08 00 00 00 6F 03 C0 04 20 00 00 00 B7");
            long timestamp;
            int mapId;
            ushort x;
            ushort y;
            Assert(TianshuRunLoopProtocol.TryParseMovement(movement, out timestamp, out mapId, out x, out y),
                "captured movement parser");
            AssertEqual(111, mapId, "movement map id");
            AssertEqual((ushort)0x03C0, x, "movement scaled x");
            AssertEqual((ushort)0x0420, y, "movement scaled y");
            AssertEqual(HexCodec.Format(movement),
                HexCodec.Format(TianshuRunLoopProtocol.BuildMovement(timestamp, mapId, x, y, 0xB7)),
                "captured movement reconstructed with live sequence");

            byte[] recovery = HexCodec.Parse("00 18 00 2E 00 00 03 21 00 00 00 01 00 00 00 03 00 00 00 00 00 00 00 B8");
            Assert(TianshuRunLoopProtocol.TryParseOneKeyRecovery(recovery), "captured one-key recovery parser");
            AssertEqual(HexCodec.Format(recovery), HexCodec.Format(TianshuRunLoopProtocol.BuildOneKeyRecovery(0xB8)),
                "captured one-key recovery reconstructed");

            byte[] deliveryFrame = BuildRunLoopTaskFrame(19,
                "将玉雕送给到逐浪广场的海蚌族 蚌岳珊(2,16)，并领取下一环任务。", "玉雕 (0/1),");
            RunLoopTask delivery;
            Assert(TianshuRunLoopProtocol.TryParseTask(deliveryFrame, out delivery), "delivery task parser");
            AssertEqual(19, delivery.RingNumber, "delivery ring");
            AssertEqual(RunLoopTaskKind.DeliverItem, delivery.Kind, "delivery kind");
            AssertEqual("逐浪广场", delivery.TurnInMapName, "delivery map");
            AssertEqual("海蚌族蚌岳珊", delivery.TurnInNpcName, "delivery npc normalization");
            AssertEqual(2, delivery.TurnInX, "delivery x");

            byte[] huntFrame = BuildRunLoopTaskFrame(1,
                "去百鸟树林消灭5个金翅雏鸟后，到近天回廊的天空远征军斥候武诚初(20,66)处领取下一环任务。",
                "金翅雏鸟 (5/5),");
            RunLoopTask hunt;
            Assert(TianshuRunLoopProtocol.TryParseTask(huntFrame, out hunt), "hunt task parser");
            AssertEqual(RunLoopTaskKind.Hunt, hunt.Kind, "hunt kind");
            AssertEqual("百鸟树林", hunt.HuntMapName, "hunt map");
            AssertEqual(5, hunt.Progress, "hunt progress");
            Assert(hunt.IsComplete, "hunt completion");

            string encodedHtml = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                "<p>将玉雕送给到逐浪广场的<a href=\"event:x:2,y:16,m:102,n:94018\">海蚌族 蚌岳珊</a>(2,16)，并领取下一环任务。</p>"));
            byte[] encodedDeliveryFrame = BuildRunLoopTaskFrame(19, encodedHtml, "玉雕 (0/1),");
            RunLoopTask encodedDelivery;
            Assert(TianshuRunLoopProtocol.TryParseTask(encodedDeliveryFrame, out encodedDelivery),
                "base64 HTML delivery task parser");
            AssertEqual(94018, encodedDelivery.TurnInNpcId, "task-link npc id from base64 HTML");
            AssertEqual(102, encodedDelivery.TurnInMapId, "task-link map id from base64 HTML");
            AssertEqual("海蚌族蚌岳珊", encodedDelivery.TurnInNpcName, "HTML task npc normalization");

            byte[] talkFrame = BuildRunLoopTaskFrame(2,
                "与十字路口的冯奇(17,73)对话，领取下一环任务。", string.Empty);
            RunLoopTask talk;
            Assert(TianshuRunLoopProtocol.TryParseTask(talkFrame, out talk), "recorded talk-to-npc task parser");
            AssertEqual(RunLoopTaskKind.TalkToNpc, talk.Kind, "recorded talk-to-npc kind");
            AssertEqual("十字路口", talk.TurnInMapName, "recorded talk-to-npc map");
            AssertEqual("冯奇", talk.TurnInNpcName, "recorded talk-to-npc name");

            byte[] demonHuntFrame = BuildRunLoopTaskFrame(4,
                "去祭牙台地消灭5个铁甲蜥蜴（精英）后，到灵昌城的周猎户(31,130)处领取下一环任务。",
                "铁甲蜥蜴（精英） (0/5),");
            RunLoopTask demonHunt;
            Assert(TianshuRunLoopProtocol.TryParseTask(demonHuntFrame, out demonHunt), "recorded demon hunt parser");
            AssertEqual(RunLoopTaskKind.Hunt, demonHunt.Kind, "recorded demon hunt kind");
            AssertEqual("祭牙台地", demonHunt.HuntMapName, "recorded demon hunt map");
            AssertEqual("周猎户", demonHunt.TurnInNpcName, "recorded demon hunt turn-in npc");
            AssertEqual(31, demonHunt.TurnInX, "recorded demon hunt turn-in x");
            AssertEqual(130, demonHunt.TurnInY, "recorded demon hunt turn-in y");
            AssertEqual(0, demonHunt.Progress, "recorded demon hunt initial progress");
            Assert(!demonHunt.IsComplete, "recorded demon hunt initially incomplete");

            byte[] mapFrame;
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(new byte[] { 0, 0, 0, (byte)TianshuRunLoopProtocol.ServerMapInfo }, 0, 4);
                WriteUInt32BigEndian(stream, 100);
                WriteUtf8String(stream, "怒焰祭坛");
                WriteUInt32BigEndian(stream, 0x1E0);
                WriteUInt32BigEndian(stream, 0x70);
                mapFrame = stream.ToArray();
                mapFrame[0] = (byte)(mapFrame.Length >> 8);
                mapFrame[1] = (byte)mapFrame.Length;
            }
            RunLoopMapInfo map;
            Assert(TianshuRunLoopProtocol.TryParseMapInfo(mapFrame, out map), "map info parser");
            AssertEqual(100, map.MapId, "map info id");
            AssertEqual((ushort)0x1E0, map.ScaledX, "map info spawn x");

            byte[] taskListFrame = BuildRunLoopTaskListFrame(3, "与新月村的吕仁(17,61)对话，领取下一环任务。", string.Empty, "吕仁(17,61)");
            RunLoopTask taskFromList;
            Assert(TianshuRunLoopProtocol.TryParseTaskList(taskListFrame, out taskFromList), "task-list parser");
            AssertEqual(3, taskFromList.RingNumber, "task-list ring");
            AssertEqual(RunLoopTaskKind.TalkToNpc, taskFromList.Kind, "task-list kind");
            AssertEqual("新月村", taskFromList.TurnInMapName, "task-list map");
            AssertEqual("吕仁", taskFromList.TurnInNpcName, "task-list npc");
            AssertEqual(0, taskFromList.TurnInNpcId, "task-list npc id not polluted by foreign travel link");
            AssertEqual(0, taskFromList.TurnInMapId, "task-list map id not polluted by foreign travel link");

            byte[] huntListFrame = BuildRunLoopTaskListFrame(8,
                "去百鸟树林消灭金翅雏鸟，得到5个冰晶后，到近天回廊的先锋护卫姜天(13,71)处领取下一环任务。",
                "冰晶 (5/5), ", "先锋护卫姜天(13,71)");
            RunLoopTask huntFromList;
            Assert(TianshuRunLoopProtocol.TryParseTaskList(huntListFrame, out huntFromList), "hunt task-list parser");
            AssertEqual(8, huntFromList.RingNumber, "hunt task-list ring");
            AssertEqual(RunLoopTaskKind.Hunt, huntFromList.Kind, "hunt task-list kind");
            AssertEqual("百鸟树林", huntFromList.HuntMapName, "hunt task-list map");
            AssertEqual("冰晶", huntFromList.ObjectiveName, "hunt task-list objective");
            AssertEqual(5, huntFromList.Progress, "hunt task-list progress not polluted");
            AssertEqual(5, huntFromList.Required, "hunt task-list required not polluted");

            byte[] mapCells = new byte[]
            {
                0, 1, 0,
                1, 0, 1,
                0, 1, 0
            };
            byte[] mapDataFrame = BuildRunLoopMapDataFrame(3, 64, 32, 3, 3, mapCells);
            RunLoopMapGrid grid;
            Assert(TianshuRunLoopProtocol.TryParseMapData(mapDataFrame, out grid), "map-data parser");
            AssertEqual(3, grid.MapId, "map-data map id");
            AssertEqual(64, grid.RpWidth, "map-data rp width");
            AssertEqual(32, grid.RpHeight, "map-data rp height");
            AssertEqual(3, grid.Rows, "map-data rows");
            AssertEqual(3, grid.Cols, "map-data cols");
            Assert(!grid.IsWalkable(0, 0), "blocked cell is not walkable");
            Assert(grid.IsWalkable(1, 0), "open cell is walkable");
            ushort centerX;
            ushort centerY;
            Assert(grid.TryGetScaledCenter(1, 0, out centerX, out centerY), "rp-to-scaled conversion");
            AssertEqual((ushort)64, centerX, "rp center x");
            AssertEqual((ushort)0, centerY, "rp center y");
            int rpCol;
            int rpRow;
            Assert(grid.TryGetRp(centerX, centerY, out rpCol, out rpRow), "scaled-to-rp conversion");
            AssertEqual(1, rpCol, "rp col");
            AssertEqual(0, rpRow, "rp row");
        }

        private static void TestMountainClimbProtocol()
        {
            byte[] accept = HexCodec.Parse(
                "00 1D 00 18 00 01 6B 87 00 09 39 33 38 32 39 30 32 2E 31 00 00 00 00 00 00 00 00 00 1D");
            AssertEqual(HexCodec.Format(accept), HexCodec.Format(TianshuMountainClimbProtocol.BuildQuestAction(
                93063, TianshuMountainClimbProtocol.BraveTowerAcceptFunction, false, 0x1D)),
                "captured mountain task acceptance reconstructed");
            byte[] turnIn = HexCodec.Parse(
                "00 1D 00 18 00 00 1F 8B 00 09 39 33 38 32 39 30 32 2E 32 00 01 00 00 00 00 00 00 01 B7");
            AssertEqual(HexCodec.Format(turnIn), HexCodec.Format(TianshuMountainClimbProtocol.BuildQuestAction(
                8075, TianshuMountainClimbProtocol.BraveTowerTurnInFunction, true, 0x1B7)),
                "captured mountain task turn-in reconstructed");

            int npcId;
            string functionId;
            bool parsedTurnIn;
            uint sequence;
            Assert(TianshuMountainClimbProtocol.TryParseQuestAction(turnIn, out npcId, out functionId,
                out parsedTurnIn, out sequence), "mountain quest action parser");
            AssertEqual(8075, npcId, "mountain turn-in npc");
            AssertEqual(TianshuMountainClimbProtocol.BraveTowerTurnInFunction, functionId, "mountain turn-in function");
            Assert(parsedTurnIn, "mountain turn-in flag");
            AssertEqual(0x1B7U, sequence, "mountain turn-in sequence");

            byte[] directTeleport = HexCodec.Parse(
                "00 15 00 17 00 00 1F 76 00 07 39 39 39 32 30 34 30 00 00 00 B5");
            AssertEqual(HexCodec.Format(directTeleport), HexCodec.Format(TianshuBountyProtocol.BuildNpcFunction(
                TianshuMountainClimbProtocol.TowerTeleporterNpcId,
                TianshuMountainClimbProtocol.BraveTowerDirectTeleportFunction, 0xB5)),
                "captured direct teleport to brave-tower destination reconstructed");

            Assert(TianshuMountainClimbProtocol.IsQuestDetail(BuildServerStringFrame(
                TianshuMountainClimbProtocol.ServerQuestDetail,
                TianshuMountainClimbProtocol.FirstTowerTurnInFunction),
                TianshuMountainClimbProtocol.FirstTowerTurnInFunction), "mountain quest detail detection");
            Assert(TianshuMountainClimbProtocol.IsTaskAccepted(BuildServerStringFrame(
                TianshuBountyProtocol.ServerTaskUpdate, TianshuMountainClimbProtocol.RainMountainTaskId),
                TianshuMountainClimbProtocol.RainMountainTaskId), "mountain task acceptance detection");
            Assert(TianshuMountainClimbProtocol.IsTaskRemoved(BuildServerStringFrame(
                TianshuMountainClimbProtocol.ServerTaskRemoved, TianshuMountainClimbProtocol.RainMountainTaskId),
                TianshuMountainClimbProtocol.RainMountainTaskId), "mountain task removal detection");
        }

        private static void TestMapTeleportProtocol()
        {
            byte[] select = HexCodec.Parse(
                "00 18 00 2E 00 00 01 3C 00 00 00 56 00 00 00 00 00 00 00 00 00 00 00 20");
            byte[] execute = HexCodec.Parse(
                "00 18 00 2E 00 00 00 AC 00 00 00 56 00 00 00 00 00 00 00 00 00 00 00 21");
            AssertEqual(HexCodec.Format(select), HexCodec.Format(TianshuMapTeleportProtocol.BuildSelectMap(86, 0x20)),
                "captured map-select action reconstructed");
            AssertEqual(HexCodec.Format(execute), HexCodec.Format(TianshuMapTeleportProtocol.BuildTeleportToMap(86, 0x21)),
                "captured map-teleport action reconstructed");

            int actionId;
            int mapId;
            uint sequence;
            Assert(TianshuMapTeleportProtocol.TryParseMapAction(select, out actionId, out mapId, out sequence),
                "map-select parser");
            AssertEqual(TianshuMapTeleportProtocol.SelectMapAction, actionId, "map-select action id");
            AssertEqual(86, mapId, "map-select map id");
            AssertEqual(0x20U, sequence, "map-select live sequence");
            Assert(TianshuMapTeleportProtocol.TryParseMapAction(execute, out actionId, out mapId, out sequence),
                "map-teleport parser");
            AssertEqual(TianshuMapTeleportProtocol.ExecuteTeleportAction, actionId, "map-teleport action id");
            AssertEqual(86, mapId, "map-teleport map id");
            AssertEqual(0x21U, sequence, "map-teleport live sequence");

            byte[] activateFlight = HexCodec.Parse(
                "00 18 00 2E 00 00 03 24 00 00 01 22 00 00 00 00 00 00 00 00 00 00 00 07");
            byte[] fly = HexCodec.Parse(
                "00 18 00 2E 00 00 00 A9 00 00 00 00 00 00 00 15 00 00 00 23 00 00 00 09");
            AssertEqual(HexCodec.Format(activateFlight),
                HexCodec.Format(TianshuMapTeleportProtocol.BuildActivateFlight(7)),
                "captured flight-list action reconstructed");
            AssertEqual(HexCodec.Format(fly),
                HexCodec.Format(TianshuMapTeleportProtocol.BuildFlyToCoordinate(21, 35, 9)),
                "captured coordinate-flight action reconstructed");
            int flightX;
            int flightY;
            Assert(TianshuMapTeleportProtocol.TryParseFlightAction(fly, out actionId, out flightX, out flightY,
                out sequence), "coordinate-flight parser");
            AssertEqual(TianshuMapTeleportProtocol.FlyToCoordinateAction, actionId, "coordinate-flight action id");
            AssertEqual(21, flightX, "coordinate-flight x");
            AssertEqual(35, flightY, "coordinate-flight y");
            AssertEqual(9U, sequence, "coordinate-flight sequence");

            AssertEqual(43, TianshuMapTeleportCatalog.All.Count, "recorded destination count");
            AssertEqual(43, TianshuMapTeleportCatalog.All.Select(item => item.MapId).Distinct().Count(),
                "destination ids are unique");
            AssertEqual(43, TianshuMapTeleportCatalog.All.Select(item => item.MapName).Distinct().Count(),
                "destination names are unique");
            MapTeleportDestination destination;
            Assert(TianshuMapTeleportCatalog.TryGet(86, out destination) && destination.MapName == "三界关",
                "destination lookup by id");
            AssertEqual(575, destination.TotemNpcId, "recorded Sanjie Pass totem npc id");
            AssertEqual((ushort)33, destination.TotemX, "recorded Sanjie Pass totem x");
            AssertEqual((ushort)65, destination.TotemY, "recorded Sanjie Pass totem y");
            Assert(TianshuMapTeleportCatalog.TryGet("灵昌城", out destination) && destination.MapId == 12,
                "destination lookup by name");
            Assert(TianshuMapTeleportCatalog.TryGet(" 102 ", out destination) && destination.MapName == "逐浪广场",
                "destination lookup by numeric text");
            Assert(TianshuMapTeleportCatalog.TryGet(3, out destination) && destination.MapName == "新月村",
                "destination lookup for Xinyue Village");
            AssertEqual(542, destination.TotemNpcId, "recorded Xinyue Village totem npc id");
            AssertEqual((ushort)36, destination.TotemX, "recorded Xinyue Village totem x");
            AssertEqual((ushort)22, destination.TotemY, "recorded Xinyue Village totem y");
            AssertEqual((ushort)2240, destination.SpawnX, "recorded Xinyue Village spawn x");
            AssertEqual((ushort)384, destination.SpawnY, "recorded Xinyue Village spawn y");
            Assert(!TianshuMapTeleportCatalog.TryGet(999999, out destination),
                "unrecorded destination is rejected");
            AssertThrows(delegate { TianshuMapTeleportProtocol.BuildMapAction(999, 86, 1); },
                "unrecorded UI action is rejected");
            AssertThrows(delegate { TianshuMapTeleportProtocol.BuildSelectMap(999999, 1); },
                "unrecorded map id is rejected by packet builder");
            AssertThrows(delegate { TianshuMapTeleportProtocol.BuildFlyToCoordinate(-1, 1, 1); },
                "negative flight coordinate is rejected");

            BountyTravelTarget totem = TianshuMapTeleportCatalog.GetTotemTravelTarget(69);
            AssertEqual(558, totem.NpcId, "recorded Zhaofeng Terrace totem npc id");
            AssertEqual("00 1B 00 B5 00 00 00 FE 00 03 35 35 38 00 02 36 39 00 04 35 3D 32 34 00 00 00 05",
                HexCodec.Format(TianshuBountyProtocol.BuildTravelRequest(totem, 5)),
                "legacy task-link packet to recorded totem");
        }

        private static void TestRecordedMapTeleport(string path)
        {
            Assert(File.Exists(path), "recorded map-teleport database exists");
            int selectedMapId = 0;
            int pendingMapId = 0;
            int pairedRequests = 0;
            int confirmedArrivals = 0;
            int periodicFrames = 0;
            HashSet<int> requestedMaps = new HashSet<int>();
            HashSet<int> arrivedMaps = new HashSet<int>();
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + path + ";Version=3;Read Only=True;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT f.direction,f.opcode,f.bytes FROM frames f " +
                        "INNER JOIN operation_frames ofr ON ofr.frame_id=f.id " +
                        "WHERE ofr.run_id=(SELECT id FROM operation_runs ORDER BY started_utc DESC LIMIT 1) " +
                        "ORDER BY f.capture_ordinal;";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int direction = Convert.ToInt32(reader[0]);
                            int opcode = Convert.ToInt32(reader[1]);
                            byte[] bytes = (byte[])reader[2];
                            if (direction == (int)TrafficDirection.ClientToServer && opcode == 0x0042)
                            {
                                periodicFrames++;
                                continue;
                            }
                            if (direction == (int)TrafficDirection.ClientToServer &&
                                opcode == TianshuMapTeleportProtocol.ClientUiAction)
                            {
                                int actionId;
                                int mapId;
                                uint sequence;
                                if (!TianshuMapTeleportProtocol.TryParseMapAction(bytes, out actionId, out mapId, out sequence))
                                    continue;
                                Assert(TianshuMapTeleportCatalog.All.Any(item => item.MapId == mapId),
                                    "recording request map exists in catalog: " + mapId);
                                if (actionId == TianshuMapTeleportProtocol.SelectMapAction)
                                {
                                    selectedMapId = mapId;
                                    AssertEqual(HexCodec.Format(bytes),
                                        HexCodec.Format(TianshuMapTeleportProtocol.BuildSelectMap(mapId, sequence)),
                                        "recorded select action reconstructed for map " + mapId);
                                }
                                else
                                {
                                    AssertEqual(selectedMapId, mapId,
                                        "execute action follows select action for the same map");
                                    AssertEqual(HexCodec.Format(bytes),
                                        HexCodec.Format(TianshuMapTeleportProtocol.BuildTeleportToMap(mapId, sequence)),
                                        "recorded execute action reconstructed for map " + mapId);
                                    pairedRequests++;
                                    pendingMapId = mapId;
                                    requestedMaps.Add(mapId);
                                    selectedMapId = 0;
                                }
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient &&
                                opcode == TianshuMapTeleportProtocol.ServerMapInfo)
                            {
                                RunLoopMapInfo map;
                                if (!TianshuRunLoopProtocol.TryParseMapInfo(bytes, out map) || map.MapId != pendingMapId)
                                    continue;
                                MapTeleportDestination destination;
                                Assert(TianshuMapTeleportCatalog.TryGet(map.MapId, out destination),
                                    "arrival map exists in catalog: " + map.MapId);
                                AssertEqual(destination.MapName, map.MapName,
                                    "arrival map name matches catalog for map " + map.MapId);
                                AssertEqual(destination.SpawnX, map.ScaledX,
                                    "arrival X matches recording catalog for map " + map.MapId);
                                AssertEqual(destination.SpawnY, map.ScaledY,
                                    "arrival Y matches recording catalog for map " + map.MapId);
                                confirmedArrivals++;
                                arrivedMaps.Add(map.MapId);
                                pendingMapId = 0;
                            }
                        }
                    }
                }
            }
            AssertEqual(42, pairedRequests, "all 42 recorded select/execute request pairs parsed");
            AssertEqual(42, confirmedArrivals, "all 42 requests have matching SC_MAP_INFO arrivals");
            AssertEqual(42, requestedMaps.Count, "all recorded requests target distinct catalog maps");
            AssertEqual(42, arrivedMaps.Count, "all catalog maps have a confirmed arrival");
            Assert(periodicFrames > 0, "recording contains unrelated periodic 0x0042 frames that are excluded");
            AssertEqual(0, selectedMapId, "no incomplete select request remains");
            AssertEqual(0, pendingMapId, "no unconfirmed teleport request remains");
        }

        private static void TestClientLogging(string root)
        {
            ClientLogHub concurrentHub = new ClientLogHub(10000);
            int publishedEvents = 0;
            int clearedEvents = 0;
            concurrentHub.EntryPublished += delegate { Interlocked.Increment(ref publishedEvents); };
            concurrentHub.EntriesCleared += delegate { Interlocked.Increment(ref clearedEvents); };
            Thread[] writers = new Thread[4];
            for (int writerIndex = 0; writerIndex < writers.Length; writerIndex++)
            {
                int capturedWriter = writerIndex;
                writers[writerIndex] = new Thread(new ThreadStart(delegate
                {
                    for (int i = 0; i < 3000; i++)
                        concurrentHub.Publish("模块" + capturedWriter, ClientLogLevel.Info, "Running", "消息 " + i);
                }));
                writers[writerIndex].Start();
            }
            for (int i = 0; i < writers.Length; i++) writers[i].Join();

            IList<ClientLogEntry> concurrentSnapshot = concurrentHub.Snapshot();
            AssertEqual(10000, concurrentHub.Count, "unified log enforces configured capacity");
            AssertEqual(10000, concurrentSnapshot.Count, "unified log snapshot is bounded");
            AssertEqual(12000, publishedEvents, "all concurrent publications raise notifications");
            AssertEqual(2001L, concurrentSnapshot[0].Sequence, "oldest entries are evicted first");
            AssertEqual(12000L, concurrentSnapshot[concurrentSnapshot.Count - 1].Sequence, "latest entry is retained");
            for (int i = 1; i < concurrentSnapshot.Count; i++)
                Assert(concurrentSnapshot[i - 1].Sequence < concurrentSnapshot[i].Sequence,
                    "concurrent log snapshot remains sequence ordered");
            Assert(concurrentSnapshot.All(item => item.TimestampUtc.Kind == DateTimeKind.Utc),
                "unified log stores UTC timestamps");
            concurrentHub.Clear();
            AssertEqual(0, concurrentHub.Count, "manual clear removes in-memory unified logs");
            AssertEqual(1, clearedEvents, "manual clear raises one notification");

            ClientLogHub isolatedHub = new ClientLogHub(2);
            isolatedHub.EntryPublished += delegate { throw new InvalidOperationException("listener failure"); };
            isolatedHub.Publish("测试", ClientLogLevel.Info, "Ready", "日志订阅者异常不得影响业务线程");
            AssertEqual(1, isolatedHub.Count, "failing log listener is isolated from publishers");

            ClientLogHub uiHub = new ClientLogHub();
            using (ClientLogControl logControl = new ClientLogControl(uiHub))
            {
                logControl.Size = new System.Drawing.Size(850, 220);
                logControl.CreateControl();
                uiHub.Publish("自动除暴", ClientLogLevel.Info, "Running", "准备访问目标 NPC");
                uiHub.Publish("地图传送", ClientLogLevel.Warning, "Stopped", "用户停止传送");
                uiHub.Publish("自动登录", ClientLogLevel.Error, "Failed", "登录阶段失败，未记录密码");
                logControl.RefreshEntries();

                DataGridView grid = (DataGridView)typeof(ClientLogControl).GetField(
                    "logGrid", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(logControl);
                ToolStripComboBox moduleFilter = (ToolStripComboBox)typeof(ClientLogControl).GetField(
                    "moduleFilter", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(logControl);
                ToolStripComboBox levelFilter = (ToolStripComboBox)typeof(ClientLogControl).GetField(
                    "levelFilter", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(logControl);
                ToolStripTextBox keywordFilter = (ToolStripTextBox)typeof(ClientLogControl).GetField(
                    "keywordFilter", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(logControl);
                AssertEqual(3, grid.RowCount, "unified log grid renders all entries");
                Assert(grid.VirtualMode, "unified log grid uses virtual rows");
                Assert(grid.MultiSelect, "unified log grid supports multi-selection");
                Assert(grid.ContextMenuStrip.Items.Cast<ToolStripItem>().Any(item => item.Text == "复制选中日志"),
                    "unified log context menu supports selected-row copy");
                Assert(grid.ContextMenuStrip.Items.Cast<ToolStripItem>().Any(item => item.Text == "清空统一日志（仅界面）"),
                    "unified log context menu exposes memory-only clear");

                moduleFilter.SelectedItem = "地图传送";
                logControl.RefreshEntries();
                AssertEqual(1, grid.RowCount, "module filter limits visible entries");
                moduleFilter.SelectedItem = "全部模块";
                levelFilter.SelectedItem = "错误";
                logControl.RefreshEntries();
                AssertEqual(1, grid.RowCount, "level filter limits visible entries");
                levelFilter.SelectedItem = "全部级别";
                keywordFilter.Text = "NPC";
                logControl.RefreshEntries();
                AssertEqual(1, grid.RowCount, "keyword filter searches module, state and message");
                grid.Rows[0].Selected = true;
                MethodInfo buildSelectedText = typeof(ClientLogControl).GetMethod(
                    "BuildSelectedText", BindingFlags.Instance | BindingFlags.NonPublic);
                string copied = (string)buildSelectedText.Invoke(logControl, null);
                Assert(copied.Contains("自动除暴") && copied.Contains("目标 NPC"),
                    "selected log copy contains structured row data");
                Assert(!copied.Contains("password=") && !copied.Contains("ticket="),
                    "copied login-facing log does not expose credential fields");
                uiHub.Clear();
                logControl.RefreshEntries();
                AssertEqual(0, grid.RowCount, "GUI refresh reflects manual log clear");
            }

            string testRoot = Path.Combine(root, "unified-log-workbench");
            Directory.CreateDirectory(testRoot);
            string profilePath = Path.Combine(testRoot, "profile.json");
            File.WriteAllText(Path.Combine(testRoot, "protocol.json"),
                JsonConvert.SerializeObject(CreateLengthPrefixDefinition()));
            File.WriteAllText(Path.Combine(testRoot, "rules.json"),
                JsonConvert.SerializeObject(new RuleSetDocument { Rules = new List<PacketRule>() }));
            File.WriteAllText(Path.Combine(testRoot, "packet.lua"),
                "function on_frame(ctx) return nil end\nfunction main(api) return true end\n");
            File.WriteAllText(profilePath, JsonConvert.SerializeObject(new
            {
                name = "unified-log-test",
                gameUrl = "about:blank",
                activeMode = false,
                captureDirectory = "sessions",
                protocolPath = "protocol.json",
                rulesPath = "rules.json",
                packetScriptPath = "packet.lua",
                operationsPath = "operations.json",
                proxyPorts = new int[0],
                policyPorts = new int[0],
                scriptTimeoutMs = 50,
                captureQueueCapacity = 1024
            }));

            ProtocolWorkbenchService service = null;
            ProtocolWorkbenchControl workbench = null;
            BountyAutomationCoordinator bounty = null;
            DonationAutomationCoordinator donation = null;
            RunLoopAutomationCoordinator runLoop = null;
            MountainClimbAutomationCoordinator mountain = null;
            MapTeleportAutomationCoordinator mapTeleport = null;
            ClientLogHub integrationHub = new ClientLogHub();
            try
            {
                service = new ProtocolWorkbenchService(WorkbenchProfile.Load(profilePath, testRoot));
                bounty = new BountyAutomationCoordinator(service);
                donation = new DonationAutomationCoordinator(service);
                runLoop = new RunLoopAutomationCoordinator(service);
                mountain = new MountainClimbAutomationCoordinator(service);
                mapTeleport = new MapTeleportAutomationCoordinator(service);
                workbench = new ProtocolWorkbenchControl(service, null, null, bounty, donation, runLoop, mountain,
                    mapTeleport, null, integrationHub);
                workbench.Size = new System.Drawing.Size(1000, 700);
                workbench.CreateControl();
                workbench.PerformLayout();
                TabControl featureTabs = (TabControl)typeof(ProtocolWorkbenchControl).GetField(
                    "featureTabs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(workbench);
                TabControl captureTabs = (TabControl)typeof(ProtocolWorkbenchControl).GetField(
                    "captureTabs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(workbench);
                Assert(featureTabs.Multiline, "feature navigation allows multiple rows");
                AssertEqual(2, featureTabs.RowCount, "feature navigation uses two rows at the workbench test width");
                Assert(featureTabs.TabPages.Cast<TabPage>().Any(item => item.Text == "抓包"),
                    "capture feature has a top-level entry");
                Assert(!featureTabs.TabPages.Cast<TabPage>().Any(item => item.Text == "数据包" ||
                    item.Text == "连接" || item.Text == "协议/字段"),
                    "capture detail pages are not mixed with top-level features");
                AssertEqual("数据包,连接,协议/字段",
                    string.Join(",", captureTabs.TabPages.Cast<TabPage>().Select(item => item.Text).ToArray()),
                    "capture entry contains the three requested subpages");
                IList<Button> workbenchButtons = FindControls<Button>(workbench).ToList();
                Assert(workbenchButtons.Any(item => item.Text == "传送点直传"), "direct map-teleport GUI action exists");
                Assert(workbenchButtons.Any(item => item.Text == "旧链接瞬移到图腾"), "legacy totem GUI action exists");
                Assert(workbenchButtons.Any(item => item.Text == "飞行到坐标"), "coordinate-flight GUI action exists");
                Assert(FindControls<NumericUpDown>(workbench).Count() >= 2, "coordinate-flight GUI inputs exist");

                bounty.Start(1, false, false);
                bounty.Stop();
                donation.Start(TianshuDonationProtocol.DefaultDonationNpcId, 1);
                donation.Stop();
                runLoop.Start(1, true);
                runLoop.Stop();
                mountain.Start();
                mountain.Stop();
                mapTeleport.TeleportTo(TianshuMapTeleportCatalog.All[0].MapId);
                mapTeleport.Stop();

                IList<ClientLogEntry> integrationEntries = integrationHub.Snapshot();
                string[] expectedModules = { "自动除暴", "自动捐献", "自动跑环", "登山爬塔", "地图传送" };
                for (int i = 0; i < expectedModules.Length; i++)
                    Assert(integrationEntries.Any(item => item.Module == expectedModules[i]),
                        expectedModules[i] + " status is bridged to the unified log");
                Assert(integrationEntries.Any(item => item.Level == ClientLogLevel.Warning && item.State == "Stopped"),
                    "stopped automation is classified as a warning");

                MethodInfo loginStatus = typeof(ProtocolWorkbenchControl).GetMethod(
                    "OnLoginAutomationStatusChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                loginStatus.Invoke(workbench, new object[] { LoginAutomationState.Failed, "登录失败且未回显凭据。" });
                Assert(integrationHub.Snapshot().Any(item => item.Module == "自动登录" &&
                    item.Level == ClientLogLevel.Error && item.State == "Failed"),
                    "failed login status is classified as an error");
                MethodInfo audioStatus = typeof(ProtocolWorkbenchControl).GetMethod(
                    "OnAudioFilterStatusChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                audioStatus.Invoke(workbench, new object[] { "BGM 状态已更新。" });
                Assert(integrationHub.Snapshot().Any(item => item.Module == "BGM"),
                    "BGM status is bridged to the unified log");

                SplitContainer logSplit = (SplitContainer)typeof(ProtocolWorkbenchControl).GetField(
                    "logSplitContainer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(workbench);
                ToolStripButton logButton = (ToolStripButton)typeof(ProtocolWorkbenchControl).GetField(
                    "logPaneButton", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(workbench);
                Assert(!logSplit.Panel2Collapsed && logSplit.Panel2.Controls.OfType<ClientLogControl>().Any(),
                    "unified log is fixed in the lower workbench pane");
                logButton.PerformClick();
                Assert(logSplit.Panel2Collapsed, "toolbar log button collapses the fixed pane");
                logButton.PerformClick();
                Assert(!logSplit.Panel2Collapsed, "toolbar log button restores the fixed pane");

                RichTextBox auditLog = (RichTextBox)typeof(ProtocolWorkbenchControl).GetField(
                    "eventLog", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(workbench);
                service.ReportEngineEvent("TestAudit", "INFO", "audit remains independent", null);
                Assert(auditLog.Text.Contains("audit remains independent"), "audit tab remains available");
                integrationHub.Clear();
                Assert(auditLog.Text.Contains("audit remains independent"),
                    "clearing unified logs does not clear the audit tab");

                Type[] removedLogControls =
                {
                    typeof(BountyAutomationControl), typeof(DonationAutomationControl), typeof(RunLoopAutomationControl),
                    typeof(MountainClimbAutomationControl), typeof(MapTeleportAutomationControl)
                };
                foreach (Type controlType in removedLogControls)
                {
                    Control feature = FindControl(workbench, controlType);
                    Assert(feature != null, controlType.Name + " remains present");
                    Assert(!FindControls<TextBoxBase>(feature).Any(item => item.Multiline && item.ReadOnly),
                        controlType.Name + " no longer owns a duplicate multiline log box");
                }
            }
            finally
            {
                if (workbench != null) workbench.Dispose();
                if (bounty != null) bounty.Dispose();
                if (donation != null) donation.Dispose();
                if (runLoop != null) runLoop.Dispose();
                if (mountain != null) mountain.Dispose();
                if (mapTeleport != null) mapTeleport.Dispose();
                if (service != null) service.Dispose();
            }
        }

        private static Control FindControl(Control root, Type type)
        {
            if (root == null || type == null) return null;
            if (type.IsInstanceOfType(root)) return root;
            foreach (Control child in root.Controls)
            {
                Control found = FindControl(child, type);
                if (found != null) return found;
            }
            return null;
        }

        private static IEnumerable<TControl> FindControls<TControl>(Control root) where TControl : Control
        {
            if (root == null) yield break;
            TControl current = root as TControl;
            if (current != null) yield return current;
            foreach (Control child in root.Controls)
                foreach (TControl descendant in FindControls<TControl>(child))
                    yield return descendant;
        }

        private static void TestPacketGridInteractions(string root)
        {
            string testRoot = Path.Combine(root, "packet-grid");
            Directory.CreateDirectory(testRoot);
            string profilePath = Path.Combine(testRoot, "profile.json");
            File.WriteAllText(Path.Combine(testRoot, "protocol.json"),
                JsonConvert.SerializeObject(CreateLengthPrefixDefinition()));
            File.WriteAllText(Path.Combine(testRoot, "rules.json"),
                JsonConvert.SerializeObject(new RuleSetDocument { Rules = new List<PacketRule>() }));
            File.WriteAllText(Path.Combine(testRoot, "packet.lua"),
                "function on_frame(ctx) return nil end\nfunction main(api) return true end\n");
            File.WriteAllText(profilePath, JsonConvert.SerializeObject(new
            {
                name = "packet-grid-test",
                gameUrl = "about:blank",
                activeMode = false,
                captureDirectory = "sessions",
                protocolPath = "protocol.json",
                rulesPath = "rules.json",
                packetScriptPath = "packet.lua",
                operationsPath = "operations.json",
                proxyPorts = new int[0],
                policyPorts = new int[0],
                scriptTimeoutMs = 50,
                captureQueueCapacity = 1024
            }));

            string databasePath = null;
            ProtocolWorkbenchService service = null;
            ProtocolWorkbenchControl control = null;
            try
            {
                service = new ProtocolWorkbenchService(WorkbenchProfile.Load(profilePath, testRoot));
                databasePath = service.DatabasePath;
                control = new ProtocolWorkbenchControl(service);
                control.CreateControl();
                FieldInfo packetGridField = typeof(ProtocolWorkbenchControl).GetField(
                    "packetGrid", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(packetGridField != null, "packet grid field exists");
                DataGridView packetGrid = (DataGridView)packetGridField.GetValue(control);
                Assert(packetGrid.MultiSelect, "packet grid enables Ctrl and Shift multi-selection");
                AssertEqual(DataGridViewSelectionMode.FullRowSelect, packetGrid.SelectionMode,
                    "packet grid selects complete rows");
                Assert(packetGrid.ContextMenuStrip != null, "packet grid has a context menu");
                string[] menuNames = packetGrid.ContextMenuStrip.Items.Cast<ToolStripItem>()
                    .Where(item => !(item is ToolStripSeparator)).Select(item => item.Text).ToArray();
                Assert(menuNames.Contains("复制选中行"), "packet menu restores selected-row copy");
                Assert(menuNames.Contains("复制有效数据 Hex"), "packet menu copies effective hex");
                Assert(menuNames.Contains("复制原始数据 Hex"), "packet menu copies original hex");
                Assert(menuNames.Contains("清空选中数据包（仅界面）"), "packet menu clears selected rows");
                Assert(menuNames.Contains("清空全部显示缓存（保留 SQLite）"), "packet menu clears all UI cache");

                for (int i = 0; i < 3; i++)
                {
                    service.RecordChunk(new TransportChunk
                    {
                        ConnectionId = 0,
                        TimestampUtc = DateTime.UtcNow.AddMilliseconds(i),
                        Direction = TrafficDirection.ClientToServer,
                        Operation = TransportOperation.Send,
                        OriginalBytes = new byte[] { 0, 6, 1, 0, (byte)i, 1 },
                        EffectiveBytes = new byte[] { 0, 6, 1, 0, (byte)i, 2 },
                        RuleAction = RuleAction.Pass,
                        NativeResult = 6
                    });
                }
                AssertEqual(3, packetGrid.RowCount, "packet grid receives test rows");

                packetGrid.ClearSelection();
                packetGrid.Rows[0].Selected = true;
                packetGrid.Rows[2].Selected = true;
                MethodInfo rightClickHandler = typeof(ProtocolWorkbenchControl).GetMethod(
                    "OnPacketCellMouseDown", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(rightClickHandler != null, "packet right-click handler exists");
                rightClickHandler.Invoke(control, new object[]
                {
                    packetGrid,
                    new DataGridViewCellMouseEventArgs(0, 0, 1, 1,
                        new MouseEventArgs(MouseButtons.Right, 1, 1, 1, 0))
                });
                Assert(packetGrid.Rows[0].Selected && packetGrid.Rows[2].Selected,
                    "right-click preserves an existing multi-selection");

                ToolStripItem clearSelected = packetGrid.ContextMenuStrip.Items.Cast<ToolStripItem>()
                    .First(item => item.Text == "清空选中数据包（仅界面）");
                clearSelected.PerformClick();
                AssertEqual(1, packetGrid.RowCount, "clear-selected removes only selected UI rows");

                ToolStripItem clearAll = packetGrid.ContextMenuStrip.Items.Cast<ToolStripItem>()
                    .First(item => item.Text == "清空全部显示缓存（保留 SQLite）");
                clearAll.PerformClick();
                AssertEqual(0, packetGrid.RowCount, "clear-all removes remaining UI rows");
            }
            finally
            {
                if (control != null) control.Dispose();
                if (service != null) service.Dispose();
            }

            using (SQLiteConnection connection = new SQLiteConnection(
                "Data Source=" + databasePath + ";Version=3;Read Only=True;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM chunks;";
                    AssertEqual(3, Convert.ToInt32(command.ExecuteScalar()),
                        "packet UI clear actions preserve all SQLite chunks");
                }
            }
        }

        private static void TestRecordedMountainClimb(string path)
        {
            Assert(File.Exists(path), "recorded mountain-climb database exists");
            int acceptActions = 0;
            int turnInActions = 0;
            int taskRemovals = 0;
            int rainFootsteps = 0;
            int slopeFootsteps = 0;
            int creekFootsteps = 0;
            HashSet<int> arrivedMaps = new HashSet<int>();
            HashSet<int> portalNpcIds = new HashSet<int>();
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + path + ";Version=3;Read Only=True;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT direction,opcode,bytes FROM frames ORDER BY capture_ordinal;";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int direction = Convert.ToInt32(reader[0]);
                            int opcode = Convert.ToInt32(reader[1]);
                            byte[] bytes = (byte[])reader[2];
                            if (direction == (int)TrafficDirection.ClientToServer &&
                                opcode == TianshuMountainClimbProtocol.ClientQuestAction)
                            {
                                int npcId;
                                string functionId;
                                bool turnIn;
                                uint sequence;
                                Assert(TianshuMountainClimbProtocol.TryParseQuestAction(bytes, out npcId, out functionId,
                                    out turnIn, out sequence), "recorded mountain quest action parses");
                                AssertEqual(HexCodec.Format(bytes), HexCodec.Format(TianshuMountainClimbProtocol.BuildQuestAction(
                                    npcId, functionId, turnIn, sequence)), "recorded mountain quest action reconstructed");
                                if (turnIn) turnInActions++; else acceptActions++;
                            }
                            else if (direction == (int)TrafficDirection.ClientToServer &&
                                opcode == TianshuRunLoopProtocol.ClientMovement)
                            {
                                long timestamp;
                                int mapId;
                                ushort x;
                                ushort y;
                                if (!TianshuRunLoopProtocol.TryParseMovement(bytes, out timestamp, out mapId, out x, out y)) continue;
                                if (mapId == 18) rainFootsteps++;
                                else if (mapId == 19) slopeFootsteps++;
                                else if (mapId == 20) creekFootsteps++;
                            }
                            else if (direction == (int)TrafficDirection.ClientToServer &&
                                opcode == TianshuBountyProtocol.ClientNpcOpen && bytes.Length == 12)
                            {
                                portalNpcIds.Add((bytes[4] << 24) | (bytes[5] << 16) | (bytes[6] << 8) | bytes[7]);
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient &&
                                opcode == TianshuRunLoopProtocol.ServerMapInfo)
                            {
                                RunLoopMapInfo map;
                                if (TianshuRunLoopProtocol.TryParseMapInfo(bytes, out map)) arrivedMaps.Add(map.MapId);
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient &&
                                opcode == TianshuMountainClimbProtocol.ServerTaskRemoved &&
                                (TianshuMountainClimbProtocol.IsTaskRemoved(bytes, TianshuMountainClimbProtocol.BraveTowerTaskId) ||
                                TianshuMountainClimbProtocol.IsTaskRemoved(bytes, TianshuMountainClimbProtocol.FirstTowerTaskId) ||
                                TianshuMountainClimbProtocol.IsTaskRemoved(bytes, TianshuMountainClimbProtocol.RainMountainTaskId)))
                            {
                                taskRemovals++;
                            }
                        }
                    }
                }
            }
            AssertEqual(3, acceptActions, "recording accepts all three mountain tasks");
            Assert(turnInActions >= 3, "recording turns in all three mountain tasks");
            AssertEqual(3, taskRemovals, "server removes all three completed tasks");
            Assert(rainFootsteps >= 25 && slopeFootsteps >= 25 && creekFootsteps >= 10,
                "recording contains verified rain-mountain walking paths");
            foreach (int mapId in new[] { 13, 25, 30, 55, 65, 18, 19, 20 })
                Assert(arrivedMaps.Contains(mapId), "recording reaches required map " + mapId);
            foreach (int npcId in new[] { 14, 196, 101, 110, 71, 75, 111, 112, 113, 214, 246, 247 })
                Assert(portalNpcIds.Contains(npcId), "recording contains portal/NPC " + npcId);
        }

        private static void TestRecordedMountainShortcut(string path)
        {
            Assert(File.Exists(path), "recorded mountain shortcut database exists");
            bool shortcutAdvertised = false;
            bool shortcutRequested = false;
            bool braveTowerTurnedIn = false;
            bool braveTowerRemoved = false;
            int firstMapAfterShortcut = -1;

            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + path + ";Version=3;Read Only=True;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT direction,opcode,bytes FROM frames ORDER BY capture_ordinal;";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int direction = Convert.ToInt32(reader[0]);
                            int opcode = Convert.ToInt32(reader[1]);
                            byte[] bytes = (byte[])reader[2];
                            if (direction == (int)TrafficDirection.ServerToClient &&
                                opcode == TianshuBountyProtocol.ServerNpcDialog && bytes.Length >= 8 &&
                                ((bytes[4] << 24) | (bytes[5] << 16) | (bytes[6] << 8) | bytes[7]) ==
                                    TianshuMountainClimbProtocol.TowerTeleporterNpcId &&
                                TianshuBountyProtocol.ContainsText(bytes,
                                    TianshuMountainClimbProtocol.BraveTowerDirectTeleportFunction))
                            {
                                shortcutAdvertised = true;
                            }
                            else if (direction == (int)TrafficDirection.ClientToServer &&
                                opcode == TianshuBountyProtocol.ClientNpcFunction && bytes.Length >= 8 &&
                                ((bytes[4] << 24) | (bytes[5] << 16) | (bytes[6] << 8) | bytes[7]) ==
                                    TianshuMountainClimbProtocol.TowerTeleporterNpcId &&
                                TianshuBountyProtocol.ContainsText(bytes,
                                    TianshuMountainClimbProtocol.BraveTowerDirectTeleportFunction))
                            {
                                shortcutRequested = true;
                            }
                            else if (shortcutRequested && firstMapAfterShortcut < 0 &&
                                direction == (int)TrafficDirection.ServerToClient &&
                                opcode == TianshuRunLoopProtocol.ServerMapInfo)
                            {
                                RunLoopMapInfo map;
                                if (TianshuRunLoopProtocol.TryParseMapInfo(bytes, out map))
                                    firstMapAfterShortcut = map.MapId;
                            }
                            else if (shortcutRequested && direction == (int)TrafficDirection.ClientToServer &&
                                opcode == TianshuMountainClimbProtocol.ClientQuestAction)
                            {
                                int npcId;
                                string functionId;
                                bool turnIn;
                                uint requestSequence;
                                if (TianshuMountainClimbProtocol.TryParseQuestAction(bytes, out npcId, out functionId,
                                    out turnIn, out requestSequence) && npcId == 8075 && turnIn &&
                                    functionId == TianshuMountainClimbProtocol.BraveTowerTurnInFunction)
                                    braveTowerTurnedIn = true;
                            }
                            else if (shortcutRequested && direction == (int)TrafficDirection.ServerToClient &&
                                opcode == TianshuMountainClimbProtocol.ServerTaskRemoved &&
                                TianshuMountainClimbProtocol.IsTaskRemoved(bytes,
                                    TianshuMountainClimbProtocol.BraveTowerTaskId))
                            {
                                braveTowerRemoved = true;
                            }
                        }
                    }
                }
            }

            Assert(shortcutAdvertised, "tower teleporter advertises direct floor-ten function");
            Assert(shortcutRequested, "recording selects direct floor-ten function");
            AssertEqual(TianshuMountainClimbProtocol.BraveTowerDestinationMapId, firstMapAfterShortcut,
                "direct teleport's first confirmed destination is brave-tower floor ten");
            Assert(braveTowerTurnedIn, "recording turns in brave-tower task to explorer 8075 after shortcut");
            Assert(braveTowerRemoved, "server confirms brave-tower task removal after shortcut");
        }

        private static byte[] BuildRunLoopTaskFrame(int ring, string description, string tracker)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(new byte[] { 0, 0, 0, (byte)TianshuRunLoopProtocol.ServerTaskUpdate }, 0, 4);
                WriteUtf8String(stream, TianshuRunLoopProtocol.TaskId);
                WriteUtf8String(stream, "跑环任务");
                WriteUtf8String(stream, "跑环任务（第" + ring + "环）");
                WriteUtf8String(stream, description);
                WriteUtf8String(stream, tracker);
                byte[] frame = stream.ToArray();
                frame[0] = (byte)(frame.Length >> 8);
                frame[1] = (byte)frame.Length;
                return frame;
            }
        }

        private static byte[] BuildRunLoopTaskListFrame(int ring, string description, string tracker, string finishNpc)
        {
            byte[] inflated;
            using (MemoryStream content = new MemoryStream())
            {
                content.WriteByte(0);
                content.WriteByte(2);
                WriteUtf8String(content, "奇怪的萝卜");
                WriteUtf8String(content, "<a href='event:x:19,y:115,m:9,n:5008'>出云子</a>");
                WriteUtf8String(content, "知客蜂 (5/25), ");
                WriteUtf8String(content, TianshuRunLoopProtocol.TaskId);
                content.WriteByte(0);
                content.WriteByte(0);
                WriteUtf8String(content, "跑环任务");
                WriteUtf8String(content, "跑环任务（第" + ring + "环）");
                WriteUtf8String(content, description);
                WriteUtf8String(content, tracker);
                WriteUtf8String(content, finishNpc);
                inflated = content.ToArray();
            }

            byte[] deflated;
            using (MemoryStream compressed = new MemoryStream())
            {
                using (DeflateStream deflate = new DeflateStream(compressed, CompressionMode.Compress, true))
                    deflate.Write(inflated, 0, inflated.Length);
                deflated = compressed.ToArray();
            }

            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(new byte[] { 0, 0, 0, (byte)TianshuRunLoopProtocol.ServerTaskList }, 0, 4);
                int compressedLength = deflated.Length + 2;
                stream.WriteByte((byte)(compressedLength >> 8));
                stream.WriteByte((byte)compressedLength);
                stream.WriteByte(0x78);
                stream.WriteByte(0x9C);
                stream.Write(deflated, 0, deflated.Length);
                byte[] frame = stream.ToArray();
                frame[0] = (byte)(frame.Length >> 8);
                frame[1] = (byte)frame.Length;
                return frame;
            }
        }

        private static byte[] BuildRunLoopMapDataFrame(int mapId, int rpWidth, int rpHeight, int rows, int cols, byte[] cells)
        {
            byte[] deflated;
            using (MemoryStream compressed = new MemoryStream())
            {
                using (DeflateStream deflate = new DeflateStream(compressed, CompressionMode.Compress, true))
                    deflate.Write(cells, 0, cells.Length);
                deflated = compressed.ToArray();
            }

            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(new byte[] { 0, 0, 0, (byte)TianshuRunLoopProtocol.ServerMapData2 }, 0, 4);
                WriteUInt16BigEndian(stream, (ushort)mapId);
                WriteUtf8String(stream, "新月村");
                int[] shorts = { 180, 200, 150, rows, cols, rpWidth, rpHeight, rows, cols };
                for (int i = 0; i < shorts.Length; i++) WriteUInt16BigEndian(stream, (ushort)shorts[i]);
                WriteUInt32BigEndian(stream, 0);
                WriteUInt16BigEndian(stream, 0);
                int compressedLength = deflated.Length + 2;
                WriteUInt32BigEndian(stream, (uint)compressedLength);
                stream.WriteByte(0x78);
                stream.WriteByte(0x9C);
                stream.Write(deflated, 0, deflated.Length);
                byte[] frame = stream.ToArray();
                frame[0] = (byte)(frame.Length >> 8);
                frame[1] = (byte)frame.Length;
                return frame;
            }
        }

        private static void TestRecordedRunLoop(string path)
        {
            Assert(File.Exists(path), "recorded run-loop database exists");
            int parsedTasks = 0;
            int deliveryTasks = 0;
            int huntTasks = 0;
            int movements = 0;
            int recoveries = 0;
            int acceptFunctions = 0;
            int turnInFunctions = 0;
            int learnedEntities = 0;
            bool learnedJiangTian = false;
            bool ringTwenty = false;
            bool wrappedToOne = false;
            int previousRing = 0;
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + path + ";Version=3;Read Only=True;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT direction,opcode,bytes FROM frames ORDER BY capture_ordinal;";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int direction = Convert.ToInt32(reader[0]);
                            int opcode = Convert.ToInt32(reader[1]);
                            byte[] bytes = (byte[])reader[2];
                            if (direction == (int)TrafficDirection.ClientToServer && opcode == TianshuRunLoopProtocol.ClientMovement)
                            {
                                long timestamp;
                                int mapId;
                                ushort x;
                                ushort y;
                                if (TianshuRunLoopProtocol.TryParseMovement(bytes, out timestamp, out mapId, out x, out y)) movements++;
                            }
                            else if (direction == (int)TrafficDirection.ClientToServer && opcode == TianshuRunLoopProtocol.ClientUiAction &&
                                TianshuRunLoopProtocol.TryParseOneKeyRecovery(bytes)) recoveries++;
                            else if (direction == (int)TrafficDirection.ClientToServer && opcode == TianshuBountyProtocol.ClientNpcFunction)
                            {
                                string raw = Encoding.UTF8.GetString(bytes);
                                if (raw.IndexOf(TianshuRunLoopProtocol.AcceptFunctionId, StringComparison.Ordinal) >= 0) acceptFunctions++;
                                if (raw.IndexOf(TianshuRunLoopProtocol.TurnInFunctionId, StringComparison.Ordinal) >= 0) turnInFunctions++;
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient && opcode == TianshuRunLoopProtocol.ServerTaskUpdate)
                            {
                                RunLoopTask task;
                                if (!TianshuRunLoopProtocol.TryParseTask(bytes, out task)) continue;
                                parsedTasks++;
                                if (task.Kind == RunLoopTaskKind.DeliverItem) deliveryTasks++;
                                if (task.Kind == RunLoopTaskKind.Hunt) huntTasks++;
                                if (task.RingNumber == 20) ringTwenty = true;
                                if (previousRing == 20 && task.RingNumber == 1) wrappedToOne = true;
                                previousRing = task.RingNumber;
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient &&
                                (opcode == TianshuRunLoopProtocol.ServerEntityList || opcode == TianshuRunLoopProtocol.ServerNearbyEntities))
                            {
                                IList<RunLoopEntity> entities = TianshuRunLoopProtocol.ExtractEntities(bytes);
                                learnedEntities += entities.Count;
                                for (int i = 0; i < entities.Count; i++)
                                    if (entities[i].Id == 93192 && entities[i].Name == "先锋护卫姜天") learnedJiangTian = true;
                            }
                        }
                    }
                }
            }
            Assert(parsedTasks >= 8, "recording contains parsed run-loop task/progress updates");
            Assert(deliveryTasks >= 2, "recording contains item-delivery rings");
            Assert(huntTasks >= 5, "recording contains hunt progress updates");
            Assert(movements >= 100, "recording contains encounter walking packets");
            Assert(recoveries >= 4, "recording contains periodic one-key recovery packets");
            Assert(acceptFunctions >= 1, "recording contains run-loop accept function");
            Assert(turnInFunctions >= 3, "recording contains repeated turn-in function");
            Assert(ringTwenty && wrappedToOne, "recording proves 20-ring round boundary");
            Assert(learnedEntities >= 10 && learnedJiangTian, "runtime entity table learns NPC name/id mappings");
        }

        private static void TestRecordedRunLoopDemonstration(string path)
        {
            Assert(File.Exists(path), "recorded run-loop demonstration database exists");
            int parsedTasks = 0;
            int talkTasks = 0;
            int huntTasks = 0;
            int travelLinks = 0;
            int learnedEntities = 0;
            bool learnedFengQi = false;
            bool learnedZhouHu = false;
            bool learnedWuXiuLuo = false;
            bool sawMap101 = false;
            bool sawMap13 = false;
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + path + ";Version=3;Read Only=True;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT direction,opcode,bytes FROM frames ORDER BY capture_ordinal;";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int direction = Convert.ToInt32(reader[0]);
                            int opcode = Convert.ToInt32(reader[1]);
                            byte[] bytes = (byte[])reader[2];
                            if (direction == (int)TrafficDirection.ServerToClient &&
                                opcode == TianshuRunLoopProtocol.ServerTaskUpdate)
                            {
                                RunLoopTask task;
                                if (!TianshuRunLoopProtocol.TryParseTask(bytes, out task)) continue;
                                parsedTasks++;
                                if (task.Kind == RunLoopTaskKind.TalkToNpc) talkTasks++;
                                if (task.Kind == RunLoopTaskKind.Hunt) huntTasks++;
                            }
                            else if (direction == (int)TrafficDirection.ClientToServer &&
                                opcode == TianshuBountyProtocol.ClientTravelLink)
                            {
                                travelLinks++;
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient &&
                                opcode == TianshuRunLoopProtocol.ServerMapInfo)
                            {
                                RunLoopMapInfo map;
                                if (TianshuRunLoopProtocol.TryParseMapInfo(bytes, out map))
                                {
                                    if (map.MapId == 101) sawMap101 = true;
                                    if (map.MapId == 13) sawMap13 = true;
                                }
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient &&
                                (opcode == TianshuRunLoopProtocol.ServerEntityList ||
                                opcode == TianshuRunLoopProtocol.ServerNearbyEntities))
                            {
                                IList<RunLoopEntity> entities = TianshuRunLoopProtocol.ExtractEntities(bytes);
                                learnedEntities += entities.Count;
                                for (int i = 0; i < entities.Count; i++)
                                {
                                    RunLoopEntity entity = entities[i];
                                    if (entity.Id == 10102 && entity.Name == "冯奇") learnedFengQi = true;
                                    if (entity.Id == 8032 && entity.Name == "周猎户") learnedZhouHu = true;
                                    if (entity.Id == 8011 && entity.Name == "舞修罗") learnedWuXiuLuo = true;
                                }
                            }
                        }
                    }
                }
            }
            Assert(parsedTasks >= 3, "demonstration recording contains three parsed run-loop task updates");
            Assert(talkTasks >= 2, "demonstration recording contains two talk-to-npc tasks");
            Assert(huntTasks >= 1, "demonstration recording contains one hunt task");
            Assert(travelLinks >= 4, "demonstration recording contains task-link travel requests");
            Assert(sawMap101 && sawMap13, "demonstration recording contains expected turn-in maps");
            Assert(learnedEntities >= 20, "demonstration recording contains map entity tables");
            Assert(learnedFengQi && learnedZhouHu && learnedWuXiuLuo,
                "demonstration recording learns run-loop turn-in NPC ids");
        }

        private static byte[] BuildInventoryUpdateFrame(ushort bag, ushort slot, ushort count, string name, string templateId)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(new byte[] { 0, 0, 0, 0x24 }, 0, 4);
                stream.WriteByte((byte)(bag >> 8));
                stream.WriteByte((byte)bag);
                stream.WriteByte((byte)(slot >> 8));
                stream.WriteByte((byte)slot);
                stream.WriteByte((byte)(count >> 8));
                stream.WriteByte((byte)count);
                WriteUtf8String(stream, name);
                WriteUtf8String(stream, templateId);
                byte[] frame = stream.ToArray();
                frame[0] = (byte)(frame.Length >> 8);
                frame[1] = (byte)frame.Length;
                return frame;
            }
        }

        private static byte[] BuildInventorySnapshotFrame(ushort bag, ushort slot, ushort count)
        {
            byte[] name = Encoding.UTF8.GetBytes("上古神器碎片(一等)");
            byte[] raw;
            using (MemoryStream payload = new MemoryStream())
            {
                payload.Write(new byte[] { 0xAA, 0x55, 0, 0, 0 }, 0, 5);
                payload.WriteByte((byte)(bag >> 8));
                payload.WriteByte((byte)bag);
                payload.WriteByte((byte)(slot >> 8));
                payload.WriteByte((byte)slot);
                payload.WriteByte((byte)(count >> 8));
                payload.WriteByte((byte)count);
                payload.WriteByte(0);
                payload.WriteByte(0x77);
                payload.WriteByte((byte)(name.Length >> 8));
                payload.WriteByte((byte)name.Length);
                payload.Write(name, 0, name.Length);
                payload.WriteByte(0);
                payload.WriteByte(0);
                raw = payload.ToArray();
            }
            byte[] deflated;
            using (MemoryStream compressed = new MemoryStream())
            {
                using (DeflateStream deflater = new DeflateStream(compressed, CompressionMode.Compress, true))
                    deflater.Write(raw, 0, raw.Length);
                deflated = compressed.ToArray();
            }
            int zlibLength = 2 + deflated.Length + 4;
            byte[] frame = new byte[6 + zlibLength];
            frame[0] = (byte)(frame.Length >> 8);
            frame[1] = (byte)frame.Length;
            frame[2] = (byte)(TianshuDonationProtocol.ServerInventorySnapshot >> 8);
            frame[3] = (byte)TianshuDonationProtocol.ServerInventorySnapshot;
            frame[4] = (byte)(zlibLength >> 8);
            frame[5] = (byte)zlibLength;
            frame[6] = 0x78;
            frame[7] = 0x01;
            Buffer.BlockCopy(deflated, 0, frame, 8, deflated.Length);
            return frame;
        }

        private static void TestRecordedBounty(string path)
        {
            Assert(File.Exists(path), "recorded bounty database exists");
            int parsedTasks = 0;
            int maximumTaskNumber = 0;
            int giverDialogs = 0;
            int teleportDialogs = 0;
            bool roundReward = false;
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + path + ";Version=3;Read Only=True;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT direction,opcode,bytes FROM frames ORDER BY capture_ordinal;";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int direction = Convert.ToInt32(reader[0]);
                            int opcode = Convert.ToInt32(reader[1]);
                            byte[] bytes = (byte[])reader[2];
                            if (direction != (int)TrafficDirection.ServerToClient) continue;
                            if (opcode == TianshuBountyProtocol.ServerTaskUpdate)
                            {
                                BountyTravelTarget enemy;
                                BountyTravelTarget giver;
                                int taskNumber;
                                if (TianshuBountyProtocol.TryParseTaskUpdate(bytes, out enemy, out giver)) parsedTasks++;
                                if (TianshuBountyProtocol.TryParseTaskNumber(bytes, out taskNumber))
                                    maximumTaskNumber = Math.Max(maximumTaskNumber, taskNumber);
                            }
                            else if (opcode == TianshuBountyProtocol.ServerNpcDialog)
                            {
                                int npcId;
                                string functionId;
                                if (TianshuBountyProtocol.TryParseNpcFunction(bytes, "除暴安良", out npcId, out functionId) &&
                                    npcId == 8011 && functionId == "374") giverDialogs++;
                            }
                            else if (opcode == TianshuBountyProtocol.ServerConfirmationDialog)
                            {
                                BountyConfirmationDialog dialog;
                                if (TianshuBountyProtocol.TryParseConfirmationDialog(bytes, out dialog) &&
                                    dialog.Title.IndexOf("传送", StringComparison.Ordinal) >= 0) teleportDialogs++;
                            }
                            else if (opcode == TianshuBountyProtocol.ServerSystemMessage &&
                                TianshuBountyProtocol.ContainsText(bytes, "任务奖励"))
                            {
                                roundReward = true;
                            }
                        }
                    }
                }
            }
            Assert(parsedTasks >= 9, "complete recording task HTML count");
            AssertEqual(10, maximumTaskNumber, "complete recording reaches task ten");
            Assert(giverDialogs >= 10, "giver is revisited for every turn-in and round reward");
            Assert(teleportDialogs >= 18, "dynamic teleport confirmations parsed");
            Assert(roundReward, "tenth-task round reward detected");
        }

        private static void TestRecordedDonation(string path)
        {
            Assert(File.Exists(path), "recorded donation database exists");
            int donationRequests = 0;
            int successes = 0;
            int panelReady = 0;
            int targetUpdates = 0;
            int sourceRemovals = 0;
            int donationDialogs = 0;
            HashSet<ushort> sourceSlots = new HashSet<ushort>();
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + path + ";Version=3;Read Only=True;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT direction,opcode,bytes FROM frames ORDER BY capture_ordinal;";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int direction = Convert.ToInt32(reader[0]);
                            int opcode = Convert.ToInt32(reader[1]);
                            byte[] bytes = (byte[])reader[2];
                            if (direction == (int)TrafficDirection.ClientToServer && opcode == TianshuDonationProtocol.ClientItemTransfer)
                            {
                                ushort slot;
                                uint sequence;
                                if (TianshuDonationProtocol.TryParseDonationRequest(bytes, out slot, out sequence))
                                {
                                    donationRequests++;
                                    sourceSlots.Add(slot);
                                    AssertEqual(HexCodec.Format(bytes),
                                        HexCodec.Format(TianshuDonationProtocol.BuildDonateItem(slot, sequence)),
                                        "recorded donation reconstructed without hard-coded slot/sequence");
                                }
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient && opcode == TianshuBountyProtocol.ServerNpcDialog)
                            {
                                int npcId;
                                string functionId;
                                if (TianshuBountyProtocol.TryParseNpcFunction(bytes, TianshuDonationProtocol.DonationFunctionLabel,
                                    out npcId, out functionId) && npcId == TianshuDonationProtocol.DefaultDonationNpcId) donationDialogs++;
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient && opcode == TianshuDonationProtocol.ServerDonationPanel)
                            {
                                ushort panel;
                                if (TianshuDonationProtocol.TryParsePanelReady(bytes, out panel) && panel == 3) panelReady++;
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient && opcode == TianshuDonationProtocol.ServerItemUpdate)
                            {
                                DonationInventoryItem item;
                                if (TianshuDonationProtocol.TryParseInventoryUpdate(bytes, out item) && item.IsTargetFragment &&
                                    item.BagId == TianshuDonationProtocol.SourceBagId) targetUpdates++;
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient && opcode == TianshuDonationProtocol.ServerItemRemove)
                            {
                                ushort bag;
                                ushort slot;
                                if (TianshuDonationProtocol.TryParseInventoryRemoval(bytes, out bag, out slot) &&
                                    bag == TianshuDonationProtocol.SourceBagId) sourceRemovals++;
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient && opcode == TianshuBountyProtocol.ServerSystemMessage &&
                                TianshuDonationProtocol.IsDonationSuccess(bytes)) successes++;
                        }
                    }
                }
            }
            Assert(donationRequests >= 15, "recording contains the earlier full stack and marked donation samples");
            Assert(successes >= 15, "every recorded donation has a server success message");
            Assert(panelReady >= donationRequests, "panel-ready response precedes each donation");
            Assert(donationDialogs >= donationRequests, "donation function is dynamically advertised for each visit");
            Assert(targetUpdates >= donationRequests - 1, "source inventory updates expose item id/name and changing count");
            Assert(sourceRemovals >= 1, "last source item is removed after stack exhaustion");
            Assert(sourceSlots.Contains(0x7E) && sourceSlots.Contains(0x12), "recording proves source slot changes and must be queried dynamically");
        }

        private static void TestRecordedDonationConfirmation(string path)
        {
            Assert(File.Exists(path), "recorded donation-confirm database exists");
            int stagedItems = 0;
            int confirmations = 0;
            int matchedSuccesses = 0;
            bool transferPending = false;
            bool stagedForConfirmation = false;
            bool confirmationPending = false;
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + path + ";Version=3;Read Only=True;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT direction,opcode,bytes FROM frames ORDER BY capture_ordinal;";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int direction = Convert.ToInt32(reader[0]);
                            int opcode = Convert.ToInt32(reader[1]);
                            byte[] bytes = (byte[])reader[2];
                            if (direction == (int)TrafficDirection.ClientToServer &&
                                opcode == TianshuDonationProtocol.ClientItemTransfer)
                            {
                                ushort slot;
                                uint sequence;
                                if (TianshuDonationProtocol.TryParseDonationRequest(bytes, out slot, out sequence))
                                {
                                    transferPending = true;
                                    stagedForConfirmation = false;
                                }
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient &&
                                opcode == TianshuDonationProtocol.ServerItemUpdate)
                            {
                                DonationInventoryItem item;
                                if (transferPending && TianshuDonationProtocol.TryParseInventoryUpdate(bytes, out item) &&
                                    item.BagId == TianshuDonationProtocol.DonationContainerId && item.Slot == 0 &&
                                    item.IsTargetFragment && item.Count > 0)
                                {
                                    stagedItems++;
                                    stagedForConfirmation = true;
                                }
                            }
                            else if (direction == (int)TrafficDirection.ClientToServer &&
                                opcode == TianshuDonationProtocol.ClientUiAction)
                            {
                                uint sequence;
                                if (TianshuDonationProtocol.TryParseDonationConfirm(bytes, out sequence))
                                {
                                    confirmations++;
                                    AssertEqual(HexCodec.Format(bytes),
                                        HexCodec.Format(TianshuDonationProtocol.BuildDonationConfirm(sequence)),
                                        "recorded donation button reconstructed with live sequence");
                                    if (stagedForConfirmation)
                                    {
                                        confirmationPending = true;
                                        stagedForConfirmation = false;
                                        transferPending = false;
                                    }
                                }
                            }
                            else if (direction == (int)TrafficDirection.ServerToClient &&
                                opcode == TianshuBountyProtocol.ServerSystemMessage &&
                                TianshuDonationProtocol.IsDonationSuccess(bytes) && confirmationPending)
                            {
                                matchedSuccesses++;
                                confirmationPending = false;
                            }
                        }
                    }
                }
            }
            Assert(stagedItems >= 10, "recording contains repeated server-confirmed temporary-container updates");
            Assert(confirmations >= 10, "recording contains repeated 0x03B9 donation-button actions");
            Assert(matchedSuccesses >= 10, "donation success follows staging and 0x03B9 confirmation");
        }

        private static void TestRecordedInventorySnapshot(string path)
        {
            Assert(File.Exists(path), "recorded initial inventory database exists");
            int snapshotFrames = 0;
            DonationInventoryItem target = null;
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + path + ";Version=3;Read Only=True;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT bytes FROM frames WHERE direction=@direction AND opcode=@opcode ORDER BY capture_ordinal;";
                    command.Parameters.AddWithValue("@direction", (int)TrafficDirection.ServerToClient);
                    command.Parameters.AddWithValue("@opcode", TianshuDonationProtocol.ServerInventorySnapshot);
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            snapshotFrames++;
                            IList<DonationInventoryItem> items;
                            if (!TianshuDonationProtocol.TryParseInventorySnapshot((byte[])reader[0], out items)) continue;
                            for (int i = 0; i < items.Count; i++)
                                if (items[i].IsTargetFragment) target = items[i];
                        }
                    }
                }
            }
            Assert(snapshotFrames >= 1, "login recording contains compressed initial inventory snapshot");
            Assert(target != null, "target fragment found by name in initial inventory snapshot");
            AssertEqual((ushort)1, target.BagId, "live snapshot target bag");
            AssertEqual((ushort)13, target.Slot, "live snapshot target slot");
            AssertEqual((ushort)730, target.Count, "live snapshot target count");
        }

        private static byte[] BuildConfirmationFrame(uint contextId, string title, string content, string token)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(new byte[] { 0, 0, 0, (byte)TianshuBountyProtocol.ServerConfirmationDialog }, 0, 4);
                WriteUInt32BigEndian(stream, contextId);
                WriteUtf8String(stream, title);
                WriteUtf8String(stream, content);
                WriteUtf8String(stream, token);
                byte[] frame = stream.ToArray();
                frame[0] = (byte)(frame.Length >> 8);
                frame[1] = (byte)frame.Length;
                return frame;
            }
        }

        private static byte[] BuildNpcDialogFrame()
        {
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(new byte[] { 0, 0, 0, (byte)TianshuBountyProtocol.ServerNpcDialog }, 0, 4);
                WriteUInt32BigEndian(stream, 8011);
                WriteUtf8String(stream, "舞修罗");
                WriteUtf8String(stream, "205011");
                WriteUtf8String(stream, "任务列表");
                WriteUtf8String(stream, "先天下之忧而忧");
                stream.WriteByte(0);
                stream.WriteByte(1);
                WriteUtf8String(stream, "除暴安良");
                WriteUtf8String(stream, "374");
                byte[] frame = stream.ToArray();
                frame[0] = (byte)(frame.Length >> 8);
                frame[1] = (byte)frame.Length;
                return frame;
            }
        }

        private static byte[] BuildNpcDialogFrame(int npcId, string npcName, string label, string functionId)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(new byte[] { 0, 0, 0, (byte)TianshuBountyProtocol.ServerNpcDialog }, 0, 4);
                WriteUInt32BigEndian(stream, (uint)npcId);
                WriteUtf8String(stream, npcName);
                WriteUtf8String(stream, "0");
                WriteUtf8String(stream, "功能列表");
                stream.WriteByte(0);
                stream.WriteByte(1);
                WriteUtf8String(stream, label);
                WriteUtf8String(stream, functionId);
                byte[] frame = stream.ToArray();
                frame[0] = (byte)(frame.Length >> 8);
                frame[1] = (byte)frame.Length;
                return frame;
            }
        }

        private static void WriteUtf8String(Stream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            stream.WriteByte((byte)(bytes.Length >> 8));
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteUInt32BigEndian(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteUInt16BigEndian(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void TestRuleActions(string root)
        {
            string path = Path.Combine(root, "rules.json");
            RuleEngine engine = new RuleEngine(path, null);
            PacketContext context = new PacketContext
            {
                ActiveMode = true,
                Direction = TrafficDirection.ClientToServer,
                State = "Connected",
                Connection = new ConnectionSession { Kind = ConnectionKind.Game }
            };
            ProtocolFrame frame = new ProtocolFrame
            {
                Direction = TrafficDirection.ClientToServer,
                Opcode = 0x100,
                Bytes = new byte[] { 0, 6, 1, 0, 0xAA, 0xBB },
                Status = FrameStatus.Decoded
            };

            foreach (RuleAction action in new[] { RuleAction.Drop, RuleAction.Delay, RuleAction.Replace, RuleAction.Duplicate, RuleAction.Inject })
            {
                PacketRule rule = new PacketRule
                {
                    Id = "test-" + action,
                    Name = action.ToString(),
                    Enabled = true,
                    Direction = TrafficDirection.ClientToServer,
                    ConnectionKind = ConnectionKind.Game,
                    Opcode = 0x100,
                    Action = action,
                    ReplacementHex = "00 06 01 00 CC DD",
                    DelayMs = 25
                };
                engine.Save(new List<PacketRule> { rule });
                RuleDecision decision = engine.Evaluate(context, frame);
                AssertEqual(action, decision.Action, "rule action " + action);
                if (action == RuleAction.Replace)
                {
                    AssertEqual("00 06 01 00 CC DD", HexCodec.Format(decision.Bytes), "replacement bytes");
                }
            }
        }

        private static void TestLua(string root)
        {
            string path = Path.Combine(root, "packet.lua");
            File.WriteAllText(path,
                "function on_frame(ctx) return {action='replace', hex='00 06 01 00 CC DD'} end\n" +
                "function main(api) api:mark('ok'); api:assert(true, 'must pass'); return true end\n");
            LuaScenarioHost host = new LuaScenarioHost(path, 50);
            RuleDecision decision = host.EvaluatePacket(
                new PacketContext { ActiveMode = true, Direction = TrafficDirection.ClientToServer, State = "Connected" },
                new ProtocolFrame { Bytes = new byte[] { 0, 6, 1, 0, 0xAA, 0xBB }, Direction = TrafficDirection.ClientToServer });
            AssertEqual(RuleAction.Replace, decision.Action, "Lua replace");
            AssertEqual("00 06 01 00 CC DD", HexCodec.Format(decision.Bytes), "Lua bytes");
            ScenarioResult result = host.Run("main");
            AssertEqual(ScenarioStatus.Passed, result.Status, "Lua scenario");
            AssertEqual(1, result.Log.Count, "Lua mark");
        }

        private static void TestStateTracker()
        {
            ProtocolDefinition definition = CreateLengthPrefixDefinition();
            definition.StateTransitions.Add(new StateTransitionDefinition
            {
                FromState = "Connected",
                ToState = "InWorld",
                Direction = TrafficDirection.ServerToClient,
                Opcode = 200
            });
            StateTracker tracker = new StateTracker(definition);
            tracker.AcceptConnectionEvent(7, "Connected");
            StateTransition transition = tracker.Accept(new ProtocolFrame
            {
                ConnectionId = 7,
                TimestampUtc = DateTime.UtcNow,
                Direction = TrafficDirection.ServerToClient,
                Opcode = 200,
                Bytes = new byte[] { 0, 4, 0, 200 }
            });
            Assert(transition != null, "state transition expected");
            AssertEqual("InWorld", tracker.GetCurrentState(7), "current state");
        }

        private static void TestStoreAndPcapng(string root)
        {
            string directory = Path.Combine(root, "store");
            SqliteCaptureStore store = new SqliteCaptureStore(directory, 1024);
            store.StartSession("test", true);
            ConnectionSession connection = new ConnectionSession
            {
                Id = 1,
                SocketHandle = new IntPtr(42),
                OpenedUtc = DateTime.UtcNow,
                State = "Connected",
                Kind = ConnectionKind.Game,
                RemoteEndPoint = "127.0.0.1:12345",
                RemotePort = 12345
            };
            store.EnqueueConnection(connection);
            store.EnqueueChunk(new TransportChunk
            {
                ConnectionId = 1,
                TimestampUtc = DateTime.UtcNow,
                Direction = TrafficDirection.ClientToServer,
                Operation = TransportOperation.Send,
                OriginalBytes = new byte[] { 0, 6, 1, 0, 1, 2 },
                EffectiveBytes = new byte[] { 0, 6, 1, 0, 3, 4 },
                RuleAction = RuleAction.Replace,
                NativeResult = 6
            });
            store.EnqueueEvent(new WorkbenchEvent { TimestampUtc = DateTime.UtcNow, Category = "Test", Level = "INFO", Message = "ok" });
            store.CompleteSession();
            IList<TransportChunk> chunks = store.ReadChunks(10);
            AssertEqual(1, chunks.Count, "stored chunks");
            string database = store.DatabasePath;
            string pcap = Path.Combine(root, "capture.pcapng");
            PcapngExporter exporter = new PcapngExporter();
            exporter.Export(database, pcap);
            byte[] header = File.ReadAllBytes(pcap);
            Assert(header.Length > 48, "pcapng length");
            AssertEqual(0x0A, header[0], "pcapng magic byte");
            OfflineReplayResult replay = new OfflineReplayEngine().Replay(database, CreateLengthPrefixDefinition());
            AssertEqual(1, replay.FrameCount, "offline replay frame count");
            store.Dispose();
        }

        private static void TestLoopbackCapture(string root)
        {
            string testRoot = Path.Combine(root, "loopback");
            Directory.CreateDirectory(testRoot);
            string profilePath = Path.Combine(testRoot, "profile.json");
            string protocolPath = Path.Combine(testRoot, "protocol.json");
            string rulesPath = Path.Combine(testRoot, "rules.json");
            string luaPath = Path.Combine(testRoot, "packet.lua");
            File.WriteAllText(protocolPath, JsonConvert.SerializeObject(CreateLengthPrefixDefinition()));
            File.WriteAllText(rulesPath, JsonConvert.SerializeObject(new RuleSetDocument
            {
                Rules = new List<PacketRule>
                {
                    new PacketRule
                    {
                        Id = "loopback-replace",
                        Name = "loopback-replace",
                        Enabled = true,
                        ConnectionKind = ConnectionKind.Game,
                        Direction = TrafficDirection.ClientToServer,
                        Opcode = 0x100,
                        Action = RuleAction.Replace,
                        ReplacementHex = "00 06 01 00 CC DD"
                    }
                }
            }));
            File.WriteAllText(luaPath, "function on_frame(ctx) return nil end\nfunction main(api) return true end\n");
            TcpListener proxyPortReservation = new TcpListener(IPAddress.Loopback, 0);
            proxyPortReservation.Start();
            int configuredProxyPort = ((IPEndPoint)proxyPortReservation.LocalEndpoint).Port;
            proxyPortReservation.Stop();
            File.WriteAllText(profilePath, JsonConvert.SerializeObject(new
            {
                name = "loopback",
                gameUrl = "about:blank",
                activeMode = true,
                captureDirectory = "sessions",
                protocolPath = "protocol.json",
                rulesPath = "rules.json",
                packetScriptPath = "packet.lua",
                proxyPorts = new[] { configuredProxyPort },
                policyPorts = new int[0],
                scriptTimeoutMs = 50,
                captureQueueCapacity = 4096
            }));

            TcpListener redirectListener = new TcpListener(IPAddress.Loopback, 0);
            redirectListener.Start();
            int redirectPort = ((IPEndPoint)redirectListener.LocalEndpoint).Port;

            WorkbenchProfile profile = WorkbenchProfile.Load(profilePath, testRoot);
            ProtocolWorkbenchService service = new ProtocolWorkbenchService(profile);
            WinsockCaptureEngine engine = new WinsockCaptureEngine(service, profile, redirectPort);
            service.AttachCaptureEngine(engine);
            service.StartCapture();

            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            byte[] received = null;
            Exception serverError = null;
            Thread server = new Thread(new ThreadStart(delegate
            {
                try
                {
                    using (TcpClient accepted = listener.AcceptTcpClient())
                    {
                        accepted.ReceiveTimeout = 3000;
                        received = new byte[6];
                        int offset = 0;
                        while (offset < received.Length)
                        {
                            int count = accepted.GetStream().Read(received, offset, received.Length - offset);
                            if (count == 0) break;
                            offset += count;
                        }
                    }
                }
                catch (Exception ex)
                {
                    serverError = ex;
                }
            }));
            server.Start();

            using (TcpClient client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port);
                byte[] original = new byte[] { 0, 6, 1, 0, 0xAA, 0xBB };
                client.GetStream().Write(original, 0, original.Length);
                client.GetStream().Flush();
            }
            server.Join(5000);
            listener.Stop();

            if (serverError != null) throw serverError;
            Assert(received != null, "loopback server received data");
            AssertEqual("00 06 01 00 CC DD", HexCodec.Format(received), "hook replacement reached server");

            byte[] redirected = null;
            Thread redirectServer = new Thread(new ThreadStart(delegate
            {
                try
                {
                    using (TcpClient accepted = redirectListener.AcceptTcpClient())
                    {
                        redirected = new byte[1];
                        accepted.GetStream().Read(redirected, 0, 1);
                    }
                }
                catch (Exception ex) { serverError = ex; }
            }));
            redirectServer.IsBackground = true;
            redirectServer.Start();
            using (Socket redirectedSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            using (SocketAsyncEventArgs connectArgs = new SocketAsyncEventArgs())
            using (ManualResetEvent connectCompleted = new ManualResetEvent(false))
            {
                connectArgs.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 80);
                connectArgs.Completed += delegate { connectCompleted.Set(); };
                if (!redirectedSocket.ConnectAsync(connectArgs)) connectCompleted.Set();
                Assert(connectCompleted.WaitOne(5000), "ConnectEx redirect completed");
                AssertEqual(SocketError.Success, connectArgs.SocketError, "ConnectEx redirect socket result");
                redirectedSocket.Send(new byte[] { 0x5A });
            }
            redirectServer.Join(5000);
            if (serverError != null) throw serverError;
            Assert(redirected != null && redirected[0] == 0x5A, "ConnectEx external HTTP was transparently redirected to local proxy");

            redirected = null;
            Thread configuredProxyServer = new Thread(new ThreadStart(delegate
            {
                try
                {
                    using (TcpClient accepted = redirectListener.AcceptTcpClient())
                    {
                        redirected = new byte[1];
                        accepted.GetStream().Read(redirected, 0, 1);
                    }
                }
                catch (Exception ex) { serverError = ex; }
            }));
            configuredProxyServer.IsBackground = true;
            configuredProxyServer.Start();
            using (TcpClient proxyClient = new TcpClient())
            {
                proxyClient.Connect(IPAddress.Loopback, configuredProxyPort);
                proxyClient.GetStream().WriteByte(0x6B);
            }
            configuredProxyServer.Join(5000);
            redirectListener.Stop();
            service.Dispose();
            if (serverError != null) throw serverError;
            Assert(redirected != null && redirected[0] == 0x6B, "configured loopback HTTP proxy was transparently redirected");
        }

        private static void TestOperationSchemaMigration(string root)
        {
            string path = Path.Combine(root, "legacy-v1.sqlite");
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"
CREATE TABLE sessions(id INTEGER PRIMARY KEY AUTOINCREMENT,profile_name TEXT NOT NULL,started_utc TEXT NOT NULL,ended_utc TEXT,active_mode INTEGER NOT NULL,application_version TEXT NOT NULL);
CREATE TABLE chunks(id INTEGER PRIMARY KEY,session_id INTEGER NOT NULL,connection_id INTEGER NOT NULL,timestamp_utc TEXT NOT NULL,direction INTEGER NOT NULL,operation INTEGER NOT NULL,stream_offset INTEGER NOT NULL,thread_id INTEGER NOT NULL,native_result INTEGER NOT NULL,native_error INTEGER NOT NULL,original_bytes BLOB NOT NULL,effective_bytes BLOB NOT NULL,rule_action INTEGER NOT NULL,rule_id TEXT,note TEXT);
CREATE TABLE frames(id INTEGER PRIMARY KEY,session_id INTEGER NOT NULL,connection_id INTEGER NOT NULL,first_chunk_id INTEGER,timestamp_utc TEXT NOT NULL,direction INTEGER NOT NULL,stream_offset INTEGER NOT NULL,bytes BLOB NOT NULL,opcode INTEGER,name TEXT,status INTEGER NOT NULL,parse_error TEXT,fields_json TEXT);
CREATE TABLE state_transitions(id INTEGER PRIMARY KEY AUTOINCREMENT,session_id INTEGER NOT NULL,connection_id INTEGER NOT NULL,timestamp_utc TEXT NOT NULL,from_state TEXT,to_state TEXT,trigger TEXT,confirmed INTEGER NOT NULL);
PRAGMA user_version=1;";
                    command.ExecuteNonQuery();
                }
            }
            SqliteCaptureStore.MigrateDatabase(path);
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                connection.Open();
                AssertEqual(2L, ExecuteScalarLong(connection, "PRAGMA user_version;"), "schema version");
                Assert(HasColumn(connection, "chunks", "capture_ordinal"), "chunk capture ordinal migrated");
                Assert(HasColumn(connection, "frames", "capture_ordinal"), "frame capture ordinal migrated");
                AssertEqual(1L, ExecuteScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='operation_runs';"), "operation table created");
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO operation_runs(id,session_id,definition_id,definition_snapshot_json,started_utc,start_boundary_ordinal,status,outcome) VALUES('crashed',1,'d','{}','2026-01-01T00:00:00.0000000Z',10,0,0);";
                    command.ExecuteNonQuery();
                }
            }
            SqliteCaptureStore.MigrateDatabase(path);
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                connection.Open();
                AssertEqual((long)OperationRunStatus.Interrupted, ExecuteScalarLong(connection, "SELECT status FROM operation_runs WHERE id='crashed';"), "crashed run recovered as interrupted");
            }
        }

        private static void TestAtomicOperationWorkflow(string root)
        {
            string testRoot = Path.Combine(root, "atomic");
            Directory.CreateDirectory(testRoot);
            string protocolPath = Path.Combine(testRoot, "protocol.json");
            string rulesPath = Path.Combine(testRoot, "rules.json");
            string luaPath = Path.Combine(testRoot, "packet.lua");
            string operationsPath = Path.Combine(testRoot, "operations.json");
            string profilePath = Path.Combine(testRoot, "profile.json");
            File.WriteAllText(protocolPath, JsonConvert.SerializeObject(CreateLengthPrefixDefinition()));
            File.WriteAllText(rulesPath, "{\"rules\":[]}");
            File.WriteAllText(luaPath, "function on_frame(ctx) return nil end\nfunction main(api) return true end\n");
            File.WriteAllText(operationsPath, "{\"version\":1,\"operations\":[]}");
            File.WriteAllText(profilePath, JsonConvert.SerializeObject(new
            {
                name = "atomic",
                gameUrl = "about:blank",
                activeMode = false,
                captureDirectory = "sessions",
                protocolPath = "protocol.json",
                rulesPath = "rules.json",
                packetScriptPath = "packet.lua",
                operationsPath = "operations.json",
                operationRecorderEnabled = false,
                proxyPorts = new int[0],
                policyPorts = new[] { 843 },
                scriptTimeoutMs = 50,
                captureQueueCapacity = 4096
            }));

            ProtocolWorkbenchService service = new ProtocolWorkbenchService(WorkbenchProfile.Load(profilePath, testRoot));
            AtomicOperationDefinition definition = new AtomicOperationDefinition
            {
                Name = "打开背包",
                Module = "背包",
                Purpose = "记录打开背包请求与响应"
            };
            service.Operations.SaveDefinition(definition);
            service.SelectOperationDefinition(definition);
            service.SetOperationRecorderEnabled(true, false);
            AssertEqual(OperationRecordingState.Ready, service.OperationRecorder.State, "recorder ready state");
            using (AtomicOperationControl control = new AtomicOperationControl(service))
            {
                Assert(control.Controls.Count > 0, "atomic operation UI constructed");
            }

            ConnectionSession game = new ConnectionSession
            {
                Id = 1,
                SocketHandle = new IntPtr(1),
                Kind = ConnectionKind.Game,
                OpenedUtc = DateTime.UtcNow,
                State = "Connected",
                RemoteEndPoint = "127.0.0.1:7800",
                RemotePort = 7800
            };
            ConnectionSession proxy = new ConnectionSession
            {
                Id = 2,
                SocketHandle = new IntPtr(2),
                Kind = ConnectionKind.HttpProxy,
                OpenedUtc = DateTime.UtcNow,
                State = "Connected",
                RemoteEndPoint = "127.0.0.1:7890",
                RemotePort = 7890
            };
            service.RecordConnection(game, "Connected");
            service.RecordConnection(proxy, "Connected");

            service.RecordChunk(CreateChunk(1, 0x10, 0x01));
            AtomicOperationRun firstActive = service.StartAtomicOperation();
            AssertEqual(OperationRecordingState.Recording, service.OperationRecorder.State, "recorder recording state");
            AssertThrows(delegate { service.StartAtomicOperation(); }, "parallel operation recording rejected");
            service.RecordChunk(CreateChunk(2, 0x55, 0x01));
            service.RecordChunk(CreateChunk(1, 0x20, 0x01));
            service.MarkAtomicOperationStep(null, null);
            service.RecordChunk(CreateChunk(1, 0x30, 0x02));
            service.StopAtomicOperation(OperationOutcome.Succeeded, "背包面板已打开", "first sample");
            AssertEqual(OperationRecordingState.Ready, service.OperationRecorder.State, "recorder returned to ready");
            service.RecordChunk(CreateChunk(1, 0x40, 0x03));

            service.StartAtomicOperation();
            service.RecordChunk(CreateChunk(1, 0x21, 0x01));
            service.RecordChunk(CreateChunk(1, 0x31, 0x02));
            service.StopAtomicOperation(OperationOutcome.Succeeded, "背包面板已打开", "second sample");

            string originalDatabase = service.DatabasePath;
            SessionDatabaseRenameResult liveRename = service.RenameSessionDatabase(originalDatabase, "打开背包-成功");
            Assert(liveRename.CurrentSession, "live session rename is identified");
            AssertEqual("打开背包-成功", SqliteCaptureStore.ExtractSemanticDatabaseName(liveRename.NewPath), "semantic suffix extracted");
            AssertEqual("选择角色-进入游戏", SqliteCaptureStore.ExtractSemanticDatabaseName("session-20260831-103808-457选择角色-进入游戏.sqlite"), "legacy suffix without separator extracted");
            Assert(File.Exists(liveRename.NewPath), "renamed live database exists");
            Assert(!File.Exists(originalDatabase), "old live database path removed");
            service.RecordChunk(CreateChunk(1, 0x41, 0x03));

            IList<AtomicOperationRun> runs = service.Operations.GetRuns(definition.Id);
            AssertEqual(2, runs.Count, "two operation runs");
            AtomicOperationRun first = FindRun(runs, firstActive.Id);
            IList<OperationFrameSample> firstFrames = service.Operations.GetFrames(first, true);
            AssertEqual(2, firstFrames.Count, "strict boundary and game-only frame count");
            AssertEqual(1, service.Operations.GetSteps(first).Count, "step marker count");
            Assert(service.Operations.GetConnectionCandidates(first).Any(item => item.Connection.Id == 2 && !item.Included), "proxy is available but excluded");

            service.Operations.SaveFrameAnnotation(first, new FrameSemanticAnnotation
            {
                FrameId = firstFrames[1].FrameId,
                Role = FrameSemanticRole.Response,
                Meaning = "背包响应",
                Confirmed = true
            });
            AssertEqual("背包响应", service.Operations.GetFrames(first, true)[1].Meaning, "frame semantic annotation");

            OperationComparison comparison = service.CompareOperations(runs);
            AssertEqual(2, comparison.Frames.Count, "aligned comparison frame count");
            Assert(comparison.Frames.Any(item => item.DynamicMaskHex.Contains("FF")), "dynamic byte mask detected");

            string bundle = Path.Combine(testRoot, "bag.tsqop.sqlite");
            service.ExportOperationBundle(runs, comparison, bundle);
            Assert(File.Exists(bundle), "operation bundle exists");
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + bundle + ";Version=3;"))
            {
                connection.Open();
                AssertEqual(2L, ExecuteScalarLong(connection, "SELECT COUNT(*) FROM bundle_operation_runs;"), "bundle run count");
                AssertEqual(4L, ExecuteScalarLong(connection, "SELECT COUNT(*) FROM bundle_frames;"), "bundle frame count");
                AssertEqual(4L, ExecuteScalarLong(connection, "SELECT COUNT(*) FROM bundle_chunks;"), "bundle chunk count");
                AssertEqual(1L, ExecuteScalarLong(connection, "SELECT COUNT(*) FROM bundle_connections;"), "only included game connection exported");
                AssertEqual(0L, ExecuteScalarLong(connection, "SELECT COUNT(*) FROM bundle_connections WHERE remote_port=7890;"), "excluded proxy not exported");
                AssertEqual(1L, ExecuteScalarLong(connection, "SELECT COUNT(*) FROM bundle_frame_annotations WHERE meaning='背包响应';"), "bundle annotation");
                AssertEqual(1L, ExecuteScalarLong(connection, "SELECT COUNT(*) FROM bundle_manifest WHERE key='credentials_included' AND value='false';"), "credential exclusion manifest");
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT original_bytes,effective_bytes FROM bundle_chunks ORDER BY capture_ordinal LIMIT 1;";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        Assert(reader.Read(), "bundle chunk bytes readable");
                        AssertEqual("00 06 00 01 20 7F", HexCodec.Format((byte[])reader[0]), "bundle original bytes");
                        AssertEqual("00 06 00 01 20 7F", HexCodec.Format((byte[])reader[1]), "bundle effective bytes");
                    }
                }
            }

            AtomicOperationDefinition concurrentDefinition = new AtomicOperationDefinition { Name = "并发边界", Module = "测试" };
            service.Operations.SaveDefinition(concurrentDefinition);
            service.SelectOperationDefinition(concurrentDefinition);
            service.RecordChunk(CreateChunk(1, 0x01, 0x03));
            service.StartAtomicOperation();
            List<Thread> producers = new List<Thread>();
            for (int producerIndex = 0; producerIndex < 4; producerIndex++)
            {
                int capturedIndex = producerIndex;
                Thread producer = new Thread(new ThreadStart(delegate
                {
                    for (int i = 0; i < 100; i++)
                    {
                        service.RecordChunk(CreateChunk(1, (byte)(capturedIndex + i), 0x03));
                    }
                }));
                producers.Add(producer);
                producer.Start();
            }
            foreach (Thread producer in producers) producer.Join();
            service.StopAtomicOperation(OperationOutcome.Succeeded, "concurrent", null);
            service.RecordChunk(CreateChunk(1, 0x02, 0x03));
            AtomicOperationRun concurrentRun = service.Operations.GetRuns(concurrentDefinition.Id).Single();
            IList<OperationFrameSample> concurrentFrames = service.Operations.GetFrames(concurrentRun, true);
            AssertEqual(400, concurrentFrames.Count, "concurrent strict boundary frame count");
            AssertEqual(400, concurrentFrames.Select(item => item.FrameId).Distinct().Count(), "concurrent frames linked exactly once");
            service.Dispose();

            ProtocolWorkbenchService reopened = new ProtocolWorkbenchService(WorkbenchProfile.Load(profilePath, testRoot));
            SessionDatabaseRenameResult historicalRename = reopened.RenameSessionDatabase(liveRename.NewPath, "打开背包-历史样本");
            Assert(!historicalRename.CurrentSession, "closed session rename is identified");
            Assert(File.Exists(historicalRename.NewPath), "renamed historical database exists");
            Assert(!File.Exists(liveRename.NewPath), "old historical database path removed");
            Assert(reopened.Operations.GetDefinitions().Any(item => item.Id == definition.Id), "saved template survives service restart");
            reopened.Dispose();
        }

        private static void TestPersistentOperationTemplates(string root)
        {
            string testRoot = Path.Combine(root, "persistent-templates");
            string protocolDirectory = Path.Combine(testRoot, "protocols", "v5");
            Directory.CreateDirectory(protocolDirectory);
            File.WriteAllText(Path.Combine(protocolDirectory, "protocol.json"), JsonConvert.SerializeObject(CreateLengthPrefixDefinition()));
            File.WriteAllText(Path.Combine(protocolDirectory, "rules.json"), "{\"rules\":[]}");
            File.WriteAllText(Path.Combine(protocolDirectory, "packet.lua"), "function on_frame(ctx) return nil end\nfunction main(api) return true end\n");
            string legacyCatalog = Path.Combine(protocolDirectory, "operations.json");
            File.WriteAllText(legacyCatalog, JsonConvert.SerializeObject(new OperationCatalogDocument
            {
                Operations = new List<AtomicOperationDefinition>
                {
                    new AtomicOperationDefinition { Id = "legacy-template", Name = "旧模板", Module = "迁移" }
                }
            }));
            string profilePath = Path.Combine(testRoot, "profile.json");
            File.WriteAllText(profilePath, JsonConvert.SerializeObject(new
            {
                name = "v5",
                gameUrl = "about:blank",
                activeMode = false,
                captureDirectory = "sessions",
                protocolPath = "protocols/v5/protocol.json",
                rulesPath = "protocols/v5/rules.json",
                packetScriptPath = "protocols/v5/packet.lua",
                operationsPath = "protocols/v5/operations.json",
                operationRecorderEnabled = false,
                proxyPorts = new int[0],
                policyPorts = new int[0],
                scriptTimeoutMs = 50,
                captureQueueCapacity = 4096
            }));

            ProtocolWorkbenchService first = new ProtocolWorkbenchService(WorkbenchProfile.Load(profilePath, testRoot));
            Assert(first.Operations.GetDefinitions().Any(item => item.Id == "legacy-template"), "legacy template migrated");
            AtomicOperationDefinition added = new AtomicOperationDefinition { Name = "持久模板", Module = "测试" };
            first.Operations.SaveDefinition(added);
            first.Dispose();

            string persistentCatalog = Path.Combine(testRoot, "data", "settings", "operation-templates.json");
            Assert(File.Exists(persistentCatalog), "persistent template catalog created");
            File.WriteAllText(legacyCatalog, "{\"version\":1,\"operations\":[]}");
            ProtocolWorkbenchService second = new ProtocolWorkbenchService(WorkbenchProfile.Load(profilePath, testRoot));
            IList<AtomicOperationDefinition> persisted = second.Operations.GetDefinitions();
            Assert(persisted.Any(item => item.Id == "legacy-template"), "migrated template survives legacy overwrite");
            Assert(persisted.Any(item => item.Id == added.Id), "new template survives service restart");
            second.Dispose();
        }

        private static void TestLoginProtocol()
        {
            byte[] tokenBytes = Enumerable.Range(0, 48).Select(value => (byte)value).ToArray();
            List<byte> tokenFrame = StartFrame(TianshuLoginProtocol.ServerClientToken);
            AppendString(tokenFrame, Convert.ToBase64String(tokenBytes));
            byte[] finishedTokenFrame = FinishFrame(tokenFrame);
            string encoded;
            byte[] decoded;
            Assert(TianshuLoginProtocol.TryParseClientToken(finishedTokenFrame, out encoded, out decoded), "Base64 ticket parses");
            AssertEqual(48, decoded.Length, "decoded ticket length");
            AssertEqual(Convert.ToBase64String(tokenBytes), encoded, "ticket text preserved");

            TransportChunk serverTicketChunk = new TransportChunk
            {
                Direction = TrafficDirection.ServerToClient,
                OriginalBytes = (byte[])finishedTokenFrame.Clone(),
                EffectiveBytes = (byte[])finishedTokenFrame.Clone()
            };
            Assert(SensitiveTrafficRedactor.RedactLoginSecrets(serverTicketChunk,
                new ConnectionSession { Kind = ConnectionKind.Game, RemotePort = 7800 }), "server ticket is redacted");
            Assert(!Encoding.ASCII.GetString(serverTicketChunk.EffectiveBytes).Contains(encoded),
                "server capture contains no replayable ticket");

            List<byte> clientTicketFrame = StartFrame(TianshuLoginProtocol.ClientUserToken2);
            AppendString(clientTicketFrame, encoded);
            AppendUInt32(clientTicketFrame, 0);
            AppendUInt32(clientTicketFrame, 0);
            AppendString(clientTicketFrame, "client-fingerprint");
            TransportChunk clientTicketChunk = new TransportChunk
            {
                Direction = TrafficDirection.ClientToServer,
                OriginalBytes = FinishFrame(clientTicketFrame),
                EffectiveBytes = FinishFrame(clientTicketFrame)
            };
            Assert(SensitiveTrafficRedactor.RedactLoginSecrets(clientTicketChunk,
                new ConnectionSession { Kind = ConnectionKind.Game, RemotePort = 7804 }), "client ticket is redacted");
            Assert(!Encoding.ASCII.GetString(clientTicketChunk.EffectiveBytes).Contains(encoded),
                "client capture contains no replayable ticket");

            List<byte> listFrame = StartFrame(TianshuLoginProtocol.ServerGameServerList);
            AppendUInt32(listFrame, 5);
            AppendServer(listFrame, "9053", "春山如笑三线", "127.0.0.1", "7810", 4);
            AppendServer(listFrame, "9051", "春山如笑一线", "127.0.0.1", "7804,7805,7806,7807", 2);
            AppendServer(listFrame, "9052", "春山如笑二线", "127.0.0.1", "7808,7809", 2);
            AppendServer(listFrame, "9053", "春山如笑三线", "127.0.0.1", "7810", 2);
            AppendServer(listFrame, "9054", "春山如笑四线", "127.0.0.1", "7811", 2);
            IList<TianshuGameServer> servers;
            string error;
            Assert(TianshuLoginProtocol.TryParseGameServerList(FinishFrame(listFrame), out servers, out error), "server list parses: " + error);
            AssertEqual(5, servers.Count, "server list count");
            AssertEqual(1, TianshuLoginProtocol.FindPhysicalLine(servers, 1).PacketIndex, "line one packet index");
            AssertEqual(3, TianshuLoginProtocol.FindPhysicalLine(servers, 3).PacketIndex, "physical line preferred over recommendation");
            AssertEqual(7811, TianshuLoginProtocol.FindPhysicalLine(servers, 4).Ports[0], "line four port");

            List<byte> roleListFrame = StartFrame(TianshuLoginProtocol.ServerRoleInfoList);
            AppendUInt16(roleListFrame, 1);
            AppendUInt32(roleListFrame, 0x10203040);
            AppendString(roleListFrame, "测试角色");
            AppendString(roleListFrame, "男");
            AppendString(roleListFrame, "修真");
            AppendUInt32(roleListFrame, 137);
            AppendString(roleListFrame, "126000");
            AppendString(roleListFrame, "136002");
            AppendString(roleListFrame, "0");
            AppendString(roleListFrame, string.Empty);
            uint[] roleAttributes = { 2799, 1651, 32, 62, 55, 204, 244, 2191, 536, 573, 146, 147 };
            foreach (uint attribute in roleAttributes) AppendUInt32(roleListFrame, attribute);
            AppendString(roleListFrame, "测试称号");
            AppendUInt16(roleListFrame, 0);
            IList<TianshuRoleSummary> roles;
            Assert(TianshuLoginProtocol.TryParseRoleInfoList(FinishFrame(roleListFrame), out roles, out error),
                "role list parses: " + error);
            AssertEqual(1, roles.Count, "role list count");
            AssertEqual(0x10203040, roles[0].CharacterId, "role character id");
            AssertEqual(137, roles[0].Level, "role level");
            AssertEqual(2191, roles[0].PhysicalAttack, "role physical attack");
            Assert(!roles[0].IsPendingDeletion, "active role status");

            List<byte> selectRoleFrame = StartFrame(TianshuLoginProtocol.ClientSelectRole);
            AppendUInt32(selectRoleFrame, 0x10203040);
            int selectedCharacterId;
            Assert(TianshuLoginProtocol.TryParseSelectRole(FinishFrame(selectRoleFrame), out selectedCharacterId),
                "select role request parses");
            AssertEqual(0x10203040, selectedCharacterId, "selected character id");

            const string user = "private-user";
            const string password = "private-password";
            List<byte> loginFrame = StartFrame(TianshuLoginProtocol.ClientUserPassword);
            AppendString(loginFrame, user);
            AppendString(loginFrame, password);
            byte[] loginBytes = FinishFrame(loginFrame);
            TransportChunk chunk = new TransportChunk
            {
                Direction = TrafficDirection.ClientToServer,
                OriginalBytes = (byte[])loginBytes.Clone(),
                EffectiveBytes = (byte[])loginBytes.Clone()
            };
            Assert(SensitiveTrafficRedactor.RedactLoginCredentials(chunk,
                new ConnectionSession { RemotePort = 7800 }), "login frame is redacted");
            AssertEqual(loginBytes.Length, chunk.EffectiveBytes.Length, "redaction preserves frame length");
            AssertEqual(TianshuLoginProtocol.ClientUserPassword, (chunk.EffectiveBytes[2] << 8) | chunk.EffectiveBytes[3], "redaction preserves opcode");
            string capturedText = Encoding.UTF8.GetString(chunk.EffectiveBytes);
            Assert(!capturedText.Contains(user) && !capturedText.Contains(password), "capture contains no credentials");
        }

        private static void TestLoginCredentialStore(string root)
        {
            string path = Path.Combine(root, "credentials", "auto-login.json");
            LoginCredentialStore store = new LoginCredentialStore(path);
            LoginAutomationSettings settings = new LoginAutomationSettings
            {
                Enabled = true,
                Username = "test-account",
                LineNumber = 4,
                RoleSlot = 3
            };
            const string password = "not-a-real-password";
            store.Save(settings, password);
            string json = File.ReadAllText(path);
            Assert(!json.Contains(password), "credential file must not contain plaintext password");
            LoginAutomationSettings loaded = store.Load();
            AssertEqual(2, loaded.Version, "credential schema version");
            Assert(loaded.HasSavedPassword, "encrypted password stored");
            AssertEqual(password, store.ReadPassword(loaded), "DPAPI password roundtrip");
            AssertEqual(4, loaded.LineNumber, "line selection roundtrip");
            AssertEqual(3, loaded.RoleSlot, "role slot roundtrip");
            store.ClearPassword(loaded);
            Assert(!store.Load().HasSavedPassword, "saved password cleared");
        }

        private static void TestMultiAccountIsolation(string root)
        {
            string baseDirectory = Path.Combine(root, "multi-account");
            Directory.CreateDirectory(baseDirectory);
            AccountProfileStore accounts = new AccountProfileStore(baseDirectory);
            accounts.EnsureInitialized(null);
            Assert(accounts.GetAccount(AccountProfileStore.DefaultAccountId) != null, "default account is created");

            LauncherAccountProfile first = accounts.CreateAccount("测试甲");
            LauncherAccountProfile second = accounts.CreateAccount("测试乙");
            LoginCredentialStore firstCredentials = new LoginCredentialStore(accounts.GetCredentialPath(first.Id));
            LoginCredentialStore secondCredentials = new LoginCredentialStore(accounts.GetCredentialPath(second.Id));
            firstCredentials.Save(new LoginAutomationSettings
            {
                Enabled = true,
                Username = "account-a",
                LineNumber = 1,
                RoleSlot = 2
            }, "password-a");
            secondCredentials.Save(new LoginAutomationSettings
            {
                Enabled = true,
                Username = "account-b",
                LineNumber = 4,
                RoleSlot = 5
            }, "password-b");
            AssertEqual("password-a", firstCredentials.ReadPassword(firstCredentials.Load()), "first account password");
            AssertEqual("password-b", secondCredentials.ReadPassword(secondCredentials.Load()), "second account password");
            Assert(accounts.GetCredentialPath(first.Id) != accounts.GetCredentialPath(second.Id), "credential files are account scoped");

            string firstInstanceId = Guid.NewGuid().ToString("N");
            string secondInstanceId = Guid.NewGuid().ToString("N");
            ClientInstanceContext firstContext = ClientInstanceContext.Parse(new[]
            {
                "--account", first.Id, "--instance", firstInstanceId, "--window-slot", "1"
            }, baseDirectory, accounts);
            ClientInstanceContext secondContext = ClientInstanceContext.Parse(new[]
            {
                "--account", second.Id, "--instance", secondInstanceId, "--window-slot", "2"
            }, baseDirectory, accounts);
            Assert(firstContext.ManagedLaunch && secondContext.ManagedLaunch, "managed instances are detected");
            Assert(firstContext.InstanceRoot != secondContext.InstanceRoot, "instance roots are unique");
            Assert(firstContext.CredentialPath != secondContext.CredentialPath, "instance credentials stay account scoped");

            string protocolDirectory = Path.Combine(baseDirectory, "protocols", "v5");
            Directory.CreateDirectory(Path.Combine(protocolDirectory, "scripts"));
            File.WriteAllText(Path.Combine(protocolDirectory, "protocol.json"), "{\"version\":1}");
            File.WriteAllText(Path.Combine(protocolDirectory, "rules.json"), "[]");
            File.WriteAllText(Path.Combine(protocolDirectory, "scripts", "packet.lua"), "function main() end");
            string settingsDirectory = Path.Combine(baseDirectory, "data", "settings");
            Directory.CreateDirectory(settingsDirectory);
            File.WriteAllText(Path.Combine(settingsDirectory, "operation-templates.json"), "{\"version\":1,\"templates\":[]}");
            string profileDirectory = Path.Combine(baseDirectory, "profiles");
            Directory.CreateDirectory(profileDirectory);
            string profilePath = Path.Combine(profileDirectory, "v5.json");
            File.WriteAllText(profilePath, JsonConvert.SerializeObject(new WorkbenchProfile(), Formatting.Indented));
            WorkbenchProfile source = WorkbenchProfile.Load(profilePath, baseDirectory);
            string firstOperationCatalog = Path.Combine(accounts.GetAccountDirectory(first.Id), "operation-templates.json");
            string secondOperationCatalog = Path.Combine(accounts.GetAccountDirectory(second.Id), "operation-templates.json");
            WorkbenchProfile firstRuntime = source.CreateIsolatedRuntime(
                firstContext.InstanceRoot, firstContext.ShortInstanceId, firstOperationCatalog);
            WorkbenchProfile secondRuntime = source.CreateIsolatedRuntime(
                secondContext.InstanceRoot, secondContext.ShortInstanceId, secondOperationCatalog);
            Assert(firstRuntime.CaptureDirectory != secondRuntime.CaptureDirectory, "capture directories are instance scoped");
            Assert(firstRuntime.OperationsPath != secondRuntime.OperationsPath, "operation templates are account scoped");
            Assert(firstRuntime.ProtocolPath != secondRuntime.ProtocolPath, "protocol definitions are instance scoped");
            Assert(File.Exists(firstRuntime.ProtocolPath) && File.Exists(secondRuntime.ProtocolPath), "runtime protocol copies exist");
            File.WriteAllText(firstRuntime.RulesPath, "[{\"name\":\"first-only\"}]");
            Assert(!File.ReadAllText(secondRuntime.RulesPath).Contains("first-only"), "rule edits do not cross instances");
            Assert(!File.ReadAllText(source.Resolve(source.RulesPath)).Contains("first-only"), "rule edits do not change source profile");
            AssertEqual(firstOperationCatalog, firstRuntime.OperationsPath, "operation templates persist with the account");

            string recordPath = Path.Combine(firstContext.InstanceRoot, "instance.json");
            using (ClientInstanceLease lease = new ClientInstanceLease(firstContext))
            {
                ClientInstanceRecord live = JsonConvert.DeserializeObject<ClientInstanceRecord>(File.ReadAllText(recordPath));
                Assert(!live.EndedUtc.HasValue, "live instance record has no end time");
                AssertEqual(first.Id, live.AccountId, "instance record account");
            }
            ClientInstanceRecord ended = JsonConvert.DeserializeObject<ClientInstanceRecord>(File.ReadAllText(recordPath));
            Assert(ended.EndedUtc.HasValue, "disposed instance record is ended");

            MultiAccountManager manager = new MultiAccountManager(
                baseDirectory, Process.GetCurrentProcess().MainModule.FileName, profilePath, accounts, firstContext);
            ClientLogHub multiAccountLog = new ClientLogHub();
            using (MultiAccountControl control = new MultiAccountControl(manager, multiAccountLog))
            {
                control.CreateControl();
                Assert(control.Controls.Count > 0, "multi-account management UI is constructed");
                Button newButton = FindControls<Button>(control).First(item => item.Text == "新建");
                newButton.PerformClick();
                Assert(multiAccountLog.Snapshot().Any(item => item.Module == "多开管理"),
                    "multi-account UI status is published to the unified log");
                Assert(!multiAccountLog.Snapshot().Any(item => item.Message.Contains("password-a") ||
                    item.Message.Contains("password-b")), "multi-account logs contain no stored password");
            }

            string catalogJson = File.ReadAllText(Path.Combine(accounts.RootDirectory, "accounts.json"));
            Assert(!catalogJson.Contains("password-a") && !catalogJson.Contains("password-b"), "catalog contains no plaintext passwords");
            bool invalidRejected = false;
            try
            {
                ClientInstanceContext.Parse(new[] { "--account", "..\\escape" }, baseDirectory, accounts);
            }
            catch (ArgumentException)
            {
                invalidRejected = true;
            }
            Assert(invalidRejected, "path traversal account id is rejected");
        }

        private static List<byte> StartFrame(int opcode)
        {
            return new List<byte> { 0, 0, (byte)(opcode >> 8), (byte)opcode };
        }

        private static byte[] FinishFrame(List<byte> frame)
        {
            frame[0] = (byte)(frame.Count >> 8);
            frame[1] = (byte)frame.Count;
            return frame.ToArray();
        }

        private static void AppendServer(List<byte> bytes, string id, string name, string address, string ports, uint state)
        {
            AppendString(bytes, id);
            AppendString(bytes, name);
            AppendString(bytes, address);
            AppendString(bytes, ports);
            AppendUInt32(bytes, state);
        }

        private static void AppendString(List<byte> bytes, string value)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(value);
            AppendUInt16(bytes, encoded.Length);
            bytes.AddRange(encoded);
        }

        private static void AppendUInt16(List<byte> bytes, int value)
        {
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }

        private static void AppendUInt32(List<byte> bytes, uint value)
        {
            bytes.Add((byte)(value >> 24));
            bytes.Add((byte)(value >> 16));
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }

        private static TransportChunk CreateChunk(long connectionId, byte payload, byte opcode)
        {
            byte[] bytes = new byte[] { 0, 6, 0, opcode, payload, 0x7F };
            return new TransportChunk
            {
                ConnectionId = connectionId,
                TimestampUtc = DateTime.UtcNow,
                Direction = TrafficDirection.ClientToServer,
                Operation = TransportOperation.Send,
                OriginalBytes = (byte[])bytes.Clone(),
                EffectiveBytes = (byte[])bytes.Clone(),
                NativeResult = bytes.Length
            };
        }

        private static void TestAudioFilterProxy(string root)
        {
            byte[] wav = CreateTestWave();
            TcpListener upstream = new TcpListener(IPAddress.Loopback, 0);
            upstream.Start();
            int upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
            Exception serverError = null;
            Thread serverThread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    using (TcpClient accepted = upstream.AcceptTcpClient())
                    using (NetworkStream stream = accepted.GetStream())
                    {
                        ReadHttpHeader(stream);
                        byte[] header = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: " + wav.Length +
                            "\r\nConnection: close\r\n\r\n");
                        stream.Write(header, 0, header.Length);
                        stream.Write(wav, 0, wav.Length);
                    }
                }
                catch (Exception ex) { serverError = ex; }
            }));
            serverThread.IsBackground = true;
            serverThread.Start();

            string cache = Path.Combine(root, "audio-cache");
            AudioFilterProxyOptions options = new AudioFilterProxyOptions
            {
                SoundHost = "127.0.0.1",
                SoundPathPrefix = "/sound",
                CacheDirectory = cache,
                BitrateKbps = 0,
                Mp3Quality = 2,
                UpstreamTimeoutMs = 30000
            };
            using (AudioFilterProxy proxy = new AudioFilterProxy(options))
            {
                proxy.Start();
                Assert(proxy.FfmpegAvailable, "FFmpeg must be available for realtime filtering");
                byte[] first = RequestThroughProxy(proxy.Port, upstreamPort);
                serverThread.Join(30000);
                upstream.Stop();
                Assert(serverError == null, "test BGM origin server failed: " + serverError);
                Assert(Encoding.ASCII.GetString(first, 0, Math.Min(first.Length, 128)).Contains("200 OK"), "filtered response status");
                Assert(Encoding.ASCII.GetString(first, 0, Math.Min(first.Length, 256)).Contains("audio/mpeg"), "filtered response type");
                int firstBody = FindHttpBodyOffset(first);
                Assert(firstBody > 0 && first.Length - firstBody > 1000, "filtered MP3 body");
                AssertEqual(1L, proxy.ProxyRequests, "one HTTP proxy request");
                AssertEqual(1L, proxy.SoundRequests, "one sound request was detected");
                AssertEqual(1L, proxy.FilteredRequests, "one source was filtered");
                AssertEqual(1, Directory.GetFiles(cache, "*.mp3").Length, "filtered cache file");
                string[] originals = Directory.GetFiles(Path.Combine(cache, "original"), "*.audio");
                AssertEqual(1, originals.Length, "original source is preserved for diagnostics");
                Assert(wav.SequenceEqual(File.ReadAllBytes(originals[0])), "preserved source is byte-exact");
                string filteredPath = Directory.GetFiles(cache, "*.mp3")[0];
                IDictionary<string, string> probe = ProbeAudio(filteredPath);
                AssertEqual("22050", probe["sample_rate"], "source sample rate is preserved");
                AssertEqual("1", probe["channels"], "source channel layout is preserved");

                byte[] second = RequestThroughProxy(proxy.Port, upstreamPort);
                int secondBody = FindHttpBodyOffset(second);
                Assert(secondBody > 0 && second.Length - secondBody == first.Length - firstBody, "cached MP3 response body");
                AssertEqual(2L, proxy.SoundRequests, "cached request is still counted as sound");
                AssertEqual(1L, proxy.CacheHits, "second request used cache without origin");

                TcpListener tunnelOrigin = new TcpListener(IPAddress.Loopback, 0);
                tunnelOrigin.Start();
                int tunnelPort = ((IPEndPoint)tunnelOrigin.LocalEndpoint).Port;
                Thread tunnelServer = new Thread(new ThreadStart(delegate
                {
                    using (TcpClient accepted = tunnelOrigin.AcceptTcpClient())
                    {
                        int value = accepted.GetStream().ReadByte();
                        accepted.GetStream().WriteByte((byte)(value + 1));
                    }
                }));
                tunnelServer.IsBackground = true;
                tunnelServer.Start();
                using (TcpClient tunnelClient = new TcpClient())
                {
                    tunnelClient.Connect(IPAddress.Loopback, proxy.Port);
                    NetworkStream tunnelStream = tunnelClient.GetStream();
                    byte[] connect = Encoding.ASCII.GetBytes(
                        "CONNECT 127.0.0.1:" + tunnelPort + " HTTP/1.1\r\nHost: 127.0.0.1:" + tunnelPort + "\r\n\r\n");
                    tunnelStream.Write(connect, 0, connect.Length);
                    ReadHttpHeader(tunnelStream);
                    tunnelStream.WriteByte(0x32);
                    AssertEqual(0x33, tunnelStream.ReadByte(), "HTTP CONNECT tunnel roundtrip");
                }
                tunnelServer.Join(5000);
                tunnelOrigin.Stop();
                AssertEqual(3L, proxy.ProxyRequests, "CONNECT request counted by proxy");
            }
        }

        private static byte[] RequestThroughProxy(int proxyPort, int upstreamPort)
        {
            using (TcpClient client = new TcpClient())
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                client.Connect(IPAddress.Loopback, proxyPort);
                using (NetworkStream stream = client.GetStream())
                using (MemoryStream response = new MemoryStream())
                {
                    byte[] request = Encoding.ASCII.GetBytes(
                        "GET /assets/music?id=no-extension HTTP/1.1\r\nHost: 127.0.0.1:" + upstreamPort +
                        "\r\nAccept: */*\r\nConnection: close\r\n\r\n");
                    stream.Write(request, 0, request.Length);
                    byte[] buffer = new byte[32768];
                    int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) response.Write(buffer, 0, count);
                    return response.ToArray();
                }
            }
        }

        private static void ReadHttpHeader(Stream stream)
        {
            int matched = 0;
            byte[] marker = { 13, 10, 13, 10 };
            while (matched < marker.Length)
            {
                int value = stream.ReadByte();
                if (value < 0) throw new EndOfStreamException("HTTP request header ended early.");
                matched = value == marker[matched] ? matched + 1 : (value == marker[0] ? 1 : 0);
            }
        }

        private static int FindHttpBodyOffset(byte[] response)
        {
            for (int i = 0; i <= response.Length - 4; i++)
            {
                if (response[i] == 13 && response[i + 1] == 10 && response[i + 2] == 13 && response[i + 3] == 10)
                    return i + 4;
            }
            return -1;
        }

        private static byte[] CreateTestWave()
        {
            const int sampleRate = 22050;
            const int seconds = 2;
            int sampleCount = sampleRate * seconds;
            using (MemoryStream memory = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(memory))
            {
                int dataLength = sampleCount * 2;
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataLength);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataLength);
                for (int i = 0; i < sampleCount; i++)
                {
                    double time = (double)i / sampleRate;
                    double music = Math.Sin(2 * Math.PI * 440 * time) * 9000;
                    double noise = Math.Sin(2 * Math.PI * 18000 * time) * 2500;
                    writer.Write((short)Math.Max(short.MinValue, Math.Min(short.MaxValue, music + noise)));
                }
                writer.Flush();
                return memory.ToArray();
            }
        }

        private static IDictionary<string, string> ProbeAudio(string path)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = "ffprobe";
            start.Arguments = "-v error -select_streams a:0 -show_entries stream=sample_rate,channels " +
                "-of default=noprint_wrappers=1 \"" + path.Replace("\"", "\\\"") + "\"";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                Assert(process.WaitForExit(10000) && process.ExitCode == 0, "ffprobe failed: " + error);
                Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int separator = line.IndexOf('=');
                    if (separator > 0) values[line.Substring(0, separator)] = line.Substring(separator + 1);
                }
                return values;
            }
        }

        private static AtomicOperationRun FindRun(IList<AtomicOperationRun> runs, string id)
        {
            foreach (AtomicOperationRun run in runs) if (run.Id == id) return run;
            throw new InvalidOperationException("Operation run not found: " + id);
        }

        private static long ExecuteScalarLong(SQLiteConnection connection, string sql)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static bool HasColumn(SQLiteConnection connection, string table, string column)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(" + table + ");";
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        private static ProtocolDefinition CreateLengthPrefixDefinition()
        {
            return new ProtocolDefinition
            {
                Framing = new FramingDefinition
                {
                    Mode = FramingMode.LengthPrefix,
                    LengthOffset = 0,
                    LengthSize = 2,
                    LengthEndian = ByteOrder.Big,
                    LengthIncludesHeader = true,
                    MaximumFrameLength = 4096
                },
                OpcodeOffset = 2,
                OpcodeSize = 2,
                OpcodeEndian = ByteOrder.Big
            };
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(message + ": expected=" + expected + " actual=" + actual);
            }
        }

        private static void AssertThrows(Action action, string message)
        {
            try
            {
                action();
            }
            catch
            {
                return;
            }
            throw new InvalidOperationException(message);
        }
    }
}
