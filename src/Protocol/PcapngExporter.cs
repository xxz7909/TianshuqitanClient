using System;
using System.Data.SQLite;
using System.IO;
using System.Text;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class PcapngExporter : IPcapngExporter
    {
        private const ushort DltUser0 = 147;

        public void Export(string databasePath, string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            {
                throw new FileNotFoundException("Capture database not found.", databasePath);
            }

            using (FileStream stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + databasePath + ";Version=3;Read Only=True;"))
            {
                connection.Open();
                WriteSectionHeader(writer);
                WriteInterfaceDescription(writer);

                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT timestamp_utc, connection_id, direction, operation, stream_offset, rule_action, " +
                        "original_bytes, effective_bytes FROM chunks ORDER BY id;";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            DateTime timestamp = DateTime.Parse(reader.GetString(0)).ToUniversalTime();
                            long connectionId = reader.GetInt64(1);
                            byte direction = (byte)reader.GetInt32(2);
                            byte operation = (byte)reader.GetInt32(3);
                            long streamOffset = reader.GetInt64(4);
                            byte ruleAction = (byte)reader.GetInt32(5);
                            byte[] original = (byte[])reader[6];
                            byte[] effective = (byte[])reader[7];
                            WritePacket(writer, timestamp, connectionId, direction, operation, streamOffset, ruleAction, original, effective);
                        }
                    }
                }
            }
        }

        public void WriteWiresharkDissector(string destinationPath)
        {
            const string script = @"-- Tianshu Qitan DLT_USER0 application payload dissector
local tsq = Proto('tsq_workbench', 'Tianshu Qitan Workbench')
local f_magic = ProtoField.string('tsq.magic', 'Magic')
local f_version = ProtoField.uint8('tsq.version', 'Version')
local f_direction = ProtoField.uint8('tsq.direction', 'Direction', base.DEC, {[0]='C2S',[1]='S2C'})
local f_operation = ProtoField.uint8('tsq.operation', 'Operation', base.DEC)
local f_action = ProtoField.uint8('tsq.rule_action', 'Rule action', base.DEC)
local f_connection = ProtoField.uint64('tsq.connection', 'Connection ID', base.DEC)
local f_offset = ProtoField.uint64('tsq.stream_offset', 'Stream offset', base.DEC)
local f_original_len = ProtoField.uint32('tsq.original_length', 'Original length', base.DEC)
local f_effective_len = ProtoField.uint32('tsq.effective_length', 'Effective length', base.DEC)
local f_original = ProtoField.bytes('tsq.original', 'Original bytes')
local f_effective = ProtoField.bytes('tsq.effective', 'Effective bytes')
tsq.fields = {f_magic,f_version,f_direction,f_operation,f_action,f_connection,f_offset,
              f_original_len,f_effective_len,f_original,f_effective}

function tsq.dissector(buffer, pinfo, tree)
    if buffer:len() < 32 or buffer(0,4):string() ~= 'TSQ1' then return 0 end
    pinfo.cols.protocol = 'TSQ'
    local root = tree:add(tsq, buffer())
    root:add(f_magic, buffer(0,4))
    root:add(f_version, buffer(4,1))
    root:add(f_direction, buffer(5,1))
    root:add(f_operation, buffer(6,1))
    root:add(f_action, buffer(7,1))
    root:add_le(f_connection, buffer(8,8))
    root:add_le(f_offset, buffer(16,8))
    local olen = buffer(24,4):le_uint()
    local elen = buffer(28,4):le_uint()
    root:add_le(f_original_len, buffer(24,4))
    root:add_le(f_effective_len, buffer(28,4))
    if 32 + olen + elen <= buffer:len() then
        root:add(f_original, buffer(32,olen))
        root:add(f_effective, buffer(32+olen,elen))
    end
    return buffer:len()
end

DissectorTable.get('wtap_encap'):add(wtap.USER0, tsq)
";
            File.WriteAllText(destinationPath, script, new UTF8Encoding(false));
        }

        private static void WriteSectionHeader(BinaryWriter writer)
        {
            writer.Write(0x0A0D0D0Au);
            writer.Write(28u);
            writer.Write(0x1A2B3C4Du);
            writer.Write((ushort)1);
            writer.Write((ushort)0);
            writer.Write(-1L);
            writer.Write(28u);
        }

        private static void WriteInterfaceDescription(BinaryWriter writer)
        {
            writer.Write(1u);
            writer.Write(20u);
            writer.Write(DltUser0);
            writer.Write((ushort)0);
            writer.Write(1024u * 1024u * 4u);
            writer.Write(20u);
        }

        private static void WritePacket(
            BinaryWriter writer,
            DateTime timestamp,
            long connectionId,
            byte direction,
            byte operation,
            long streamOffset,
            byte ruleAction,
            byte[] original,
            byte[] effective)
        {
            original = original ?? new byte[0];
            effective = effective ?? new byte[0];
            int packetLength = 32 + original.Length + effective.Length;
            int paddedLength = (packetLength + 3) & ~3;
            uint blockLength = (uint)(32 + paddedLength);
            long microseconds = (timestamp.ToUniversalTime().Ticks - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks) / 10;

            writer.Write(6u);
            writer.Write(blockLength);
            writer.Write(0u);
            writer.Write((uint)((ulong)microseconds >> 32));
            writer.Write((uint)((ulong)microseconds & 0xFFFFFFFFu));
            writer.Write((uint)packetLength);
            writer.Write((uint)packetLength);

            writer.Write(Encoding.ASCII.GetBytes("TSQ1"));
            writer.Write((byte)1);
            writer.Write(direction);
            writer.Write(operation);
            writer.Write(ruleAction);
            writer.Write(connectionId);
            writer.Write(streamOffset);
            writer.Write(original.Length);
            writer.Write(effective.Length);
            writer.Write(original);
            writer.Write(effective);
            for (int i = packetLength; i < paddedLength; i++)
            {
                writer.Write((byte)0);
            }
            writer.Write(blockLength);
        }
    }
}
