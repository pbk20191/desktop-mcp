using System.Text.RegularExpressions;

namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// Makes the input guard structural (Decisions 11): no test may build a real injector except inside
/// <c>new GuardedInjector(...)</c>, nor the public DesktopInput constructor that injects through SendInput unguarded.
/// </summary>
public partial class GuardedInputRuleTests
{
    // Built by concatenation so this file does not match itself.
    private static readonly string NewInjector = "new " + "SendInputInjector(";
    private static readonly string NewInput = "new " + "DesktopInput(";

    [Fact]
    public void NoTestInjectsWithoutTheGuard()
    {
        var directory = Path.GetDirectoryName(RepoFile.Find(Path.Combine("windows", "tests", "DesktopMcp.Desktop.Tests", "GuardedInjector.cs")))!;
        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, path);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar) || relative.StartsWith("obj" + Path.DirectorySeparatorChar)
                || relative == nameof(GuardedInputRuleTests) + ".cs") continue;
            var text = File.ReadAllText(path);

            foreach (var index in Occurrences(text, NewInjector))
            {
                var arguments = Arguments(text, index + NewInjector.Length);
                var guarded = text[..index].TrimEnd().EndsWith("new GuardedInjector(", StringComparison.Ordinal) && arguments.Count == 0;
                var fakeSend = Path.GetFileName(path) == "SendInputInjectorTests.cs" && arguments.Count > 0;
                if (!guarded && !fakeSend) violations.Add($"{relative}: an unguarded SendInputInjector");
            }

            foreach (var index in Occurrences(text, NewInput))
            {
                if (Arguments(text, index + NewInput.Length).Count == 3) violations.Add($"{relative}: the public 3-argument DesktopInput constructor");
            }

            if (TargetTypedInput().IsMatch(text)) violations.Add($"{relative}: a target-typed DesktopInput construction");
        }
        Assert.Empty(violations);
    }

    private static IEnumerable<int> Occurrences(string text, string value)
    {
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
            yield return index;
    }

    /// <summary>The top-level arguments of the call whose opening parenthesis ends just before <paramref name="start"/>.</summary>
    private static List<string> Arguments(string text, int start)
    {
        var arguments = new List<string>();
        var depth = 0;
        var current = start;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ']' or '}') depth--;
            else if (c == ')')
            {
                if (depth == 0)
                {
                    var last = text[current..i].Trim();
                    if (last.Length > 0 || arguments.Count > 0) arguments.Add(last);
                    return arguments;
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(text[current..i].Trim());
                current = i + 1;
            }
        }
        return arguments;
    }

    // "DesktopInput x = new(" or "DesktopInput Name(...) =>\n new(": a target-typed construction hides its argument count from
    // the check above, so the tests spell the type out.
    [GeneratedRegex(@"\bDesktopInput\b[^;{]*=>?\s*new\s*\(")]
    private static partial Regex TargetTypedInput();
}
