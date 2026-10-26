using System.Text;

namespace DesktopMcp.Core.Text;

/// <summary>
/// Python's <c>subprocess.list2cmdline</c> (the command line Popen builds from an argument list on Windows; P3
/// Decisions 34): arguments joined by spaces, quoted when empty or containing a space or tab, a quote escaped with a
/// backslash and the backslashes before it doubled, and the backslashes before a closing quote doubled.
/// </summary>
public static class CommandLine
{
    public static string Join(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = new StringBuilder();
        foreach (var arg in args)
        {
            var backslashes = 0;
            if (result.Length > 0) result.Append(' ');
            var needQuote = arg.Length == 0 || arg.Contains(' ', StringComparison.Ordinal) || arg.Contains('\t', StringComparison.Ordinal);
            if (needQuote) result.Append('"');
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    result.Append('\\', backslashes * 2).Append("\\\"");
                    backslashes = 0;
                }
                else
                {
                    result.Append('\\', backslashes).Append(c);
                    backslashes = 0;
                }
            }
            result.Append('\\', backslashes);
            if (needQuote) result.Append('\\', backslashes).Append('"');
        }
        return result.ToString();
    }
}
