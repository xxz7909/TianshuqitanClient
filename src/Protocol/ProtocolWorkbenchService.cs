using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class ProtocolWorkbenchService : IDisposable
    {
        private readonly object syncRoot = new object();
        private readonly object waitRoot = new object();
        private readonly Dictionary<string, FrameStream> streams = new Dictionary<string, FrameStream>();
        private readonly Dictionary<long, ConnectionSession> connections = new Dictionary<long, ConnectionSession>();
        private readonly WorkbenchProfile profile;
        private readonly ProtocolDefinition definition;
        private readonly GenericFrameDecoder decoder;
        private readonly StateTracker stateTracker;
        private readonly LuaScenarioHost luaHost;
        private readonly RuleEngine ruleEngine;
        private readonly SqliteCaptureStore store;
        private readonly PcapngExporter pcapngExporter;
        private readonly OperationRepository operationRepository;
        private readonly AtomicOperationRecorder operationRecorder;
        private readonly OperationAnalyzer operationAnalyzer;
        private readonly OperationBundleExporter operationBundleExporter;
        private ITransportCaptureEngine captureEngine;
        private ProtocolFrame lastFrame;
        private bool activeMode;
        private bool disposed;

        public ProtocolWorkbenchService(WorkbenchProfile profile)
        {
            this.profile = profile;
            activeMode = profile.ActiveMode;
            definition = ProtocolDefinition.Load(profile.Resolve(profile.ProtocolPath));
            decoder = new GenericFrameDecoder(definition);
            stateTracker = new StateTracker(definition);
            luaHost = new LuaScenarioHost(profile.Resolve(profile.PacketScriptPath), profile.ScriptTimeoutMs);
            luaHost.LogProduced += OnLuaLog;
            luaHost.WaitStateRequested = WaitState;
            luaHost.WaitFrameRequested = WaitFrame;
            luaHost.SendRequested = Send;
            luaHost.InjectReceiveRequested = InjectReceive;
            ruleEngine = new RuleEngine(profile.Resolve(profile.RulesPath), luaHost);
            pcapngExporter = new PcapngExporter();

            string captureDirectory = profile.Resolve(profile.CaptureDirectory);
            store = new SqliteCaptureStore(captureDirectory, profile.CaptureQueueCapacity);
            store.StartSession(profile.Name, activeMode);
            string operationsPath = ResolveOperationCatalogPath(profile);
            operationRepository = new OperationRepository(operationsPath, captureDirectory, store);
            operationRecorder = new AtomicOperationRecorder(store, profile.OperationRecorderEnabled, GetStateSnapshot);
            operationRecorder.StateChanged += OnOperationRecordingStateChanged;
            operationRecorder.RunChanged += OnOperationRunChanged;
            operationRecorder.StepAdded += OnOperationStepAdded;
            operationAnalyzer = new OperationAnalyzer(operationRepository);
            operationBundleExporter = new OperationBundleExporter();
        }

        public event Action<ConnectionSession> ConnectionChanged;
        public event Action<TransportChunk> ChunkCaptured;
        public event Action<ProtocolFrame> FrameCaptured;
        public event Action<StateTransition> StateChanged;
        public event Action<WorkbenchEvent> EventRaised;
        public event Action<ScenarioResult> ScenarioCompleted;
        public event Action<bool> ActiveModeChanged;
        public event Action<OperationRecordingState> OperationRecordingStateChanged;
        public event Action<AtomicOperationRun> OperationRunChanged;
        public event Action<OperationStep> OperationStepAdded;
        public event Action<SessionDatabaseRenameResult> SessionDatabaseRenamed;

        public WorkbenchProfile Profile { get { return profile; } }
        public ProtocolDefinition Definition { get { return definition; } }
        public RuleEngine Rules { get { return ruleEngine; } }
        public IStateTracker States { get { return stateTracker; } }
        public string DatabasePath { get { return store.DatabasePath; } }
        public bool ActiveMode { get { lock (syncRoot) { return activeMode; } } }
        public IAtomicOperationRecorder OperationRecorder { get { return operationRecorder; } }
        public IOperationRepository Operations { get { return operationRepository; } }

        public SessionDatabaseRenameResult RenameSessionDatabase(string sourceDatabasePath, string semanticName)
        {
            SessionDatabaseRenameResult result = operationRepository.RenameSessionDatabase(sourceDatabasePath, semanticName);
            if (result.CurrentSession)
            {
                operationRecorder.UpdateSourceDatabasePath(result.OriginalPath, result.NewPath);
            }
            PublishEvent(
                "SessionDatabase",
                "INFO",
                "Session database renamed: " + Path.GetFileName(result.OriginalPath) + " -> " + Path.GetFileName(result.NewPath),
                null);
            Action<SessionDatabaseRenameResult> handler = SessionDatabaseRenamed;
            if (handler != null) handler(result);
            return result;
        }

        public void AttachCaptureEngine(ITransportCaptureEngine engine)
        {
            captureEngine = engine;
        }

        public void StartCapture()
        {
            if (captureEngine == null)
            {
                throw new InvalidOperationException("Capture engine is not attached.");
            }
            captureEngine.Start();
            PublishEvent("Capture", "INFO", "Winsock capture started. Database: " + DatabasePath, null);
        }

        public void StopCapture()
        {
            if (captureEngine != null)
            {
                captureEngine.Stop();
            }
        }

        public void SetActiveMode(bool value, bool persist)
        {
            lock (syncRoot)
            {
                activeMode = value;
            }
            if (persist)
            {
                try
                {
                    profile.ActiveMode = value;
                    profile.Save();
                }
                catch (Exception ex)
                {
                    PublishEvent("Profile", "ERROR", "Cannot persist ACTIVE mode: " + ex.Message, null);
                }
            }
            Action<bool> handler = ActiveModeChanged;
            if (handler != null)
            {
                handler(value);
            }
            PublishEvent("ActiveMode", value ? "WARN" : "INFO", value ? "ACTIVE mutation mode enabled." : "Mutation bypass enabled.", null);
        }

        public void EmergencyBypass()
        {
            SetActiveMode(false, false);
            PublishEvent("EmergencyBypass", "WARN", "All new mutation rules were bypassed. Pending synthetic injections are cancelled where possible.", null);
        }

        public void SetOperationRecorderEnabled(bool value, bool persist)
        {
            operationRecorder.SetEnabled(value);
            if (persist)
            {
                try
                {
                    profile.OperationRecorderEnabled = value;
                    profile.Save();
                }
                catch (Exception ex)
                {
                    PublishEvent("Profile", "ERROR", "Cannot persist operation recorder mode: " + ex.Message, null);
                }
            }
            PublishEvent("AtomicOperation", "INFO", value ? "Atomic operation recorder enabled." : "Atomic operation recorder disabled.", null);
        }

        public void SelectOperationDefinition(AtomicOperationDefinition definition)
        {
            operationRecorder.SelectDefinition(definition);
            PublishEvent("AtomicOperation", "INFO", "Selected operation template: " + definition.Name, null);
        }

        public AtomicOperationRun StartAtomicOperation()
        {
            AtomicOperationRun run = operationRecorder.Start();
            PublishEvent("AtomicOperation", "INFO", "Recording started: " + run.DefinitionId, null);
            return run;
        }

        public AtomicOperationRun StopAtomicOperation(OperationOutcome outcome, string actualResult, string notes)
        {
            AtomicOperationRun run = operationRecorder.Stop(outcome, actualResult, notes);
            PublishEvent("AtomicOperation", "INFO", "Recording stopped: " + run.Id, null);
            return run;
        }

        public AtomicOperationRun DiscardAtomicOperation(string reason)
        {
            AtomicOperationRun run = operationRecorder.Discard(reason);
            PublishEvent("AtomicOperation", "WARN", "Recording discarded: " + run.Id, null);
            return run;
        }

        public OperationStep MarkAtomicOperationStep(string name, string description)
        {
            OperationStep step = operationRecorder.MarkStep(name, description);
            PublishEvent("AtomicOperation", "INFO", "Step marked: " + step.Name, null);
            return step;
        }

        public void ToggleAtomicOperationRecording()
        {
            if (operationRecorder.State == OperationRecordingState.Disabled)
            {
                SetOperationRecorderEnabled(true, true);
                if (operationRecorder.SelectedDefinition == null) return;
            }
            if (operationRecorder.State == OperationRecordingState.Recording)
                StopAtomicOperation(OperationOutcome.Unknown, null, "Stopped with Ctrl+F8.");
            else
                StartAtomicOperation();
        }

        public OperationComparison CompareOperations(IList<AtomicOperationRun> runs)
        {
            OperationComparison comparison = operationAnalyzer.Compare(runs);
            store.EnqueueOperationComparison(comparison);
            PublishEvent("AtomicOperation", "INFO", "Compared " + runs.Count + " operation runs.", null);
            return comparison;
        }

        public void ExportOperationBundle(IList<AtomicOperationRun> runs, OperationComparison comparison, string path)
        {
            store.Flush();
            operationBundleExporter.Export(runs, comparison, path);
            PublishEvent("Export", "INFO", "Atomic operation bundle exported to " + path, null);
        }

        public RuleDecision EvaluatePayload(ConnectionSession connection, TrafficDirection direction, byte[] bytes)
        {
            if (bytes == null)
            {
                bytes = new byte[0];
            }
            if (definition.Framing.Mode != FramingMode.RawChunk)
            {
                return EvaluateFramedPayload(connection, direction, bytes);
            }

            ProtocolFrame frame;
            int consumed;
            decoder.TryDecode(direction, bytes, 0, bytes.Length, 0, out frame, out consumed);
            if (frame == null)
            {
                frame = CreateRawFrame(connection, direction, bytes);
            }
            frame.ConnectionId = connection == null ? 0 : connection.Id;
            frame.SessionId = store.SessionId;
            frame.TimestampUtc = DateTime.UtcNow;

            PacketContext context = new PacketContext
            {
                SessionId = store.SessionId,
                Connection = connection,
                Direction = direction,
                State = connection == null ? "Disconnected" : stateTracker.GetCurrentState(connection.Id),
                TimestampUtc = DateTime.UtcNow,
                ActiveMode = ActiveMode
            };
            RuleDecision decision = ruleEngine.Evaluate(context, frame);
            if (definition.Framing.Mode == FramingMode.RawChunk && decision.Action != RuleAction.Pass)
            {
                bool safeReplacement = decision.Action == RuleAction.Replace &&
                    decision.Bytes != null && decision.Bytes.Length == bytes.Length;
                if (!safeReplacement)
                {
                    return new RuleDecision
                    {
                        Action = RuleAction.Pass,
                        Bytes = bytes,
                        RuleId = decision.RuleId,
                        Reason = "Action bypassed until a confirmed protocol framer is configured."
                    };
                }
            }
            return decision;
        }

        private RuleDecision EvaluateFramedPayload(ConnectionSession connection, TrafficDirection direction, byte[] bytes)
        {
            List<byte> output = new List<byte>(bytes.Length);
            int offset = 0;
            bool changed = false;
            bool delayed = false;
            int delayMs = 0;
            string ruleId = null;
            string reason = null;

            while (offset < bytes.Length)
            {
                ProtocolFrame frame;
                int consumed;
                if (!decoder.TryDecode(direction, bytes, offset, bytes.Length - offset, offset, out frame, out consumed) || frame == null)
                {
                    for (int i = offset; i < bytes.Length; i++)
                    {
                        output.Add(bytes[i]);
                    }
                    break;
                }

                frame.ConnectionId = connection == null ? 0 : connection.Id;
                frame.SessionId = store.SessionId;
                frame.TimestampUtc = DateTime.UtcNow;
                PacketContext context = CreatePacketContext(connection, direction);
                RuleDecision decision = ruleEngine.Evaluate(context, frame);
                byte[] decidedBytes = decision.Bytes ?? frame.Bytes;
                if (decision.Action != RuleAction.Pass)
                {
                    changed = true;
                    ruleId = decision.RuleId;
                    reason = decision.Reason;
                }

                if (decision.Action == RuleAction.Drop)
                {
                    // Omit this logical frame from the effective stream.
                }
                else if (decision.Action == RuleAction.Duplicate)
                {
                    output.AddRange(decidedBytes);
                    output.AddRange(decidedBytes);
                }
                else if (decision.Action == RuleAction.Inject)
                {
                    output.AddRange(frame.Bytes);
                    output.AddRange(decidedBytes);
                }
                else
                {
                    output.AddRange(decidedBytes);
                    if (decision.Action == RuleAction.Delay)
                    {
                        delayed = true;
                        delayMs = Math.Max(delayMs, decision.DelayMs);
                    }
                }
                offset += consumed;
            }

            byte[] effective = output.ToArray();
            if (!changed)
            {
                return RuleDecision.Pass(effective);
            }
            if (effective.Length == 0)
            {
                return new RuleDecision { Action = RuleAction.Drop, Bytes = effective, RuleId = ruleId, Reason = reason };
            }
            return new RuleDecision
            {
                Action = delayed ? RuleAction.Delay : RuleAction.Replace,
                Bytes = effective,
                DelayMs = delayMs,
                RuleId = ruleId,
                Reason = reason ?? "Composed framed mutation"
            };
        }

        private PacketContext CreatePacketContext(ConnectionSession connection, TrafficDirection direction)
        {
            return new PacketContext
            {
                SessionId = store.SessionId,
                Connection = connection,
                Direction = direction,
                State = connection == null ? "Disconnected" : stateTracker.GetCurrentState(connection.Id),
                TimestampUtc = DateTime.UtcNow,
                ActiveMode = ActiveMode
            };
        }

        private static ProtocolFrame CreateRawFrame(ConnectionSession connection, TrafficDirection direction, byte[] bytes)
        {
            return new ProtocolFrame
            {
                ConnectionId = connection == null ? 0 : connection.Id,
                TimestampUtc = DateTime.UtcNow,
                Direction = direction,
                Bytes = HexCodec.Copy(bytes),
                Status = FrameStatus.Raw
            };
        }

        public void RecordConnection(ConnectionSession connection, string eventName)
        {
            if (connection == null)
            {
                return;
            }
            lock (syncRoot)
            {
                connections[connection.Id] = connection.Clone();
            }
            store.EnqueueConnection(connection);
            StateTransition transition = stateTracker.AcceptConnectionEvent(connection.Id, eventName);
            if (transition != null)
            {
                transition.SessionId = store.SessionId;
                operationRecorder.ObserveState(connection, transition);
                store.EnqueueStateTransition(transition);
                RaiseStateChanged(transition);
            }
            Action<ConnectionSession> handler = ConnectionChanged;
            if (handler != null)
            {
                handler(connection.Clone());
            }
            lock (waitRoot)
            {
                Monitor.PulseAll(waitRoot);
            }
        }

        public void RecordChunk(TransportChunk chunk)
        {
            if (chunk == null)
            {
                return;
            }
            ConnectionSession connection = GetConnection(chunk.ConnectionId);
            // Redact plaintext credentials and replayable login tickets from the capture
            // copy only; Winsock already transmitted the untouched buffer before this call.
            SensitiveTrafficRedactor.RedactLoginSecrets(chunk, connection);
            store.PrepareChunk(chunk);
            List<ProtocolFrame> decodedFrames = chunk.EffectiveBytes == null || chunk.EffectiveBytes.Length == 0
                ? new List<ProtocolFrame>()
                : DecodeChunk(chunk);
            List<StateTransition> transitions = new List<StateTransition>();
            for (int i = 0; i < decodedFrames.Count; i++)
            {
                ProtocolFrame frame = decodedFrames[i];
                store.PrepareFrame(frame);
                StateTransition transition = stateTracker.Accept(frame);
                if (transition != null)
                {
                    transitions.Add(transition);
                }
            }
            operationRecorder.ObserveBatch(connection, chunk, decodedFrames, transitions);
            store.EnqueueChunk(chunk);
            Action<TransportChunk> chunkHandler = ChunkCaptured;
            if (chunkHandler != null) chunkHandler(chunk);

            for (int i = 0; i < decodedFrames.Count; i++)
            {
                ProtocolFrame frame = decodedFrames[i];
                store.EnqueueFrame(frame);
                Action<ProtocolFrame> frameHandler = FrameCaptured;
                if (frameHandler != null)
                {
                    frameHandler(frame);
                }
                lock (waitRoot)
                {
                    lastFrame = frame;
                    Monitor.PulseAll(waitRoot);
                }
            }
            for (int i = 0; i < transitions.Count; i++)
            {
                store.EnqueueStateTransition(transitions[i]);
                RaiseStateChanged(transitions[i]);
            }
        }

        public void AddMarker(string text, long? connectionId)
        {
            PublishEvent("Marker", "INFO", string.IsNullOrWhiteSpace(text) ? "Manual marker" : text, connectionId);
        }

        public void ReportEngineEvent(string category, string level, string message, long? connectionId)
        {
            PublishEvent(category, level, message, connectionId);
        }

        public void ReloadDefinitions()
        {
            ruleEngine.Reload();
            luaHost.Reload();
            PublishEvent("Configuration", "INFO", "Rules and Lua packet script reloaded. Protocol framing changes require restart.", null);
        }

        public void RunScenario(string name)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                ScenarioResult result = luaHost.Run(string.IsNullOrWhiteSpace(name) ? "main" : name);
                store.EnqueueScenarioResult(result);
                WriteScenarioReports(result);
                Action<ScenarioResult> handler = ScenarioCompleted;
                if (handler != null)
                {
                    handler(result);
                }
            });
        }

        public void ExportPcapng(string path)
        {
            pcapngExporter.Export(DatabasePath, path);
            pcapngExporter.WriteWiresharkDissector(Path.ChangeExtension(path, ".lua"));
            PublishEvent("Export", "INFO", "PCAPNG and Wireshark dissector exported to " + path, null);
        }

        public OfflineReplayResult ReplayCurrentSession()
        {
            OfflineReplayEngine replay = new OfflineReplayEngine();
            OfflineReplayResult result = replay.Replay(DatabasePath, definition);
            PublishEvent(
                "OfflineReplay",
                "INFO",
                "chunks=" + result.ChunkCount +
                " frames=" + result.FrameCount +
                " incompleteStreams=" + result.IncompleteStreamCount +
                " suggested=" + result.SuggestedFraming +
                " confidence=" + result.BigEndianLengthPrefixConfidence.ToString("P1"),
                null);
            return result;
        }

        public IList<TransportChunk> ReadRecentChunks(int maximumCount)
        {
            return store.ReadChunks(maximumCount);
        }

        public bool Send(long connectionId, byte[] bytes)
        {
            return captureEngine != null && ActiveMode && captureEngine.Send(connectionId, bytes);
        }

        public bool InjectReceive(long connectionId, byte[] bytes)
        {
            return captureEngine != null && ActiveMode && captureEngine.InjectReceive(connectionId, bytes);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            StopCapture();
            if (captureEngine != null)
            {
                captureEngine.Dispose();
            }
            luaHost.LogProduced -= OnLuaLog;
            operationRecorder.Interrupt("Application closed while recording.");
            operationRecorder.StateChanged -= OnOperationRecordingStateChanged;
            operationRecorder.RunChanged -= OnOperationRunChanged;
            operationRecorder.StepAdded -= OnOperationStepAdded;
            store.Dispose();
        }

        private List<ProtocolFrame> DecodeChunk(TransportChunk chunk)
        {
            List<ProtocolFrame> result = new List<ProtocolFrame>();
            if (definition.Framing.Mode == FramingMode.RawChunk)
            {
                ProtocolFrame frame;
                int consumed;
                decoder.TryDecode(chunk.Direction, chunk.EffectiveBytes, 0, chunk.EffectiveBytes.Length, chunk.StreamOffset, out frame, out consumed);
                if (frame != null)
                {
                    PopulateFrame(frame, chunk);
                    result.Add(frame);
                }
                return result;
            }

            string key = chunk.ConnectionId + ":" + (int)chunk.Direction;
            lock (syncRoot)
            {
                FrameStream stream;
                if (!streams.TryGetValue(key, out stream))
                {
                    stream = new FrameStream();
                    streams.Add(key, stream);
                }
                stream.Append(chunk.EffectiveBytes);
                ProtocolFrame frame;
                while (stream.TryRead(decoder, chunk.Direction, out frame))
                {
                    PopulateFrame(frame, chunk);
                    result.Add(frame);
                }
            }
            return result;
        }

        private void PopulateFrame(ProtocolFrame frame, TransportChunk chunk)
        {
            frame.SessionId = store.SessionId;
            frame.ConnectionId = chunk.ConnectionId;
            frame.FirstChunkId = chunk.Id;
            frame.TimestampUtc = chunk.TimestampUtc;
        }

        private bool WaitState(string state, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
            lock (waitRoot)
            {
                while (DateTime.UtcNow < deadline)
                {
                    if (AnyConnectionInState(state))
                    {
                        return true;
                    }
                    int remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    Monitor.Wait(waitRoot, remaining);
                }
            }
            return AnyConnectionInState(state);
        }

        private bool AnyConnectionInState(string state)
        {
            // Connection ids are learned from observed state transitions; the last frame is sufficient for scenario waits.
            ProtocolFrame frame = lastFrame;
            return frame != null && string.Equals(stateTracker.GetCurrentState(frame.ConnectionId), state, StringComparison.OrdinalIgnoreCase);
        }

        private string WaitFrame(string signature, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
            lock (waitRoot)
            {
                ProtocolFrame initial = lastFrame;
                while (DateTime.UtcNow < deadline)
                {
                    if (lastFrame != null && lastFrame != initial &&
                        (string.IsNullOrWhiteSpace(signature) || signature == "*" ||
                         string.Equals(lastFrame.Signature, signature, StringComparison.OrdinalIgnoreCase)))
                    {
                        return HexCodec.Format(lastFrame.Bytes);
                    }
                    int remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    Monitor.Wait(waitRoot, remaining);
                }
            }
            return null;
        }

        private void OnLuaLog(string message)
        {
            PublishEvent("Lua", "INFO", message, null);
        }

        private void PublishEvent(string category, string level, string message, long? connectionId)
        {
            WorkbenchEvent workbenchEvent = new WorkbenchEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Category = category,
                Level = level,
                Message = message,
                ConnectionId = connectionId
            };
            store.EnqueueEvent(workbenchEvent);
            Action<WorkbenchEvent> handler = EventRaised;
            if (handler != null)
            {
                handler(workbenchEvent);
            }
        }

        private void RaiseStateChanged(StateTransition transition)
        {
            Action<StateTransition> handler = StateChanged;
            if (handler != null)
            {
                handler(transition);
            }
        }

        private ConnectionSession GetConnection(long connectionId)
        {
            lock (syncRoot)
            {
                ConnectionSession connection;
                return connections.TryGetValue(connectionId, out connection) ? connection.Clone() : null;
            }
        }

        private string GetStateSnapshot()
        {
            Dictionary<string, string> snapshot = new Dictionary<string, string>();
            lock (syncRoot)
            {
                foreach (ConnectionSession connection in connections.Values)
                {
                    if (connection.Kind == ConnectionKind.Game)
                    {
                        snapshot[connection.Id.ToString()] = stateTracker.GetCurrentState(connection.Id);
                    }
                }
            }
            return JsonConvert.SerializeObject(snapshot);
        }

        private void OnOperationRecordingStateChanged(OperationRecordingState value)
        {
            Action<OperationRecordingState> handler = OperationRecordingStateChanged;
            if (handler != null) handler(value);
        }

        private void OnOperationRunChanged(AtomicOperationRun run)
        {
            Action<AtomicOperationRun> handler = OperationRunChanged;
            if (handler != null) handler(run);
        }

        private void OnOperationStepAdded(OperationStep step)
        {
            Action<OperationStep> handler = OperationStepAdded;
            if (handler != null) handler(step);
        }

        private void WriteScenarioReports(ScenarioResult result)
        {
            try
            {
                string directory = Path.Combine(Path.GetDirectoryName(DatabasePath), "reports");
                Directory.CreateDirectory(directory);
                string safeName = MakeSafeFileName(result.Name);
                string prefix = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + safeName);
                File.WriteAllText(prefix + ".json", JsonConvert.SerializeObject(result, Formatting.Indented), new UTF8Encoding(false));

                StringBuilder html = new StringBuilder();
                html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>")
                    .Append(SecurityElement.Escape(result.Name))
                    .Append("</title><style>body{font-family:Segoe UI,Arial;margin:2em} .Passed{color:#087f23}.Failed,.Error,.TimedOut{color:#b00020}pre{background:#f4f4f4;padding:1em}</style></head><body>")
                    .Append("<h1>").Append(SecurityElement.Escape(result.Name)).Append("</h1>")
                    .Append("<p class=\"").Append(result.Status).Append("\">Status: ").Append(result.Status).Append("</p>")
                    .Append("<p>").Append(SecurityElement.Escape(result.Message ?? string.Empty)).Append("</p><pre>");
                for (int i = 0; i < result.Log.Count; i++)
                {
                    html.Append(SecurityElement.Escape(result.Log[i])).Append(Environment.NewLine);
                }
                html.Append("</pre></body></html>");
                File.WriteAllText(prefix + ".html", html.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                PublishEvent("Report", "ERROR", "Cannot write scenario report: " + ex.Message, null);
            }
        }

        private static string ResolveOperationCatalogPath(WorkbenchProfile selectedProfile)
        {
            string configured = string.IsNullOrWhiteSpace(selectedProfile.OperationsPath)
                ? "protocols/" + selectedProfile.Name + "/operations.json"
                : selectedProfile.OperationsPath;
            string configuredPath = selectedProfile.Resolve(configured);
            string normalized = configured.Replace('\\', '/').TrimStart('/');
            bool legacyPublishedPath = normalized.StartsWith("protocols/", StringComparison.OrdinalIgnoreCase) &&
                normalized.EndsWith("/operations.json", StringComparison.OrdinalIgnoreCase);
            bool persistentDataPath = string.Equals(
                normalized,
                "data/settings/operation-templates.json",
                StringComparison.OrdinalIgnoreCase);
            if (!legacyPublishedPath && !persistentDataPath)
            {
                return configuredPath;
            }

            string persistentPath = selectedProfile.Resolve("data/settings/operation-templates.json");
            string legacyPath = legacyPublishedPath
                ? configuredPath
                : selectedProfile.Resolve("protocols/" + selectedProfile.Name + "/operations.json");
            if (!File.Exists(persistentPath) && File.Exists(legacyPath))
            {
                string directory = Path.GetDirectoryName(persistentPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.Copy(legacyPath, persistentPath, false);
                Logger.Info("Migrated operation templates to persistent storage: " + persistentPath);
            }
            return persistentPath;
        }

        private static string MakeSafeFileName(string value)
        {
            string result = string.IsNullOrWhiteSpace(value) ? "scenario" : value;
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++)
            {
                result = result.Replace(invalid[i], '_');
            }
            return result;
        }
    }
}
