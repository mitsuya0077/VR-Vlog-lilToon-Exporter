using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

namespace VRVlog.LilToonExporter.LanTransfer
{
    internal static class LanTransferProtocol
    {
        internal const string QrPrefix = "vrvlog-transfer:";
        internal const long MaximumSize = 256L * 1024 * 1024;
        internal const int MaximumHeaderBytes = 8192;
        internal const int MaximumQrBytes = 4096;

        internal static bool IsPrivateIPv4(IPAddress address)
        {
            if (address == null || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            var b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168;
        }

        internal static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            new SecureRandom().NextBytes(bytes);
            return bytes;
        }

        internal static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal static string DisplayName(string name)
        {
            var result = new StringBuilder();
            var text = name ?? "avatar.vrm";
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                // Names are display-only. Remove control and bidi formatting characters.
                if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
                if (result.Length >= 256) break;
                if (char.IsSurrogate(c))
                {
                    if (!char.IsHighSurrogate(c) || i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) continue;
                    if (result.Length + 2 > 256) break;
                    if (CharUnicodeInfo.GetUnicodeCategory(text, i) == UnicodeCategory.Format) { i++; continue; }
                    result.Append(c).Append(text[++i]);
                    continue;
                }
                result.Append(c);
            }
            return result.Length == 0 ? "avatar.vrm" : result.ToString();
        }

        internal static string JsonString(string value)
        {
            var result = new StringBuilder("\"");
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    default:
                        if (c < 0x20 || char.IsSurrogate(c)) result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else result.Append(c);
                        break;
                }
            }
            return result.Append('"').ToString();
        }

        internal static string QrPayload(string host, int port, string id, byte[] token, string certificateHash,
            string name, long size, string fileHash, long expiresAt)
        {
            var payload = QrPrefix + "{\"v\":1,\"host\":" + JsonString(host) + ",\"port\":" + port.ToString(CultureInfo.InvariantCulture)
                + ",\"id\":" + JsonString(id) + ",\"token\":" + JsonString(Base64Url(token))
                + ",\"certificateSha256\":" + JsonString(certificateHash) + ",\"name\":" + JsonString(DisplayName(name))
                + ",\"size\":" + size.ToString(CultureInfo.InvariantCulture) + ",\"sha256\":" + JsonString(fileHash)
                + ",\"expiresAt\":" + expiresAt.ToString(CultureInfo.InvariantCulture) + "}";
            if (Encoding.UTF8.GetByteCount(payload) > MaximumQrBytes) throw new InvalidOperationException("転送QRのサイズ上限を超えました。");
            return payload;
        }

        internal sealed class Request
        {
            internal string Method;
            internal string Path;
            internal readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            internal bool Authenticated(byte[] token, string id)
            {
                if (!Headers.TryGetValue("Authorization", out var auth) || !auth.StartsWith("Bearer ", StringComparison.Ordinal)
                    || !Headers.TryGetValue("X-VRVlog-Transfer", out var requestId) || !string.Equals(id, requestId, StringComparison.Ordinal)) return false;
                var encoded = auth.Substring(7);
                if (encoded.Length != 43) return false;
                foreach (var c in encoded) if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_')) return false;
                try
                {
                    var decoded = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + "=");
                    return string.Equals(Base64Url(decoded), encoded, StringComparison.Ordinal) && Arrays.FixedTimeEquals(token, decoded);
                }
                catch (FormatException) { return false; }
            }
        }

        internal static Request ReadRequest(Stream stream)
        {
            var bytes = new List<byte>();
            var matched = 0;
            var ending = new byte[] { 13, 10, 13, 10 };
            while (matched != 4)
            {
                var value = stream.ReadByte();
                if (value < 0) throw new InvalidDataException("Incomplete HTTP header.");
                if (value != 13 && value != 10 && (value < 32 || value > 126)) throw new InvalidDataException("Invalid HTTP header.");
                bytes.Add((byte)value);
                if (bytes.Count > MaximumHeaderBytes) throw new InvalidDataException("HTTP header too large.");
                matched = value == ending[matched] ? matched + 1 : value == 13 ? 1 : 0;
            }
            var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines[0].Length > 512) throw new InvalidDataException("HTTP request line too large.");
            var first = lines[0].Split(' ');
            if (first.Length != 3 || first[2] != "HTTP/1.1") throw new InvalidDataException("Unsupported HTTP request.");
            var request = new Request { Method = first[0], Path = first[1] };
            for (var i = 1; i < lines.Length - 2; i++)
            {
                foreach (var c in lines[i]) if (c < 32 || c > 126) throw new InvalidDataException("Invalid HTTP field character.");
                var colon = lines[i].IndexOf(':');
                if (colon < 1 || lines[i][0] == ' ' || lines[i][0] == '\t') throw new InvalidDataException("Invalid HTTP field.");
                var name = lines[i].Substring(0, colon);
                foreach (var c in name) if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-')) throw new InvalidDataException("Invalid HTTP field name.");
                if (request.Headers.ContainsKey(name)) throw new InvalidDataException("Duplicate HTTP field.");
                request.Headers.Add(name, lines[i].Substring(colon + 1).Trim(' '));
            }
            if (!request.Headers.ContainsKey("Host") || request.Headers.ContainsKey("Transfer-Encoding") || request.Headers.ContainsKey("Expect")
                || request.Headers.TryGetValue("Content-Length", out var length) && length != "0") throw new InvalidDataException("HTTP request bodies are not supported.");
            return request;
        }
    }
}
