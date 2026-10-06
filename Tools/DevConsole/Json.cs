using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace SailwindCoop.DevConsole
{
    /// <summary>
    /// Turns an arbitrary script result into JSON without ever throwing: bounded depth and length,
    /// and a hook for engine types whose members must not be walked.
    /// </summary>
    internal static class Json
    {
        public const int MaxDepth = 3;
        public const int MaxItems = 200;

        /// <summary>Returns a short text for values that must not be expanded (engine objects), or null.</summary>
        public static Func<object, string> Describe;

        public static string Quote(string text)
        {
            var sb = new StringBuilder();
            WriteString(sb, text);
            return sb.ToString();
        }

        public static string Value(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value, int depth)
        {
            if (value == null) { sb.Append("null"); return; }
            try
            {
                string described = Describe != null ? Describe(value) : null;
                if (described != null) { WriteString(sb, described); return; }
            }
            catch (Exception e) { WriteString(sb, "<" + value.GetType().Name + ": " + e.Message + ">"); return; }

            switch (value)
            {
                case bool b: sb.Append(b ? "true" : "false"); return;
                case string s: WriteString(sb, s); return;
                case float f: WriteNumber(sb, f, f.ToString("R", CultureInfo.InvariantCulture)); return;
                case double d: WriteNumber(sb, d, d.ToString("R", CultureInfo.InvariantCulture)); return;
                case Type t: WriteString(sb, t.FullName); return;
            }
            Type type = value.GetType();
            if (type.IsPrimitive && !(value is char) || value is decimal)
            { sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (type.IsEnum || value is char || value is Guid || value is DateTime || value is TimeSpan ||
                value is Delegate || value is MemberInfo)
            { WriteString(sb, value.ToString()); return; }

            if (depth >= MaxDepth) { WriteString(sb, Short(value)); return; }

            if (value is IDictionary map)
            {
                sb.Append('{');
                int n = 0;
                foreach (DictionaryEntry entry in map)
                {
                    if (n > 0) sb.Append(',');
                    if (n == MaxItems) { WriteString(sb, "..."); sb.Append(':'); WriteString(sb, "+" + (map.Count - n) + " more"); break; }
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    Write(sb, entry.Value, depth + 1);
                    n++;
                }
                sb.Append('}');
                return;
            }
            if (value is IEnumerable list)
            {
                sb.Append('[');
                int n = 0;
                foreach (object item in list)
                {
                    if (n > 0) sb.Append(',');
                    if (n == MaxItems) { WriteString(sb, "... more"); break; }
                    Write(sb, item, depth + 1);
                    n++;
                }
                sb.Append(']');
                return;
            }
            if (OverridesToString(type)) { WriteString(sb, value.ToString()); return; }

            sb.Append('{');
            bool first = true;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, field.Name);
                sb.Append(':');
                object member;
                try { member = field.GetValue(value); }
                catch (Exception e) { member = "<" + e.Message + ">"; }
                Write(sb, member, depth + 1);
            }
            sb.Append('}');
        }

        private static string Short(object value)
        {
            try { return OverridesToString(value.GetType()) ? value.ToString() : "<" + value.GetType().Name + ">"; }
            catch (Exception e) { return "<" + value.GetType().Name + ": " + e.Message + ">"; }
        }

        private static bool OverridesToString(Type type)
        {
            var method = type.GetMethod("ToString", Type.EmptyTypes);
            return method != null && method.DeclaringType != typeof(object) && method.DeclaringType != typeof(ValueType);
        }

        private static void WriteNumber(StringBuilder sb, double number, string text)
        {
            if (double.IsNaN(number) || double.IsInfinity(number)) WriteString(sb, text);
            else sb.Append(text);
        }

        private static void WriteString(StringBuilder sb, string text)
        {
            sb.Append('"');
            foreach (char c in text ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
