using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace WpadManager.Core.Json
{
    // Tiny zero-dependency JSON library. Pretty-prints by default (git-friendly),
    // writes enums as their names (human-readable), and supports typed (de)serialization
    // of plain objects with public fields/properties and generic List<T>.
    public static class Json
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------- Public API ----------

        public static string Stringify(object value) { return Stringify(value, true); }

        public static string Stringify(object value, bool pretty)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, value, pretty, 0);
            return sb.ToString();
        }

        // Deeper nesting than this is rejected: the parser is recursive and a stack overflow
        // cannot be caught (the app would die on reading a hostile history file).
        private const int MaxDepth = 256;

        // Generic parse -> Dictionary<string,object> / List<object> / string / double / bool / null.
        // Any malformed input surfaces as FormatException.
        public static object Parse(string text)
        {
            try
            {
                int i = 0;
                object v = ParseValue(text, ref i, 0);
                SkipWs(text, ref i);
                if (i != text.Length) throw new FormatException("Trailing characters in JSON at " + i);
                return v;
            }
            catch (FormatException) { throw; }
            catch (ArgumentException ex) { throw new FormatException("Invalid JSON: " + ex.Message, ex); }
            catch (IndexOutOfRangeException ex) { throw new FormatException("Invalid JSON: unexpected end", ex); }
            catch (OverflowException ex) { throw new FormatException("Invalid JSON: " + ex.Message, ex); }
        }

        // Typed conversion; a document whose shape does not fit T is a FormatException too.
        public static T Deserialize<T>(string text)
        {
            object graph = Parse(text);
            try
            {
                return (T)ConvertTo(graph, typeof(T));
            }
            catch (FormatException) { throw; }
            catch (InvalidCastException ex) { throw new FormatException("JSON does not match " + typeof(T).Name + ": " + ex.Message, ex); }
            catch (ArgumentException ex) { throw new FormatException("JSON does not match " + typeof(T).Name + ": " + ex.Message, ex); }
            catch (OverflowException ex) { throw new FormatException("JSON does not match " + typeof(T).Name + ": " + ex.Message, ex); }
        }

        // ---------- Writer ----------

        private static void WriteValue(StringBuilder sb, object v, bool pretty, int indent)
        {
            if (v == null) { sb.Append("null"); return; }

            Type t = v.GetType();

            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is char) { WriteString(sb, v.ToString()); return; }
            if (t.IsEnum) { WriteString(sb, v.ToString()); return; }

            if (v is float || v is double || v is decimal)
            {
                sb.Append(Convert.ToString(v, Inv));
                return;
            }
            if (v is sbyte || v is byte || v is short || v is ushort ||
                v is int || v is uint || v is long || v is ulong)
            {
                sb.Append(Convert.ToString(v, Inv));
                return;
            }

            IDictionary dict = v as IDictionary;
            if (dict != null) { WriteDict(sb, dict, pretty, indent); return; }

            IEnumerable en = v as IEnumerable;
            if (en != null) { WriteArray(sb, en, pretty, indent); return; }

            WritePoco(sb, v, pretty, indent);
        }

        private static void WriteDict(StringBuilder sb, IDictionary dict, bool pretty, int indent)
        {
            sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry e in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                NewlineIndent(sb, pretty, indent + 1);
                WriteString(sb, Convert.ToString(e.Key, Inv));
                sb.Append(pretty ? ": " : ":");
                WriteValue(sb, e.Value, pretty, indent + 1);
            }
            if (!first) NewlineIndent(sb, pretty, indent);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable en, bool pretty, int indent)
        {
            sb.Append('[');
            bool first = true;
            foreach (object item in en)
            {
                if (!first) sb.Append(',');
                first = false;
                NewlineIndent(sb, pretty, indent + 1);
                WriteValue(sb, item, pretty, indent + 1);
            }
            if (!first) NewlineIndent(sb, pretty, indent);
            sb.Append(']');
        }

        private static void WritePoco(StringBuilder sb, object v, bool pretty, int indent)
        {
            Type t = v.GetType();
            sb.Append('{');
            bool first = true;

            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < fields.Length; i++)
            {
                object val = fields[i].GetValue(v);
                if (!first) sb.Append(',');
                first = false;
                NewlineIndent(sb, pretty, indent + 1);
                WriteString(sb, fields[i].Name);
                sb.Append(pretty ? ": " : ":");
                WriteValue(sb, val, pretty, indent + 1);
            }

            PropertyInfo[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < props.Length; i++)
            {
                if (!props[i].CanRead) continue;
                if (props[i].GetIndexParameters().Length > 0) continue;
                object val = props[i].GetValue(v, null);
                if (!first) sb.Append(',');
                first = false;
                NewlineIndent(sb, pretty, indent + 1);
                WriteString(sb, props[i].Name);
                sb.Append(pretty ? ": " : ":");
                WriteValue(sb, val, pretty, indent + 1);
            }

            if (!first) NewlineIndent(sb, pretty, indent);
            sb.Append('}');
        }

        private static void NewlineIndent(StringBuilder sb, bool pretty, int indent)
        {
            if (!pretty) return;
            sb.Append('\n');
            for (int i = 0; i < indent; i++) sb.Append("  ");
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------- Parser ----------

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }

        private static object ParseValue(string s, ref int i, int depth)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            if (depth > MaxDepth) throw new FormatException("JSON is nested too deep (more than " + MaxDepth + " levels)");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i, depth + 1);
            if (c == '[') return ParseArray(s, ref i, depth + 1);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' || c == 'f') return ParseBool(s, ref i);
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i, int depth)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at " + i);
                i++;
                object val = ParseValue(s, ref i, depth);
                d[key] = val;
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; break; }
                throw new FormatException("Expected ',' or '}' at " + i);
            }
            return d;
        }

        private static List<object> ParseArray(string s, ref int i, int depth)
        {
            List<object> list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                object val = ParseValue(s, ref i, depth);
                list.Add(val);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; break; }
                throw new FormatException("Expected ',' or ']' at " + i);
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected string at " + i);
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            string hex = s.Substring(i, 4);
                            i += 4;
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, Inv));
                            break;
                        default: throw new FormatException("Bad escape \\" + e);
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("Unterminated string");
        }

        private static object ParseBool(string s, ref int i)
        {
            if (s[i] == 't') { Expect(s, ref i, "true"); return true; }
            Expect(s, ref i, "false"); return false;
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            string num = s.Substring(start, i - start);
            return double.Parse(num, NumberStyles.Float, Inv);
        }

        private static void Expect(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length || s.Substring(i, lit.Length) != lit)
                throw new FormatException("Expected '" + lit + "' at " + i);
            i += lit.Length;
        }

        // ---------- Typed conversion ----------

        private static object ConvertTo(object value, Type t)
        {
            if (t == typeof(object)) return value;

            Type under = Nullable.GetUnderlyingType(t);
            if (under != null)
            {
                if (value == null) return null;
                return ConvertTo(value, under);
            }

            if (value == null)
            {
                if (t.IsValueType) return Activator.CreateInstance(t);
                return null;
            }

            if (t == typeof(string)) return Convert.ToString(value, Inv);
            if (t.IsEnum) return Enum.Parse(t, Convert.ToString(value, Inv), true);
            if (t == typeof(bool)) return Convert.ToBoolean(value, Inv);
            if (t == typeof(int)) return Convert.ToInt32(value, Inv);
            if (t == typeof(long)) return Convert.ToInt64(value, Inv);
            if (t == typeof(short)) return Convert.ToInt16(value, Inv);
            if (t == typeof(byte)) return Convert.ToByte(value, Inv);
            if (t == typeof(double)) return Convert.ToDouble(value, Inv);
            if (t == typeof(float)) return Convert.ToSingle(value, Inv);
            if (t == typeof(decimal)) return Convert.ToDecimal(value, Inv);

            // List<E>
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type elem = t.GetGenericArguments()[0];
                IList target = (IList)Activator.CreateInstance(t);
                IList src = value as IList;
                if (src != null)
                    for (int i = 0; i < src.Count; i++) target.Add(ConvertTo(src[i], elem));
                return target;
            }

            // Array
            if (t.IsArray)
            {
                Type elem = t.GetElementType();
                IList src = value as IList;
                int n = src != null ? src.Count : 0;
                Array arr = Array.CreateInstance(elem, n);
                for (int i = 0; i < n; i++) arr.SetValue(ConvertTo(src[i], elem), i);
                return arr;
            }

            // POCO
            Dictionary<string, object> dict = value as Dictionary<string, object>;
            if (dict == null) throw new FormatException("Cannot convert JSON to " + t.Name);
            object obj = Activator.CreateInstance(t);

            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < fields.Length; i++)
            {
                object raw;
                if (TryGet(dict, fields[i].Name, out raw))
                    fields[i].SetValue(obj, ConvertTo(raw, fields[i].FieldType));
            }
            PropertyInfo[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < props.Length; i++)
            {
                if (!props[i].CanWrite) continue;
                if (props[i].GetIndexParameters().Length > 0) continue;
                object raw;
                if (TryGet(dict, props[i].Name, out raw))
                    props[i].SetValue(obj, ConvertTo(raw, props[i].PropertyType), null);
            }
            return obj;
        }

        private static bool TryGet(Dictionary<string, object> d, string name, out object val)
        {
            if (d.TryGetValue(name, out val)) return true;
            foreach (KeyValuePair<string, object> kv in d)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    val = kv.Value;
                    return true;
                }
            }
            val = null;
            return false;
        }
    }
}
