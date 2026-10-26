using System.Text;
using System.Text.RegularExpressions;

namespace DesktopMcp.Core.FileSystem;

/// <summary>
/// CPython 3.13's fnmatch.translate (Lib/fnmatch.py: _translate and _join_translated_parts), matched case-insensitively
/// as ntpath.normcase does (Decisions 12). Consecutive stars collapse, and every interior star-fixed pair becomes the
/// atomic group (?&gt;.*?FIXED), so a many-star pattern cannot backtrack catastrophically.
/// The output equals CPython's except where .NET regex syntax must differ:
/// <list type="bullet">
/// <item>the end anchor is \z (Python's \Z means end of string; .NET's \Z also matches before a final newline);</item>
/// <item>every [ inside a set is escaped, because .NET reads "-[" in a set as character class subtraction;</item>
/// <item>a literal hyphen that starts a range is \x2D, because .NET's \- is a literal that cannot start a range.</item>
/// </list>
/// </summary>
internal static class Fnmatch
{
    // Stands for a star in the translated parts, as CPython's STAR sentinel object does.
    private const string? Star = null;

    public static Regex ToRegex(string pattern) =>
        new(@"\A" + Translate(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string Translate(string pattern) => JoinTranslatedParts(TranslateParts(pattern));

    private static List<string?> TranslateParts(string pat)
    {
        var res = new List<string?>();
        int i = 0, n = pat.Length;
        while (i < n)
        {
            var c = pat[i];
            i++;
            if (c == '*')
            {
                // Compress consecutive stars into one.
                if (res.Count == 0 || res[^1] is not Star) res.Add(Star);
            }
            else if (c == '?')
            {
                res.Add(".");
            }
            else if (c == '[')
            {
                var j = i;
                if (j < n && pat[j] == '!') j++;
                if (j < n && pat[j] == ']') j++;
                while (j < n && pat[j] != ']') j++;
                if (j >= n)
                {
                    res.Add(@"\[");
                    continue;
                }
                var stuff = pat[i..j];
                if (!stuff.Contains('-'))
                {
                    stuff = stuff.Replace(@"\", @"\\");
                }
                else
                {
                    var chunks = new List<string>();
                    var k = pat[i] == '!' ? i + 2 : i + 1;
                    while (true)
                    {
                        k = k < j ? pat.IndexOf('-', k, j - k) : -1;
                        if (k < 0) break;
                        chunks.Add(pat[i..k]);
                        i = k + 1;
                        k += 3;
                    }
                    var chunk = pat[i..j];
                    if (chunk.Length > 0) chunks.Add(chunk);
                    else chunks[^1] += "-";
                    // Remove empty (reversed) ranges: invalid in a regex.
                    for (var m = chunks.Count - 1; m > 0; m--)
                    {
                        if (chunks[m - 1][^1] > chunks[m][0])
                        {
                            chunks[m - 1] = chunks[m - 1][..^1] + chunks[m][1..];
                            chunks.RemoveAt(m);
                        }
                    }
                    // Escape backslashes and hyphens; the hyphens that make ranges are the joins. .NET reads \- as a
                    // literal that cannot start a range, so a hyphen that starts one is written \x2D.
                    stuff = string.Join("-", chunks.Select((s, index) =>
                    {
                        var escaped = s.Replace(@"\", @"\\").Replace("-", @"\-");
                        return index < chunks.Count - 1 && escaped.EndsWith(@"\-", StringComparison.Ordinal) ? escaped[..^2] + @"\x2D" : escaped;
                    }));
                }
                // Escape set operations (&&, ~~ and ||) as CPython does, and [ for .NET's subtraction syntax.
                stuff = EscapeSetOperators(stuff);
                i = j + 1;
                if (stuff.Length == 0)
                {
                    // Empty range: never match.
                    res.Add("(?!)");
                }
                else if (stuff == "!")
                {
                    // Negated empty range: match any character.
                    res.Add(".");
                }
                else
                {
                    if (stuff[0] == '!') stuff = "^" + stuff[1..];
                    else if (stuff[0] is '^' or '[') stuff = @"\" + stuff;
                    res.Add($"[{stuff}]");
                }
            }
            else
            {
                res.Add(ReEscape(c));
            }
        }
        return res;
    }

    private static string JoinTranslatedParts(List<string?> inp)
    {
        var res = new StringBuilder();
        int i = 0, n = inp.Count;
        // Fixed pieces at the start.
        while (i < n && inp[i] is not Star) res.Append(inp[i++]);
        // Then STAR fixed STAR fixed ...: an interior pair is a minimal .*? match of the fixed part with no way to
        // backtrack into it, and the last star is a plain .*.
        while (i < n)
        {
            i++;
            if (i == n)
            {
                res.Append(".*");
                break;
            }
            var fixedPart = new StringBuilder();
            while (i < n && inp[i] is not Star) fixedPart.Append(inp[i++]);
            if (i == n) res.Append(".*").Append(fixedPart);
            else res.Append("(?>.*?").Append(fixedPart).Append(')');
        }
        return $@"(?s:{res})\z";
    }

    // re.sub(r'([&~|])', r'\\\1', stuff), plus [ (see the class comment).
    private static string EscapeSetOperators(string stuff)
    {
        var builder = new StringBuilder(stuff.Length);
        foreach (var c in stuff)
        {
            if (c is '&' or '~' or '|' or '[') builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    // re.escape: CPython escapes exactly these characters. A backslash before any of them is valid in .NET too.
    private static string ReEscape(char c) => "()[]{}?*+-|^$\\.&~# \t\n\r\v\f".Contains(c) ? "\\" + c : c.ToString();
}
