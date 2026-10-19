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
    [InlineData("<p>x &#xD800; &#xDFFF;</p>", "x � �")]
    [InlineData("<p>&#0;|&#x110000;|&#99999999999999999999;|&#x;|&#;</p>", "�|�|�|&#x;|&#;")]
    [InlineData("<img src=\"&#xD83D;\" alt=\"&#xDE00;\">", "![�](�)")]
    [InlineData("<p>&#x1F600; &#128512;</p>", "\U0001F600 \U0001F600")]
    public void HandlesEdgeCases(string html, string markdown) => Assert.Equal(markdown, HtmlToMarkdown.Convert(html));

    [Theory]
    [InlineData("div")]
    [InlineData("b")]
    [InlineData("ul><li")]
    [InlineData("blockquote")]
    public void SurvivesDeeplyNestedElements(string tag)
    {
        const int depth = 100_000;
        var html = new StringBuilder();
        for (var i = 0; i < depth; i++) html.Append('<').Append(tag).Append('>');
        html.Append("deep");
        var markdown = HtmlToMarkdown.Convert(html.ToString());
        Assert.Contains("deep", markdown);
    }

    [Fact]
    public void ListIndentationStopsGrowingAfterSixteenLevels()
    {
        var lines = HtmlToMarkdown.Convert(string.Concat(Enumerable.Repeat("<ul><li>x", 20))).Split('\n');
        Assert.Equal(20, lines.Length);
        Assert.Equal("  - x", lines[1]);
        Assert.Equal(new string(' ', 28) + "- x", lines[14]);
        Assert.All(lines[15..], line => Assert.Equal(new string(' ', 30) + "- x", line));
    }

    [Fact]
    public void ClosedDeepNestingKeepsTheFollowingContent()
    {
        const int depth = 100_000;
        var html = string.Concat(Enumerable.Repeat("<div>", depth)) + "deep" + string.Concat(Enumerable.Repeat("</div>", depth)) + "<p>after</p>";
        Assert.Equal("deep\n\nafter", HtmlToMarkdown.Convert(html));
    }

    [Theory]
    [InlineData("<div>", "</x>")]
    [InlineData("<p><table>", "<span><div>")]
    [InlineData("<b>", "</b><b>x")]
    [InlineData("<ul>", "<li><ol>")]
    [InlineData("", "<a href=\"x\">y</a> &amp; ")]
    [InlineData("", "<p>a < b <!x> &#99;")]
    [InlineData("<pre>", "<code>")]
    [InlineData("<b>", "word ", 1_000)]
    [InlineData("<a href=\"u\">", "word ", 1_000)]
    [InlineData("<pre>", "line\n", 1_000)]
    [InlineData("<ul><li>", "<li>x", 1_000)]
    public void LargeInputsStayLinear(string prefix, string unit, int prefixRepeat = 1)
    {
        prefix = string.Concat(Enumerable.Repeat(prefix, prefixRepeat));
        // About 8 MB of input, the Scrape body cap; a quadratic step would take minutes here.
        var html = new StringBuilder(prefix);
        while (html.Length < 8_000_000) html.Append(unit);
        var stopwatch = Stopwatch.StartNew();
        HtmlToMarkdown.Convert(html.ToString());
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(20));
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
