using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
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
                string bountyRecording = Environment.GetEnvironmentVariable("TIANSHU_BOUNTY_SAMPLE");
                if (!string.IsNullOrWhiteSpace(bountyRecording))
                    Run("Recorded bounty SQLite replay", delegate { TestRecordedBounty(bountyRecording); });
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
            using (MultiAccountControl control = new MultiAccountControl(manager))
            {
                control.CreateControl();
                Assert(control.Controls.Count > 0, "multi-account management UI is constructed");
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
