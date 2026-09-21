using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Shelf
{
    /// <summary>
    /// Minimal JSON reader and writer. No external dependencies, no LINQ, no lambdas.
    /// </summary>
    public static class Json
    {
        #region Reader

        /// <summary>
        /// Parses a JSON string into objects. Returns Dictionary, List, string, double, bool, or null.
        /// Throws FormatException on malformed input.
        /// </summary>
        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("JSON text is null");
            int index = 0;
            object result = ParseValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index < text.Length) throw new FormatException("Unexpected content after JSON value");
            return result;
        }

        static object ParseValue(string text, ref int index)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length) throw new FormatException("Unexpected end of JSON");

            char c = text[index];
            if (c == '{') return ParseObject(text, ref index);
            if (c == '[') return ParseArray(text, ref index);
            if (c == '"') return ParseString(text, ref index);
            if (c == 't') return ParseTrue(text, ref index);
            if (c == 'f') return ParseFalse(text, ref index);
            if (c == 'n') return ParseNull(text, ref index);
            if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(text, ref index);

            throw new FormatException("Unexpected character: " + c);
        }

        static Dictionary<string, object> ParseObject(string text, ref int index)
        {
            Dictionary<string, object> dict = new Dictionary<string, object>();
            index++; // skip '{'
            SkipWhitespace(text, ref index);

            if (index < text.Length && text[index] == '}')
            {
                index++;
                return dict;
            }

            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != '"')
                    throw new FormatException("Expected string key in object");

                string key = ParseString(text, ref index);
                SkipWhitespace(text, ref index);

                if (index >= text.Length || text[index] != ':')
                    throw new FormatException("Expected ':' after object key");
                index++; // skip ':'

                object value = ParseValue(text, ref index);
                dict[key] = value;

                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new FormatException("Unexpected end of object");

                if (text[index] == '}')
                {
                    index++;
                    return dict;
                }
                if (text[index] != ',')
                    throw new FormatException("Expected ',' or '}' in object");
                index++; // skip ','
            }
        }

        static List<object> ParseArray(string text, ref int index)
        {
            List<object> list = new List<object>();
            index++; // skip '['
            SkipWhitespace(text, ref index);

            if (index < text.Length && text[index] == ']')
            {
                index++;
                return list;
            }

            while (true)
            {
                object value = ParseValue(text, ref index);
                list.Add(value);

                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new FormatException("Unexpected end of array");

                if (text[index] == ']')
                {
                    index++;
                    return list;
                }
                if (text[index] != ',')
                    throw new FormatException("Expected ',' or ']' in array");
                index++; // skip ','
            }
        }

        static string ParseString(string text, ref int index)
        {
            index++; // skip opening '"'
            StringBuilder sb = new StringBuilder();

            while (index < text.Length)
            {
                char c = text[index];
                if (c == '"')
                {
                    index++;
                    return sb.ToString();
                }
                if (c == '\\')
                {
                    index++;
                    if (index >= text.Length) throw new FormatException("Unexpected end of string escape");
                    char esc = text[index];
                    index++;
                    switch (esc)
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
                            if (index + 4 > text.Length)
                                throw new FormatException("Invalid unicode escape");
                            int codePoint = ParseHex4(text, index);
                            index += 4;
                            // Handle surrogate pairs
                            if (codePoint >= 0xD800 && codePoint <= 0xDBFF)
                            {
                                // High surrogate, look for low surrogate
                                if (index + 6 <= text.Length && text[index] == '\\' && text[index + 1] == 'u')
                                {
                                    int low = ParseHex4(text, index + 2);
                                    if (low >= 0xDC00 && low <= 0xDFFF)
                                    {
                                        index += 6;
                                        int full = 0x10000 + ((codePoint - 0xD800) << 10) + (low - 0xDC00);
                                        sb.Append(char.ConvertFromUtf32(full));
                                        break;
                                    }
                                }
                            }
                            sb.Append((char)codePoint);
                            break;
                        default:
                            throw new FormatException("Invalid escape sequence: \\" + esc);
                    }
                }
                else
                {
                    sb.Append(c);
                    index++;
                }
            }
            throw new FormatException("Unterminated string");
        }

        static int ParseHex4(string text, int start)
        {
            int val = 0;
            for (int i = 0; i < 4; i++)
            {
                char c = text[start + i];
                int digit;
                if (c >= '0' && c <= '9') digit = c - '0';
                else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
                else throw new FormatException("Invalid hex digit: " + c);
                val = (val << 4) | digit;
            }
            return val;
        }

        static double ParseNumber(string text, ref int index)
        {
            int start = index;

            // Optional minus
            if (index < text.Length && text[index] == '-') index++;

            // Integer part
            if (index >= text.Length) throw new FormatException("Invalid number");
            if (text[index] == '0')
            {
                index++;
            }
            else if (text[index] >= '1' && text[index] <= '9')
            {
                while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
            }
            else
            {
                throw new FormatException("Invalid number");
            }

            // Fractional part
            if (index < text.Length && text[index] == '.')
            {
                index++;
                if (index >= text.Length || text[index] < '0' || text[index] > '9')
                    throw new FormatException("Invalid number: expected digit after decimal");
                while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
            }

            // Exponent
            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                index++;
                if (index < text.Length && (text[index] == '+' || text[index] == '-')) index++;
                if (index >= text.Length || text[index] < '0' || text[index] > '9')
                    throw new FormatException("Invalid number: expected digit in exponent");
                while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
            }

            string numStr = text.Substring(start, index - start);
            double result;
            if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                throw new FormatException("Invalid number: " + numStr);
            return result;
        }

        static bool ParseTrue(string text, ref int index)
        {
            if (index + 4 <= text.Length && text.Substring(index, 4) == "true")
            {
                index += 4;
                return true;
            }
            throw new FormatException("Invalid literal");
        }

        static bool ParseFalse(string text, ref int index)
        {
            if (index + 5 <= text.Length && text.Substring(index, 5) == "false")
            {
                index += 5;
                return false;
            }
            throw new FormatException("Invalid literal");
        }

        static object ParseNull(string text, ref int index)
        {
            if (index + 4 <= text.Length && text.Substring(index, 4) == "null")
            {
                index += 4;
                return null;
            }
            throw new FormatException("Invalid literal");
        }

        static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                char c = text[index];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    index++;
                else
                    break;
            }
        }

        #endregion

        #region Helpers

        /// <summary>Casts v to Dictionary, returns null if not a dictionary.</summary>
        public static Dictionary<string, object> Obj(object v)
        {
            return v as Dictionary<string, object>;
        }

        /// <summary>Casts v to List, returns null if not a list.</summary>
        public static List<object> Arr(object v)
        {
            return v as List<object>;
        }

        /// <summary>Gets a string value from a dictionary, null if absent or empty.</summary>
        public static string Str(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                string s = v.ToString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return null;
        }

        /// <summary>Gets a bool value from a dictionary, fallback if absent or not bool.</summary>
        public static bool Bool(Dictionary<string, object> d, string key, bool fallback)
        {
            if (d == null) return fallback;
            object v;
            if (d.TryGetValue(key, out v) && v is bool) return (bool)v;
            return fallback;
        }

        /// <summary>Gets an int value from a dictionary, fallback if absent.</summary>
        public static int Int(Dictionary<string, object> d, string key, int fallback)
        {
            if (d == null) return fallback;
            object v;
            if (d.TryGetValue(key, out v))
            {
                if (v is double) return (int)(double)v;
                if (v is int) return (int)v;
                if (v is long) return (int)(long)v;
            }
            return fallback;
        }

        #endregion

        #region Writer

        /// <summary>
        /// Writes an object graph to a JSON string. Handles Dictionary, List, string, bool, double, int, null.
        /// </summary>
        public static string Write(object v)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, v);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null)
            {
                sb.Append("null");
                return;
            }

            if (v is bool)
            {
                sb.Append((bool)v ? "true" : "false");
                return;
            }

            if (v is string)
            {
                WriteString(sb, (string)v);
                return;
            }

            if (v is double)
            {
                double d = (double)v;

                // NaN and the infinities are not JSON numbers. Written straight out they
                // produced a bare NaN token that this parser rejects, so the write looked
                // fine and the whole settings file was unreadable on the next start - every
                // setting lost, silently, because Config.Load falls back to defaults. A
                // window coordinate is NaN before layout and a scale over a zero dimension
                // is infinite, so null here costs the one value instead of the file.
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (v is int)
            {
                sb.Append(((int)v).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (v is long)
            {
                sb.Append(((long)v).ToString(CultureInfo.InvariantCulture));
                return;
            }

            Dictionary<string, object> dict = v as Dictionary<string, object>;
            if (dict != null)
            {
                WriteObject(sb, dict);
                return;
            }

            List<object> list = v as List<object>;
            if (list != null)
            {
                WriteArray(sb, list);
                return;
            }

            // Fallback: treat as string
            WriteString(sb, v.ToString());
        }

        static void WriteObject(StringBuilder sb, Dictionary<string, object> dict)
        {
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> kv in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                WriteValue(sb, kv.Value);
            }
            sb.Append('}');
        }

        static void WriteArray(StringBuilder sb, List<object> list)
        {
            sb.Append('[');
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteValue(sb, list[i]);
            }
            sb.Append(']');
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                {
                    sb.Append("\\\"");
                }
                else if (c == '\\')
                {
                    sb.Append("\\\\");
                }
                else if (c < 0x20)
                {
                    // Control characters as \u00XX
                    sb.Append("\\u");
                    sb.Append(((int)c).ToString("x4"));
                }
                else
                {
                    sb.Append(c);
                }
            }
            sb.Append('"');
        }

        #endregion
    }
}
