using System.Text;

namespace DesktopMcp.Core.Snapshots;

/// <summary>upstream desktop/utils.py:195-241, applied to the Snapshot text (tools/_snapshot_helpers.py:282-299).</summary>
public static class UnicodeCleanup
{
    /// <summary>Removes Private Use Area characters (BMP, planes 15 and 16), which apps such as VS Code put in names.</summary>
    public static string RemovePrivateUse(string text)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var paired = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            var length = paired ? 2 : 1;
            var codePoint = paired ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            var privateUse = codePoint is >= 0xE000 and <= 0xF8FF or >= 0xF0000 and <= 0xFFFFD or >= 0x100000 and <= 0x10FFFD;
            if (privateUse) builder ??= new StringBuilder(text, 0, i, text.Length);
            else builder?.Append(text, i, length);
            i += length - 1;
        }
        return builder?.ToString() ?? text;
    }

    /// <summary>
    /// Makes a name or value from another app safe to place in one line of the Snapshot text (Decisions 35): CR, LF and TAB
    /// become the two characters \r, \n and \t, U+2028 and U+2029 become \u2028 and \u2029, and other C0 and C1 control
    /// characters are removed. Backslashes are left alone, so the escape is not reversible; the goal is that no name can
    /// start a new line.
    /// </summary>
    public static string EscapeControl(string text)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var replacement = c switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\u2028' => "\\u2028",
                '\u2029' => "\\u2029",
                <= '\u001F' or >= '\u007F' and <= '\u009F' => "",
                _ => null,
            };
            if (replacement is null) builder?.Append(c);
            else (builder ??= new StringBuilder(text, 0, i, text.Length)).Append(replacement);
        }
        return builder?.ToString() ?? text;
    }

    /// <summary>Replaces unpaired UTF-16 surrogates with U+FFFD, so the response always encodes as UTF-8.</summary>
    public static string RepairSurrogates(string text)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }
            if (char.IsSurrogate(c))
            {
                builder ??= new StringBuilder(text, 0, i, text.Length);
                builder.Append('�');
                continue;
            }
            builder?.Append(c);
        }
        return builder?.ToString() ?? text;
    }
}
