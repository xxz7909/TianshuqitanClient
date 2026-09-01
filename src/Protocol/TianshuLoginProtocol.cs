using System;
using System.Collections.Generic;
using System.Text;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class TianshuGameServer
    {
        public int PacketIndex { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Address { get; set; }
        public string PortsText { get; set; }
        public IList<int> Ports { get; set; }
        public int State { get; set; }

        public TianshuGameServer()
        {
            Ports = new List<int>();
        }
    }

    public sealed class TianshuRoleSummary
    {
        public int PacketIndex { get; set; }
        public int CharacterId { get; set; }
        public string Name { get; set; }
        public string Gender { get; set; }
        public string Job { get; set; }
        public int Level { get; set; }
        public string PortraitImage { get; set; }
        public string BodyImage { get; set; }
        public string Appearance { get; set; }
        public string Nickname { get; set; }
        public int HitPoints { get; set; }
        public int ManaPoints { get; set; }
        public int Strength { get; set; }
        public int Intellect { get; set; }
        public int Vitality { get; set; }
        public int Belief { get; set; }
        public int Agility { get; set; }
        public int PhysicalAttack { get; set; }
        public int PhysicalDefense { get; set; }
        public int Speed { get; set; }
        public int Spirit { get; set; }
        public int Restore { get; set; }
        public string HonorTitle { get; set; }
        public int Status { get; set; }

        public bool IsPendingDeletion
        {
            get { return Status == 1; }
        }
    }

    public static class TianshuLoginProtocol
    {
        public const int ClientUserPassword = 0x0000;
        public const int ClientUserToken2 = 0x0006;
        public const int ClientSelectRole = 0x000A;
        public const int ServerLoginResult = 0x0001;
        public const int ServerRoleInfoList = 0x000C;
        public const int ServerRoleInfo = 0x0014;
        public const int ServerMapData = 0x0015;
        public const int ServerRoleStartPoint = 0x0016;
        public const int ServerClientToken = 0x00C8;
        public const int ServerGameServerList = 0x00C9;
        public const int ServerUserInfo = 0x00E5;

        public static bool TryGetOpcode(byte[] frame, out int opcode)
        {
            opcode = 0;
            if (!HasValidHeader(frame))
            {
                return false;
            }
            opcode = ReadUInt16(frame, 2);
            return true;
        }

        public static bool TryParseClientToken(byte[] frame, out string encodedToken, out byte[] decodedToken)
        {
            encodedToken = null;
            decodedToken = null;
            int opcode;
            if (!TryGetOpcode(frame, out opcode) || opcode != ServerClientToken)
            {
                return false;
            }

            int offset = 4;
            string token;
            if (!TryReadUtf8String(frame, ref offset, out token) || offset != frame.Length)
            {
                return false;
            }
            try
            {
                byte[] decoded = Convert.FromBase64String(token);
                if (decoded.Length == 0)
                {
                    return false;
                }
                encodedToken = token;
                decodedToken = decoded;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        public static bool TryParseGameServerList(byte[] frame, out IList<TianshuGameServer> servers, out string error)
        {
            servers = new List<TianshuGameServer>();
            error = null;
            int opcode;
            if (!TryGetOpcode(frame, out opcode) || opcode != ServerGameServerList)
            {
                error = "Not an SC_GAMESERVER_LIST frame.";
                return false;
            }
            if (frame.Length < 8)
            {
                error = "SC_GAMESERVER_LIST is shorter than its count field.";
                return false;
            }

            int offset = 4;
            uint count = ReadUInt32(frame, offset);
            offset += 4;
            if (count > 100)
            {
                error = "SC_GAMESERVER_LIST count is unreasonable.";
                return false;
            }

            List<TianshuGameServer> parsed = new List<TianshuGameServer>();
            for (int i = 0; i < count; i++)
            {
                string id;
                string name;
                string address;
                string portsText;
                if (!TryReadUtf8String(frame, ref offset, out id) ||
                    !TryReadUtf8String(frame, ref offset, out name) ||
                    !TryReadUtf8String(frame, ref offset, out address) ||
                    !TryReadUtf8String(frame, ref offset, out portsText) ||
                    offset + 4 > frame.Length)
                {
                    error = "SC_GAMESERVER_LIST record " + i + " is truncated.";
                    return false;
                }

                int state = unchecked((int)ReadUInt32(frame, offset));
                offset += 4;
                List<int> ports = new List<int>();
                string[] parts = portsText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                for (int partIndex = 0; partIndex < parts.Length; partIndex++)
                {
                    int port;
                    if (!int.TryParse(parts[partIndex].Trim(), out port) || port <= 0 || port > 65535)
                    {
                        error = "SC_GAMESERVER_LIST record " + i + " contains an invalid port.";
                        return false;
                    }
                    ports.Add(port);
                }

                parsed.Add(new TianshuGameServer
                {
                    PacketIndex = i,
                    Id = id,
                    Name = name,
                    Address = address,
                    PortsText = portsText,
                    Ports = ports,
                    State = state
                });
            }
            if (offset != frame.Length)
            {
                error = "SC_GAMESERVER_LIST has " + (frame.Length - offset) + " trailing byte(s).";
                return false;
            }
            servers = parsed;
            return true;
        }

        public static TianshuGameServer FindPhysicalLine(IList<TianshuGameServer> servers, int lineNumber)
        {
            if (servers == null || lineNumber < 1 || lineNumber > 4)
            {
                return null;
            }
            string marker = GetChineseLineMarker(lineNumber);
            for (int i = 0; i < servers.Count; i++)
            {
                string name = servers[i].Name ?? string.Empty;
                if (servers[i].State != 4 && name.IndexOf(marker, StringComparison.Ordinal) >= 0)
                {
                    return servers[i];
                }
            }
            // Older entrance servers can return only a recommended alias. It still
            // points at the same physical server, so retain it as a safe fallback.
            for (int i = 0; i < servers.Count; i++)
            {
                string name = servers[i].Name ?? string.Empty;
                if (name.IndexOf(marker, StringComparison.Ordinal) >= 0) return servers[i];
            }
            return null;
        }

        public static bool TryParseRoleInfoList(byte[] frame, out IList<TianshuRoleSummary> roles, out string error)
        {
            roles = new List<TianshuRoleSummary>();
            error = null;
            int opcode;
            if (!TryGetOpcode(frame, out opcode) || opcode != ServerRoleInfoList)
            {
                error = "Not an SC_ROLE_INFO_LIST frame.";
                return false;
            }
            if (frame.Length < 6)
            {
                error = "SC_ROLE_INFO_LIST is shorter than its count field.";
                return false;
            }

            int offset = 4;
            int count = ReadUInt16(frame, offset);
            offset += 2;
            if (count > 5)
            {
                error = "SC_ROLE_INFO_LIST count exceeds the five client role slots.";
                return false;
            }

            List<TianshuRoleSummary> parsed = new List<TianshuRoleSummary>();
            for (int i = 0; i < count; i++)
            {
                if (offset + 4 > frame.Length)
                {
                    error = "SC_ROLE_INFO_LIST record " + i + " is missing its character id.";
                    return false;
                }
                int characterId = unchecked((int)ReadUInt32(frame, offset));
                offset += 4;

                string name;
                string gender;
                string job;
                string portraitImage;
                string bodyImage;
                string appearance;
                string nickname;
                if (!TryReadUtf8String(frame, ref offset, out name) ||
                    !TryReadUtf8String(frame, ref offset, out gender) ||
                    !TryReadUtf8String(frame, ref offset, out job) ||
                    offset + 4 > frame.Length)
                {
                    error = "SC_ROLE_INFO_LIST record " + i + " is truncated before its level.";
                    return false;
                }
                int level = unchecked((int)ReadUInt32(frame, offset));
                offset += 4;
                if (!TryReadUtf8String(frame, ref offset, out portraitImage) ||
                    !TryReadUtf8String(frame, ref offset, out bodyImage) ||
                    !TryReadUtf8String(frame, ref offset, out appearance) ||
                    !TryReadUtf8String(frame, ref offset, out nickname) ||
                    offset + 48 > frame.Length)
                {
                    error = "SC_ROLE_INFO_LIST record " + i + " is truncated before its attributes.";
                    return false;
                }

                int[] attributes = new int[12];
                for (int attributeIndex = 0; attributeIndex < attributes.Length; attributeIndex++)
                {
                    attributes[attributeIndex] = unchecked((int)ReadUInt32(frame, offset));
                    offset += 4;
                }
                string honorTitle;
                if (!TryReadUtf8String(frame, ref offset, out honorTitle) || offset + 2 > frame.Length)
                {
                    error = "SC_ROLE_INFO_LIST record " + i + " is truncated before its status.";
                    return false;
                }
                int status = ReadUInt16(frame, offset);
                offset += 2;

                parsed.Add(new TianshuRoleSummary
                {
                    PacketIndex = i,
                    CharacterId = characterId,
                    Name = name,
                    Gender = gender,
                    Job = job,
                    Level = level,
                    PortraitImage = portraitImage,
                    BodyImage = bodyImage,
                    Appearance = appearance,
                    Nickname = nickname,
                    HitPoints = attributes[0],
                    ManaPoints = attributes[1],
                    Strength = attributes[2],
                    Intellect = attributes[3],
                    Vitality = attributes[4],
                    Belief = attributes[5],
                    Agility = attributes[6],
                    PhysicalAttack = attributes[7],
                    PhysicalDefense = attributes[8],
                    Speed = attributes[9],
                    Spirit = attributes[10],
                    Restore = attributes[11],
                    HonorTitle = honorTitle,
                    Status = status
                });
            }
            if (offset != frame.Length)
            {
                error = "SC_ROLE_INFO_LIST has " + (frame.Length - offset) + " trailing byte(s).";
                return false;
            }
            roles = parsed;
            return true;
        }

        public static bool TryParseSelectRole(byte[] frame, out int characterId)
        {
            characterId = 0;
            int opcode;
            if (!TryGetOpcode(frame, out opcode) || opcode != ClientSelectRole || frame.Length != 8)
            {
                return false;
            }
            characterId = unchecked((int)ReadUInt32(frame, 4));
            return true;
        }

        public static string GetChineseLineMarker(int lineNumber)
        {
            switch (lineNumber)
            {
                case 1: return "一线";
                case 2: return "二线";
                case 3: return "三线";
                case 4: return "四线";
                default: return string.Empty;
            }
        }

        internal static bool TryGetCredentialStringRanges(byte[] frame, out int userOffset, out int userLength, out int passwordOffset, out int passwordLength)
        {
            userOffset = userLength = passwordOffset = passwordLength = 0;
            int opcode;
            if (!TryGetOpcode(frame, out opcode) || opcode != ClientUserPassword)
            {
                return false;
            }
            int offset = 4;
            if (!TryGetStringRange(frame, ref offset, out userOffset, out userLength) ||
                !TryGetStringRange(frame, ref offset, out passwordOffset, out passwordLength))
            {
                return false;
            }
            return offset == frame.Length;
        }

        internal static bool TryGetTicketStringRange(byte[] frame, int expectedOpcode, out int tokenOffset, out int tokenLength)
        {
            tokenOffset = tokenLength = 0;
            int opcode;
            if (!TryGetOpcode(frame, out opcode) || opcode != expectedOpcode ||
                (opcode != ServerClientToken && opcode != ClientUserToken2))
            {
                return false;
            }
            int offset = 4;
            if (!TryGetStringRange(frame, ref offset, out tokenOffset, out tokenLength))
            {
                return false;
            }
            return opcode == ServerClientToken ? offset == frame.Length : offset + 8 <= frame.Length;
        }

        private static bool HasValidHeader(byte[] frame)
        {
            return frame != null && frame.Length >= 4 && ReadUInt16(frame, 0) == frame.Length;
        }

        private static bool TryReadUtf8String(byte[] bytes, ref int offset, out string value)
        {
            value = null;
            int stringOffset;
            int stringLength;
            if (!TryGetStringRange(bytes, ref offset, out stringOffset, out stringLength))
            {
                return false;
            }
            try
            {
                value = new UTF8Encoding(false, true).GetString(bytes, stringOffset, stringLength);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static bool TryGetStringRange(byte[] bytes, ref int offset, out int stringOffset, out int stringLength)
        {
            stringOffset = stringLength = 0;
            if (bytes == null || offset < 0 || offset + 2 > bytes.Length)
            {
                return false;
            }
            int length = ReadUInt16(bytes, offset);
            offset += 2;
            if (offset + length > bytes.Length)
            {
                return false;
            }
            stringOffset = offset;
            stringLength = length;
            offset += length;
            return true;
        }

        private static int ReadUInt16(byte[] bytes, int offset)
        {
            return (bytes[offset] << 8) | bytes[offset + 1];
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) |
                   ((uint)bytes[offset + 1] << 16) |
                   ((uint)bytes[offset + 2] << 8) |
                   bytes[offset + 3];
        }
    }

    public static class SensitiveTrafficRedactor
    {
        public static bool RedactLoginSecrets(TransportChunk chunk, ConnectionSession connection)
        {
            if (chunk == null || connection == null || connection.Kind != ConnectionKind.Game)
            {
                return false;
            }

            bool changed = false;
            byte[] original = RedactFrame(chunk.OriginalBytes, chunk.Direction, connection.RemotePort);
            if (original != null)
            {
                chunk.OriginalBytes = original;
                changed = true;
            }
            byte[] effective = RedactFrame(chunk.EffectiveBytes, chunk.Direction, connection.RemotePort);
            if (effective != null)
            {
                chunk.EffectiveBytes = effective;
                changed = true;
            }
            if (changed)
            {
                const string note = "Sensitive login fields or tickets redacted before capture persistence.";
                chunk.Note = string.IsNullOrWhiteSpace(chunk.Note) ? note : chunk.Note + " " + note;
            }
            return changed;
        }

        public static bool RedactLoginCredentials(TransportChunk chunk, ConnectionSession connection)
        {
            if (chunk == null || connection == null ||
                chunk.Direction != TrafficDirection.ClientToServer ||
                connection.RemotePort < 7800 || connection.RemotePort > 7803)
            {
                return false;
            }

            bool changed = false;
            byte[] original = RedactCredentialFrame(chunk.OriginalBytes);
            if (original != null)
            {
                chunk.OriginalBytes = original;
                changed = true;
            }
            byte[] effective = RedactCredentialFrame(chunk.EffectiveBytes);
            if (effective != null)
            {
                chunk.EffectiveBytes = effective;
                changed = true;
            }
            if (changed)
            {
                const string note = "Sensitive login fields redacted before capture persistence.";
                chunk.Note = string.IsNullOrWhiteSpace(chunk.Note) ? note : chunk.Note + " " + note;
            }
            return changed;
        }

        private static byte[] RedactFrame(byte[] bytes, TrafficDirection direction, int remotePort)
        {
            int opcode;
            if (!TianshuLoginProtocol.TryGetOpcode(bytes, out opcode)) return null;
            if (direction == TrafficDirection.ClientToServer &&
                opcode == TianshuLoginProtocol.ClientUserPassword &&
                remotePort >= 7800 && remotePort <= 7803)
            {
                return RedactCredentialFrame(bytes);
            }

            int tokenOffset;
            int tokenLength;
            bool isServerTicket = direction == TrafficDirection.ServerToClient &&
                opcode == TianshuLoginProtocol.ServerClientToken;
            bool isClientTicket = direction == TrafficDirection.ClientToServer &&
                opcode == TianshuLoginProtocol.ClientUserToken2;
            if ((!isServerTicket && !isClientTicket) ||
                !TianshuLoginProtocol.TryGetTicketStringRange(bytes, opcode, out tokenOffset, out tokenLength))
            {
                return null;
            }
            byte[] copy = HexCodec.Copy(bytes);
            Fill(copy, tokenOffset, tokenLength, (byte)'*');
            return copy;
        }

        private static byte[] RedactCredentialFrame(byte[] bytes)
        {
            int userOffset;
            int userLength;
            int passwordOffset;
            int passwordLength;
            if (!TianshuLoginProtocol.TryGetCredentialStringRanges(
                bytes, out userOffset, out userLength, out passwordOffset, out passwordLength))
            {
                return null;
            }
            byte[] copy = HexCodec.Copy(bytes);
            Fill(copy, userOffset, userLength, (byte)'*');
            Fill(copy, passwordOffset, passwordLength, (byte)'*');
            return copy;
        }

        private static void Fill(byte[] bytes, int offset, int length, byte value)
        {
            for (int i = 0; i < length; i++)
            {
                bytes[offset + i] = value;
            }
        }
    }
}
