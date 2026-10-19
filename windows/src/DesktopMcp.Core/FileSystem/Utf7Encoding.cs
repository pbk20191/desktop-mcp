using System.Text;

namespace DesktopMcp.Core.FileSystem;

/// <summary>
/// Python's utf-7 codec (CPython PyUnicode_EncodeUTF7 and PyUnicode_DecodeUTF7). .NET's UTF7Encoding is obsolete and
/// writes different bytes: Python sends set O and whitespace directly and omits the '-' before a non-base64 character.
/// Decoding replaces each malformed sequence with U+FFFD (errors='replace'); encoding never fails.
/// </summary>
internal sealed class Utf7Encoding : Encoding
{
    private const string Base64Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    public override string EncodingName => "utf-7";

    public override string WebName => "utf-7";

    public override int GetMaxByteCount(int charCount) => checked((charCount * 8 + 2) / 3 + 2);

    public override int GetMaxCharCount(int byteCount) => byteCount + 1;

    public override int GetByteCount(char[] chars, int index, int count) => Encode(chars.AsSpan(index, count)).Count;

    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
    {
        var encoded = Encode(chars.AsSpan(charIndex, charCount));
        encoded.CopyTo(bytes, byteIndex);
        return encoded.Count;
    }

    public override int GetCharCount(byte[] bytes, int index, int count) => Decode(bytes.AsSpan(index, count)).Length;

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        var decoded = Decode(bytes.AsSpan(byteIndex, byteCount));
        decoded.CopyTo(chars.AsSpan(charIndex));
        return decoded.Length;
    }

    // CPython's utf7_category: 0 set D, 1 set O, 2 whitespace, 3 special ('+', '\', '~', controls).
    private static int Category(int c) => c switch
    {
        >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' => 0,
        '\'' or '(' or ')' or ',' or '-' or '.' or '/' or ':' or '?' => 0,
        ' ' or '\t' or '\r' or '\n' => 2,
        '+' or '\\' or '~' => 3,
        >= 0x21 and <= 0x7D => 1,
        _ => 3,
    };

    private static bool EncodeDirect(int c) => c is > 0 and < 128 && Category(c) != 3;

    private static bool IsBase64(int c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/';

    private static int FromBase64(int c) => Base64Chars.IndexOf((char)c, StringComparison.Ordinal);

    private static List<byte> Encode(ReadOnlySpan<char> text)
    {
        var output = new List<byte>(text.Length);
        var inShift = false;
        var bits = 0;
        var buffer = 0L;
        foreach (var ch in text)
        {
            if (inShift)
            {
                if (EncodeDirect(ch))
                {
                    if (bits > 0) output.Add((byte)Base64Chars[(int)((buffer << (6 - bits)) & 0x3F)]);
                    bits = 0;
                    buffer = 0;
                    inShift = false;
                    // A character outside the base64 set ends the shift by itself; only base64 characters and '-' need a '-'.
                    if (IsBase64(ch) || ch == '-') output.Add((byte)'-');
                    output.Add((byte)ch);
                    continue;
                }
            }
            else if (ch == '+')
            {
                output.Add((byte)'+');
                output.Add((byte)'-');
                continue;
            }
            else if (EncodeDirect(ch))
            {
                output.Add((byte)ch);
                continue;
            }
            else
            {
                output.Add((byte)'+');
                inShift = true;
            }
            bits += 16;
            buffer = (buffer << 16) | ch;
            while (bits >= 6)
            {
                output.Add((byte)Base64Chars[(int)((buffer >> (bits - 6)) & 0x3F)]);
                bits -= 6;
            }
            buffer &= (1L << bits) - 1;
        }
        if (bits > 0) output.Add((byte)Base64Chars[(int)((buffer << (6 - bits)) & 0x3F)]);
        if (inShift) output.Add((byte)'-');
        return output;
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        var output = new StringBuilder(bytes.Length);
        var inShift = false;
        var bits = 0;
        var buffer = 0L;
        char? surrogate = null;
        var i = 0;
        while (i < bytes.Length)
        {
            int ch = bytes[i];
            if (inShift)
            {
                if (IsBase64(ch))
                {
                    buffer = (buffer << 6) | (long)FromBase64(ch);
                    bits += 6;
                    i++;
                    if (bits >= 16)
                    {
                        var outCh = (char)(buffer >> (bits - 16));
                        bits -= 16;
                        buffer &= (1L << bits) - 1;
                        if (surrogate is { } high)
                        {
                            surrogate = null;
                            if (char.IsLowSurrogate(outCh))
                            {
                                output.Append(high).Append(outCh);
                                continue;
                            }
                            output.Append(high);
                        }
                        if (char.IsHighSurrogate(outCh)) surrogate = outCh;
                        else output.Append(outCh);
                    }
                    continue;
                }
                // Leaving the base64 section.
                inShift = false;
                if (bits >= 6 || (bits > 0 && buffer != 0))
                {
                    // A partial character or non-zero padding: the terminator and any pending surrogate go with it.
                    surrogate = null;
                    i++;
                    output.Append('�');
                    continue;
                }
                if (surrogate is { } pending && ch is <= 127 and not '+') output.Append(pending);
                surrogate = null;
                // '-' is absorbed; any other character is read again outside the shift.
                if (ch == '-') i++;
                continue;
            }
            if (ch == '+')
            {
                i++;
                if (i < bytes.Length && bytes[i] == '-')
                {
                    i++;
                    output.Append('+');
                }
                else if (i < bytes.Length && !IsBase64(bytes[i]))
                {
                    i++;
                    output.Append('�');
                }
                else
                {
                    inShift = true;
                    surrogate = null;
                    bits = 0;
                    buffer = 0;
                }
                continue;
            }
            i++;
            output.Append(ch <= 127 ? (char)ch : '�');
        }
        if (inShift && (surrogate is not null || bits >= 6 || (bits > 0 && buffer != 0))) output.Append('�');
        return output.ToString();
    }
}
