using System.Globalization;
using System.Numerics;
using System.Text;

namespace DesktopMcp.Core.Text;

/// <summary>
/// Python's formatting rules that upstream's output strings depend on. Python formats the exact binary value of a
/// double and rounds exact ties half to even; .NET rounds ties away from zero, so the rounding is done here on the
/// exact decimal expansion.
/// </summary>
public static class PyText
{
    /// <summary>Python's <c>f"{n:,}"</c>.</summary>
    public static string Thousands(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>Python's <c>f"{x:.Nf}"</c>.</summary>
    public static string Fixed(double value, int decimals)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        var (digits, scale) = Exact(value);
        var rounded = RoundToDecimals(digits, scale, decimals).ToString(CultureInfo.InvariantCulture);
        rounded = rounded.PadLeft(decimals + 1, '0');
        var text = decimals == 0 ? rounded : rounded[..^decimals] + "." + rounded[^decimals..];
        return double.IsNegative(value) ? "-" + text : text;
    }

    /// <summary>Python's <c>format(x, "g")</c>: six significant digits, trailing zeros removed.</summary>
    public static string FormatG(double value)
    {
        const int precision = 6;
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        var sign = double.IsNegative(value) ? "-" : "";
        if (value == 0) return sign + "0";
        var (digits, scale) = Exact(value);
        var exponent = digits.Length - 1 - scale;
        var significant = RoundToDecimals(digits, scale, precision - 1 - exponent);
        if (significant.ToString(CultureInfo.InvariantCulture).Length > precision)
        {
            exponent++;
            significant /= 10;
        }
        if (exponent >= -4 && exponent < precision)
            return StripZeros(Fixed(value, precision - 1 - exponent));
        var mantissa = significant.ToString(CultureInfo.InvariantCulture);
        var text = StripZeros(mantissa[..1] + "." + mantissa[1..]);
        return $"{sign}{text}e{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent):00}";
    }

    /// <summary>
    /// Python's <c>repr(float)</c> (also <c>str(float)</c>): the shortest digits that round-trip, in exponent form when
    /// the decimal exponent is below -4 or above 15 (<c>1e-05</c>, <c>1e+16</c>), else with at least one decimal (<c>100.0</c>).
    /// </summary>
    public static string FloatRepr(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        if (value == 0) return double.IsNegative(value) ? "-0.0" : "0.0";
        // .NET's "R" is also the shortest round-trip form, only spelled differently ("1E-05", "1E+16", "0.0001").
        var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var e = shortest.IndexOf('E');
        var mantissa = e < 0 ? shortest : shortest[..e];
        var exponent = e < 0 ? 0 : int.Parse(shortest[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        var allDigits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        var integerLength = point < 0 ? mantissa.Length : point;
        var leadingZeros = allDigits.Length - allDigits.TrimStart('0').Length;
        var digits = allDigits.TrimStart('0').TrimEnd('0');
        // value == 0.<digits> * 10^decimalPoint
        var decimalPoint = integerLength - leadingZeros + exponent;
        string text;
        if (decimalPoint is > 16 or < -3)
        {
            var power = decimalPoint - 1;
            text = $"{digits[0]}{(digits.Length > 1 ? "." + digits[1..] : "")}e{(power < 0 ? '-' : '+')}{Math.Abs(power):00}";
        }
        else if (decimalPoint <= 0) text = "0." + new string('0', -decimalPoint) + digits;
        else if (decimalPoint >= digits.Length) text = digits + new string('0', decimalPoint - digits.Length) + ".0";
        else text = digits[..decimalPoint] + "." + digits[decimalPoint..];
        return double.IsNegative(value) ? "-" + text : text;
    }

    /// <summary>Python's <c>round(x, digits)</c>: the exact binary value rounded half to even, as the nearest double.</summary>
    public static double Round(double value, int digits) =>
        double.IsFinite(value) ? double.Parse(Fixed(value, digits), CultureInfo.InvariantCulture) : value;

    /// <summary>Python's <c>round(x)</c> with no digits: half to even on the exact value.</summary>
    public static long RoundToInteger(double value) => (long)Math.Round(value, MidpointRounding.ToEven);

    /// <summary>Python's <c>str.capitalize()</c> for the strings UIA returns: first character upper, the rest lower.</summary>
    public static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    /// <summary>Python's <c>repr(str)</c> for the strings our messages quote.</summary>
    public static string Repr(string value)
    {
        var quote = value.Contains('\'') && !value.Contains('"') ? '"' : '\'';
        var builder = new StringBuilder().Append(quote);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append(@"\\"); break;
                case '\n': builder.Append(@"\n"); break;
                case '\r': builder.Append(@"\r"); break;
                case '\t': builder.Append(@"\t"); break;
                default:
                    if (c == quote) builder.Append('\\').Append(c);
                    else if (c < 0x20 || c == 0x7f) builder.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:x2}");
                    else builder.Append(c);
                    break;
            }
        }
        return builder.Append(quote).ToString();
    }

    /// <summary>Python's <c>shlex.quote</c>.</summary>
    public static string ShellQuote(string value)
    {
        if (value.Length == 0) return "''";
        if (value.All(c => char.IsAsciiLetterOrDigit(c) || "@%+=:,./-_".Contains(c))) return value;
        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    /// <summary>Python's <c>shlex.join</c>.</summary>
    public static string ShellJoin(IEnumerable<string> args) => string.Join(' ', args.Select(ShellQuote));

    /// <summary>Python's <c>len(str)</c>: code points, so a surrogate pair counts once.</summary>
    public static int CodePointLength(string value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++, count++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
        }
        return count;
    }

    /// <summary>Python's <c>value[:codePoints]</c>.</summary>
    public static string Prefix(string value, int codePoints)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++, count++)
        {
            if (count == codePoints) return value[..i];
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
        }
        return value;
    }

    private static string StripZeros(string text) =>
        text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;

    // |value| == digits * 10^-scale, exactly.
    private static (string Digits, int Scale) Exact(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent = 1;
        else mantissa |= 1L << 52;
        exponent -= 1075;
        if (exponent >= 0)
            return ((new BigInteger(mantissa) << exponent).ToString(CultureInfo.InvariantCulture), 0);
        // mantissa / 2^e == mantissa * 5^e / 10^e
        return ((new BigInteger(mantissa) * BigInteger.Pow(5, -exponent)).ToString(CultureInfo.InvariantCulture), -exponent);
    }

    // digits * 10^-scale rounded half to even to `decimals` places, as an integer count of 10^-decimals.
    private static BigInteger RoundToDecimals(string digits, int scale, int decimals)
    {
        var number = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        if (scale <= decimals) return number * BigInteger.Pow(10, decimals - scale);
        var divisor = BigInteger.Pow(10, scale - decimals);
        var quotient = BigInteger.DivRem(number, divisor, out var remainder);
        var twice = remainder * 2;
        if (twice > divisor || (twice == divisor && !quotient.IsEven)) quotient += 1;
        return quotient;
    }
}
