using System.Diagnostics;
using System.Text;
using DesktopMcp.Core.Scrape;

namespace DesktopMcp.Core.Tests;

public class HtmlToMarkdownTests
{
    [Theory]
    [InlineData("<h1>Title</h1><p>Hello <b>world</b>.</p><p>Second</p>", "# Title\n\nHello **world**.\n\nSecond")]
    [InlineData("<html><head><title> Page </title></head><body><p>Body</p></body></html>", "Page\n\nBody")]
    [InlineData("<title>T</title><h1>H</h1>", "# H")]
    [InlineData("<p><a href=\"https://x.test/a\">Link text</a> and <a href=\"#top\">anchor</a> and <a href=\"javascript:void(0)\">js</a></p>",
        "[Link text](https://x.test/a) and anchor and js")]
    [InlineData("<ul><li>One</li><li>Two<ol><li>A</li><li>B</li></ol></li></ul>", "- One\n- Two\n  1. A\n  2. B")]
    [InlineData("<ul><li>One<li>Two</ul><p>A<p>B", "- One\n- Two\n\nA\n\nB")]
    [InlineData("<p>Keep</p><script>var x = \"<p>no</p>\";</script><style>p{}</style><noscript>n</noscript>", "Keep")]
    [InlineData("<p>Fish &amp; Chips&nbsp;&lt;3 &#x41;</p>", "Fish & Chips <3 A")]
    [InlineData("<p>Use <code>ls -la</code></p><pre><code>line 1\n  line 2\n</code></pre>", "Use `ls -la`\n\n```\nline 1\n  line 2\n```")]
    [InlineData("<table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>", "| A | B |\n| 1 | 2 |")]
    [InlineData("<p>a<br>b</p>", "a  \nb")]
    [InlineData("<img src=\"/x.png\" alt=\"X\">", "![X](/x.png)")]
    [InlineData("<blockquote>Quote</blockquote>", "> Quote")]
    [InlineData("<P>Hi <A HREF='u'>x</A> <EM>e</EM> <i> </i></P>", "Hi [x](u) *e*")]
    [InlineData("<p>a</p><hr><p>b</p>", "a\n\n---\n\nb")]
    [InlineData("<h2>Two</h2><h3>Three", "## Two\n\n### Three")]
    [InlineData("<div>  spaced \n\t words  </div>", "spaced words")]
    [InlineData("<!DOCTYPE html><!-- comment --><p>x &lt; y</p>", "x < y")]
    [InlineData("a < b", "a < b")]
    [InlineData("   ", "")]
    public void ConvertsLikeTheMacOsPort(string html, string markdown) => Assert.Equal(markdown, HtmlToMarkdown.Convert(html));

    [Theory]
    [InlineData("<svg><title>Icon</title></svg><p>Body</p>", "Body")]
    [InlineData("<p>a</p><!-- never closed <p>b</p>", "a")]
    [InlineData("<p>a</p><script>never closed <p>b</p>", "a")]
    [InlineData("<p>x &#xD800; &#xDFFF;</p>", "x \uFFFD \uFFFD")]
    [InlineData("<p>&#0;|&#x110000;|&#99999999999999999999;|&#x;|&#;</p>", "\uFFFD|\uFFFD|\uFFFD|&#x;|&#;")]
    [InlineData("<img src=\"&#xD83D;\" alt=\"&#xDE00;\">", "![\uFFFD](\uFFFD)")]
    [InlineData("<p>&#x1F600; &#128512;</p>", "\U0001F600 \U0001F600")]
    public void HandlesEdgeCases(string html, string markdown) => Assert.Equal(markdown, HtmlToMarkdown.Convert(html));

    [Theory]
    [InlineData("div", "deep")]
    // Only the outer 16 wrappers render as Markdown; deeper inline nesting has no Markdown meaning.
    [InlineData("b", "********************************deep********************************")]
    public void SurvivesDeeplyNestedElements(string tag, string expected) => Assert.Equal(expected, ConvertNested(tag));

    [Theory]
    [InlineData("ul><li", "\n" + "                  - deep")]
    [InlineData("blockquote", "\n\n>\n\ndeep")]
    public void SurvivesDeeplyNestedBlocks(string tag, string expectedEnd) => Assert.EndsWith(expectedEnd, ConvertNested(tag));

    private static string ConvertNested(string tag)
    {
        const int depth = 100_000;
        var html = new StringBuilder();
        for (var i = 0; i < depth; i++) html.Append('<').Append(tag).Append('>');
        html.Append("deep");
        return HtmlToMarkdown.Convert(html.ToString());
    }

