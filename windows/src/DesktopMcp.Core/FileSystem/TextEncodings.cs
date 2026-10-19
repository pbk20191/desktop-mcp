using System.Globalization;
using System.Text;
using DesktopMcp.Core.Interop;

namespace DesktopMcp.Core.FileSystem;

/// <summary>Python codec names as .NET encodings (Decisions 10).</summary>
internal static class TextEncodings
{
    static TextEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// The encoding for a Python codec name, decoding with U+FFFD for bad bytes (errors='replace') and encoding strictly.
    /// <c>Signature</c> marks codecs that write a BOM (utf-8-sig, utf-16, utf-32).
    /// </summary>
    public static (Encoding Encoding, bool Signature) Get(string name)
    {
        var key = name.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        var (encoding, signature) = key switch
        {
            "utf-8" or "utf8" or "u8" or "utf" or "cp65001" => ((Encoding)new UTF8Encoding(false), false),
            "utf-8-sig" or "utf8-sig" => (new UTF8Encoding(true), true),
            "utf-16" or "utf16" => (new UnicodeEncoding(false, true), true),
            "utf-16-le" or "utf-16le" => (new UnicodeEncoding(false, false), false),
            "utf-16-be" or "utf-16be" => (new UnicodeEncoding(true, false), false),
            "utf-32" or "utf32" => (new UTF32Encoding(false, true), true),
            "utf-32-le" or "utf-32le" => (new UTF32Encoding(false, false), false),
            "utf-32-be" or "utf-32be" => (new UTF32Encoding(true, false), false),
            "latin-1" or "latin1" or "latin" or "l1" or "iso-8859-1" or "iso8859-1" => (Encoding.Latin1, false),
            "ascii" or "us-ascii" => (Encoding.ASCII, false),
            "utf-7" or "utf7" or "u7" => (new Utf7Encoding(), false),
            "mbcs" or "ansi" or "dbcs" => (Encoding.GetEncoding((int)CoreNative.GetACP()), false),
            "oem" => (Encoding.GetEncoding((int)CoreNative.GetOEMCP()), false),
            // .NET knows "unicode" (UTF-16); Python does not.
            "unicode" => throw Unknown(name),
            _ => (Lookup(key, name), false),
        };
        return (Configure(encoding), signature);
    }

    /// <summary>A writable copy with Python's error handling.</summary>
    public static Encoding Configure(Encoding encoding)
    {
        var configured = (Encoding)encoding.Clone();
        configured.EncoderFallback = EncoderFallback.ExceptionFallback;
        configured.DecoderFallback = new DecoderReplacementFallback("\uFFFD");
        return configured;
    }

    /// <summary>Any name .NET knows (the name as given first, so ks_c_5601-1987 works), cpNNN, or Python's iso8859-N.</summary>
    private static Encoding Lookup(string key, string name)
    {
        if (key.StartsWith("cp", StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var codePage))
        {
            try
            {
                return Encoding.GetEncoding(codePage);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                throw Unknown(name);
            }
        }
        string[] candidates = key.StartsWith("iso8859-", StringComparison.Ordinal) ? [name.Trim(), key, "iso-" + key[3..]] : [name.Trim(), key];
        foreach (var candidate in candidates)
        {
            try
            {
                return Encoding.GetEncoding(candidate);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
            }
        }
        throw Unknown(name);
    }

    private static ArgumentException Unknown(string name) => new($"unknown encoding: {name}");
}
