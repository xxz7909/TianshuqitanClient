using System;
using System.Collections.Generic;
using System.Data.SQLite;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class OfflineReplayEngine
    {
        public OfflineReplayResult Replay(string databasePath, ProtocolDefinition definition)
        {
            GenericFrameDecoder decoder = new GenericFrameDecoder(definition);
            StateTracker stateTracker = new StateTracker(definition);
            Dictionary<string, FrameStream> streams = new Dictionary<string, FrameStream>();
            OfflineReplayResult result = new OfflineReplayResult();
            int lengthSamples = 0;
            int lengthMatches = 0;

            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + databasePath + ";Version=3;Read Only=True;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT c.id,c.connection_id,c.timestamp_utc,c.direction,c.stream_offset,c.effective_bytes,co.kind " +
                        "FROM chunks c LEFT JOIN connections co ON co.id=c.connection_id " +
                        "WHERE length(c.effective_bytes)>0 ORDER BY c.id;";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.ChunkCount++;
                            long chunkId = reader.GetInt64(0);
                            long connectionId = reader.GetInt64(1);
                            DateTime timestamp = DateTime.Parse(reader.GetString(2)).ToUniversalTime();
                            TrafficDirection direction = (TrafficDirection)reader.GetInt32(3);
                            long streamOffset = reader.GetInt64(4);
                            byte[] bytes = (byte[])reader[5];
                            ConnectionKind kind = reader.IsDBNull(6) ? ConnectionKind.Unknown : (ConnectionKind)reader.GetInt32(6);

                            if (kind == ConnectionKind.Game && bytes.Length >= 2)
                            {
                                int firstLength = (bytes[0] << 8) | bytes[1];
                                lengthSamples++;
                                if (firstLength >= 4 && firstLength <= bytes.Length)
                                {
                                    lengthMatches++;
                                }
                            }

                            if (definition.Framing.Mode == FramingMode.RawChunk)
                            {
                                ProtocolFrame raw;
                                int consumed;
                                decoder.TryDecode(direction, bytes, 0, bytes.Length, streamOffset, out raw, out consumed);
                                if (raw != null)
                                {
                                    Populate(raw, connectionId, chunkId, timestamp);
                                    Accept(raw, stateTracker, result);
                                }
                                continue;
                            }

                            string key = connectionId + ":" + (int)direction;
                            FrameStream stream;
                            if (!streams.TryGetValue(key, out stream))
                            {
                                stream = new FrameStream();
                                streams.Add(key, stream);
                            }
                            stream.Append(bytes);
                            ProtocolFrame frame;
                            while (stream.TryRead(decoder, direction, out frame))
                            {
                                Populate(frame, connectionId, chunkId, timestamp);
                                Accept(frame, stateTracker, result);
                            }
                        }
                    }
                }
            }

            foreach (FrameStream stream in streams.Values)
            {
                if (stream.Count > 0)
                {
                    result.IncompleteStreamCount++;
                }
            }
            result.BigEndianLengthPrefixConfidence = lengthSamples == 0 ? 0 : (double)lengthMatches / lengthSamples;
            result.SuggestedFraming = result.BigEndianLengthPrefixConfidence >= 0.8
                ? "LengthPrefix(offset=0,size=2,endian=Big,includesHeader=true)"
                : "Insufficient evidence";
            result.ObservedTransitions = stateTracker.GetObservedTransitions();
            return result;
        }

        private static void Populate(ProtocolFrame frame, long connectionId, long chunkId, DateTime timestamp)
        {
            frame.ConnectionId = connectionId;
            frame.FirstChunkId = chunkId;
            frame.TimestampUtc = timestamp;
        }

        private static void Accept(ProtocolFrame frame, StateTracker tracker, OfflineReplayResult result)
        {
            result.FrameCount++;
            long count;
            result.SignatureCounts.TryGetValue(frame.Signature, out count);
            result.SignatureCounts[frame.Signature] = count + 1;
            tracker.Accept(frame);
        }
    }
}
