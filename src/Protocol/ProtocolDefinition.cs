using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace TianshuQitanLauncher.Protocol
{
    public enum FramingMode
    {
        RawChunk = 0,
        FixedLength = 1,
        LengthPrefix = 2,
        Delimiter = 3
    }

    public enum ByteOrder
    {
        Big = 0,
        Little = 1
    }

    public sealed class FramingDefinition
    {
        public FramingMode Mode { get; set; }
        public int FixedLength { get; set; }
        public int LengthOffset { get; set; }
        public int LengthSize { get; set; }
        public ByteOrder LengthEndian { get; set; }
        public bool LengthIncludesHeader { get; set; }
        public int HeaderLength { get; set; }
        public string DelimiterHex { get; set; }
        public int MaximumFrameLength { get; set; }

        public FramingDefinition()
        {
            Mode = FramingMode.RawChunk;
            LengthSize = 2;
            LengthEndian = ByteOrder.Big;
            LengthIncludesHeader = true;
            DelimiterHex = "00";
            MaximumFrameLength = 1024 * 1024;
        }
    }

    public sealed class FieldDefinition
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public int Offset { get; set; }
        public int Length { get; set; }
        public ByteOrder Endian { get; set; }
        public IDictionary<string, string> EnumValues { get; set; }

        public FieldDefinition()
        {
            Type = "hex";
            Endian = ByteOrder.Big;
            EnumValues = new Dictionary<string, string>();
        }
    }

    public sealed class StateTransitionDefinition
    {
        public string FromState { get; set; }
        public string ToState { get; set; }
        public TrafficDirection? Direction { get; set; }
        public long? Opcode { get; set; }
        public string Event { get; set; }
        public IDictionary<string, string> FieldEquals { get; set; }

        public StateTransitionDefinition()
        {
            FieldEquals = new Dictionary<string, string>();
        }
    }

    public sealed class MessageDefinition
    {
        public TrafficDirection? Direction { get; set; }
        public long Opcode { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public IList<FieldDefinition> Fields { get; set; }

        public MessageDefinition()
        {
            Status = "unconfirmed";
            Fields = new List<FieldDefinition>();
        }
    }

    public sealed class ProtocolDefinition
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public FramingDefinition Framing { get; set; }
        public int OpcodeOffset { get; set; }
        public int OpcodeSize { get; set; }
        public ByteOrder OpcodeEndian { get; set; }
        public IList<FieldDefinition> Fields { get; set; }
        public IList<MessageDefinition> Messages { get; set; }
        public IList<StateTransitionDefinition> StateTransitions { get; set; }

        public ProtocolDefinition()
        {
            Name = "Discovery";
            Version = "0.1.0";
            Framing = new FramingDefinition();
            OpcodeEndian = ByteOrder.Big;
            Fields = new List<FieldDefinition>();
            Messages = new List<MessageDefinition>();
            StateTransitions = new List<StateTransitionDefinition>();
        }

        public static ProtocolDefinition Load(string path)
        {
            if (!File.Exists(path))
            {
                return new ProtocolDefinition();
            }
            ProtocolDefinition definition = JsonConvert.DeserializeObject<ProtocolDefinition>(File.ReadAllText(path));
            return definition ?? new ProtocolDefinition();
        }
    }

    public sealed class GenericFrameDecoder : IFrameDecoder
    {
        private readonly ProtocolDefinition definition;
        private readonly byte[] delimiter;

        public GenericFrameDecoder(ProtocolDefinition definition)
        {
            this.definition = definition ?? new ProtocolDefinition();
            delimiter = SafeParseHex(this.definition.Framing.DelimiterHex);
        }

        public ProtocolDefinition Definition
        {
            get { return definition; }
        }

        public bool TryDecode(
            TrafficDirection direction,
            byte[] buffer,
            int offset,
            int count,
            long streamOffset,
            out ProtocolFrame frame,
            out int consumed)
        {
            frame = null;
            consumed = 0;
            if (buffer == null || count <= 0)
            {
                return false;
            }

            int frameLength;
            switch (definition.Framing.Mode)
            {
                case FramingMode.FixedLength:
                    frameLength = definition.Framing.FixedLength;
                    if (frameLength <= 0 || count < frameLength)
                    {
                        return false;
                    }
                    break;

                case FramingMode.LengthPrefix:
                    int prefixEnd = definition.Framing.LengthOffset + definition.Framing.LengthSize;
                    if (definition.Framing.LengthOffset < 0 || count < prefixEnd)
                    {
                        return false;
                    }
                    long encodedLength = ReadUnsigned(
                        buffer,
                        offset + definition.Framing.LengthOffset,
                        definition.Framing.LengthSize,
                        definition.Framing.LengthEndian);
                    if (encodedLength > int.MaxValue)
                    {
                        consumed = 1;
                        return false;
                    }
                    frameLength = (int)encodedLength;
                    if (!definition.Framing.LengthIncludesHeader)
                    {
                        frameLength += definition.Framing.HeaderLength;
                    }
                    if (frameLength <= 0 || frameLength > definition.Framing.MaximumFrameLength)
                    {
                        consumed = 1;
                        return false;
                    }
                    if (count < frameLength)
                    {
                        return false;
                    }
                    break;

                case FramingMode.Delimiter:
                    if (delimiter.Length == 0)
                    {
                        return false;
                    }
                    int delimiterIndex = IndexOf(buffer, offset, count, delimiter);
                    if (delimiterIndex < 0)
                    {
                        return false;
                    }
                    frameLength = delimiterIndex - offset + delimiter.Length;
                    break;

                default:
                    frameLength = count;
                    break;
            }

            byte[] bytes = new byte[frameLength];
            Buffer.BlockCopy(buffer, offset, bytes, 0, frameLength);
            frame = DecodeFields(direction, bytes, streamOffset);
            consumed = frameLength;
            return true;
        }

        private ProtocolFrame DecodeFields(TrafficDirection direction, byte[] bytes, long streamOffset)
        {
            ProtocolFrame frame = new ProtocolFrame();
            frame.Direction = direction;
            frame.StreamOffset = streamOffset;
            frame.Bytes = bytes;
            frame.Status = FrameStatus.Decoded;

            if (definition.OpcodeSize > 0 && definition.OpcodeOffset >= 0 &&
                definition.OpcodeOffset + definition.OpcodeSize <= bytes.Length)
            {
                frame.Opcode = ReadUnsigned(bytes, definition.OpcodeOffset, definition.OpcodeSize, definition.OpcodeEndian);
            }

            IList<FieldDefinition> fields = definition.Fields;
            if (frame.Opcode.HasValue && definition.Messages != null)
            {
                for (int i = 0; i < definition.Messages.Count; i++)
                {
                    MessageDefinition message = definition.Messages[i];
                    if (message.Opcode == frame.Opcode.Value &&
                        (!message.Direction.HasValue || message.Direction.Value == direction))
                    {
                        frame.Name = message.Name;
                        if (message.Fields != null && message.Fields.Count > 0)
                        {
                            fields = message.Fields;
                        }
                        break;
                    }
                }
            }

            for (int i = 0; i < fields.Count; i++)
            {
                FieldDefinition fieldDefinition = fields[i];
                DecodedField field = DecodeField(bytes, fieldDefinition);
                if (field != null)
                {
                    frame.Fields.Add(field);
                }
            }
            return frame;
        }

        private static DecodedField DecodeField(byte[] bytes, FieldDefinition definition)
        {
            if (definition == null || definition.Offset < 0 || definition.Length <= 0 ||
                definition.Offset + definition.Length > bytes.Length)
            {
                return null;
            }

            string type = (definition.Type ?? "hex").ToLowerInvariant();
            string value;
            if (type == "u8" || type == "u16" || type == "u32" || type == "uint")
            {
                value = ReadUnsigned(bytes, definition.Offset, definition.Length, definition.Endian)
                    .ToString(CultureInfo.InvariantCulture);
            }
            else if (type == "ascii")
            {
                value = Encoding.ASCII.GetString(bytes, definition.Offset, definition.Length).TrimEnd('\0');
            }
            else if (type == "utf8")
            {
                value = Encoding.UTF8.GetString(bytes, definition.Offset, definition.Length).TrimEnd('\0');
            }
            else
            {
                byte[] segment = new byte[definition.Length];
                Buffer.BlockCopy(bytes, definition.Offset, segment, 0, segment.Length);
                value = HexCodec.Format(segment);
            }

            string display = value;
            if (definition.EnumValues != null && definition.EnumValues.ContainsKey(value))
            {
                display = definition.EnumValues[value];
            }

            return new DecodedField
            {
                Name = definition.Name,
                Type = definition.Type,
                Offset = definition.Offset,
                Length = definition.Length,
                Value = value,
                DisplayValue = display
            };
        }

        internal static long ReadUnsigned(byte[] bytes, int offset, int length, ByteOrder order)
        {
            if (length <= 0 || length > 8 || offset < 0 || offset + length > bytes.Length)
            {
                throw new ArgumentOutOfRangeException("length");
            }

            long value = 0;
            if (order == ByteOrder.Big)
            {
                for (int i = 0; i < length; i++)
                {
                    value = (value << 8) | bytes[offset + i];
                }
            }
            else
            {
                for (int i = length - 1; i >= 0; i--)
                {
                    value = (value << 8) | bytes[offset + i];
                }
            }
            return value;
        }

        private static int IndexOf(byte[] buffer, int offset, int count, byte[] needle)
        {
            int end = offset + count - needle.Length;
            for (int i = offset; i <= end; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (buffer[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return i;
                }
            }
            return -1;
        }

        private static byte[] SafeParseHex(string text)
        {
            try
            {
                return HexCodec.Parse(text);
            }
            catch
            {
                return new byte[0];
            }
        }
    }

    public sealed class FrameStream
    {
        private byte[] buffer;
        private int count;
        private long streamOffset;

        public FrameStream()
        {
            buffer = new byte[4096];
        }

        public int Count
        {
            get { return count; }
        }

        public long StreamOffset
        {
            get { return streamOffset; }
        }

        public void Append(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return;
            }
            EnsureCapacity(count + bytes.Length);
            Buffer.BlockCopy(bytes, 0, buffer, count, bytes.Length);
            count += bytes.Length;
        }

        public bool TryRead(IFrameDecoder decoder, TrafficDirection direction, out ProtocolFrame frame)
        {
            frame = null;
            if (count == 0)
            {
                return false;
            }

            int consumed;
            bool decoded = decoder.TryDecode(direction, buffer, 0, count, streamOffset, out frame, out consumed);
            if (decoded)
            {
                Consume(consumed);
                return true;
            }

            if (consumed > 0)
            {
                Consume(consumed);
            }
            return false;
        }

        private void Consume(int length)
        {
            if (length <= 0)
            {
                return;
            }
            if (length >= count)
            {
                streamOffset += count;
                count = 0;
                return;
            }
            Buffer.BlockCopy(buffer, length, buffer, 0, count - length);
            count -= length;
            streamOffset += length;
        }

        private void EnsureCapacity(int required)
        {
            if (required <= buffer.Length)
            {
                return;
            }
            int size = buffer.Length;
            while (size < required)
            {
                size *= 2;
            }
            Array.Resize(ref buffer, size);
        }
    }
}
