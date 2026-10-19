using System.Text;
using System.Text.RegularExpressions;

namespace DesktopMcp.Core.FileSystem;

/// <summary>Python's fnmatch.translate, matched case-insensitively (ntpath.normcase).</summary>
internal static class Fnmatch
{
    public static Regex ToRegex(string pattern) =>
        new("^" + Translate(pattern) + @"\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    internal static string Translate(string pattern)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    builder.Append(".*");
                    while (i + 1 < pattern.Length && pattern[i + 1] == '*') i++;
                    break;
                case '?':
                    builder.Append('.');
                    break;
                case '[':
                    var j = i + 1;
                    if (j < pattern.Length && pattern[j] == '!') j++;
                    if (j < pattern.Length && pattern[j] == ']') j++;
                    while (j < pattern.Length && pattern[j] != ']') j++;
                    if (j >= pattern.Length)
                    {
                        builder.Append(@"\[");
                        break;
                    }
                    var body = pattern[(i + 1)..j].Replace(@"\", @"\\");
                    i = j;
                    if (body.StartsWith('!')) body = "^" + body[1..];
                    else if (body.StartsWith('^')) body = @"\" + body;
                    builder.Append('[').Append(body).Append(']');
                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return builder.ToString();
    }
}
