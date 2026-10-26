using System.Text;

namespace DesktopMcp.Core.FileSystem;

/// <summary>Python's text-mode file I/O on Windows (Decisions 10).</summary>
internal static class TextFiles
{
    /// <summary>open(path, encoding=..., errors='replace').read(): BOM handling for signature codecs, universal newlines.</summary>
    public static string Read(string path, string encodingName)
    {
        var (encoding, signature) = TextEncodings.Get(encodingName);
        var bytes = File.ReadAllBytes(path);
        var offset = 0;
        if (signature)
        {
            var preamble = encoding.GetPreamble();
            if (bytes.AsSpan().StartsWith(preamble))
            {
                offset = preamble.Length;
            }
            else if (encoding is UnicodeEncoding && bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encoding = TextEncodings.Configure(new UnicodeEncoding(bigEndian: true, byteOrderMark: false));
                offset = 2;
            }
        }
        return encoding.GetString(bytes, offset, bytes.Length - offset).Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>readlines(): every line keeps its "\n".</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>open(path, 'w' or 'a', encoding=...).write(content): "\n" becomes "\r\n"; a BOM only into an empty file.
    /// The text is encoded before the file is opened, so an encoding error leaves the file untouched.</summary>
    public static void Write(string path, string content, bool append, string encodingName)
    {
        var (encoding, signature) = TextEncodings.Get(encodingName);
        var bytes = encoding.GetBytes(content.Replace("\n", "\r\n"));
        using var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
        if (signature && stream.Length == 0) stream.Write(encoding.GetPreamble());
        stream.Write(bytes);
    }
}
