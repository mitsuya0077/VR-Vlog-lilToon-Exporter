using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VRVlog.LilToonExporter
{
    internal static class JsonDom
    {
        public static object Parse(string json) => new Parser(json).ParseValueAndFinish();
        public static string Serialize(object value) { var b = new StringBuilder(); Write(value, b); return b.ToString(); }

        private sealed class Parser
        {
            private readonly string text; private int i;
            public Parser(string value) { text = value ?? throw new ArgumentNullException(nameof(value)); }
            public object ParseValueAndFinish() { var v = Value(); Space(); if (i != text.Length) Fail(); return v; }
            private object Value()
            {
                Space(); if (i >= text.Length) Fail(); var c = text[i];
                if (c == '{') return Object(); if (c == '[') return Array(); if (c == '"') return String();
                if (c == 't') { Word("true"); return true; } if (c == 'f') { Word("false"); return false; }
                if (c == 'n') { Word("null"); return null; } return Number();
            }
            private Dictionary<string, object> Object()
            {
                var r = new Dictionary<string, object>(StringComparer.Ordinal); i++; Space();
                if (Take('}')) return r;
                while (true) { Space(); if (i >= text.Length || text[i] != '"') Fail(); var k = String(); if (r.ContainsKey(k)) throw new FormatException($"Duplicate JSON key '{k}'."); Space(); Need(':'); r.Add(k, Value()); Space(); if (Take('}')) return r; Need(','); }
            }
            private List<object> Array()
            {
                var r = new List<object>(); i++; Space(); if (Take(']')) return r;
                while (true) { r.Add(Value()); Space(); if (Take(']')) return r; Need(','); }
            }
            private string String()
            {
                var b = new StringBuilder(); Need('"');
                while (i < text.Length) { var c = text[i++]; if (c == '"') return b.ToString(); if (c != '\\') { if (c < 0x20) Fail(); b.Append(c); continue; }
                    if (i >= text.Length) Fail(); c = text[i++]; if (c == '"' || c == '\\' || c == '/') b.Append(c); else if (c == 'b') b.Append('\b'); else if (c == 'f') b.Append('\f'); else if (c == 'n') b.Append('\n'); else if (c == 'r') b.Append('\r'); else if (c == 't') b.Append('\t'); else if (c == 'u') { if (i + 4 > text.Length) Fail(); b.Append((char)int.Parse(text.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; } else Fail(); }
                Fail(); return null;
            }
            private object Number()
            {
                var s = i; if (Take('-')) { } if (Take('0')) { } else { Digit(); while (i < text.Length && char.IsDigit(text[i])) i++; }
                var real = false; if (Take('.')) { real = true; Digit(); while (i < text.Length && char.IsDigit(text[i])) i++; }
                if (i < text.Length && (text[i] == 'e' || text[i] == 'E')) { real = true; i++; if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++; Digit(); while (i < text.Length && char.IsDigit(text[i])) i++; }
                var token = text.Substring(s, i - s); return real ? (object)double.Parse(token, CultureInfo.InvariantCulture) : long.Parse(token, CultureInfo.InvariantCulture);
            }
            private void Digit() { if (i >= text.Length || !char.IsDigit(text[i])) Fail(); }
            private void Word(string w) { if (i + w.Length > text.Length || text.Substring(i, w.Length) != w) Fail(); i += w.Length; }
            private void Space() { while (i < text.Length && char.IsWhiteSpace(text[i])) i++; }
            private bool Take(char c) { if (i < text.Length && text[i] == c) { i++; return true; } return false; }
            private void Need(char c) { if (!Take(c)) Fail(); }
            private void Fail() { throw new FormatException($"Invalid JSON at offset {i}."); }
        }

        private static void Write(object value, StringBuilder b)
        {
            if (value == null) { b.Append("null"); return; }
            if (value is string s) { Quote(s, b); return; } if (value is bool z) { b.Append(z ? "true" : "false"); return; }
            if (value is IDictionary<string, object> map) { b.Append('{'); var first = true; foreach (var p in map) { if (!first) b.Append(','); first = false; Quote(p.Key, b); b.Append(':'); Write(p.Value, b); } b.Append('}'); return; }
            if (value is IEnumerable list && !(value is string)) { b.Append('['); var first = true; foreach (var x in list) { if (!first) b.Append(','); first = false; Write(x, b); } b.Append(']'); return; }
            if (value is float f) { if (float.IsNaN(f) || float.IsInfinity(f)) throw new FormatException("JSON numbers must be finite."); b.Append(f.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is double d) { if (double.IsNaN(d) || double.IsInfinity(d)) throw new FormatException("JSON numbers must be finite."); b.Append(d.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is IFormattable formattable) { b.Append(formattable.ToString(null, CultureInfo.InvariantCulture)); return; }
            throw new NotSupportedException($"Unsupported JSON value: {value.GetType()}.");
        }
        private static void Quote(string s, StringBuilder b) { b.Append('"'); foreach (var c in s) { switch (c) { case '"': b.Append("\\\""); break; case '\\': b.Append("\\\\"); break; case '\b': b.Append("\\b"); break; case '\f': b.Append("\\f"); break; case '\n': b.Append("\\n"); break; case '\r': b.Append("\\r"); break; case '\t': b.Append("\\t"); break; default: if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); break; } } b.Append('"'); }
    }
}
