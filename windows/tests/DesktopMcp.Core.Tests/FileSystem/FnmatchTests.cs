using System.Diagnostics;
using DesktopMcp.Core.FileSystem;

namespace DesktopMcp.Core.Tests;

public sealed class FnmatchTests
{
    // Expected values are `py -3.13 -c "import fnmatch; print(fnmatch.translate(p))"` with three .NET adjustments (see
    // Fnmatch): \Z becomes \z, a [ inside a set is escaped ("[--[]" and "[a[]" below), and a hyphen that starts a
    // range is \x2D, not \- ("[--[]": CPython gives [\--[]).
    [Theory]
    [InlineData("", @"(?s:)\z")]
    [InlineData("*", @"(?s:.*)\z")]
    [InlineData("**", @"(?s:.*)\z")]
    [InlineData("a*b", @"(?s:a.*b)\z")]
    [InlineData("*.txt", @"(?s:.*\.txt)\z")]
    [InlineData("*a*a*a*a*a*b", @"(?s:(?>.*?a)(?>.*?a)(?>.*?a)(?>.*?a)(?>.*?a).*b)\z")]
    [InlineData("foo*bar*baz", @"(?s:foo(?>.*?bar).*baz)\z")]
    [InlineData("*x", @"(?s:.*x)\z")]
    [InlineData("x*", @"(?s:x.*)\z")]
    [InlineData("a?c", @"(?s:a.c)\z")]
    [InlineData("[abc]", @"(?s:[abc])\z")]
    [InlineData("[!abc]", @"(?s:[^abc])\z")]
    [InlineData("[]]", @"(?s:[]])\z")]
    [InlineData("[!]]", @"(?s:[^]])\z")]
    [InlineData("[a-z]", @"(?s:[a-z])\z")]
    [InlineData("[z-a]", @"(?s:(?!))\z")]
    [InlineData("[!z-a]", @"(?s:.)\z")]
    [InlineData("[a-c-e]", @"(?s:[a-c\-e])\z")]
    [InlineData("[a-c-e-g]", @"(?s:[a-c\-e-g])\z")]
    [InlineData("[e-c-a]", @"(?s:[\-a])\z")]
    [InlineData("[a-]", @"(?s:[a\-])\z")]
    [InlineData("[-a]", @"(?s:[\-a])\z")]
    [InlineData("[!-a]", @"(?s:[^\-a])\z")]
    [InlineData("[--[]", @"(?s:[\x2D-\[])\z")]
    [InlineData(@"[\]", @"(?s:[\\])\z")]
    [InlineData("[a&&b]", @"(?s:[a\&\&b])\z")]
    [InlineData("[~|]", @"(?s:[\~\|])\z")]
    [InlineData("[^a]", @"(?s:[\^a])\z")]
    [InlineData("[[]", @"(?s:[\[])\z")]
    [InlineData("[a[]", @"(?s:[a\[])\z")]
    [InlineData("a[", @"(?s:a\[)\z")]
    [InlineData("[!", @"(?s:\[!)\z")]
    [InlineData("[]", @"(?s:\[\])\z")]
    [InlineData("[!]", @"(?s:\[!\])\z")]
    [InlineData("a.b+c(d)", @"(?s:a\.b\+c\(d\))\z")]
    [InlineData("a b#~-&", @"(?s:a\ b\#\~\-\&)\z")]
    public void TranslateMatchesCPython(string pattern, string expected)
    {
        Assert.Equal(expected, Fnmatch.Translate(pattern));
        _ = Fnmatch.ToRegex(pattern);
    }

