using System.Text;
using System.Text.RegularExpressions;

namespace DesktopMcp.Core.Scrape;

/// <summary>
/// HTML to Markdown, ported from the macOS port's HTMLToMarkdown.swift: the same element rules, frames for inline
/// wrappers, list nesting, whitespace collapsing and final tidy. Unlike Python's markdownify, the contents of script,
/// style and other non-content elements are dropped. Two bounds keep untrusted input safe: the tree walk recurses at
/// most <see cref="HtmlParser.MaxDepth"/> deep, and list indentation stops growing after <see cref="MaxListIndent"/>
/// levels, so deeply nested lists cannot multiply the output size.
/// </summary>
public static partial class HtmlToMarkdown
{
    private static readonly HashSet<string> Skipped =
        ["script", "style", "noscript", "template", "svg", "head", "iframe", "object", "canvas", "title"];
    private static readonly HashSet<string> Blocks =
    [
        "p", "div", "section", "article", "header", "footer", "main", "nav", "aside", "form", "table", "thead", "tbody", "tfoot",
        "figure", "figcaption", "details", "summary", "dl", "dt", "dd", "fieldset", "address", "center",
    ];
    private const int MaxListIndent = 16;
    private static readonly char[] Newlines = ['\n', '\r', '\u000B', '\u000C', '\u0085', '\u2028', '\u2029'];

    public static string Convert(string html)
    {
        var document = HtmlParser.Parse(html);
        var renderer = new Renderer();
        renderer.VisitChildren(document);
        return renderer.Finish(document.Find("title")?.InnerText() ?? "");
    }

    [GeneratedRegex("[ \t\r\n\f]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s+$")]
    private static partial Regex TrailingWhitespace();

    [GeneratedRegex("\n{3,}")]
    private static partial Regex BlankLines();

    private sealed class Renderer
    {
        private sealed class Frame(string tag, string href)
        {
            public string Tag { get; } = tag;
            public string Href { get; } = href;
            public StringBuilder Text { get; } = new();
        }

        private sealed class ListState(bool ordered)
        {
            public bool Ordered { get; } = ordered;
            public int Counter { get; set; }
        }

        private readonly List<Frame> _frames = [new Frame("#root", "")];
        private readonly List<ListState> _lists = [];
        private int _preDepth;

        private StringBuilder Current => _frames[^1].Text;

        private void Emit(string text) => Current.Append(text);

        private static bool EndsWith(StringBuilder builder, string suffix)
        {
            if (builder.Length < suffix.Length) return false;
            for (var i = 0; i < suffix.Length; i++)
            {
                if (builder[builder.Length - suffix.Length + i] != suffix[i]) return false;
            }
            return true;
        }

        private void EnsureBlankLine()
        {
            if (Current.Length == 0 || EndsWith(Current, "\n\n")) return;
            Emit(EndsWith(Current, "\n") ? "\n" : "\n\n");
        }

        private void EnsureNewline()
        {
            if (Current.Length > 0 && !EndsWith(Current, "\n")) Emit("\n");
        }

        private void TrimTrailingSpaces()
        {
            while (Current.Length > 0 && Current[^1] == ' ') Current.Length--;
        }

        private void OpenFrame(string tag, string href = "") => _frames.Add(new Frame(tag, href));

        private void CloseFrame(string tag)
        {
            if (_frames.Count <= 1 || _frames[^1].Tag != tag) return;
            var frame = _frames[^1];
            _frames.RemoveAt(_frames.Count - 1);
            var inner = frame.Text.ToString();
            var trimmed = inner.Trim();
            switch (tag)
            {
                case "a":
                    if (trimmed.Length == 0) return;
                    var href = frame.Href;
                    if (href.Length == 0 || href.StartsWith("javascript:", StringComparison.Ordinal) || href.StartsWith('#')) Emit(trimmed);
                    else Emit($"[{trimmed.Replace("\n", " ")}]({href})");
                    break;
                case "strong" or "b":
                    if (trimmed.Length > 0) Emit($"**{trimmed}**");
                    break;
                case "em" or "i":
                    if (trimmed.Length > 0) Emit($"*{trimmed}*");
                    break;
                case "code":
                    if (trimmed.Length > 0) Emit($"`{trimmed}`");
                    break;
                case "pre":
                    EnsureBlankLine();
                    Emit("```\n" + inner.Trim(Newlines) + "\n```");
                    EnsureBlankLine();
                    break;
                default:
                    Emit(inner);
                    break;
            }
        }

