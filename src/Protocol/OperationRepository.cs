using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class OperationRepository : IOperationRepository
    {
        private readonly object catalogGate = new object();
        private readonly string catalogPath;
        private readonly string catalogMutexName;
        private readonly string captureDirectory;
        private readonly SqliteCaptureStore currentStore;

        public OperationRepository(string catalogPath, string captureDirectory, SqliteCaptureStore currentStore)
        {
            this.catalogPath = catalogPath;
            catalogMutexName = "Local\\TianshuQitanLauncher.Operations." + StablePathHash(Path.GetFullPath(catalogPath));
            this.captureDirectory = captureDirectory;
            this.currentStore = currentStore;
            EnsureCatalog();
        }

        public IList<AtomicOperationDefinition> GetDefinitions()
        {
            lock (catalogGate)
            {
                return ExecuteCatalogLocked(delegate
                {
                    OperationCatalogDocument document = ReadCatalog();
                    return (IList<AtomicOperationDefinition>)document.Operations.OrderBy(item => item.Module).ThenBy(item => item.Name)
                        .Select(item => item.Clone()).ToList();
                });
            }
        }

        public void SaveDefinition(AtomicOperationDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException("definition");
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                throw new ArgumentException("Operation template name is required.", "definition");
            }
            lock (catalogGate)
            {
                ExecuteCatalogLocked(delegate
                {
                    OperationCatalogDocument document = ReadCatalog();
                    AtomicOperationDefinition existing = document.Operations.FirstOrDefault(
                        item => string.Equals(item.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
                    AtomicOperationDefinition saved = definition.Clone();
                    if (string.IsNullOrWhiteSpace(saved.Id)) saved.Id = Guid.NewGuid().ToString("D");
                    saved.UpdatedUtc = DateTime.UtcNow;
                    if (existing == null)
                    {
                        document.Operations.Add(saved);
                    }
                    else
                    {
                        int index = document.Operations.IndexOf(existing);
                        document.Operations[index] = saved;
                    }
                    WriteCatalog(document);
                    definition.Id = saved.Id;
                    definition.UpdatedUtc = saved.UpdatedUtc;
                });
            }
        }

        public void DeleteDefinition(string definitionId)
        {
            if (string.IsNullOrWhiteSpace(definitionId)) return;
            lock (catalogGate)
            {
                ExecuteCatalogLocked(delegate
                {
                    OperationCatalogDocument document = ReadCatalog();
                    AtomicOperationDefinition existing = document.Operations.FirstOrDefault(
                        item => string.Equals(item.Id, definitionId, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        document.Operations.Remove(existing);
                        WriteCatalog(document);
                    }
                });
            }
        }

        public SessionDatabaseRenameResult RenameSessionDatabase(string sourceDatabasePath, string semanticName)
        {
            if (string.IsNullOrWhiteSpace(sourceDatabasePath))
            {
                throw new ArgumentException("请选择需要命名的会话库。", "sourceDatabasePath");
            }

            string source = Path.GetFullPath(sourceDatabasePath);
            string captureRoot = Path.GetFullPath(captureDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!source.StartsWith(captureRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("只能重命名会话目录中的 SQLite 文件。");
            }

            bool current = IsCurrent(source);
            string target = current
                ? currentStore.RenameDatabase(semanticName)
                : SqliteCaptureStore.RenameClosedDatabase(source, semanticName);
            return new SessionDatabaseRenameResult
            {
                OriginalPath = source,
                NewPath = target,
                CurrentSession = current
            };
        }

        public IList<AtomicOperationRun> GetRuns(string definitionId)
        {
            currentStore.Flush();
            List<AtomicOperationRun> runs = new List<AtomicOperationRun>();
            foreach (string database in EnumerateSessionDatabases())
            {
                if (!string.Equals(database, currentStore.DatabasePath, StringComparison.OrdinalIgnoreCase))
                {
                    try { SqliteCaptureStore.MigrateDatabase(database); }
                    catch (Exception ex) { Logger.Error("Cannot migrate operation database " + database, ex); continue; }
                }
                try
                {
                    using (SQLiteConnection connection = SqliteCaptureStore.OpenDatabase(database))
                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "SELECT id,session_id,definition_id,definition_snapshot_json,started_utc,stopped_utc," +
                            "start_boundary_ordinal,end_boundary_ordinal,status,outcome,start_state,end_state,actual_result,notes " +
                            "FROM operation_runs WHERE (@definition='' OR definition_id=@definition) ORDER BY started_utc DESC;";
                        command.Parameters.AddWithValue("@definition", definitionId ?? string.Empty);
                        using (SQLiteDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read()) runs.Add(ReadRun(reader, database));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Cannot read operation runs from " + database, ex);
                }
            }
            return runs.OrderByDescending(item => item.StartedUtc).ToList();
        }

        public IList<OperationStep> GetSteps(AtomicOperationRun run)
        {
            ValidateRun(run);
            FlushIfCurrent(run.SourceDatabasePath);
            List<OperationStep> result = new List<OperationStep>();
            using (SQLiteConnection connection = SqliteCaptureStore.OpenDatabase(run.SourceDatabasePath))
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT id,run_id,sequence,boundary_ordinal,timestamp_utc,name,description " +
                    "FROM operation_steps WHERE run_id=@run ORDER BY sequence;";
                command.Parameters.AddWithValue("@run", run.Id);
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new OperationStep
                        {
                            Id = reader.GetString(0),
                            RunId = reader.GetString(1),
                            Sequence = reader.GetInt32(2),
                            BoundaryOrdinal = reader.GetInt64(3),
                            TimestampUtc = ParseUtc(reader.GetString(4)),
                            Name = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                            Description = reader.IsDBNull(6) ? string.Empty : reader.GetString(6)
                        });
                    }
                }
            }
            return result;
        }

        public IList<ConnectionSession> GetConnections(AtomicOperationRun run)
        {
            ValidateRun(run);
            FlushIfCurrent(run.SourceDatabasePath);
            List<ConnectionSession> result = new List<ConnectionSession>();
            using (SQLiteConnection connection = SqliteCaptureStore.OpenDatabase(run.SourceDatabasePath))
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT c.id,c.session_id,c.socket_handle,c.local_endpoint,c.remote_endpoint,c.remote_port,c.kind," +
                    "c.opened_utc,c.closed_utc,c.state,c.last_error,c.client_stream_offset,c.server_stream_offset " +
                    "FROM connections c JOIN operation_connections oc ON oc.connection_id=c.id " +
                    "WHERE oc.run_id=@run AND oc.included=1 ORDER BY c.id;";
                command.Parameters.AddWithValue("@run", run.Id);
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new ConnectionSession
                        {
                            Id = reader.GetInt64(0),
                            SessionId = reader.GetInt64(1),
                            SocketHandle = new IntPtr(reader.GetInt64(2)),
                            LocalEndPoint = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            RemoteEndPoint = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                            RemotePort = reader.GetInt32(5),
                            Kind = (ConnectionKind)reader.GetInt32(6),
                            OpenedUtc = ParseUtc(reader.GetString(7)),
                            ClosedUtc = reader.IsDBNull(8) ? (DateTime?)null : ParseUtc(reader.GetString(8)),
                            State = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                            LastError = reader.GetInt32(10),
                            ClientStreamOffset = reader.GetInt64(11),
                            ServerStreamOffset = reader.GetInt64(12)
                        });
                    }
                }
            }
            return result;
        }

        public IList<OperationConnectionCandidate> GetConnectionCandidates(AtomicOperationRun run)
        {
            ValidateRun(run);
            FlushIfCurrent(run.SourceDatabasePath);
            List<OperationConnectionCandidate> result = new List<OperationConnectionCandidate>();
            using (SQLiteConnection connection = SqliteCaptureStore.OpenDatabase(run.SourceDatabasePath))
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT c.id,c.session_id,c.socket_handle,c.local_endpoint,c.remote_endpoint,c.remote_port,c.kind," +
                    "c.opened_utc,c.closed_utc,c.state,c.last_error,c.client_stream_offset,c.server_stream_offset," +
                    "COALESCE(oc.included,0),COALESCE(oc.automatic,0) FROM connections c " +
                    "LEFT JOIN operation_connections oc ON oc.connection_id=c.id AND oc.run_id=@run " +
                    "WHERE c.session_id=@session AND c.opened_utc<=@stopped AND (c.closed_utc IS NULL OR c.closed_utc>=@started) " +
                    "ORDER BY c.id;";
                command.Parameters.AddWithValue("@run", run.Id);
                command.Parameters.AddWithValue("@session", run.SessionId);
                command.Parameters.AddWithValue("@started", FormatUtc(run.StartedUtc));
                command.Parameters.AddWithValue("@stopped", FormatUtc(run.StoppedUtc ?? DateTime.UtcNow));
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new OperationConnectionCandidate
                        {
                            Connection = new ConnectionSession
                            {
                                Id = reader.GetInt64(0),
                                SessionId = reader.GetInt64(1),
                                SocketHandle = new IntPtr(reader.GetInt64(2)),
                                LocalEndPoint = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                                RemoteEndPoint = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                                RemotePort = reader.GetInt32(5),
                                Kind = (ConnectionKind)reader.GetInt32(6),
                                OpenedUtc = ParseUtc(reader.GetString(7)),
                                ClosedUtc = reader.IsDBNull(8) ? (DateTime?)null : ParseUtc(reader.GetString(8)),
                                State = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                                LastError = reader.GetInt32(10),
                                ClientStreamOffset = reader.GetInt64(11),
                                ServerStreamOffset = reader.GetInt64(12)
                            },
                            Included = reader.GetInt32(13) != 0,
                            Automatic = reader.GetInt32(14) != 0
                        });
                    }
                }
            }
            return result;
        }

        public IList<OperationFrameSample> GetFrames(AtomicOperationRun run, bool includeNoise)
        {
            ValidateRun(run);
            FlushIfCurrent(run.SourceDatabasePath);
            List<OperationFrameSample> result = new List<OperationFrameSample>();
            using (SQLiteConnection connection = SqliteCaptureStore.OpenDatabase(run.SourceDatabasePath))
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT f.id,f.direction,f.opcode,f.name,f.bytes,f.fields_json,of.capture_ordinal," +
                    "COALESCE(fa.role,0),COALESCE(fa.meaning,''),COALESCE(fa.confirmed,0) " +
                    "FROM operation_frames of JOIN frames f ON f.id=of.frame_id " +
                    "JOIN operation_connections oc ON oc.run_id=of.run_id AND oc.connection_id=f.connection_id AND oc.included=1 " +
                    "LEFT JOIN frame_annotations fa ON fa.run_id=of.run_id AND fa.frame_id=f.id " +
                    "WHERE of.run_id=@run " + (includeNoise ? string.Empty : "AND COALESCE(fa.role,0)<>@noise ") +
                    "ORDER BY of.capture_ordinal,f.id;";
                command.Parameters.AddWithValue("@run", run.Id);
                if (!includeNoise) command.Parameters.AddWithValue("@noise", (int)FrameSemanticRole.Noise);
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    int sequence = 0;
                    while (reader.Read())
                    {
                        result.Add(new OperationFrameSample
                        {
                            RunId = run.Id,
                            SourceDatabasePath = run.SourceDatabasePath,
                            FrameId = reader.GetInt64(0),
                            Sequence = ++sequence,
                            Direction = (TrafficDirection)reader.GetInt32(1),
                            Opcode = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2),
                            Name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            Bytes = (byte[])reader[4],
                            Fields = DeserializeFields(reader.IsDBNull(5) ? null : reader.GetString(5)),
                            Role = (FrameSemanticRole)reader.GetInt32(7),
                            Meaning = reader.GetString(8),
                            Confirmed = reader.GetInt32(9) != 0
                        });
                    }
                }
            }
            return result;
        }

        public IList<FieldSemanticAnnotation> GetFieldAnnotations(AtomicOperationRun run, long frameId)
        {
            ValidateRun(run);
            FlushIfCurrent(run.SourceDatabasePath);
            List<FieldSemanticAnnotation> result = new List<FieldSemanticAnnotation>();
            using (SQLiteConnection connection = SqliteCaptureStore.OpenDatabase(run.SourceDatabasePath))
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT run_id,frame_id,field_name,field_offset,field_length,semantic_name,meaning,value_type," +
                    "dynamic_kind,notes,confirmed,updated_utc FROM field_annotations WHERE run_id=@run AND frame_id=@frame;";
                command.Parameters.AddWithValue("@run", run.Id);
                command.Parameters.AddWithValue("@frame", frameId);
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new FieldSemanticAnnotation
                        {
                            RunId = reader.GetString(0),
                            FrameId = reader.GetInt64(1),
                            FieldName = reader.GetString(2),
                            Offset = reader.GetInt32(3),
                            Length = reader.GetInt32(4),
                            SemanticName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                            Meaning = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                            ValueType = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                            DynamicKind = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                            Notes = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                            Confirmed = reader.GetInt32(10) != 0,
                            UpdatedUtc = ParseUtc(reader.GetString(11))
                        });
                    }
                }
            }
            return result;
        }

        public void SaveRun(AtomicOperationRun run)
        {
            ValidateRun(run);
            if (IsCurrent(run.SourceDatabasePath)) currentStore.EnqueueOperationRun(run);
            else ExecuteRunWrite(run.SourceDatabasePath, delegate(SQLiteConnection connection) { WriteRunDirect(connection, run); });
        }

        public void SaveStep(AtomicOperationRun run, OperationStep step)
        {
            ValidateRun(run);
            if (step == null) throw new ArgumentNullException("step");
            if (IsCurrent(run.SourceDatabasePath)) currentStore.EnqueueOperationStep(step);
            else ExecuteRunWrite(run.SourceDatabasePath, delegate(SQLiteConnection connection) { WriteStepDirect(connection, step); });
        }

        public void SaveFrameAnnotation(AtomicOperationRun run, FrameSemanticAnnotation annotation)
        {
            ValidateRun(run);
            if (annotation == null) throw new ArgumentNullException("annotation");
            annotation.RunId = run.Id;
            annotation.UpdatedUtc = DateTime.UtcNow;
            if (IsCurrent(run.SourceDatabasePath)) currentStore.EnqueueFrameAnnotation(annotation);
            else ExecuteRunWrite(run.SourceDatabasePath, delegate(SQLiteConnection connection) { WriteFrameAnnotationDirect(connection, annotation); });
        }

        public void SaveFieldAnnotation(AtomicOperationRun run, FieldSemanticAnnotation annotation)
        {
            ValidateRun(run);
            if (annotation == null) throw new ArgumentNullException("annotation");
            annotation.RunId = run.Id;
            annotation.UpdatedUtc = DateTime.UtcNow;
            if (IsCurrent(run.SourceDatabasePath)) currentStore.EnqueueFieldAnnotation(annotation);
            else ExecuteRunWrite(run.SourceDatabasePath, delegate(SQLiteConnection connection) { WriteFieldAnnotationDirect(connection, annotation); });
        }

        public void SaveAttachment(AtomicOperationRun run, OperationAttachment attachment)
        {
            ValidateRun(run);
            if (attachment == null) throw new ArgumentNullException("attachment");
            attachment.RunId = run.Id;
            attachment.CreatedUtc = DateTime.UtcNow;
            if (IsCurrent(run.SourceDatabasePath)) currentStore.EnqueueOperationAttachment(attachment);
            else ExecuteRunWrite(run.SourceDatabasePath, delegate(SQLiteConnection connection) { WriteAttachmentDirect(connection, attachment); });
        }

        public void SetConnectionIncluded(AtomicOperationRun run, long connectionId, bool included)
        {
            ValidateRun(run);
            OperationConnectionLink link = new OperationConnectionLink
            {
                RunId = run.Id,
                ConnectionId = connectionId,
                Automatic = false,
                Included = included
            };
            FlushIfCurrent(run.SourceDatabasePath);
            ExecuteRunWrite(run.SourceDatabasePath, delegate(SQLiteConnection connection)
            {
                WriteConnectionDirect(connection, link);
                if (included)
                {
                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "INSERT OR IGNORE INTO operation_chunks(run_id,chunk_id,capture_ordinal) " +
                            "SELECT @run,id,capture_ordinal FROM chunks WHERE connection_id=@connection " +
                            "AND capture_ordinal>@start AND capture_ordinal<=@end; " +
                            "INSERT OR IGNORE INTO operation_frames(run_id,frame_id,capture_ordinal) " +
                            "SELECT @run,id,capture_ordinal FROM frames WHERE connection_id=@connection " +
                            "AND capture_ordinal>@start AND capture_ordinal<=@end;";
                        command.Parameters.AddWithValue("@run", run.Id);
                        command.Parameters.AddWithValue("@connection", connectionId);
                        command.Parameters.AddWithValue("@start", run.StartBoundaryOrdinal);
                        command.Parameters.AddWithValue("@end", run.EndBoundaryOrdinal ?? long.MaxValue);
                        command.ExecuteNonQuery();
                    }
                }
            });
        }

        private IEnumerable<string> EnumerateSessionDatabases()
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(captureDirectory))
            {
                foreach (string path in Directory.GetFiles(captureDirectory, "session-*.sqlite"))
                {
                    paths.Add(Path.GetFullPath(path));
                }
            }
            paths.Add(Path.GetFullPath(currentStore.DatabasePath));
            return paths;
        }

        private void EnsureCatalog()
        {
            lock (catalogGate)
            {
                ExecuteCatalogLocked(delegate
                {
                    if (File.Exists(catalogPath)) return;
                    string directory = Path.GetDirectoryName(catalogPath);
                    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                    WriteCatalog(new OperationCatalogDocument());
                });
            }
        }

        private OperationCatalogDocument ReadCatalog()
        {
            try
            {
                OperationCatalogDocument document = JsonConvert.DeserializeObject<OperationCatalogDocument>(File.ReadAllText(catalogPath));
                return document ?? new OperationCatalogDocument();
            }
            catch (Exception ex)
            {
                string backup = catalogPath + ".bak";
                if (File.Exists(backup))
                {
                    try
                    {
                        OperationCatalogDocument recovered = JsonConvert.DeserializeObject<OperationCatalogDocument>(File.ReadAllText(backup));
                        if (recovered != null)
                        {
                            Logger.Error("Recovered operation template catalog from backup " + backup, ex);
                            return recovered;
                        }
                    }
                    catch (Exception backupError)
                    {
                        Logger.Error("Cannot recover operation template catalog backup " + backup, backupError);
                    }
                }
                throw new InvalidDataException("Cannot read operation template catalog: " + catalogPath, ex);
            }
        }

        private void WriteCatalog(OperationCatalogDocument document)
        {
            string directory = Path.GetDirectoryName(catalogPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temporary = catalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(document, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(catalogPath)) File.Replace(temporary, catalogPath, catalogPath + ".bak");
                else File.Move(temporary, catalogPath);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private T ExecuteCatalogLocked<T>(Func<T> action)
        {
            using (Mutex mutex = new Mutex(false, catalogMutexName))
            {
                bool entered = false;
                try
                {
                    try { entered = mutex.WaitOne(10000); }
                    catch (AbandonedMutexException) { entered = true; }
                    if (!entered) throw new TimeoutException("等待原子操作模板文件锁超时。");
                    return action();
                }
                finally
                {
                    if (entered) mutex.ReleaseMutex();
                }
            }
        }

        private void ExecuteCatalogLocked(Action action)
        {
            ExecuteCatalogLocked(delegate
            {
                action();
                return true;
            });
        }

        private static string StablePathHash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
                StringBuilder text = new StringBuilder(16);
                for (int i = 0; i < 8; i++) text.Append(hash[i].ToString("X2"));
                return text.ToString();
            }
        }

        private void FlushIfCurrent(string databasePath)
        {
            if (IsCurrent(databasePath)) currentStore.Flush();
        }

        private bool IsCurrent(string databasePath)
        {
            return string.Equals(Path.GetFullPath(databasePath), Path.GetFullPath(currentStore.DatabasePath), StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateRun(AtomicOperationRun run)
        {
            if (run == null) throw new ArgumentNullException("run");
            if (string.IsNullOrWhiteSpace(run.SourceDatabasePath))
            {
                throw new InvalidOperationException("The operation run does not identify its source database.");
            }
        }

        private static AtomicOperationRun ReadRun(SQLiteDataReader reader, string database)
        {
            return new AtomicOperationRun
            {
                Id = reader.GetString(0),
                SessionId = reader.GetInt64(1),
                DefinitionId = reader.GetString(2),
                DefinitionSnapshotJson = reader.GetString(3),
                SourceDatabasePath = Path.GetFullPath(database),
                StartedUtc = ParseUtc(reader.GetString(4)),
                StoppedUtc = reader.IsDBNull(5) ? (DateTime?)null : ParseUtc(reader.GetString(5)),
                StartBoundaryOrdinal = reader.GetInt64(6),
                EndBoundaryOrdinal = reader.IsDBNull(7) ? (long?)null : reader.GetInt64(7),
                Status = (OperationRunStatus)reader.GetInt32(8),
                Outcome = (OperationOutcome)reader.GetInt32(9),
                StartState = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                EndState = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                ActualResult = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                Notes = reader.IsDBNull(13) ? string.Empty : reader.GetString(13)
            };
        }

        private static IList<DecodedField> DeserializeFields(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<DecodedField>();
            try { return JsonConvert.DeserializeObject<IList<DecodedField>>(json) ?? new List<DecodedField>(); }
            catch { return new List<DecodedField>(); }
        }

        private static void ExecuteRunWrite(string databasePath, Action<SQLiteConnection> action)
        {
            using (SQLiteConnection connection = SqliteCaptureStore.OpenDatabase(databasePath)) action(connection);
        }

        private static void WriteRunDirect(SQLiteConnection connection, AtomicOperationRun run)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "UPDATE operation_runs SET outcome=@outcome,actual_result=@actual,notes=@notes WHERE id=@id;";
                command.Parameters.AddWithValue("@outcome", (int)run.Outcome);
                command.Parameters.AddWithValue("@actual", run.ActualResult ?? string.Empty);
                command.Parameters.AddWithValue("@notes", run.Notes ?? string.Empty);
                command.Parameters.AddWithValue("@id", run.Id);
                command.ExecuteNonQuery();
            }
        }

        private static void WriteStepDirect(SQLiteConnection connection, OperationStep step)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE operation_steps SET name=@name,description=@description WHERE id=@id;";
                command.Parameters.AddWithValue("@name", step.Name ?? string.Empty);
                command.Parameters.AddWithValue("@description", step.Description ?? string.Empty);
                command.Parameters.AddWithValue("@id", step.Id);
                command.ExecuteNonQuery();
            }
        }

        private static void WriteFrameAnnotationDirect(SQLiteConnection connection, FrameSemanticAnnotation annotation)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "INSERT OR REPLACE INTO frame_annotations(run_id,frame_id,role,meaning,confirmed,updated_utc) VALUES(@run,@frame,@role,@meaning,@confirmed,@updated);";
                command.Parameters.AddWithValue("@run", annotation.RunId);
                command.Parameters.AddWithValue("@frame", annotation.FrameId);
                command.Parameters.AddWithValue("@role", (int)annotation.Role);
                command.Parameters.AddWithValue("@meaning", annotation.Meaning ?? string.Empty);
                command.Parameters.AddWithValue("@confirmed", annotation.Confirmed ? 1 : 0);
                command.Parameters.AddWithValue("@updated", FormatUtc(annotation.UpdatedUtc));
                command.ExecuteNonQuery();
            }
        }

        private static void WriteFieldAnnotationDirect(SQLiteConnection connection, FieldSemanticAnnotation annotation)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "INSERT OR REPLACE INTO field_annotations(run_id,frame_id,field_name,field_offset,field_length,semantic_name," +
                    "meaning,value_type,dynamic_kind,notes,confirmed,updated_utc) VALUES(@run,@frame,@field,@offset,@length," +
                    "@semantic,@meaning,@type,@dynamic,@notes,@confirmed,@updated);";
                command.Parameters.AddWithValue("@run", annotation.RunId);
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
                command.Parameters.AddWithValue("@updated", FormatUtc(annotation.UpdatedUtc));
                command.ExecuteNonQuery();
            }
        }

        private static void WriteAttachmentDirect(SQLiteConnection connection, OperationAttachment attachment)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "INSERT OR REPLACE INTO operation_attachments(id,run_id,step_id,file_name,media_type,bytes,created_utc) VALUES(@id,@run,@step,@file,@media,@bytes,@created);";
                command.Parameters.AddWithValue("@id", attachment.Id);
                command.Parameters.AddWithValue("@run", attachment.RunId);
                command.Parameters.AddWithValue("@step", (object)attachment.StepId ?? DBNull.Value);
                command.Parameters.AddWithValue("@file", attachment.FileName ?? string.Empty);
                command.Parameters.AddWithValue("@media", attachment.MediaType ?? "application/octet-stream");
                command.Parameters.AddWithValue("@bytes", attachment.Bytes ?? new byte[0]);
                command.Parameters.AddWithValue("@created", FormatUtc(attachment.CreatedUtc));
                command.ExecuteNonQuery();
            }
        }

        private static void WriteConnectionDirect(SQLiteConnection connection, OperationConnectionLink link)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "INSERT OR REPLACE INTO operation_connections(run_id,connection_id,automatic,included) VALUES(@run,@connection,@automatic,@included);";
                command.Parameters.AddWithValue("@run", link.RunId);
                command.Parameters.AddWithValue("@connection", link.ConnectionId);
                command.Parameters.AddWithValue("@automatic", link.Automatic ? 1 : 0);
                command.Parameters.AddWithValue("@included", link.Included ? 1 : 0);
                command.ExecuteNonQuery();
            }
        }

        internal static string FormatUtc(DateTime value)
        {
            return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        internal static DateTime ParseUtc(string value)
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }
    }
}