    [Theory]
    [InlineData("*.txt", "a.txt", true)]
    [InlineData("*.txt", "A.TXT", true)]
    [InlineData("?.log", "c.log", true)]
    [InlineData("[ab].txt", "b.txt", true)]
    [InlineData("[!ab].txt", "c.txt", true)]
    [InlineData("[!ab].txt", "a.txt", false)]
    [InlineData("*", ".hidden", true)]
    [InlineData("a.txt", "a.txtx", false)]
    [InlineData("a*", "a\nb", true)]
    [InlineData("a", "a\n", false)]
    [InlineData("[z-a]", "z", false)]
    [InlineData("[z-a]", "a", false)]
    [InlineData("[z-a]", "-", false)]
    [InlineData("[!z-a]", "q", true)]
    [InlineData("[--[]", "-", true)]
    [InlineData("[--[]", "[", true)]
    [InlineData("[--[]", "0", true)]
    [InlineData("[--[]", "{", false)]
    [InlineData("[a-[]", "b", false)]
    [InlineData("[a-[]", "[", false)]
    [InlineData("[a[]", "[", true)]
    [InlineData("[a[]", "a", true)]
    [InlineData("[]]", "]", true)]
    [InlineData("[!]]", "]", false)]
    [InlineData("a[", "a[", true)]
    [InlineData("foo*bar*baz", "fooXbarYbarZbaz", true)]
    [InlineData("foo*bar*baz", "foobarba", false)]
    public void FnmatchIsCaseInsensitiveAndMatchesLikePython(string pattern, string name, bool expected) =>
        Assert.Equal(expected, Fnmatch.ToRegex(pattern).IsMatch(name));

    // Sets with hyphens, brackets and reversed ranges match the same printable ASCII characters as CPython's regex
    // (generated with re.compile(fnmatch.translate(p)).match, case-sensitive).
    [Theory]
    [InlineData(@"[--[]", @"-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[")]
    [InlineData(@"[!--[]", @" !""#$%&'()*+,\]^_`abcdefghijklmnopqrstuvwxyz{|}~")]
    [InlineData(@"[+--]", @"+,-")]
    [InlineData(@"[*--]", @"*+,-")]
    [InlineData(@"[--/]", @"-./")]
    [InlineData(@"[a-c-e]", @"-abce")]
    [InlineData(@"[e-c-a]", @"-a")]
    [InlineData(@"[a-c-e-g]", @"-abcefg")]
    [InlineData(@"[!-a]", @" !""#$%&'()*+,./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\]^_`bcdefghijklmnopqrstuvwxyz{|}~")]
    [InlineData(@"[a-]", @"-a")]
    [InlineData(@"[\-a]", @"\]^_`a")]
    [InlineData(@"[a-\]", @"")]
    [InlineData(@"[\--]", @"")]
    [InlineData(@"[]-a]", @"]^_`a")]
    [InlineData(@"[!]-a]", @" !""#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\bcdefghijklmnopqrstuvwxyz{|}~")]
    [InlineData(@"[^-a]", @"^_`a")]
    [InlineData(@"[a[-z]", @"[\]^_`abcdefghijklmnopqrstuvwxyz")]
    [InlineData(@"[%-&]", @"%&")]
    [InlineData(@"[|-~]", @"|}~")]
    [InlineData(@"[z-a]", @"")]
    [InlineData(@"[!z-a]", @" !""#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\]^_`abcdefghijklmnopqrstuvwxyz{|}~")]
    [InlineData(@"[--]", @"-")]
    [InlineData(@"[---]", @"-")]
    [InlineData(@"[----]", @"-")]
    [InlineData(@"[-]", @"-")]
    public void SetsMatchWhatCPythonMatches(string pattern, string expected)
    {
        var regex = new System.Text.RegularExpressions.Regex(@"\A" + Fnmatch.Translate(pattern));
        var printable = Enumerable.Range(0x20, 0x7f - 0x20).Select(c => ((char)c).ToString());
        Assert.Equal(expected, string.Concat(printable.Where(c => regex.IsMatch(c))));
    }

    [Fact]
    public async Task AManyStarPatternDoesNotBacktrackCatastrophically()
    {
        var regex = Fnmatch.ToRegex("*a*a*a*a*a*b");
        var name = new string('a', 240);
        var stopwatch = Stopwatch.StartNew();
        // The old greedy translation took hours on this name; run it off the test thread so a regression fails, not hangs.
        var match = Task.Run(() => regex.IsMatch(name));
        var finished = await Task.WhenAny(match, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.True(finished == match, "matching took longer than 1 s");
        Assert.False(await match);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }
}
