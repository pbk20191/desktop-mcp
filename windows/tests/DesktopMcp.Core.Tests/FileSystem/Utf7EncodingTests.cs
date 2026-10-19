using System.Text.RegularExpressions;
using DesktopMcp.Core.FileSystem;

namespace DesktopMcp.Core.Tests;

/// <summary>Vectors from CPython 3.13: bytes.decode('utf-7', 'replace') and str.encode('utf-7').</summary>
public sealed class Utf7EncodingTests
{
    private static readonly System.Text.Encoding Utf7 = TextEncodings.Get("utf-7").Encoding;

    // xUnit mangles lone surrogates in InlineData strings, so the expected text is escaped.
    [Theory]
    [InlineData("4869204d6f6d202d2b4a6a6f2d2d21", @"Hi Mom -\u263a-!")]
    [InlineData("2b414745415967426a", @"abc")]
    [InlineData("2b414745", @"a")]
    [InlineData("2b41472d", @"\ufffd")]
    [InlineData("2b412d", @"\ufffd")]
    [InlineData("2b2d", @"+")]
    [InlineData("2b21", @"\ufffd")]
    [InlineData("618062", @"a\ufffdb")]
    [InlineData("2b3244336541412d", @"\ud83d\ude00")]
    [InlineData("2b324433654141", @"\ud83d\ude00")]
    [InlineData("2b3244332d78", @"\ufffdx")]
    [InlineData("2b324430", @"\ufffd")]
    [InlineData("2b3244332d", @"\ufffd")]
    [InlineData("2b3244302e78", @"\ud83d.x")]
    [InlineData("2b4147452e78", @"a.x")]
    [InlineData("7e5c", @"~\u005c")]
    [InlineData("2b414745412d", @"a\ufffd")]
    [InlineData("2b414745412e", @"a\ufffd")]
    [InlineData("2b4147454159512d", @"aa")]
    [InlineData("2b414745415951", @"aa")]
    [InlineData("782b", @"x")]
    [InlineData("2b414745412b4147492d", @"a\u00f8\u0188")]
    [InlineData("2b3244304159512d", @"\ud83da")]
    [InlineData("2b3244304159512e", @"\ud83da.")]
    [InlineData("2b324430415951", @"\ud83da")]
    public void DecodesLikePython(string hex, string escapedExpected) =>
        Assert.Equal(Regex.Unescape(escapedExpected), Utf7.GetString(Convert.FromHexString(hex)));

    [Theory]
    [InlineData("Hi \u263a!", "4869202b4a6a6f21")]
    [InlineData("a+b", "612b2d62")]
    [InlineData("~\u005c", "2b4148344158412d")]
    [InlineData("1-\u263a-2", "312d2b4a6a6f2d2d32")]
    [InlineData("\u263aa", "2b4a6a6f2d61")]
    [InlineData("\u263a.", "2b4a6a6f2e")]
    [InlineData("\ud83d\ude00", "2b3244336541412d")]
    [InlineData("\u0000", "2b4141412d")]
    [InlineData("\u00e9 \u00e9", "2b414f6b202b414f6b2d")]
    [InlineData("\u263a\u263a-", "2b4a6a6f6d4f672d2d")]
    [InlineData("a\u0009b\u000d\u000a", "6109620d0a")]
    [InlineData("\u263a+", "2b4a6a6f414b772d")]
    public void EncodesLikePython(string text, string hex) => Assert.Equal(hex, Convert.ToHexStringLower(Utf7.GetBytes(text)));
}
