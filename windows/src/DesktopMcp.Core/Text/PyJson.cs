using System.Globalization;
using System.Text;

namespace DesktopMcp.Core.Text;

/// <summary>
/// Python's <c>json.dumps(obj, indent=2)</c> of a flat dict (ensure_ascii on, default separators), for
/// launch_executable's result (upstream tools/app.py:160-168, P3 Decisions 34). Values are integers, strings, string
/// lists and null; anything else is an <see cref="ArgumentException"/>.
/// </summary>
public static class PyJson
{
    public static string Dumps(IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count == 0) return "{}";
        var builder = new StringBuilder("{");
        for (var i = 0; i < fields.Count; i++)
        {
            builder.Append(i == 0 ? "\n  " : ",\n  ");
            AppendString(builder, fields[i].Key);
            builder.Append(": ");
            AppendValue(builder, fields[i].Value, fields[i].Key);
        }
        return builder.Append("\n}").ToString();
    }

    private static void AppendValue(StringBuilder builder, object? value, string key)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case string text:
                AppendString(builder, text);
                break;
            case long or int:
                builder.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                break;
            case IReadOnlyList<string> list:
                if (list.Count == 0)
                {
                    builder.Append("[]");
                    break;
                }
                builder.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    builder.Append(i == 0 ? "\n    " : ",\n    ");
                    AppendString(builder, list[i]);
                }
                builder.Append("\n  ]");
                break;
            default:
                throw new ArgumentException($"PyJson cannot write the value of '{key}' ({value.GetType().Name}).", nameof(value));
        }
    }

    // json.encoder.py_encode_basestring_ascii: the short escapes, \uXXXX (lower-case hex) for everything else outside
    // ' '..'~', UTF-16 code units one by one (so an astral character is its surrogate pair).
    private static void AppendString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c is >= ' ' and <= '~') builder.Append(c);
                    else builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
            }
        }
        builder.Append('"');
    }
}
