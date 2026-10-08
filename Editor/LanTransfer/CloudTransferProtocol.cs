using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VRVlog.LilToonExporter.LanTransfer
{
    internal static class CloudTransferProtocol
    {
        internal const string ServiceUrl = "https://vrvlog-transfer.mouri0077.workers.dev";
        internal const long MaximumSize = 256L * 1024 * 1024;
        internal const int PartSize = 8 * 1024 * 1024;
        internal const int LifetimeMinutes = 3;

        // Cloud names are display metadata only. Keep the service's 256 UTF-16
        // unit bound while removing path separators without changing VRM bytes.
        internal static string DisplayName(string name) => LanTransferProtocol.DisplayName(name).Replace('/', '-').Replace('\\', '-');

        internal static bool IsHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (var c in value) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }

        internal static bool IsToken(string value) => IsBase64Url(value, 32);
        internal static bool IsKey(string value) => IsBase64Url(value, 64);
        private static bool IsBase64Url(string value, int bytes)
        {
            if (value == null || value.Length != (bytes * 8 + 5) / 6) return false;
            foreach (var c in value) if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_')) return false;
            try { return LanTransferProtocol.Base64Url(Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + (bytes == 64 ? "==" : "="))) == value; }
            catch (FormatException) { return false; }
        }

        internal static string CreateBody(string name, long size, string hash) =>
            "{\"name\":" + LanTransferProtocol.JsonString(name) + ",\"size\":" + size.ToString(CultureInfo.InvariantCulture) + ",\"sha256\":" + LanTransferProtocol.JsonString(hash) + "}";

        internal static string Qr(string id, string token, string name, long size, string hash, long expiresAt, string key) =>
            LanTransferProtocol.QrPrefix + "{\"v\":3,\"id\":" + LanTransferProtocol.JsonString(id) + ",\"token\":" + LanTransferProtocol.JsonString(token)
            + ",\"name\":" + LanTransferProtocol.JsonString(name) + ",\"size\":" + size.ToString(CultureInfo.InvariantCulture)
            + ",\"sha256\":" + LanTransferProtocol.JsonString(hash) + ",\"expiresAt\":" + expiresAt.ToString(CultureInfo.InvariantCulture) + ",\"key\":" + LanTransferProtocol.JsonString(key) + "}";

        // The service contract is a bounded flat JSON object. Reject duplicate
        // fields, arrays, nested objects and number coercions before using it.
        internal sealed class Fields
        {
            private readonly Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
            internal string Text(string name) => values.TryGetValue(name, out var value) && value is string text ? text : throw Invalid();
            internal long Number(string name) => values.TryGetValue(name, out var value) && value is long number ? number : throw Invalid();
            internal void Exact(params string[] names)
            {
                if (values.Count != names.Length) throw Invalid();
                foreach (var name in names) if (!values.ContainsKey(name)) throw Invalid();
            }
            internal static Fields Read(string json)
            {
                if (json == null || Encoding.UTF8.GetByteCount(json) > 4096) throw Invalid();
                var result = new Fields(); var index = 0;
                Skip(json, ref index); Expect(json, ref index, '{'); Skip(json, ref index);
                if (index < json.Length && json[index] == '}') index++;
                else while (true)
                {
                    var key = String(json, ref index); Skip(json, ref index); Expect(json, ref index, ':'); Skip(json, ref index);
                    object value;
                    if (index < json.Length && json[index] == '"') value = String(json, ref index);
                    else
                    {
                        var start = index;
                        while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
                        var literal = json.Substring(start, index - start);
                        if (literal.Length == 0 || literal.Length > 1 && literal[0] == '0' || !long.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw Invalid();
                        value = number;
                    }
                    if (result.values.ContainsKey(key)) throw Invalid();
                    result.values.Add(key, value); Skip(json, ref index);
                    if (index < json.Length && json[index] == '}') { index++; break; }
                    Expect(json, ref index, ','); Skip(json, ref index);
                }
                Skip(json, ref index); if (index != json.Length) throw Invalid();
                return result;
            }
            private static string String(string json, ref int index)
            {
                Expect(json, ref index, '"'); var result = new StringBuilder();
                while (index < json.Length)
                {
                    var c = json[index++];
                    if (c == '"') return result.ToString();
                    if (c < 32) throw Invalid();
                    if (c != '\\') { result.Append(c); continue; }
                    if (index >= json.Length) throw Invalid();
                    switch (json[index++])
                    {
                        case '"': result.Append('"'); break;
                        case '\\': result.Append('\\'); break;
                        case '/': result.Append('/'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u':
                            if (index + 4 > json.Length || !ushort.TryParse(json.Substring(index, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)) throw Invalid();
                            result.Append((char)code); index += 4; break;
                        default: throw Invalid();
                    }
                }
                throw Invalid();
            }
            private static void Skip(string json, ref int index) { while (index < json.Length && (json[index] == ' ' || json[index] == '\t' || json[index] == '\r' || json[index] == '\n')) index++; }
            private static void Expect(string json, ref int index, char value) { if (index >= json.Length || json[index++] != value) throw Invalid(); }
        }
        internal static InvalidDataException Invalid() => new InvalidDataException("クラウド転送の応答を確認できませんでした。新しい転送を作成してください。");
    }
}
