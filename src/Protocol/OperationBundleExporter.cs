using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class OperationBundleExporter : IOperationBundleExporter
    {
        public void Export(IList<AtomicOperationRun> runs, OperationComparison comparison, string destinationPath)
        {
            if (runs == null || runs.Count == 0) throw new ArgumentException("Select at least one operation run.", "runs");
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination path is required.", "destinationPath");
            foreach (AtomicOperationRun run in runs)
            {
                if (run == null || string.IsNullOrWhiteSpace(run.SourceDatabasePath) || !File.Exists(run.SourceDatabasePath))
                {
                    throw new FileNotFoundException("An operation run source database is unavailable.", run == null ? null : run.SourceDatabasePath);
                }
            }

            string fullPath = Path.GetFullPath(destinationPath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            DeleteIfExists(fullPath);
            DeleteIfExists(fullPath + "-wal");
            DeleteIfExists(fullPath + "-shm");

            using (SQLiteConnection destination = new SQLiteConnection("Data Source=" + fullPath + ";Version=3;Pooling=False;"))
            {
                destination.Open();
                CreateSchema(destination);
                WriteManifest(destination, "format", "tsqop-sqlite");
                WriteManifest(destination, "format_version", "1");
                WriteManifest(destination, "created_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                WriteManifest(destination, "run_count", runs.Count.ToString(CultureInfo.InvariantCulture));
                WriteManifest(destination, "contains_original_bytes", "true");
                WriteManifest(destination, "contains_effective_bytes", "true");
                WriteManifest(destination, "credentials_included", "false");

                int sourceNumber = 0;
                foreach (IGrouping<string, AtomicOperationRun> sourceGroup in runs.GroupBy(
                    item => Path.GetFullPath(item.SourceDatabasePath), StringComparer.OrdinalIgnoreCase))
                {
                    string sourceKey = "source-" + (++sourceNumber).ToString(CultureInfo.InvariantCulture);
                    CopySource(destination, sourceGroup.Key, sourceKey, sourceGroup.ToList());
                }

                if (comparison != null)
                {
                    using (SQLiteCommand command = destination.CreateCommand())
                    {
                        command.CommandText =
                            "INSERT INTO bundle_comparisons(id,definition_id,created_utc,run_ids_json,result_json) " +
                            "VALUES(@id,@definition,@created,@runs,@result);";
                        command.Parameters.AddWithValue("@id", comparison.Id);
                        command.Parameters.AddWithValue("@definition", comparison.DefinitionId ?? string.Empty);
                        command.Parameters.AddWithValue("@created", comparison.CreatedUtc.ToString("o", CultureInfo.InvariantCulture));
                        command.Parameters.AddWithValue("@runs", JsonConvert.SerializeObject(comparison.RunIds));
                        command.Parameters.AddWithValue("@result", JsonConvert.SerializeObject(comparison));
                        command.ExecuteNonQuery();
                    }
                }
                using (SQLiteCommand command = destination.CreateCommand())
                {
                    command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=DELETE; VACUUM;";
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void CopySource(
            SQLiteConnection destination,
            string sourcePath,
            string sourceKey,
            IList<AtomicOperationRun> runs)
        {
            using (SQLiteCommand attach = destination.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE @source AS source_db;";
                attach.Parameters.AddWithValue("@source", sourcePath);
                attach.ExecuteNonQuery();
            }
            try
            {
                using (SQLiteTransaction transaction = destination.BeginTransaction())
                {
                    using (SQLiteCommand command = destination.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText =
                            "INSERT INTO bundle_sources(source_key,file_name) VALUES(@source,@file);";
                        command.Parameters.AddWithValue("@source", sourceKey);
                        command.Parameters.AddWithValue("@file", Path.GetFileName(sourcePath));
                        command.ExecuteNonQuery();
                    }
                    for (int i = 0; i < runs.Count; i++) CopyRun(destination, transaction, sourceKey, runs[i]);
                    transaction.Commit();
                }
            }
            finally
            {
                using (SQLiteCommand detach = destination.CreateCommand())
                {
                    detach.CommandText = "DETACH DATABASE source_db;";
                    detach.ExecuteNonQuery();
                }
            }
        }

        private static void CopyRun(
            SQLiteConnection destination,
            SQLiteTransaction transaction,
            string sourceKey,
            AtomicOperationRun run)
        {
            ExecuteCopy(destination, transaction,
                "INSERT OR REPLACE INTO bundle_operation_runs " +
                "SELECT @source,id,session_id,definition_id,definition_snapshot_json,started_utc,stopped_utc," +
                "start_boundary_ordinal,end_boundary_ordinal,status,outcome,start_state,end_state,actual_result,notes " +
                "FROM source_db.operation_runs WHERE id=@run;", sourceKey, run.Id);
            ExecuteCopy(destination, transaction,
                "INSERT OR IGNORE INTO bundle_connections " +
                "SELECT @source,c.id,c.session_id,c.socket_handle,c.local_endpoint,c.remote_endpoint,c.remote_port,c.kind," +
                "c.opened_utc,c.closed_utc,c.state,c.last_error,c.client_stream_offset,c.server_stream_offset " +
                "FROM source_db.connections c JOIN source_db.operation_connections oc ON oc.connection_id=c.id " +
                "WHERE oc.run_id=@run AND oc.included=1;", sourceKey, run.Id);
            ExecuteCopy(destination, transaction,
                "INSERT OR REPLACE INTO bundle_operation_connections " +
                "SELECT @source,run_id,connection_id,automatic,included FROM source_db.operation_connections " +
                "WHERE run_id=@run AND included=1;",
                sourceKey, run.Id);
            ExecuteCopy(destination, transaction,
                "INSERT OR IGNORE INTO bundle_chunks " +
                "SELECT @source,c.id,c.session_id,c.connection_id,c.timestamp_utc,c.direction,c.operation,c.stream_offset," +
                "c.thread_id,c.native_result,c.native_error,c.original_bytes,c.effective_bytes,c.rule_action,c.rule_id,c.note,c.capture_ordinal " +
                "FROM source_db.chunks c JOIN source_db.operation_chunks oc ON oc.chunk_id=c.id " +
                "JOIN source_db.operation_connections cn ON cn.run_id=oc.run_id AND cn.connection_id=c.connection_id AND cn.included=1 " +
                "WHERE oc.run_id=@run;", sourceKey, run.Id);
            ExecuteCopy(destination, transaction,
                "INSERT OR REPLACE INTO bundle_operation_chunks " +
                "SELECT @source,oc.run_id,oc.chunk_id,oc.capture_ordinal FROM source_db.operation_chunks oc " +
                "JOIN source_db.chunks c ON c.id=oc.chunk_id " +
                "JOIN source_db.operation_connections cn ON cn.run_id=oc.run_id AND cn.connection_id=c.connection_id " +
                "WHERE oc.run_id=@run AND cn.included=1;",
                sourceKey, run.Id);
            ExecuteCopy(destination, transaction,
                "INSERT OR IGNORE INTO bundle_frames " +
                "SELECT @source,f.id,f.session_id,f.connection_id,f.first_chunk_id,f.timestamp_utc,f.direction,f.stream_offset," +
                "f.bytes,f.opcode,f.name,f.status,f.parse_error,f.fields_json,f.capture_ordinal " +
                "FROM source_db.frames f JOIN source_db.operation_frames ofr ON ofr.frame_id=f.id " +
                "JOIN source_db.operation_connections cn ON cn.run_id=ofr.run_id AND cn.connection_id=f.connection_id AND cn.included=1 " +
                "WHERE ofr.run_id=@run;", sourceKey, run.Id);
            ExecuteCopy(destination, transaction,
                "INSERT OR REPLACE INTO bundle_operation_frames " +
                "SELECT @source,ofr.run_id,ofr.frame_id,ofr.capture_ordinal FROM source_db.operation_frames ofr " +
                "JOIN source_db.frames f ON f.id=ofr.frame_id " +
                "JOIN source_db.operation_connections cn ON cn.run_id=ofr.run_id AND cn.connection_id=f.connection_id " +
                "WHERE ofr.run_id=@run AND cn.included=1;",
                sourceKey, run.Id);
            ExecuteCopy(destination, transaction,
                "INSERT INTO bundle_state_transitions " +
                "SELECT @source,s.id,s.session_id,s.connection_id,s.timestamp_utc,s.from_state,s.to_state,s.trigger,s.confirmed,s.capture_ordinal " +
                "FROM source_db.state_transitions s JOIN source_db.operation_connections cn ON cn.connection_id=s.connection_id " +
                "WHERE cn.run_id=@run AND cn.included=1 AND s.capture_ordinal>@startOrdinal AND s.capture_ordinal<=@endOrdinal;",
                sourceKey, run.Id, run.StartBoundaryOrdinal, run.EndBoundaryOrdinal ?? long.MaxValue);
            ExecuteCopy(destination, transaction,
                "INSERT INTO bundle_operation_states " +
                "SELECT @source,s.id,s.run_id,s.connection_id,s.capture_ordinal,s.timestamp_utc,s.from_state,s.to_state,s.trigger " +
                "FROM source_db.operation_states s JOIN source_db.operation_connections cn " +
                "ON cn.run_id=s.run_id AND cn.connection_id=s.connection_id " +
                "WHERE s.run_id=@run AND cn.included=1;", sourceKey, run.Id);
            ExecuteCopy(destination, transaction,
                "INSERT OR REPLACE INTO bundle_operation_steps " +
                "SELECT @source,id,run_id,sequence,boundary_ordinal,timestamp_utc,name,description " +
                "FROM source_db.operation_steps WHERE run_id=@run;", sourceKey, run.Id);
            ExecuteCopy(destination, transaction,
                "INSERT OR REPLACE INTO bundle_frame_annotations " +
                "SELECT @source,a.run_id,a.frame_id,a.role,a.meaning,a.confirmed,a.updated_utc " +
                "FROM source_db.frame_annotations a JOIN source_db.operation_frames ofr " +
                "ON ofr.run_id=a.run_id AND ofr.frame_id=a.frame_id JOIN source_db.frames f ON f.id=a.frame_id " +
                "JOIN source_db.operation_connections cn ON cn.run_id=a.run_id AND cn.connection_id=f.connection_id " +
                "WHERE a.run_id=@run AND cn.included=1;", sourceKey, run.Id);
            ExecuteCopy(destination, transaction,
                "INSERT OR REPLACE INTO bundle_field_annotations " +
                "SELECT @source,a.run_id,a.frame_id,a.field_name,a.field_offset,a.field_length,a.semantic_name,a.meaning,a.value_type," +
                "a.dynamic_kind,a.notes,a.confirmed,a.updated_utc FROM source_db.field_annotations a " +
                "JOIN source_db.frames f ON f.id=a.frame_id JOIN source_db.operation_connections cn " +
                "ON cn.run_id=a.run_id AND cn.connection_id=f.connection_id WHERE a.run_id=@run AND cn.included=1;",
                sourceKey, run.Id);
            ExecuteCopy(destination, transaction,
                "INSERT OR REPLACE INTO bundle_attachments " +
                "SELECT @source,id,run_id,step_id,file_name,media_type,bytes,created_utc " +
                "FROM source_db.operation_attachments WHERE run_id=@run;", sourceKey, run.Id);
        }

        private static void ExecuteCopy(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            string sql,
            string sourceKey,
            string runId)
        {
            ExecuteCopy(connection, transaction, sql, sourceKey, runId, 0, long.MaxValue);
        }

        private static void ExecuteCopy(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            string sql,
            string sourceKey,
            string runId,
            long startOrdinal,
            long endOrdinal)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("@source", sourceKey);
                command.Parameters.AddWithValue("@run", runId);
                command.Parameters.AddWithValue("@startOrdinal", startOrdinal);
                command.Parameters.AddWithValue("@endOrdinal", endOrdinal);
                command.ExecuteNonQuery();
            }
        }

        private static void CreateSchema(SQLiteConnection connection)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
PRAGMA foreign_keys=OFF;
CREATE TABLE bundle_manifest(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE bundle_sources(source_key TEXT PRIMARY KEY,file_name TEXT NOT NULL);
CREATE TABLE bundle_operation_runs(source_key TEXT NOT NULL,id TEXT PRIMARY KEY,session_id INTEGER,definition_id TEXT,
 definition_snapshot_json TEXT,started_utc TEXT,stopped_utc TEXT,start_boundary_ordinal INTEGER,end_boundary_ordinal INTEGER,
 status INTEGER,outcome INTEGER,start_state TEXT,end_state TEXT,actual_result TEXT,notes TEXT);
CREATE TABLE bundle_connections(source_key TEXT,id INTEGER,session_id INTEGER,socket_handle INTEGER,local_endpoint TEXT,
 remote_endpoint TEXT,remote_port INTEGER,kind INTEGER,opened_utc TEXT,closed_utc TEXT,state TEXT,last_error INTEGER,
 client_stream_offset INTEGER,server_stream_offset INTEGER,PRIMARY KEY(source_key,id));
CREATE TABLE bundle_chunks(source_key TEXT,id INTEGER,session_id INTEGER,connection_id INTEGER,timestamp_utc TEXT,direction INTEGER,
 operation INTEGER,stream_offset INTEGER,thread_id INTEGER,native_result INTEGER,native_error INTEGER,original_bytes BLOB,
 effective_bytes BLOB,rule_action INTEGER,rule_id TEXT,note TEXT,capture_ordinal INTEGER,PRIMARY KEY(source_key,id));
CREATE TABLE bundle_frames(source_key TEXT,id INTEGER,session_id INTEGER,connection_id INTEGER,first_chunk_id INTEGER,
 timestamp_utc TEXT,direction INTEGER,stream_offset INTEGER,bytes BLOB,opcode INTEGER,name TEXT,status INTEGER,
 parse_error TEXT,fields_json TEXT,capture_ordinal INTEGER,PRIMARY KEY(source_key,id));
CREATE TABLE bundle_state_transitions(source_key TEXT,id INTEGER,session_id INTEGER,connection_id INTEGER,timestamp_utc TEXT,
 from_state TEXT,to_state TEXT,trigger TEXT,confirmed INTEGER,capture_ordinal INTEGER,PRIMARY KEY(source_key,id));
CREATE TABLE bundle_operation_connections(source_key TEXT,run_id TEXT,connection_id INTEGER,automatic INTEGER,included INTEGER,
 PRIMARY KEY(source_key,run_id,connection_id));
CREATE TABLE bundle_operation_chunks(source_key TEXT,run_id TEXT,chunk_id INTEGER,capture_ordinal INTEGER,
 PRIMARY KEY(source_key,run_id,chunk_id));
CREATE TABLE bundle_operation_frames(source_key TEXT,run_id TEXT,frame_id INTEGER,capture_ordinal INTEGER,
 PRIMARY KEY(source_key,run_id,frame_id));
CREATE TABLE bundle_operation_states(source_key TEXT,id INTEGER,run_id TEXT,connection_id INTEGER,capture_ordinal INTEGER,
 timestamp_utc TEXT,from_state TEXT,to_state TEXT,trigger TEXT,PRIMARY KEY(source_key,id));
CREATE TABLE bundle_operation_steps(source_key TEXT,id TEXT,run_id TEXT,sequence INTEGER,boundary_ordinal INTEGER,
 timestamp_utc TEXT,name TEXT,description TEXT,PRIMARY KEY(source_key,id));
CREATE TABLE bundle_frame_annotations(source_key TEXT,run_id TEXT,frame_id INTEGER,role INTEGER,meaning TEXT,
 confirmed INTEGER,updated_utc TEXT,PRIMARY KEY(source_key,run_id,frame_id));
CREATE TABLE bundle_field_annotations(source_key TEXT,run_id TEXT,frame_id INTEGER,field_name TEXT,field_offset INTEGER,
 field_length INTEGER,semantic_name TEXT,meaning TEXT,value_type TEXT,dynamic_kind TEXT,notes TEXT,confirmed INTEGER,
 updated_utc TEXT,PRIMARY KEY(source_key,run_id,frame_id,field_name,field_offset));
CREATE TABLE bundle_attachments(source_key TEXT,id TEXT,run_id TEXT,step_id TEXT,file_name TEXT,media_type TEXT,bytes BLOB,
 created_utc TEXT,PRIMARY KEY(source_key,id));
CREATE TABLE bundle_comparisons(id TEXT PRIMARY KEY,definition_id TEXT,created_utc TEXT,run_ids_json TEXT,result_json TEXT);
CREATE INDEX ix_bundle_frames_run ON bundle_operation_frames(run_id,capture_ordinal);
CREATE INDEX ix_bundle_chunks_run ON bundle_operation_chunks(run_id,capture_ordinal);
PRAGMA user_version=1;";
                command.ExecuteNonQuery();
            }
        }

        private static void WriteManifest(SQLiteConnection connection, string key, string value)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO bundle_manifest(key,value) VALUES(@key,@value);";
                command.Parameters.AddWithValue("@key", key);
                command.Parameters.AddWithValue("@value", value);
                command.ExecuteNonQuery();
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
