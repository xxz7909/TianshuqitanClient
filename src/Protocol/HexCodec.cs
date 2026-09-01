using System;
using System.Globalization;
using System.Text;

namespace TianshuQitanLauncher.Protocol
{
    public static class HexCodec
    {
        public static byte[] Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new byte[0];
            }

            StringBuilder clean = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (Uri.IsHexDigit(c))
                {
                    clean.Append(c);
                }
            }

            if ((clean.Length & 1) != 0)
            {
                throw new FormatException("Hex text must contain an even number of digits.");
            }

            byte[] bytes = new byte[clean.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = byte.Parse(clean.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return bytes;
        }

        public static string Format(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder result = new StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0)
                {
                    result.Append(' ');
                }
                result.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return result.ToString();
        }

        public static string FormatAscii(byte[] bytes)
        {
            if (bytes == null)
            {
                return string.Empty;
            }

            StringBuilder result = new StringBuilder(bytes.Length);
            for (int i = 0; i < bytes.Length; i++)
            {
                byte value = bytes[i];
                result.Append(value >= 32 && value <= 126 ? (char)value : '.');
            }
            return result.ToString();
        }

        public static byte[] Copy(byte[] source)
        {
            if (source == null || source.Length == 0)
            {
                return new byte[0];
            }
            byte[] result = new byte[source.Length];
            Buffer.BlockCopy(source, 0, result, 0, source.Length);
            return result;
        }
    }
}
