using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace EmbyIcons.Compat
{
    internal sealed class SimpleJsonException : Exception
    {
        public SimpleJsonException(string message) : base(message) { }
    }

    internal static class SimpleJson
    {
        private static readonly HashSet<Type> _numericTypes = new HashSet<Type>
        {
            typeof(sbyte), typeof(byte), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong),
            typeof(float), typeof(double), typeof(decimal)
        };

        public static string Serialize(object? value, bool indented = false)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, indented, 0);
            return sb.ToString();
        }

        public static T? Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return default;

            return (T?)Populate(Parse(json), typeof(T));
        }

        public static object? Parse(string json)
        {
            var index = 0;
            var tree = ParseValue(json, ref index);
            SkipWhitespace(json, ref index);
            if (index != json.Length)
                throw new SimpleJsonException("Unexpected trailing content in JSON input");

            return tree;
        }


        private static void WriteValue(StringBuilder sb, object? value, bool indented, int depth)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case char c:
                    WriteString(sb, c.ToString());
                    break;
                case Guid g:
                    WriteString(sb, g.ToString());
                    break;
                case DateTime dt:
                    WriteString(sb, dt.ToString("o", CultureInfo.InvariantCulture));
                    break;
                case Enum e:
                    WriteString(sb, e.ToString());
                    break;
                case float or double or decimal:
                    sb.Append(Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                    break;
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case IDictionary dict:
                    WriteDictionary(sb, dict, indented, depth);
                    break;
                case IEnumerable enumerable:
                    WriteArray(sb, enumerable, indented, depth);
                    break;
                default:
                    WriteObject(sb, value, indented, depth);
                    break;
            }
        }

        private static void WriteObject(StringBuilder sb, object value, bool indented, int depth)
        {
            var innerDepth = depth + 1;
            sb.Append('{');
            var first = true;

            foreach (var prop in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
                    continue;

                var propValue = prop.GetValue(value);
                if (propValue == null)
                    continue;

                if (!first) sb.Append(',');
                first = false;

                if (indented) { sb.Append('\n'); AppendIndent(sb, innerDepth); }
                WriteString(sb, prop.Name);
                sb.Append(indented ? ": " : ":");
                WriteValue(sb, propValue, indented, innerDepth);
            }

            if (!first && indented) { sb.Append('\n'); AppendIndent(sb, depth); }
            sb.Append('}');
        }

        private static void WriteDictionary(StringBuilder sb, IDictionary dict, bool indented, int depth)
        {
            var innerDepth = depth + 1;
            sb.Append('{');
            var first = true;

            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Value == null)
                    continue;

                if (!first) sb.Append(',');
                first = false;

                if (indented) { sb.Append('\n'); AppendIndent(sb, innerDepth); }
                WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                sb.Append(indented ? ": " : ":");
                WriteValue(sb, entry.Value, indented, innerDepth);
            }

            if (!first && indented) { sb.Append('\n'); AppendIndent(sb, depth); }
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable enumerable, bool indented, int depth)
        {
            var innerDepth = depth + 1;
            sb.Append('[');
            var first = true;

            foreach (var item in enumerable)
            {
                if (!first) sb.Append(',');
                first = false;

                if (indented) { sb.Append('\n'); AppendIndent(sb, innerDepth); }
                WriteValue(sb, item, indented, innerDepth);
            }

            if (!first && indented) { sb.Append('\n'); AppendIndent(sb, depth); }
            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var c in value)
            {
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
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void AppendIndent(StringBuilder sb, int depth)
        {
            sb.Append(' ', depth * 2);
        }


        private static object? ParseValue(string json, ref int index)
        {
            SkipWhitespace(json, ref index);
            if (index >= json.Length)
                throw new SimpleJsonException("Unexpected end of JSON input");

            var c = json[index];
            switch (c)
            {
                case '"': return ParseString(json, ref index);
                case '{': return ParseObject(json, ref index);
                case '[': return ParseArray(json, ref index);
                case 't': Expect(json, ref index, "true"); return true;
                case 'f': Expect(json, ref index, "false"); return false;
                case 'n': Expect(json, ref index, "null"); return null;
                default:
                    if (c == '-' || char.IsDigit(c))
                        return ParseNumber(json, ref index);
                    throw new SimpleJsonException($"Unexpected character '{c}' in JSON input");
            }
        }

        private static void Expect(string json, ref int index, string literal)
        {
            if (index + literal.Length > json.Length || string.CompareOrdinal(json, index, literal, 0, literal.Length) != 0)
                throw new SimpleJsonException($"Invalid JSON literal, expected '{literal}'");
            index += literal.Length;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
                index++;
        }

        private static Dictionary<string, object?> ParseObject(string json, ref int index)
        {
            index++;
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == '}')
            {
                index++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != '"')
                    throw new SimpleJsonException("Expected string key in JSON object");

                var key = ParseString(json, ref index);
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':')
                    throw new SimpleJsonException("Expected ':' in JSON object");
                index++;

                result[key] = ParseValue(json, ref index);
                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                    throw new SimpleJsonException("Unterminated JSON object");

                if (json[index] == ',') { index++; continue; }
                if (json[index] == '}') { index++; break; }
                throw new SimpleJsonException("Expected ',' or '}' in JSON object");
            }

            return result;
        }

        private static List<object?> ParseArray(string json, ref int index)
        {
            index++;
            var result = new List<object?>();
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == ']')
            {
                index++;
                return result;
            }

            while (true)
            {
                result.Add(ParseValue(json, ref index));
                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                    throw new SimpleJsonException("Unterminated JSON array");

                if (json[index] == ',') { index++; continue; }
                if (json[index] == ']') { index++; break; }
                throw new SimpleJsonException("Expected ',' or ']' in JSON array");
            }

            return result;
        }

        private static string ParseString(string json, ref int index)
        {
            index++;
            var sb = new StringBuilder();

            while (true)
            {
                if (index >= json.Length)
                    throw new SimpleJsonException("Unterminated JSON string");

                var c = json[index++];
                if (c == '"') break;

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (index >= json.Length)
                    throw new SimpleJsonException("Unterminated JSON escape sequence");

                var escape = json[index++];
                switch (escape)
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
                        if (index + 4 > json.Length)
                            throw new SimpleJsonException("Invalid unicode escape in JSON string");
                        sb.Append((char)ushort.Parse(json.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default:
                        throw new SimpleJsonException($"Invalid escape sequence '\\{escape}' in JSON string");
                }
            }

            return sb.ToString();
        }

        private static object ParseNumber(string json, ref int index)
        {
            var start = index;
            if (json[index] == '-') index++;
            while (index < json.Length && char.IsDigit(json[index])) index++;

            var isFloat = false;
            if (index < json.Length && json[index] == '.')
            {
                isFloat = true;
                index++;
                while (index < json.Length && char.IsDigit(json[index])) index++;
            }

            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                isFloat = true;
                index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-')) index++;
                while (index < json.Length && char.IsDigit(json[index])) index++;
            }

            var text = json.Substring(start, index - start);
            if (!isFloat && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                return longValue;

            return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }


        private static bool IsNumeric(Type type) => _numericTypes.Contains(type);

        private static object? Populate(object? node, Type targetType)
        {
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
            {
                if (node == null) return null;
                targetType = underlying;
            }

            if (targetType == typeof(object))
                return node;

            if (node == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            if (targetType == typeof(string))
                return node as string ?? Convert.ToString(node, CultureInfo.InvariantCulture);

            if (targetType == typeof(bool))
                return node is bool b ? b : Convert.ToBoolean(node, CultureInfo.InvariantCulture);

            if (targetType == typeof(Guid))
                return node is string guidText ? Guid.Parse(guidText) : Guid.Empty;

            if (targetType == typeof(DateTime))
                return node is string dateText
                    ? DateTime.Parse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    : Convert.ToDateTime(node, CultureInfo.InvariantCulture);

            if (targetType.IsEnum)
                return node is string enumText
                    ? Enum.Parse(targetType, enumText, ignoreCase: true)
                    : Enum.ToObject(targetType, Convert.ToInt64(node, CultureInfo.InvariantCulture));

            if (IsNumeric(targetType))
                return Convert.ChangeType(node, targetType, CultureInfo.InvariantCulture);

            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elementType = targetType.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(targetType)!;
                if (node is List<object?> items)
                {
                    foreach (var item in items)
                        list.Add(Populate(item, elementType));
                }
                return list;
            }

            if (targetType.IsArray)
            {
                var elementType = targetType.GetElementType()!;
                var items = node as List<object?> ?? new List<object?>();
                var array = Array.CreateInstance(elementType, items.Count);
                for (var i = 0; i < items.Count; i++)
                    array.SetValue(Populate(items[i], elementType), i);
                return array;
            }

            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var args = targetType.GetGenericArguments();
                var dict = (IDictionary)Activator.CreateInstance(targetType)!;
                if (node is Dictionary<string, object?> map)
                {
                    foreach (var kvp in map)
                    {
                        var key = args[0] == typeof(string) ? (object)kvp.Key : Convert.ChangeType(kvp.Key, args[0], CultureInfo.InvariantCulture);
                        dict[key] = Populate(kvp.Value, args[1]);
                    }
                }
                return dict;
            }

            if (node is Dictionary<string, object?> obj)
            {
                var instance = Activator.CreateInstance(targetType)!;
                foreach (var prop in targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanWrite || prop.GetIndexParameters().Length > 0)
                        continue;
                    if (obj.TryGetValue(prop.Name, out var rawValue))
                        prop.SetValue(instance, Populate(rawValue, prop.PropertyType));
                }
                return instance;
            }

            throw new SimpleJsonException($"Cannot map JSON value to type '{targetType.FullName}'");
        }
    }
}
