using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class SqliteCaptureStore : ICaptureStore
    {
        private readonly BlockingCollection<StoreItem> queue;
        private readonly Thread writerThread;
        private SQLiteConnection writerConnection;
        private long nextConnectionId;
        private long nextChunkId;
        private long nextFrameId;
        private long droppedItems;
        private bool completed;

        public SqliteCaptureStore(string directory, int queueCapacity)
        {
            Directory.CreateDirectory(directory);
            DatabasePath = Path.Combine(directory, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".sqlite");
            queue = new BlockingCollection<StoreItem>(queueCapacity);
            writerConnection = Open(DatabasePath);
            InitializeSchema(writerConnection);
            writerThread = new Thread(WriterLoop);
            writerThread.Name = "Protocol capture SQLite writer";
            writerThread.IsBackground = true;
        }

        public string DatabasePath { get; private set; }
        public long SessionId { get; private set; }

        public long StartSession(string profileName, bool activeMode)
        {
            if (SessionId != 0)
            {
                return SessionId;
            }

            using (SQLiteCommand command = writerConnection.CreateCommand())
            {
                command.CommandText =
                    "INSERT INTO sessions(profile_name, started_utc, active_mode, application_version) " +
                    "VALUES(@profile, @started, @active, @version);";
                command.Parameters.AddWithValue("@profile", profileName ?? string.Empty);
                command.Parameters.AddWithValue("@started", FormatUtc(DateTime.UtcNow));
                command.Parameters.AddWithValue("@active", activeMode ? 1 : 0);
                command.Parameters.AddWithValue("@version", typeof(SqliteCaptureStore).Assembly.GetName().Version.ToString());
                command.ExecuteNonQuery();
                SessionId = writerConnection.LastInsertRowId;
            }
            writerThread.Start();
            return SessionId;
        }

        public long AllocateConnectionId()
        {
            return Interlocked.Increment(ref nextConnectionId);
        }

        public void EnqueueConnection(ConnectionSession connection)
        {
            if (connection == null)
            {
                return;
            }
            if (connection.Id == 0)
            {
                connection.Id = AllocateConnectionId();
            }
            connection.SessionId = SessionId;
            TryEnqueue(new StoreItem(StoreItemKind.Connection, connection.Clone()));
        }

        public void EnqueueChunk(TransportChunk chunk)
        {
            if (chunk == null)
            {
                return;
            }
            PrepareChunk(chunk);
            TryEnqueue(new StoreItem(StoreItemKind.Chunk, chunk));
        }

        internal void PrepareChunk(TransportChunk chunk)
        {
            if (chunk.Id == 0) chunk.Id = Interlocked.Increment(ref nextChunkId);
            chunk.SessionId = SessionId;
        }

        public void EnqueueFrame(ProtocolFrame frame)
        {
            if (frame == null)
            {
                return;
            }
            PrepareFrame(frame);
            TryEnqueue(new StoreItem(StoreItemKind.Frame, frame));
        }

        internal void PrepareFrame(ProtocolFrame frame)
        {
            if (frame.Id == 0) frame.Id = Interlocked.Increment(ref nextFrameId);
            frame.SessionId = SessionId;
        }

        public void EnqueueStateTransition(StateTransition transition)
        {
            if (transition == null)
            {
                return;
            }
            transition.SessionId = SessionId;
            TryEnqueue(new StoreItem(StoreItemKind.StateTransition, transition));
        }

        public void EnqueueEvent(WorkbenchEvent workbenchEvent)
        {
            if (workbenchEvent == null)
            {
                return;
            }
            TryEnqueue(new StoreItem(StoreItemKind.Event, workbenchEvent));
        }

        public void EnqueueScenarioResult(ScenarioResult result)
        {
            if (result == null)
            {
                return;
            }
            TryEnqueue(new StoreItem(StoreItemKind.ScenarioResult, result));
        }

        public void EnqueueOperationRun(AtomicOperationRun run)
        {
            if (run != null) TryEnqueue(new StoreItem(StoreItemKind.OperationRun, run));
        }

        public void EnqueueOperationStep(OperationStep step)
        {
            if (step != null) TryEnqueue(new StoreItem(StoreItemKind.OperationStep, step));
        }

        public void EnqueueOperationConnection(OperationConnectionLink link)
        {
            if (link != null) TryEnqueue(new StoreItem(StoreItemKind.OperationConnection, link));
        }

        public void EnqueueOperationChunk(OperationChunkLink link)
        {
            if (link != null) TryEnqueue(new StoreItem(StoreItemKind.OperationChunk, link));
        }

        public void EnqueueOperationFrame(OperationFrameLink link)
        {
            if (link != null) TryEnqueue(new StoreItem(StoreItemKind.OperationFrame, link));
        }

        public void EnqueueOperationState(OperationStateLink link)
        {
            if (link != null) TryEnqueue(new StoreItem(StoreItemKind.OperationState, link));
        }

        public void EnqueueFrameAnnotation(FrameSemanticAnnotation annotation)
        {
            if (annotation != null) TryEnqueue(new StoreItem(StoreItemKind.FrameAnnotation, annotation));
        }

        public void EnqueueFieldAnnotation(FieldSemanticAnnotation annotation)
        {
            if (annotation != null) TryEnqueue(new StoreItem(StoreItemKind.FieldAnnotation, annotation));
        }

        public void EnqueueOperationAttachment(OperationAttachment attachment)
        {
            if (attachment != null) TryEnqueue(new StoreItem(StoreItemKind.OperationAttachment, attachment));
        }

        public void EnqueueOperationComparison(OperationComparison comparison)
        {
            if (comparison != null) TryEnqueue(new StoreItem(StoreItemKind.OperationComparison, comparison));
        }

        public void Flush()
        {
            if (completed)
            {
                return;
            }
            using (ManualResetEvent completedEvent = new ManualResetEvent(false))
            {
                if (!TryEnqueue(new StoreItem(StoreItemKind.Barrier, completedEvent)))
                {
                    throw new InvalidOperationException("The capture storage queue is full; cannot flush it safely.");
                }
                if (!completedEvent.WaitOne(10000))
                {
                    throw new TimeoutException("Timed out while flushing the capture database.");
                }
            }
        }

        public string RenameDatabase(string semanticName)
        {
            if (completed)
            {
                throw new InvalidOperationException("当前会话已经结束，无法通过活动写入器重命名。");
            }

            string target = BuildSemanticDatabasePath(DatabasePath, semanticName);
            if (string.Equals(Path.GetFullPath(DatabasePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                return DatabasePath;
            }
            ValidateRenameTarget(DatabasePath, target);

            DatabaseRenameRequest request = new DatabaseRenameRequest(target);
            if (!TryEnqueue(new StoreItem(StoreItemKind.RenameDatabase, request)))
            {
                request.Completion.Dispose();
                throw new InvalidOperationException("会话库写入队列已满，无法安全重命名。");
            }
            if (!request.Completion.WaitOne(10000))
            {
                throw new TimeoutException("等待会话库重命名超时；录制仍会继续，请稍后重试。");
            }
            request.Completion.Dispose();
            if (request.Error != null)
            {
                throw new IOException("无法重命名会话库：" + request.Error.Message, request.Error);
            }
            return request.ResultPath;
        }

        public static string RenameClosedDatabase(string sourcePath, string semanticName)
        {
            string source = Path.GetFullPath(sourcePath);
            string target = BuildSemanticDatabasePath(source, semanticName);
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return source;
            ValidateRenameTarget(source, target);
            SQLiteConnection.ClearAllPools();
            MoveDatabaseFiles(source, target);
            return target;
        }

        public static string BuildSemanticDatabasePath(string sourcePath, string semanticName)
        {
            if (string.IsNullOrWhiteSpace(semanticName))
            {
                throw new ArgumentException("会话语义名称不能为空。", "semanticName");
            }

            string safeName = semanticName.Trim();
            if (safeName.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
            {
                safeName = safeName.Substring(0, safeName.Length - ".sqlite".Length);
            }
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++) safeName = safeName.Replace(invalid[i], '_');
            safeName = safeName.Trim().Trim('.');
            if (safeName.Length == 0) throw new ArgumentException("会话语义名称不包含有效字符。", "semanticName");
            if (safeName.Length > 80) safeName = safeName.Substring(0, 80).TrimEnd();

            string source = Path.GetFullPath(sourcePath);
            string baseName = Path.GetFileNameWithoutExtension(source);
            if (baseName.StartsWith("session-", StringComparison.OrdinalIgnoreCase) && baseName.Length >= 27)
            {
                // session-yyyyMMdd-HHmmss-fff is 27 characters. Replace any prior semantic suffix.
                baseName = baseName.Substring(0, 27);
            }
            string fileName = baseName + "-" + safeName + ".sqlite";
            return Path.Combine(Path.GetDirectoryName(source), fileName);
        }

        public static string ExtractSemanticDatabaseName(string pathOrFileName)
        {
            string baseName = Path.GetFileNameWithoutExtension(pathOrFileName ?? string.Empty);
            if (baseName.StartsWith("session-", StringComparison.OrdinalIgnoreCase) && baseName.Length >= 27)
            {
                if (baseName.Length == 27) return string.Empty;
                return baseName[27] == '-' ? baseName.Substring(28) : baseName.Substring(27);
            }
            return baseName;
        }

        public IList<TransportChunk> ReadChunks(int maximumCount)
        {
            List<TransportChunk> chunks = new List<TransportChunk>();
            using (SQLiteConnection connection = Open(DatabasePath))
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT id, session_id, connection_id, timestamp_utc, direction, operation, stream_offset, " +
                    "thread_id, native_result, native_error, original_bytes, effective_bytes, rule_action, rule_id, note, capture_ordinal " +
                    "FROM chunks ORDER BY id DESC LIMIT @limit;";
                command.Parameters.AddWithValue("@limit", Math.Max(1, maximumCount));
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        chunks.Add(new TransportChunk
                        {
                            Id = reader.GetInt64(0),
                            SessionId = reader.GetInt64(1),
                            ConnectionId = reader.GetInt64(2),
                            TimestampUtc = ParseUtc(reader.GetString(3)),
                            Direction = (TrafficDirection)reader.GetInt32(4),
                            Operation = (TransportOperation)reader.GetInt32(5),
                            StreamOffset = reader.GetInt64(6),
                            ThreadId = reader.GetInt32(7),
                            NativeResult = reader.GetInt32(8),
                            NativeError = reader.GetInt32(9),
                            OriginalBytes = (byte[])reader[10],
                            EffectiveBytes = (byte[])reader[11],
                            RuleAction = (RuleAction)reader.GetInt32(12),
                            RuleId = reader.IsDBNull(13) ? null : reader.GetString(13),
                            Note = reader.IsDBNull(14) ? null : reader.GetString(14),
                            CaptureOrdinal = reader.GetInt64(15)
                        });
                    }
                }
            }
            chunks.Reverse();
            return chunks;
        }

        public void CompleteSession()
        {
            if (completed)
            {
                return;
            }
            completed = true;
            queue.CompleteAdding();
            if (writerThread.IsAlive)
            {
                writerThread.Join(10000);
            }

            using (SQLiteCommand command = writerConnection.CreateCommand())
            {
                command.CommandText = "UPDATE sessions SET ended_utc=@ended WHERE id=@id;";
                command.Parameters.AddWithValue("@ended", FormatUtc(DateTime.UtcNow));
                command.Parameters.AddWithValue("@id", SessionId);
                command.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
            CompleteSession();
            queue.Dispose();
            writerConnection.Dispose();
        }

        private bool TryEnqueue(StoreItem item)
        {
            if (completed || !queue.TryAdd(item))
            {
                Interlocked.Increment(ref droppedItems);
                return false;
            }
            return true;
        }

        private void WriterLoop()
        {
            try
            {
                while (!queue.IsCompleted)
                {
                    StoreItem first;
                    if (!queue.TryTake(out first, 100))
                    {
                        WriteGapIfNeeded(null);
                        continue;
                    }

                    if (IsControlItem(first))
                    {
                        ProcessControlItem(first);
                        continue;
                    }

                    StoreItem controlItem = null;
                    using (SQLiteTransaction transaction = writerConnection.BeginTransaction())
                    {
                        WriteGapIfNeeded(transaction);
                        WriteItem(first, transaction);
                        StoreItem item;
                        int batchCount = 1;
                        while (batchCount < 256 && queue.TryTake(out item))
                        {
                            if (IsControlItem(item))
                            {
                                controlItem = item;
                                break;
                            }
                            WriteItem(item, transaction);
                            batchCount++;
                        }
                        transaction.Commit();
                    }
                    if (controlItem != null)
                    {
                        ProcessControlItem(controlItem);
                    }
                }

                // BlockingCollection.IsCompleted becomes true only after CompleteAdding and
                // after the queue is empty, so every data/control item has already been handled.
            }
            catch (Exception ex)
            {
                Logger.Error("Capture database writer failed", ex);
            }
        }

        private static bool IsControlItem(StoreItem item)
        {
            return item.Kind == StoreItemKind.Barrier || item.Kind == StoreItemKind.RenameDatabase;
        }

        private void ProcessControlItem(StoreItem item)
        {
            if (item.Kind == StoreItemKind.Barrier)
            {
                ((ManualResetEvent)item.Value).Set();
                return;
            }
            ProcessDatabaseRename((DatabaseRenameRequest)item.Value);
        }

        private void ProcessDatabaseRename(DatabaseRenameRequest request)
        {
            string original = DatabasePath;
            bool moved = false;
            try
            {
                using (SQLiteCommand checkpoint = writerConnection.CreateCommand())
                {
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    checkpoint.ExecuteNonQuery();
                }
                writerConnection.Close();
                writerConnection.Dispose();
                writerConnection = null;
                SQLiteConnection.ClearAllPools();
                MoveDatabaseFiles(original, request.TargetPath);
                moved = true;
                writerConnection = Open(request.TargetPath);
                DatabasePath = request.TargetPath;
                request.ResultPath = DatabasePath;
            }
            catch (Exception ex)
            {
                request.Error = ex;
                try
                {
                    if (writerConnection == null)
                    {
                        if (moved && File.Exists(request.TargetPath) && !File.Exists(original))
                        {
                            MoveDatabaseFiles(request.TargetPath, original);
                        }
                        writerConnection = Open(File.Exists(original) ? original : request.TargetPath);
                        DatabasePath = File.Exists(original) ? original : request.TargetPath;
                    }
                }
                catch (Exception recoveryError)
                {
                    Logger.Error("Cannot recover the capture database after a rename failure", recoveryError);
                }
            }
            finally
            {
                request.Completion.Set();
            }
        }

        private static void ValidateRenameTarget(string source, string target)
        {
            if (!File.Exists(source)) throw new FileNotFoundException("找不到会话库。", source);
            if (File.Exists(target)) throw new IOException("目标会话库已经存在：" + Path.GetFileName(target));
        }

        private static void MoveDatabaseFiles(string source, string target)
        {
            string[] suffixes = { string.Empty, "-wal", "-shm", "-journal" };
            for (int i = 0; i < suffixes.Length; i++)
            {
                string sourcePart = source + suffixes[i];
                string targetPart = target + suffixes[i];
                if (File.Exists(sourcePart) && File.Exists(targetPart))
                    throw new IOException("目标文件已经存在：" + Path.GetFileName(targetPart));
            }
            for (int i = 0; i < suffixes.Length; i++)
            {
                string sourcePart = source + suffixes[i];
                if (File.Exists(sourcePart)) File.Move(sourcePart, target + suffixes[i]);
            }
        }

        private void WriteGapIfNeeded(SQLiteTransaction transaction)
        {
            long dropped = Interlocked.Exchange(ref droppedItems, 0);
            if (dropped <= 0)
            {
                return;
            }
            WorkbenchEvent gap = new WorkbenchEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Category = "CaptureGap",
                Level = "ERROR",
                Message = dropped.ToString(CultureInfo.InvariantCulture) + " capture records were dropped because the storage queue was full."
            };
            WriteEvent(gap, transaction);
        }

        private void WriteItem(StoreItem item, SQLiteTransaction transaction)
        {
            switch (item.Kind)
            {
                case StoreItemKind.Connection:
                    WriteConnection((ConnectionSession)item.Value, transaction);
                    break;
                case StoreItemKind.Chunk:
                    WriteChunk((TransportChunk)item.Value, transaction);
                    break;
                case StoreItemKind.Frame:
                    WriteFrame((ProtocolFrame)item.Value, transaction);
                    break;
                case StoreItemKind.StateTransition:
                    WriteStateTransition((StateTransition)item.Value, transaction);
                    break;
                case StoreItemKind.Event:
                    WriteEvent((WorkbenchEvent)item.Value, transaction);
                    break;
                case StoreItemKind.ScenarioResult:
                    WriteScenarioResult((ScenarioResult)item.Value, transaction);
                    break;
                case StoreItemKind.OperationRun:
                    WriteOperationRun((AtomicOperationRun)item.Value, transaction);
                    break;
                case StoreItemKind.OperationStep:
                    WriteOperationStep((OperationStep)item.Value, transaction);
                    break;
                case StoreItemKind.OperationConnection:
                    WriteOperationConnection((OperationConnectionLink)item.Value, transaction);
                    break;
                case StoreItemKind.OperationChunk:
                    WriteOperationChunk((OperationChunkLink)item.Value, transaction);
                    break;
                case StoreItemKind.OperationFrame:
                    WriteOperationFrame((OperationFrameLink)item.Value, transaction);
                    break;
                case StoreItemKind.OperationState:
                    WriteOperationState((OperationStateLink)item.Value, transaction);
                    break;
                case StoreItemKind.FrameAnnotation:
                    WriteFrameAnnotation((FrameSemanticAnnotation)item.Value, transaction);
                    break;
                case StoreItemKind.FieldAnnotation:
                    WriteFieldAnnotation((FieldSemanticAnnotation)item.Value, transaction);
                    break;
                case StoreItemKind.OperationAttachment:
                    WriteOperationAttachment((OperationAttachment)item.Value, transaction);
                    break;
                case StoreItemKind.OperationComparison:
                    WriteOperationComparison((OperationComparison)item.Value, transaction);
                    break;
            }
        }

        private void WriteConnection(ConnectionSession connection, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT OR REPLACE INTO connections(id, session_id, socket_handle, local_endpoint, remote_endpoint, " +
                "remote_port, kind, opened_utc, closed_utc, state, last_error, client_stream_offset, server_stream_offset) " +
                "VALUES(@id,@session,@socket,@local,@remote,@port,@kind,@opened,@closed,@state,@error,@clientOffset,@serverOffset);"))
            {
                command.Parameters.AddWithValue("@id", connection.Id);
                command.Parameters.AddWithValue("@session", SessionId);
                command.Parameters.AddWithValue("@socket", connection.SocketHandle.ToInt64());
                command.Parameters.AddWithValue("@local", connection.LocalEndPoint ?? string.Empty);
                command.Parameters.AddWithValue("@remote", connection.RemoteEndPoint ?? string.Empty);
                command.Parameters.AddWithValue("@port", connection.RemotePort);
                command.Parameters.AddWithValue("@kind", (int)connection.Kind);
                command.Parameters.AddWithValue("@opened", FormatUtc(connection.OpenedUtc));
                command.Parameters.AddWithValue("@closed", connection.ClosedUtc.HasValue ? (object)FormatUtc(connection.ClosedUtc.Value) : DBNull.Value);
                command.Parameters.AddWithValue("@state", connection.State ?? string.Empty);
                command.Parameters.AddWithValue("@error", connection.LastError);
                command.Parameters.AddWithValue("@clientOffset", connection.ClientStreamOffset);
                command.Parameters.AddWithValue("@serverOffset", connection.ServerStreamOffset);
                command.ExecuteNonQuery();
            }
        }

        private void WriteChunk(TransportChunk chunk, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT INTO chunks(id,session_id,connection_id,timestamp_utc,direction,operation,stream_offset,thread_id," +
                "native_result,native_error,original_bytes,effective_bytes,rule_action,rule_id,note,capture_ordinal) " +
                "VALUES(@id,@session,@connection,@timestamp,@direction,@operation,@offset,@thread,@result,@error,@original," +
                "@effective,@action,@rule,@note,@ordinal);"))
            {
                command.Parameters.AddWithValue("@id", chunk.Id);
                command.Parameters.AddWithValue("@session", SessionId);
                command.Parameters.AddWithValue("@connection", chunk.ConnectionId);
                command.Parameters.AddWithValue("@timestamp", FormatUtc(chunk.TimestampUtc));
                command.Parameters.AddWithValue("@direction", (int)chunk.Direction);
                command.Parameters.AddWithValue("@operation", (int)chunk.Operation);
                command.Parameters.AddWithValue("@offset", chunk.StreamOffset);
                command.Parameters.AddWithValue("@thread", chunk.ThreadId);
                command.Parameters.AddWithValue("@result", chunk.NativeResult);
                command.Parameters.AddWithValue("@error", chunk.NativeError);
                command.Parameters.AddWithValue("@original", chunk.OriginalBytes ?? new byte[0]);
                command.Parameters.AddWithValue("@effective", chunk.EffectiveBytes ?? new byte[0]);
                command.Parameters.AddWithValue("@action", (int)chunk.RuleAction);
                command.Parameters.AddWithValue("@rule", (object)chunk.RuleId ?? DBNull.Value);
                command.Parameters.AddWithValue("@note", (object)chunk.Note ?? DBNull.Value);
                command.Parameters.AddWithValue("@ordinal", chunk.CaptureOrdinal);
                command.ExecuteNonQuery();
            }
        }

        private void WriteFrame(ProtocolFrame frame, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT INTO frames(id,session_id,connection_id,first_chunk_id,timestamp_utc,direction,stream_offset,bytes," +
                "opcode,name,status,parse_error,fields_json,capture_ordinal) VALUES(@id,@session,@connection,@chunk,@timestamp,@direction," +
                "@offset,@bytes,@opcode,@name,@status,@error,@fields,@ordinal);"))
            {
                command.Parameters.AddWithValue("@id", frame.Id);
                command.Parameters.AddWithValue("@session", SessionId);
                command.Parameters.AddWithValue("@connection", frame.ConnectionId);
                command.Parameters.AddWithValue("@chunk", frame.FirstChunkId);
                command.Parameters.AddWithValue("@timestamp", FormatUtc(frame.TimestampUtc));
                command.Parameters.AddWithValue("@direction", (int)frame.Direction);
                command.Parameters.AddWithValue("@offset", frame.StreamOffset);
                command.Parameters.AddWithValue("@bytes", frame.Bytes ?? new byte[0]);
                command.Parameters.AddWithValue("@opcode", frame.Opcode.HasValue ? (object)frame.Opcode.Value : DBNull.Value);
                command.Parameters.AddWithValue("@name", (object)frame.Name ?? DBNull.Value);
                command.Parameters.AddWithValue("@status", (int)frame.Status);
                command.Parameters.AddWithValue("@error", (object)frame.ParseError ?? DBNull.Value);
                command.Parameters.AddWithValue("@fields", JsonConvert.SerializeObject(frame.Fields));
                command.Parameters.AddWithValue("@ordinal", frame.CaptureOrdinal);
                command.ExecuteNonQuery();
            }
        }

        private void WriteStateTransition(StateTransition transition, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT INTO state_transitions(session_id,connection_id,timestamp_utc,from_state,to_state,trigger,confirmed,capture_ordinal) " +
                "VALUES(@session,@connection,@timestamp,@from,@to,@trigger,@confirmed,@ordinal);"))
            {
                command.Parameters.AddWithValue("@session", SessionId);
                command.Parameters.AddWithValue("@connection", transition.ConnectionId);
                command.Parameters.AddWithValue("@timestamp", FormatUtc(transition.TimestampUtc));
                command.Parameters.AddWithValue("@from", transition.FromState ?? string.Empty);
                command.Parameters.AddWithValue("@to", transition.ToState ?? string.Empty);
                command.Parameters.AddWithValue("@trigger", transition.Trigger ?? string.Empty);
                command.Parameters.AddWithValue("@confirmed", transition.Confirmed ? 1 : 0);
                command.Parameters.AddWithValue("@ordinal", transition.CaptureOrdinal);
                command.ExecuteNonQuery();
            }
        }

        private void WriteEvent(WorkbenchEvent workbenchEvent, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT INTO events(session_id,timestamp_utc,category,level,message,connection_id) " +
                "VALUES(@session,@timestamp,@category,@level,@message,@connection);"))
            {
                command.Parameters.AddWithValue("@session", SessionId);
                command.Parameters.AddWithValue("@timestamp", FormatUtc(workbenchEvent.TimestampUtc));
                command.Parameters.AddWithValue("@category", workbenchEvent.Category ?? string.Empty);
                command.Parameters.AddWithValue("@level", workbenchEvent.Level ?? "INFO");
                command.Parameters.AddWithValue("@message", workbenchEvent.Message ?? string.Empty);
                command.Parameters.AddWithValue("@connection", workbenchEvent.ConnectionId.HasValue ? (object)workbenchEvent.ConnectionId.Value : DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        private void WriteScenarioResult(ScenarioResult result, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT INTO scenario_results(session_id,name,status,started_utc,finished_utc,message,log_json) " +
                "VALUES(@session,@name,@status,@started,@finished,@message,@log);"))
            {
                command.Parameters.AddWithValue("@session", SessionId);
                command.Parameters.AddWithValue("@name", result.Name ?? string.Empty);
                command.Parameters.AddWithValue("@status", (int)result.Status);
                command.Parameters.AddWithValue("@started", FormatUtc(result.StartedUtc));
                command.Parameters.AddWithValue("@finished", FormatUtc(result.FinishedUtc));
                command.Parameters.AddWithValue("@message", result.Message ?? string.Empty);
                command.Parameters.AddWithValue("@log", JsonConvert.SerializeObject(result.Log));
                command.ExecuteNonQuery();
            }
        }

        private void WriteOperationRun(AtomicOperationRun run, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT OR REPLACE INTO operation_runs(id,session_id,definition_id,definition_snapshot_json,started_utc," +
                "stopped_utc,start_boundary_ordinal,end_boundary_ordinal,status,outcome,start_state,end_state,actual_result,notes) " +
                "VALUES(@id,@session,@definition,@snapshot,@started,@stopped,@startOrdinal,@endOrdinal,@status,@outcome," +
                "@startState,@endState,@actual,@notes);"))
            {
                command.Parameters.AddWithValue("@id", run.Id);
                command.Parameters.AddWithValue("@session", SessionId);
                command.Parameters.AddWithValue("@definition", run.DefinitionId ?? string.Empty);
                command.Parameters.AddWithValue("@snapshot", run.DefinitionSnapshotJson ?? string.Empty);
                command.Parameters.AddWithValue("@started", FormatUtc(run.StartedUtc));
                command.Parameters.AddWithValue("@stopped", run.StoppedUtc.HasValue ? (object)FormatUtc(run.StoppedUtc.Value) : DBNull.Value);
                command.Parameters.AddWithValue("@startOrdinal", run.StartBoundaryOrdinal);
                command.Parameters.AddWithValue("@endOrdinal", run.EndBoundaryOrdinal.HasValue ? (object)run.EndBoundaryOrdinal.Value : DBNull.Value);
                command.Parameters.AddWithValue("@status", (int)run.Status);
                command.Parameters.AddWithValue("@outcome", (int)run.Outcome);
                command.Parameters.AddWithValue("@startState", run.StartState ?? string.Empty);
                command.Parameters.AddWithValue("@endState", run.EndState ?? string.Empty);
                command.Parameters.AddWithValue("@actual", run.ActualResult ?? string.Empty);
                command.Parameters.AddWithValue("@notes", run.Notes ?? string.Empty);
                command.ExecuteNonQuery();
            }
        }

        private void WriteOperationStep(OperationStep step, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT OR REPLACE INTO operation_steps(id,run_id,sequence,boundary_ordinal,timestamp_utc,name,description) " +
                "VALUES(@id,@run,@sequence,@ordinal,@timestamp,@name,@description);"))
            {
                command.Parameters.AddWithValue("@id", step.Id);
                command.Parameters.AddWithValue("@run", step.RunId ?? string.Empty);
                command.Parameters.AddWithValue("@sequence", step.Sequence);
                command.Parameters.AddWithValue("@ordinal", step.BoundaryOrdinal);
                command.Parameters.AddWithValue("@timestamp", FormatUtc(step.TimestampUtc));
                command.Parameters.AddWithValue("@name", step.Name ?? string.Empty);
                command.Parameters.AddWithValue("@description", step.Description ?? string.Empty);
                command.ExecuteNonQuery();
            }
        }

        private void WriteOperationConnection(OperationConnectionLink link, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT OR REPLACE INTO operation_connections(run_id,connection_id,automatic,included) " +
                "VALUES(@run,@connection,@automatic,@included);"))
            {
                command.Parameters.AddWithValue("@run", link.RunId ?? string.Empty);
                command.Parameters.AddWithValue("@connection", link.ConnectionId);
                command.Parameters.AddWithValue("@automatic", link.Automatic ? 1 : 0);
                command.Parameters.AddWithValue("@included", link.Included ? 1 : 0);
                command.ExecuteNonQuery();
            }
        }

        private void WriteOperationChunk(OperationChunkLink link, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT OR IGNORE INTO operation_chunks(run_id,chunk_id,capture_ordinal) VALUES(@run,@id,@ordinal);"))
            {
                command.Parameters.AddWithValue("@run", link.RunId ?? string.Empty);
                command.Parameters.AddWithValue("@id", link.ChunkId);
                command.Parameters.AddWithValue("@ordinal", link.CaptureOrdinal);
                command.ExecuteNonQuery();
            }
        }

        private void WriteOperationFrame(OperationFrameLink link, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT OR IGNORE INTO operation_frames(run_id,frame_id,capture_ordinal) VALUES(@run,@id,@ordinal);"))
            {
                command.Parameters.AddWithValue("@run", link.RunId ?? string.Empty);
                command.Parameters.AddWithValue("@id", link.FrameId);
                command.Parameters.AddWithValue("@ordinal", link.CaptureOrdinal);
                command.ExecuteNonQuery();
            }
        }

        private void WriteOperationState(OperationStateLink link, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT INTO operation_states(run_id,connection_id,capture_ordinal,timestamp_utc,from_state,to_state,trigger) " +
                "VALUES(@run,@connection,@ordinal,@timestamp,@from,@to,@trigger);"))
            {
                command.Parameters.AddWithValue("@run", link.RunId ?? string.Empty);
                command.Parameters.AddWithValue("@connection", link.ConnectionId);
                command.Parameters.AddWithValue("@ordinal", link.CaptureOrdinal);
                command.Parameters.AddWithValue("@timestamp", FormatUtc(link.TimestampUtc));
                command.Parameters.AddWithValue("@from", link.FromState ?? string.Empty);
                command.Parameters.AddWithValue("@to", link.ToState ?? string.Empty);
                command.Parameters.AddWithValue("@trigger", link.Trigger ?? string.Empty);
                command.ExecuteNonQuery();
            }
        }

        private void WriteFrameAnnotation(FrameSemanticAnnotation annotation, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT OR REPLACE INTO frame_annotations(run_id,frame_id,role,meaning,confirmed,updated_utc) " +
                "VALUES(@run,@frame,@role,@meaning,@confirmed,@updated);"))
            {
                command.Parameters.AddWithValue("@run", annotation.RunId ?? string.Empty);
                command.Parameters.AddWithValue("@frame", annotation.FrameId);
                command.Parameters.AddWithValue("@role", (int)annotation.Role);
                command.Parameters.AddWithValue("@meaning", annotation.Meaning ?? string.Empty);
                command.Parameters.AddWithValue("@confirmed", annotation.Confirmed ? 1 : 0);
                command.Parameters.AddWithValue("@updated", FormatUtc(annotation.UpdatedUtc == DateTime.MinValue ? DateTime.UtcNow : annotation.UpdatedUtc));
                command.ExecuteNonQuery();
            }
        }

        private void WriteFieldAnnotation(FieldSemanticAnnotation annotation, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT OR REPLACE INTO field_annotations(run_id,frame_id,field_name,field_offset,field_length,semantic_name," +
                "meaning,value_type,dynamic_kind,notes,confirmed,updated_utc) VALUES(@run,@frame,@field,@offset,@length," +
                "@semantic,@meaning,@type,@dynamic,@notes,@confirmed,@updated);"))
            {
                command.Parameters.AddWithValue("@run", annotation.RunId ?? string.Empty);
                command.Parameters.AddWithValue("@frame", annotation.FrameId);
                command.Parameters.AddWithValue("@field", annotation.FieldName ?? string.Empty);
                command.Parameters.AddWithValue("@offset", annotation.Offset);
                command.Parameters.AddWithValue("@length", annotation.Length);
                command.Parameters.AddWithValue("@semantic", annotation.SemanticName ?? string.Empty);
                command.Parameters.AddWithValue("@meaning", annotation.Meaning ?? string.Empty);
                command.Parameters.AddWithValue("@type", annotation.ValueType ?? string.Empty);
                command.Parameters.AddWithValue("@dynamic", annotation.DynamicKind ?? string.Empty);
                command.Parameters.AddWithValue("@notes", annotation.Notes ?? string.Empty);
                command.Parameters.AddWithValue("@confirmed", annotation.Confirmed ? 1 : 0);
                command.Parameters.AddWithValue("@updated", FormatUtc(annotation.UpdatedUtc == DateTime.MinValue ? DateTime.UtcNow : annotation.UpdatedUtc));
                command.ExecuteNonQuery();
            }
        }

        private void WriteOperationAttachment(OperationAttachment attachment, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT OR REPLACE INTO operation_attachments(id,run_id,step_id,file_name,media_type,bytes,created_utc) " +
                "VALUES(@id,@run,@step,@file,@media,@bytes,@created);"))
            {
                command.Parameters.AddWithValue("@id", attachment.Id);
                command.Parameters.AddWithValue("@run", attachment.RunId ?? string.Empty);
                command.Parameters.AddWithValue("@step", (object)attachment.StepId ?? DBNull.Value);
                command.Parameters.AddWithValue("@file", attachment.FileName ?? string.Empty);
                command.Parameters.AddWithValue("@media", attachment.MediaType ?? "application/octet-stream");
                command.Parameters.AddWithValue("@bytes", attachment.Bytes ?? new byte[0]);
                command.Parameters.AddWithValue("@created", FormatUtc(attachment.CreatedUtc == DateTime.MinValue ? DateTime.UtcNow : attachment.CreatedUtc));
                command.ExecuteNonQuery();
            }
        }

        private void WriteOperationComparison(OperationComparison comparison, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = CreateCommand(transaction,
                "INSERT OR REPLACE INTO operation_comparisons(id,definition_id,created_utc,run_ids_json,result_json) " +
                "VALUES(@id,@definition,@created,@runs,@result);"))
            {
                command.Parameters.AddWithValue("@id", comparison.Id);
                command.Parameters.AddWithValue("@definition", comparison.DefinitionId ?? string.Empty);
                command.Parameters.AddWithValue("@created", FormatUtc(comparison.CreatedUtc));
                command.Parameters.AddWithValue("@runs", JsonConvert.SerializeObject(comparison.RunIds));
                command.Parameters.AddWithValue("@result", JsonConvert.SerializeObject(comparison));
                command.ExecuteNonQuery();
            }
        }

        private SQLiteCommand CreateCommand(SQLiteTransaction transaction, string text)
        {
            SQLiteCommand command = writerConnection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = text;
            return command;
        }

        internal static SQLiteConnection OpenDatabase(string path)
        {
            SQLiteConnection connection = new SQLiteConnection("Data Source=" + path + ";Version=3;Pooling=False;");
            connection.Open();
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
                command.ExecuteNonQuery();
            }
            return connection;
        }

        private static SQLiteConnection Open(string path)
        {
            return OpenDatabase(path);
        }

        public static void MigrateDatabase(string path)
        {
            using (SQLiteConnection connection = OpenDatabase(path))
            {
                InitializeSchema(connection);
            }
        }

        private static void InitializeSchema(SQLiteConnection connection)
        {
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                string sql = @"
CREATE TABLE IF NOT EXISTS sessions(
 id INTEGER PRIMARY KEY AUTOINCREMENT, profile_name TEXT NOT NULL, started_utc TEXT NOT NULL,
 ended_utc TEXT, active_mode INTEGER NOT NULL, application_version TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS connections(
 id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL, socket_handle INTEGER NOT NULL,
 local_endpoint TEXT, remote_endpoint TEXT, remote_port INTEGER, kind INTEGER NOT NULL,
 opened_utc TEXT NOT NULL, closed_utc TEXT, state TEXT, last_error INTEGER,
 client_stream_offset INTEGER, server_stream_offset INTEGER);
CREATE TABLE IF NOT EXISTS chunks(
 id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL, connection_id INTEGER NOT NULL,
 timestamp_utc TEXT NOT NULL, direction INTEGER NOT NULL, operation INTEGER NOT NULL,
 stream_offset INTEGER NOT NULL, thread_id INTEGER NOT NULL, native_result INTEGER NOT NULL,
 native_error INTEGER NOT NULL, original_bytes BLOB NOT NULL, effective_bytes BLOB NOT NULL,
 rule_action INTEGER NOT NULL, rule_id TEXT, note TEXT, capture_ordinal INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS frames(
 id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL, connection_id INTEGER NOT NULL,
 first_chunk_id INTEGER, timestamp_utc TEXT NOT NULL, direction INTEGER NOT NULL,
 stream_offset INTEGER NOT NULL, bytes BLOB NOT NULL, opcode INTEGER, name TEXT,
 status INTEGER NOT NULL, parse_error TEXT, fields_json TEXT, capture_ordinal INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS state_transitions(
 id INTEGER PRIMARY KEY AUTOINCREMENT, session_id INTEGER NOT NULL, connection_id INTEGER NOT NULL,
 timestamp_utc TEXT NOT NULL, from_state TEXT, to_state TEXT, trigger TEXT, confirmed INTEGER NOT NULL,
 capture_ordinal INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS events(
 id INTEGER PRIMARY KEY AUTOINCREMENT, session_id INTEGER NOT NULL, timestamp_utc TEXT NOT NULL,
 category TEXT NOT NULL, level TEXT NOT NULL, message TEXT NOT NULL, connection_id INTEGER);
CREATE TABLE IF NOT EXISTS scenario_results(
 id INTEGER PRIMARY KEY AUTOINCREMENT, session_id INTEGER NOT NULL, name TEXT NOT NULL,
 status INTEGER NOT NULL, started_utc TEXT NOT NULL, finished_utc TEXT NOT NULL,
 message TEXT, log_json TEXT);
CREATE TABLE IF NOT EXISTS operation_runs(
 id TEXT PRIMARY KEY, session_id INTEGER NOT NULL, definition_id TEXT NOT NULL,
 definition_snapshot_json TEXT NOT NULL, started_utc TEXT NOT NULL, stopped_utc TEXT,
 start_boundary_ordinal INTEGER NOT NULL, end_boundary_ordinal INTEGER, status INTEGER NOT NULL,
 outcome INTEGER NOT NULL, start_state TEXT, end_state TEXT, actual_result TEXT, notes TEXT);
CREATE TABLE IF NOT EXISTS operation_steps(
 id TEXT PRIMARY KEY, run_id TEXT NOT NULL, sequence INTEGER NOT NULL, boundary_ordinal INTEGER NOT NULL,
 timestamp_utc TEXT NOT NULL, name TEXT, description TEXT);
CREATE TABLE IF NOT EXISTS operation_connections(
 run_id TEXT NOT NULL, connection_id INTEGER NOT NULL, automatic INTEGER NOT NULL, included INTEGER NOT NULL,
 PRIMARY KEY(run_id,connection_id));
CREATE TABLE IF NOT EXISTS operation_chunks(
 run_id TEXT NOT NULL, chunk_id INTEGER NOT NULL, capture_ordinal INTEGER NOT NULL,
 PRIMARY KEY(run_id,chunk_id));
CREATE TABLE IF NOT EXISTS operation_frames(
 run_id TEXT NOT NULL, frame_id INTEGER NOT NULL, capture_ordinal INTEGER NOT NULL,
 PRIMARY KEY(run_id,frame_id));
CREATE TABLE IF NOT EXISTS operation_states(
 id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL, connection_id INTEGER NOT NULL,
 capture_ordinal INTEGER NOT NULL, timestamp_utc TEXT NOT NULL, from_state TEXT, to_state TEXT, trigger TEXT);
CREATE TABLE IF NOT EXISTS frame_annotations(
 run_id TEXT NOT NULL, frame_id INTEGER NOT NULL, role INTEGER NOT NULL, meaning TEXT,
 confirmed INTEGER NOT NULL, updated_utc TEXT NOT NULL, PRIMARY KEY(run_id,frame_id));
CREATE TABLE IF NOT EXISTS field_annotations(
 run_id TEXT NOT NULL, frame_id INTEGER NOT NULL, field_name TEXT NOT NULL, field_offset INTEGER NOT NULL,
 field_length INTEGER NOT NULL, semantic_name TEXT, meaning TEXT, value_type TEXT, dynamic_kind TEXT,
 notes TEXT, confirmed INTEGER NOT NULL, updated_utc TEXT NOT NULL,
 PRIMARY KEY(run_id,frame_id,field_name,field_offset));
CREATE TABLE IF NOT EXISTS operation_attachments(
 id TEXT PRIMARY KEY, run_id TEXT NOT NULL, step_id TEXT, file_name TEXT NOT NULL,
 media_type TEXT NOT NULL, bytes BLOB NOT NULL, created_utc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS operation_comparisons(
 id TEXT PRIMARY KEY, definition_id TEXT NOT NULL, created_utc TEXT NOT NULL,
 run_ids_json TEXT NOT NULL, result_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_chunks_connection_time ON chunks(connection_id, timestamp_utc);
CREATE INDEX IF NOT EXISTS ix_frames_connection_opcode ON frames(connection_id, opcode);
CREATE INDEX IF NOT EXISTS ix_events_category_time ON events(category, timestamp_utc);
CREATE INDEX IF NOT EXISTS ix_operation_runs_definition_time ON operation_runs(definition_id,started_utc);
CREATE INDEX IF NOT EXISTS ix_operation_chunks_ordinal ON operation_chunks(run_id,capture_ordinal);
CREATE INDEX IF NOT EXISTS ix_operation_frames_ordinal ON operation_frames(run_id,capture_ordinal);
CREATE INDEX IF NOT EXISTS ix_operation_steps_sequence ON operation_steps(run_id,sequence);";
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }

                AddColumnIfMissing(connection, transaction, "chunks", "capture_ordinal", "INTEGER NOT NULL DEFAULT 0");
                AddColumnIfMissing(connection, transaction, "frames", "capture_ordinal", "INTEGER NOT NULL DEFAULT 0");
                AddColumnIfMissing(connection, transaction, "state_transitions", "capture_ordinal", "INTEGER NOT NULL DEFAULT 0");
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        "UPDATE operation_runs SET status=@interrupted, stopped_utc=COALESCE(stopped_utc,@now), " +
                        "end_boundary_ordinal=COALESCE(end_boundary_ordinal,start_boundary_ordinal) WHERE status=@recording; " +
                        "PRAGMA user_version=2;";
                    command.Parameters.AddWithValue("@interrupted", (int)OperationRunStatus.Interrupted);
                    command.Parameters.AddWithValue("@recording", (int)OperationRunStatus.Recording);
                    command.Parameters.AddWithValue("@now", FormatUtc(DateTime.UtcNow));
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        private static void AddColumnIfMissing(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            string table,
            string column,
            string declaration)
        {
            bool exists = false;
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA table_info(" + table + ");";
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                }
            }
            if (!exists)
            {
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "ALTER TABLE " + table + " ADD COLUMN " + column + " " + declaration + ";";
                    command.ExecuteNonQuery();
                }
            }
        }

        private static string FormatUtc(DateTime value)
        {
            return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        private static DateTime ParseUtc(string value)
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        private enum StoreItemKind
        {
            Connection,
            Chunk,
            Frame,
            StateTransition,
            Event,
            ScenarioResult,
            OperationRun,
            OperationStep,
            OperationConnection,
            OperationChunk,
            OperationFrame,
            OperationState,
            FrameAnnotation,
            FieldAnnotation,
            OperationAttachment,
            OperationComparison,
            RenameDatabase,
            Barrier
        }

        private sealed class StoreItem
        {
            public StoreItem(StoreItemKind kind, object value)
            {
                Kind = kind;
                Value = value;
            }

            public StoreItemKind Kind { get; private set; }
            public object Value { get; private set; }
        }

        private sealed class DatabaseRenameRequest
        {
            public DatabaseRenameRequest(string targetPath)
            {
                TargetPath = targetPath;
                Completion = new ManualResetEvent(false);
            }

            public string TargetPath { get; private set; }
            public string ResultPath { get; set; }
            public Exception Error { get; set; }
            public ManualResetEvent Completion { get; private set; }
        }
    }
}