    [Fact]
    public void ListIndentationStopsGrowingAfterTenLevels()
    {
        var lines = HtmlToMarkdown.Convert(string.Concat(Enumerable.Repeat("<ul><li>x", 20))).Split('\n');
        Assert.Equal(20, lines.Length);
        Assert.Equal("  - x", lines[1]);
        Assert.Equal(new string(' ', 16) + "- x", lines[8]);
        Assert.All(lines[9..], line => Assert.Equal(new string(' ', 18) + "- x", line));
    }

    [Fact]
    public void ClosedDeepNestingKeepsTheFollowingContent()
    {
        const int depth = 100_000;
        var html = string.Concat(Enumerable.Repeat("<div>", depth)) + "deep" + string.Concat(Enumerable.Repeat("</div>", depth)) + "<p>after</p>";
        Assert.Equal("deep\n\nafter", HtmlToMarkdown.Convert(html));
    }

    [Fact]
    public void EndTagsOfElementsPastTheDepthCapDoNotCloseAncestors()
    {
        var depth = HtmlParser.MaxDepth + 44;
        var html = "<div id=app>" + string.Concat(Enumerable.Repeat("<div>", depth)) + string.Concat(Enumerable.Repeat("</div>", depth))
            + "<p>after</p></div>";
        var app = Assert.Single(HtmlParser.Parse(html).Children);
        Assert.Equal("app", app.Attribute("id"));
        Assert.Equal("p", app.Children[^1].Name);
    }

    [Theory]
    [InlineData("<div>", "</x>")]
    [InlineData("<p><table>", "<span><div>")]
    [InlineData("<b>", "</b><b>x")]
    [InlineData("<ul>", "<li><ol>")]
    [InlineData("", "<a href=\"x\">y</a> &amp; ")]
    [InlineData("", "<p>a < b <!x> &#99;")]
    [InlineData("<pre>", "<code>")]
    [InlineData("", "<")]
    [InlineData("", "<b>")]
    [InlineData("", "<i>x")]
    [InlineData("", "<u>x")]
    [InlineData("", "&#1;")]
    [InlineData("<table><tr>", "<td>")]
    [InlineData("<pre>", " \t")]
    [InlineData("<b>", "word ", 256)]
    [InlineData("<a href=\"u\">", "word ", 256)]
    [InlineData("<pre>", "line\n", 256)]
    [InlineData("<b><i>", "w ", 128)]
    [InlineData("<ul><li>", "<li>", 16)]
    [InlineData("<ol><li>", "<li>x", 1_000)]
    public void LargeInputsStayLinear(string prefix, string unit, int prefixRepeat = 1) =>
        AssertLinear(prefix, unit, prefixRepeat, 1_000_000, TimeSpan.FromSeconds(5));

    // At the 8 MB Scrape body cap. Run with: dotnet test tests/DesktopMcp.Core.Tests -p:RunSettingsFilePath= --filter Category=Stress
    [Theory]
    [Trait("Category", "Stress")]
    [InlineData("", "<i>x")]
    [InlineData("<pre>", "line\n", 256)]
    [InlineData("<ul><li>", "<li>", 16)]
    public void EightMegabyteInputsStayLinear(string prefix, string unit, int prefixRepeat = 1) =>
        AssertLinear(prefix, unit, prefixRepeat, 8_000_000, TimeSpan.FromSeconds(20));

    // A quadratic step would take minutes at these sizes; allocation stays within 50 bytes per input character.
    private static void AssertLinear(string prefix, string unit, int prefixRepeat, int size, TimeSpan timeLimit)
    {
        var builder = new StringBuilder(string.Concat(Enumerable.Repeat(prefix, prefixRepeat)));
        while (builder.Length < size) builder.Append(unit);
        var html = builder.ToString();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        HtmlToMarkdown.Convert(html);
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, timeLimit);
        Assert.InRange(allocated, 0, 50L * html.Length);
    }

    [Fact]
    public void NeverThrowsOnMalformedHtml()
    {
        string[] pieces =
        [
            "<", ">", "</", "/>", "<!--", "-->", "<!", "<?", "&", "&#", "&#x", ";", "&amp", "\"", "'", "=", " ", "\n", "a",
            "<p", "<a href=", "<li>", "</li>", "<ul>", "<ol>", "</ul>", "<table>", "<tr>", "<td>", "</table>", "<pre>", "</pre>",
            "<code>", "<script>", "</script", "<title>", "<style>", "<br/>", "</br>", "<h1>", "<b>", "</i>", "<img src=",
            "&#xD800;", "&#x110000;", "&#99999999999;", "\uD800", "\0",
        ];
        var random = new Random(15);
        for (var round = 0; round < 5_000; round++)
        {
            var html = new StringBuilder();
            var count = random.Next(1, 40);
            for (var i = 0; i < count; i++) html.Append(pieces[random.Next(pieces.Length)]);
            HtmlToMarkdown.Convert(html.ToString());
        }
    }
}
