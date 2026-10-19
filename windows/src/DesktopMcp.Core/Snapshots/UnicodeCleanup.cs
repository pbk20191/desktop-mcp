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
