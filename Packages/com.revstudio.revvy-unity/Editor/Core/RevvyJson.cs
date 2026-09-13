using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Kind discriminator for <see cref="RevvyJson"/> nodes.
    /// </summary>
    public enum RevvyJsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// Minimal dependency-free JSON DOM (parser + serializer).
    ///
    /// Why hand-rolled: the bridge contract forbids external NuGet packages, and
    /// <c>UnityEngine.JsonUtility</c> cannot represent the open-ended shapes MCP
    /// needs (arbitrary tool arguments, JSON Schema, unknown config fields we are
    /// contractually required to preserve — see contract §2).
    /// Object member order is preserved so round-tripped config files stay stable
    /// in diffs.
    /// </summary>
    public sealed class RevvyJson
    {
        private readonly List<string> _order;
        private readonly Dictionary<string, RevvyJson> _members;
        private readonly List<RevvyJson> _items;

        public RevvyJsonKind Kind { get; private set; }
        public bool BoolValue { get; private set; }
        public double NumberValue { get; private set; }
        public string StringValue { get; private set; }

        /// <summary>True when the number was parsed/created from an integral value.</summary>
        public bool IsIntegral { get; private set; }

        private RevvyJson(RevvyJsonKind kind)
        {
            Kind = kind;
            if (kind == RevvyJsonKind.Object)
            {
                _order = new List<string>();
                _members = new Dictionary<string, RevvyJson>(StringComparer.Ordinal);
            }
            else if (kind == RevvyJsonKind.Array)
            {
                _items = new List<RevvyJson>();
            }
        }

        // ---------------------------------------------------------------- factories

        public static RevvyJson Null()
        {
            return new RevvyJson(RevvyJsonKind.Null);
        }

        public static RevvyJson Bool(bool value)
        {
            return new RevvyJson(RevvyJsonKind.Bool) { BoolValue = value };
        }

        public static RevvyJson Number(double value)
        {
            return new RevvyJson(RevvyJsonKind.Number) { NumberValue = value, IsIntegral = false };
        }

        public static RevvyJson Number(long value)
        {
            return new RevvyJson(RevvyJsonKind.Number) { NumberValue = value, IsIntegral = true };
        }

        public static RevvyJson String(string value)
        {
            if (value == null)
            {
                return Null();
            }

            return new RevvyJson(RevvyJsonKind.String) { StringValue = value };
        }

        public static RevvyJson Object()
        {
            return new RevvyJson(RevvyJsonKind.Object);
        }

        public static RevvyJson Array()
        {
            return new RevvyJson(RevvyJsonKind.Array);
        }

        public static RevvyJson ArrayOf(IEnumerable<string> values)
        {
            RevvyJson array = Array();
            if (values != null)
            {
                foreach (string value in values)
                {
                    array.Add(String(value));
                }
            }

            return array;
        }

        // ---------------------------------------------------------------- object API

        public bool IsObject
        {
            get { return Kind == RevvyJsonKind.Object; }
        }

        public bool IsArray
        {
            get { return Kind == RevvyJsonKind.Array; }
        }

        public bool IsNull
        {
            get { return Kind == RevvyJsonKind.Null; }
        }

        public IReadOnlyList<string> Keys
        {
            get { return _order != null ? (IReadOnlyList<string>)_order : new string[0]; }
        }

        public int Count
        {
            get
            {
                if (Kind == RevvyJsonKind.Array)
                {
                    return _items.Count;
                }

                if (Kind == RevvyJsonKind.Object)
                {
                    return _order.Count;
                }

                return 0;
            }
        }

        /// <summary>Sets (or replaces) an object member. No-op on non-objects.</summary>
        public RevvyJson Set(string key, RevvyJson value)
        {
            if (Kind != RevvyJsonKind.Object || key == null)
            {
                return this;
            }

            if (!_members.ContainsKey(key))
            {
                _order.Add(key);
            }

            _members[key] = value ?? Null();
            return this;
        }

        public RevvyJson Set(string key, string value)
        {
            return Set(key, String(value));
        }

        public RevvyJson Set(string key, bool value)
        {
            return Set(key, Bool(value));
        }

        public RevvyJson Set(string key, long value)
        {
            return Set(key, Number(value));
        }

        public RevvyJson Set(string key, double value)
        {
            return Set(key, Number(value));
        }

        public bool Has(string key)
        {
            return Kind == RevvyJsonKind.Object && key != null && _members.ContainsKey(key);
        }

        public bool Remove(string key)
        {
            if (Kind != RevvyJsonKind.Object || key == null || !_members.Remove(key))
            {
                return false;
            }

            _order.Remove(key);
            return true;
        }

        /// <summary>Member lookup; returns null when absent so callers can distinguish "missing" from "null".</summary>
        public RevvyJson Get(string key)
        {
            RevvyJson value;
            if (Kind == RevvyJsonKind.Object && key != null && _members.TryGetValue(key, out value))
            {
                return value;
            }

            return null;
        }

        public RevvyJson this[string key]
        {
            get { return Get(key); }
        }

        public RevvyJson this[int index]
        {
            get
            {
                if (Kind != RevvyJsonKind.Array || index < 0 || index >= _items.Count)
                {
                    return null;
                }

                return _items[index];
            }
        }

        public IReadOnlyList<RevvyJson> Items
        {
            get { return _items != null ? (IReadOnlyList<RevvyJson>)_items : new RevvyJson[0]; }
        }

        public RevvyJson Add(RevvyJson value)
        {
            if (Kind == RevvyJsonKind.Array)
            {
                _items.Add(value ?? Null());
            }

            return this;
        }

        // ---------------------------------------------------------------- coercion

        public string AsString(string fallback = null)
        {
            switch (Kind)
            {
                case RevvyJsonKind.String:
                    return StringValue;
                case RevvyJsonKind.Number:
                    return FormatNumber();
                case RevvyJsonKind.Bool:
                    return BoolValue ? "true" : "false";
                default:
                    return fallback;
            }
        }

        public bool AsBool(bool fallback = false)
        {
            switch (Kind)
            {
                case RevvyJsonKind.Bool:
                    return BoolValue;
                case RevvyJsonKind.Number:
                    return Math.Abs(NumberValue) > double.Epsilon;
                case RevvyJsonKind.String:
                    return string.Equals(StringValue, "true", StringComparison.OrdinalIgnoreCase);
                default:
                    return fallback;
            }
        }

        public double AsDouble(double fallback = 0d)
        {
            if (Kind == RevvyJsonKind.Number)
            {
                return NumberValue;
            }

            double parsed;
            if (Kind == RevvyJsonKind.String &&
                double.TryParse(StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            return fallback;
        }

        public long AsLong(long fallback = 0L)
        {
            if (Kind == RevvyJsonKind.Number)
            {
                return (long)NumberValue;
            }

            long parsed;
            if (Kind == RevvyJsonKind.String &&
                long.TryParse(StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            return fallback;
        }

        public int AsInt(int fallback = 0)
        {
            return (int)AsLong(fallback);
        }

        // ---------------------------------------------------------------- serialize

        public override string ToString()
        {
            return ToJson(false);
        }

        public string ToJson(bool pretty = false)
        {
            StringBuilder builder = new StringBuilder(256);
            Write(builder, pretty, 0);
            return builder.ToString();
        }

        private void Write(StringBuilder builder, bool pretty, int depth)
        {
            switch (Kind)
            {
                case RevvyJsonKind.Null:
                    builder.Append("null");
                    break;
                case RevvyJsonKind.Bool:
                    builder.Append(BoolValue ? "true" : "false");
                    break;
                case RevvyJsonKind.Number:
                    builder.Append(FormatNumber());
                    break;
                case RevvyJsonKind.String:
                    WriteEscaped(builder, StringValue);
                    break;
                case RevvyJsonKind.Array:
                    WriteArray(builder, pretty, depth);
                    break;
                case RevvyJsonKind.Object:
                    WriteObject(builder, pretty, depth);
                    break;
            }
        }

        private void WriteArray(StringBuilder builder, bool pretty, int depth)
        {
            if (_items.Count == 0)
            {
                builder.Append("[]");
                return;
            }

            builder.Append('[');
            for (int i = 0; i < _items.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                NewLineIndent(builder, pretty, depth + 1);
                _items[i].Write(builder, pretty, depth + 1);
            }

            NewLineIndent(builder, pretty, depth);
            builder.Append(']');
        }

        private void WriteObject(StringBuilder builder, bool pretty, int depth)
        {
            if (_order.Count == 0)
            {
                builder.Append("{}");
                return;
            }

            builder.Append('{');
            for (int i = 0; i < _order.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                NewLineIndent(builder, pretty, depth + 1);
                WriteEscaped(builder, _order[i]);
                builder.Append(':');
                if (pretty)
                {
                    builder.Append(' ');
                }

                _members[_order[i]].Write(builder, pretty, depth + 1);
            }

            NewLineIndent(builder, pretty, depth);
            builder.Append('}');
        }

        private static void NewLineIndent(StringBuilder builder, bool pretty, int depth)
        {
            if (!pretty)
            {
                return;
            }

            builder.Append('\n');
            builder.Append(' ', depth * 2);
        }

        private string FormatNumber()
        {
            if (double.IsNaN(NumberValue) || double.IsInfinity(NumberValue))
            {
                // JSON has no NaN/Infinity literal; 0 keeps the payload parseable.
                return "0";
            }

            if (IsIntegral || NumberValue == Math.Floor(NumberValue))
            {
                if (NumberValue >= long.MinValue && NumberValue <= long.MaxValue)
                {
                    return ((long)NumberValue).ToString(CultureInfo.InvariantCulture);
                }
            }

            return NumberValue.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void WriteEscaped(StringBuilder builder, string value)
        {
            builder.Append('"');
            if (value != null)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '"':
                            builder.Append("\\\"");
                            break;
                        case '\\':
                            builder.Append("\\\\");
                            break;
                        case '\b':
                            builder.Append("\\b");
                            break;
                        case '\f':
                            builder.Append("\\f");
                            break;
                        case '\n':
                            builder.Append("\\n");
                            break;
                        case '\r':
                            builder.Append("\\r");
                            break;
                        case '\t':
                            builder.Append("\\t");
                            break;
                        default:
                            // Control chars plus U+2028/U+2029: legal JSON, but illegal raw inside JS string literals.
                            if (c < 0x20 || c == '\u2028' || c == '\u2029')
                            {
                                builder.Append("\\u");
                                builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                builder.Append(c);
                            }

                            break;
                    }
                }
            }

            builder.Append('"');
        }

        // ---------------------------------------------------------------- parse

        /// <summary>Parses JSON text. Returns null on malformed input (never throws).</summary>
        public static RevvyJson Parse(string text)
        {
            string error;
            return TryParse(text, out error);
        }

        public static RevvyJson TryParse(string text, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(text))
            {
                error = "Empty JSON document";
                return null;
            }

            try
            {
                int index = 0;
                SkipWhitespace(text, ref index);
                RevvyJson value = ParseValue(text, ref index, 0);
                SkipWhitespace(text, ref index);
                if (index != text.Length)
                {
                    error = "Trailing content at offset " + index.ToString(CultureInfo.InvariantCulture);
                    return null;
                }

                return value;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return null;
            }
        }

        private const int MaxDepth = 128;

        private static RevvyJson ParseValue(string text, ref int index, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("JSON nesting deeper than " + MaxDepth);
            }

            SkipWhitespace(text, ref index);
            if (index >= text.Length)
            {
                throw new FormatException("Unexpected end of JSON input");
            }

            char c = text[index];
            switch (c)
            {
                case '{':
                    return ParseObject(text, ref index, depth);
                case '[':
                    return ParseArray(text, ref index, depth);
                case '"':
                    return String(ParseString(text, ref index));
                case 't':
                    Expect(text, ref index, "true");
                    return Bool(true);
                case 'f':
                    Expect(text, ref index, "false");
                    return Bool(false);
                case 'n':
                    Expect(text, ref index, "null");
                    return Null();
                default:
                    return ParseNumber(text, ref index);
            }
        }

        private static RevvyJson ParseObject(string text, ref int index, int depth)
        {
            RevvyJson result = Object();
            index++; // '{'
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == '}')
            {
                index++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != '"')
                {
                    throw new FormatException("Expected object key at offset " + index.ToString(CultureInfo.InvariantCulture));
                }

                string key = ParseString(text, ref index);
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != ':')
                {
                    throw new FormatException("Expected ':' at offset " + index.ToString(CultureInfo.InvariantCulture));
                }

                index++;
                result.Set(key, ParseValue(text, ref index, depth + 1));
                SkipWhitespace(text, ref index);
                if (index >= text.Length)
                {
                    throw new FormatException("Unterminated object");
                }

                if (text[index] == ',')
                {
                    index++;
                    continue;
                }

                if (text[index] == '}')
                {
                    index++;
                    return result;
                }

                throw new FormatException("Expected ',' or '}' at offset " + index.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static RevvyJson ParseArray(string text, ref int index, int depth)
        {
            RevvyJson result = Array();
            index++; // '['
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == ']')
            {
                index++;
                return result;
            }

            while (true)
            {
                result.Add(ParseValue(text, ref index, depth + 1));
                SkipWhitespace(text, ref index);
                if (index >= text.Length)
                {
                    throw new FormatException("Unterminated array");
                }

                if (text[index] == ',')
                {
                    index++;
                    continue;
                }

                if (text[index] == ']')
                {
                    index++;
                    return result;
                }

                throw new FormatException("Expected ',' or ']' at offset " + index.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static string ParseString(string text, ref int index)
        {
            index++; // opening quote
            StringBuilder builder = new StringBuilder();
            while (index < text.Length)
            {
                char c = text[index++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (index >= text.Length)
                {
                    break;
                }

                char escape = text[index++];
                switch (escape)
                {
                    case '"':
                        builder.Append('"');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    case '/':
                        builder.Append('/');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (index + 4 > text.Length)
                        {
                            throw new FormatException("Truncated \\u escape");
                        }

                        builder.Append((char)ushort.Parse(
                            text.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default:
                        throw new FormatException("Unknown escape \\" + escape);
                }
            }

            throw new FormatException("Unterminated string literal");
        }

        private static RevvyJson ParseNumber(string text, ref int index)
        {
            int start = index;
            if (index < text.Length && text[index] == '-')
            {
                index++;
            }

            // RFC 8259 number grammar is deliberately enforced here instead of
            // delegating tokenization to double.TryParse. NumberStyles.Float accepts
            // non-JSON forms such as +18088 and 018088; accepting either in the
            // runtime config lets Unity bind a file the Python proxy rejects.
            if (index >= text.Length)
            {
                throw new FormatException("Truncated number at offset " + start.ToString(CultureInfo.InvariantCulture));
            }

            if (text[index] == '0')
            {
                index++;
                if (index < text.Length && text[index] >= '0' && text[index] <= '9')
                {
                    throw new FormatException(
                        "Leading zero in number at offset " + start.ToString(CultureInfo.InvariantCulture));
                }
            }
            else if (text[index] >= '1' && text[index] <= '9')
            {
                while (index < text.Length && text[index] >= '0' && text[index] <= '9')
                {
                    index++;
                }
            }
            else
            {
                throw new FormatException("Expected number at offset " + start.ToString(CultureInfo.InvariantCulture));
            }

            bool integral = true;
            if (index < text.Length && text[index] == '.')
            {
                integral = false;
                index++;
                int fractionStart = index;
                while (index < text.Length && text[index] >= '0' && text[index] <= '9')
                {
                    index++;
                }

                if (index == fractionStart)
                {
                    throw new FormatException(
                        "Missing fraction digits at offset " + start.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                integral = false;
                index++;
                if (index < text.Length && (text[index] == '+' || text[index] == '-'))
                {
                    index++;
                }

                int exponentStart = index;
                while (index < text.Length && text[index] >= '0' && text[index] <= '9')
                {
                    index++;
                }

                if (index == exponentStart)
                {
                    throw new FormatException(
                        "Missing exponent digits at offset " + start.ToString(CultureInfo.InvariantCulture));
                }
            }

            string literal = text.Substring(start, index - start);
            double parsed;
            if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ||
                double.IsNaN(parsed) ||
                double.IsInfinity(parsed))
            {
                throw new FormatException("Malformed number '" + literal + "'");
            }

            return new RevvyJson(RevvyJsonKind.Number)
            {
                NumberValue = parsed,
                IsIntegral = integral,
            };
        }

        private static void Expect(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length ||
                string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("Expected '" + literal + "' at offset " + index.ToString(CultureInfo.InvariantCulture));
            }

            index += literal.Length;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                char c = text[index];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                {
                    index++;
                    continue;
                }

                break;
            }
        }
    }
}