        public void VisitChildren(HtmlNode node)
        {
            foreach (var child in node.Children) Visit(child);
        }

        private void Visit(HtmlNode node)
        {
            if (node.Name == HtmlNode.TextName)
            {
                Text(node.Text);
                return;
            }
            var tag = node.Name;
            if (Skipped.Contains(tag)) return;
            switch (tag)
            {
                case "br":
                    Emit("  \n");
                    break;
                case "hr":
                    EnsureBlankLine();
                    Emit("---");
                    EnsureBlankLine();
                    break;
                case "img":
                    var src = node.Attribute("src");
                    if (src.Length > 0) Emit($"![{node.Attribute("alt")}]({src})");
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    EnsureBlankLine();
                    Emit(new string('#', tag[1] - '0') + " ");
                    VisitChildren(node);
                    EnsureBlankLine();
                    break;
                case "ul" or "ol":
                    if (_lists.Count == 0) EnsureBlankLine();
                    else EnsureNewline();
                    _lists.Add(new ListState(tag == "ol"));
                    VisitChildren(node);
                    _lists.RemoveAt(_lists.Count - 1);
                    if (_lists.Count == 0) EnsureBlankLine();
                    else EnsureNewline();
                    break;
                case "li":
                    EnsureNewline();
                    var indent = new string(' ', 2 * (Math.Clamp(_lists.Count, 1, MaxListIndent) - 1));
                    if (_lists.Count > 0 && _lists[^1].Ordered)
                    {
                        _lists[^1].Counter++;
                        Emit($"{indent}{_lists[^1].Counter}. ");
                    }
                    else
                    {
                        Emit($"{indent}- ");
                    }
                    VisitChildren(node);
                    EnsureNewline();
                    break;
                case "blockquote":
                    EnsureBlankLine();
                    Emit("> ");
                    VisitChildren(node);
                    EnsureBlankLine();
                    break;
                case "a":
                    OpenFrame("a", node.Attribute("href"));
                    VisitChildren(node);
                    CloseFrame("a");
                    break;
                case "strong" or "b" or "em" or "i":
                    OpenFrame(tag);
                    VisitChildren(node);
                    CloseFrame(tag);
                    break;
                case "code":
                    if (_preDepth > 0)
                    {
                        VisitChildren(node);
                    }
                    else
                    {
                        OpenFrame("code");
                        VisitChildren(node);
                        CloseFrame("code");
                    }
                    break;
                case "pre":
                    _preDepth++;
                    OpenFrame("pre");
                    VisitChildren(node);
                    _preDepth--;
                    CloseFrame("pre");
                    break;
                case "tr":
                    EnsureNewline();
                    Emit("| ");
                    VisitChildren(node);
                    EnsureNewline();
                    break;
                case "td" or "th":
                    VisitChildren(node);
                    TrimTrailingSpaces();
                    Emit(" | ");
                    break;
                default:
                    var block = Blocks.Contains(tag);
                    if (block) EnsureBlankLine();
                    VisitChildren(node);
                    if (block) EnsureBlankLine();
                    break;
            }
        }

        private void Text(string raw)
        {
            if (_preDepth > 0)
            {
                Emit(raw);
                return;
            }
            var collapsed = Whitespace().Replace(raw.Replace('\u00A0', ' '), " ");
            var atLineStart = Current.Length == 0 || Current[^1] is ' ' or '\n';
            if (collapsed == " " && atLineStart) return;
            Emit(collapsed.StartsWith(' ') && atLineStart ? collapsed[1..] : collapsed);
        }

        public string Finish(string title)
        {
            while (_frames.Count > 1)
            {
                var inner = _frames[^1];
                _frames.RemoveAt(_frames.Count - 1);
                _frames[^1].Text.Append(inner.Text);
            }
            // Tidy: trailing spaces (except Markdown line breaks), runs of blank lines.
            var body = string.Join("\n", _frames[0].Text.ToString().Split('\n').Select(line =>
            {
                var stripped = TrailingWhitespace().Replace(line, "");
                return line.EndsWith("  ", StringComparison.Ordinal) && stripped.Length > 0 ? stripped + "  " : stripped;
            }));
            body = BlankLines().Replace(body, "\n\n").Trim();
            var cleanTitle = title.Trim();
            return cleanTitle.Length > 0 && !body.StartsWith("# ", StringComparison.Ordinal) ? cleanTitle + "\n\n" + body : body;
        }
    }
}
