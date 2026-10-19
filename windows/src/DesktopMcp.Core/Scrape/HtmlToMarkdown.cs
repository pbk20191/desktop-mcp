using System.Text;

namespace DesktopMcp.Core.Scrape;

/// <summary>
/// HTML to Markdown, ported from the macOS port's HTMLToMarkdown.swift: the same element rules, frames for inline
/// wrappers, list nesting, whitespace collapsing and final tidy. Unlike Python's markdownify, the contents of script,
/// style and other non-content elements are dropped. Bounds keep untrusted input linear in time and memory: the tree
/// walk recurses at most <see cref="HtmlParser.MaxDepth"/> deep; inline wrappers nested more than
/// <see cref="MaxInlineFrames"/> deep render their content unwrapped, so each character is copied through at most that
/// many frames; and list indentation stops growing after <see cref="MaxListIndent"/> levels, so deeply nested lists
/// cannot multiply the output size.
/// </summary>
public static class HtmlToMarkdown
{
    private static readonly HashSet<string> Skipped =
        ["script", "style", "noscript", "template", "svg", "head", "iframe", "object", "canvas", "title"];
    private static readonly HashSet<string> Blocks =
    [
        "p", "div", "section", "article", "header", "footer", "main", "nav", "aside", "form", "table", "thead", "tbody", "tfoot",
        "figure", "figcaption", "details", "summary", "dl", "dt", "dd", "fieldset", "address", "center",
    ];
    private const int MaxListIndent = 10;
    private const int MaxInlineFrames = 16;
    private static readonly char[] Newlines = ['\n', '\r', '\u000B', '\u000C', '\u0085', '\u2028', '\u2029'];

    public static string Convert(string html)
    {
        var document = HtmlParser.Parse(html);
        var renderer = new Renderer();
        renderer.VisitChildren(document);
        return renderer.Finish(document.Find("title")?.InnerText() ?? "");
    }

    private static bool IsNewline(char c) => Array.IndexOf(Newlines, c) >= 0;

    // The [Start, End) range of builder left after trimming characters matching trim from both ends, found in one
    // forward pass over the chunks, because indexing a large StringBuilder far from its end is not constant-time.
    private static (int Start, int End) TrimRange(StringBuilder builder, Func<char, bool> trim)
    {
        var start = -1;
        var end = 0;
        var offset = 0;
        foreach (var chunk in builder.GetChunks())
        {
            var span = chunk.Span;
            for (var i = 0; i < span.Length; i++)
            {
                if (trim(span[i])) continue;
                if (start < 0) start = offset + i;
                end = offset + i + 1;
            }
            offset += span.Length;
        }
        return start < 0 ? (0, 0) : (start, end);
    }

    /// <summary>
    /// The final tidy, streamed over the rendered text's chunks: trailing whitespace goes (except a Markdown line
    /// break's two spaces), runs of blank lines become one, and the whole is trimmed. It runs twice, first to measure
    /// and then to write the result in place, so the result is the only copy.
    /// </summary>
    private ref struct Tidy(Span<char> destination)
    {
        private readonly Span<char> _destination = destination;
        private int _pendingNewlines;
        private bool _pendingBreak;
        private bool _started;
        private bool _lineHasContent;
        private char _last;
        private char _beforeLast;

        public int Length { get; private set; }
        public string Head { get; private set; } = "";

        private void Put(char c)
        {
            if (Length < _destination.Length) _destination[Length] = c;
            if (Length < 2) Head += c;
            Length++;
        }

        public void Run(StringBuilder text, StringBuilder spaces)
        {
            foreach (var chunk in text.GetChunks())
            {
                foreach (var c in chunk.Span) Next(c, spaces);
            }
        }

        private void Next(char c, StringBuilder spaces)
        {
            if (c == '\n')
            {
                if (_lineHasContent) _pendingBreak = _last == ' ' && _beforeLast == ' ';
                if (_started) _pendingNewlines = Math.Min(_pendingNewlines + 1, 2);
                spaces.Clear();
                _lineHasContent = false;
                _last = _beforeLast = '\0';
                return;
            }
            _beforeLast = _last;
            _last = c;
            if (char.IsWhiteSpace(c))
            {
                // Whitespace before the first content of the text is trimmed; within a line it waits for content.
                if (_started) spaces.Append(c);
                return;
            }
            if (_pendingBreak && _pendingNewlines > 0)
            {
                Put(' ');
                Put(' ');
            }
            for (; _pendingNewlines > 0; _pendingNewlines--) Put('\n');
            _pendingBreak = false;
            foreach (var chunk in spaces.GetChunks())
            {
                foreach (var space in chunk.Span) Put(space);
            }
            spaces.Clear();
            Put(c);
            _started = true;
            _lineHasContent = true;
        }
    }

    private sealed class Renderer
    {
        // Frames are reused, since inline wrappers open and close once per element.
        private sealed class Frame
        {
            private const int ReusedCapacity = 4096;

            public string Tag { get; private set; } = "";
            public string Href { get; private set; } = "";
            public StringBuilder Text { get; private set; } = new();

            public Frame Reset(string tag, string href)
            {
                Tag = tag;
                Href = href;
                // Clearing a large multi-chunk builder allocates its whole capacity again, so a large one is replaced.
                if (Text.Capacity > ReusedCapacity) Text = new StringBuilder();
                else Text.Clear();
                return this;
            }
        }

        private sealed class ListState(bool ordered)
        {
            public bool Ordered { get; } = ordered;
            public int Counter { get; set; }
        }

        private readonly List<Frame> _frames = [new Frame().Reset("#root", "")];
        private readonly Stack<Frame> _spareFrames = new();
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

        // Past MaxInlineFrames the wrapper is not opened, and its content renders straight into the current frame.
        private bool OpenFrame(string tag, string href = "")
        {
            if (_frames.Count > MaxInlineFrames) return false;
            _frames.Add((_spareFrames.TryPop(out var spare) ? spare : new Frame()).Reset(tag, href));
            return true;
        }

        // Wraps the frame's trimmed text and appends it to the parent frame without copying it into a string first.
        private void CloseFrame(string tag)
        {
            if (_frames.Count <= 1 || _frames[^1].Tag != tag) return;
            var frame = _frames[^1];
            _frames.RemoveAt(_frames.Count - 1);
            Wrap(tag, frame.Text, frame.Href);
            _spareFrames.Push(frame);
        }

        private void Wrap(string tag, StringBuilder inner, string href)
        {
            if (tag == "pre")
            {
                EnsureBlankLine();
                var (codeStart, codeEnd) = TrimRange(inner, IsNewline);
                Current.Append("```\n").Append(inner, codeStart, codeEnd - codeStart).Append("\n```");
                EnsureBlankLine();
                return;
            }
            var (start, end) = TrimRange(inner, char.IsWhiteSpace);
            if (start == end) return;
            switch (tag)
            {
                case "a":
                    if (href.Length == 0 || href.StartsWith("javascript:", StringComparison.Ordinal) || href.StartsWith('#'))
                    {
                        Current.Append(inner, start, end - start);
                    }
                    else
                    {
                        var textStart = Current.Append('[').Length;
                        Current.Append(inner, start, end - start).Replace('\n', ' ', textStart, end - start);
                        Current.Append("](").Append(href).Append(')');
                    }
                    break;
                case "strong" or "b":
                    Current.Append("**").Append(inner, start, end - start).Append("**");
                    break;
                case "em" or "i":
                    Current.Append('*').Append(inner, start, end - start).Append('*');
                    break;
                case "code":
                    Current.Append('`').Append(inner, start, end - start).Append('`');
                    break;
            }
        }

        public void VisitChildren(HtmlNode node)
        {
            for (var i = 0; i < node.ChildCount; i++) Visit(node.ChildAt(i));
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
                    Current.Append('#', tag[1] - '0').Append(' ');
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
                    Current.Append(' ', 2 * (Math.Clamp(_lists.Count, 1, MaxListIndent) - 1));
                    if (_lists.Count > 0 && _lists[^1].Ordered)
                    {
                        _lists[^1].Counter++;
                        Current.Append(_lists[^1].Counter).Append(". ");
                    }
                    else
                    {
                        Emit("- ");
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
                    var link = OpenFrame("a", node.Attribute("href"));
                    VisitChildren(node);
                    if (link) CloseFrame("a");
                    break;
                case "strong" or "b" or "em" or "i":
                    var emphasis = OpenFrame(tag);
                    VisitChildren(node);
                    if (emphasis) CloseFrame(tag);
                    break;
                case "code":
                    if (_preDepth > 0)
                    {
                        VisitChildren(node);
                    }
                    else
                    {
                        var code = OpenFrame("code");
                        VisitChildren(node);
                        if (code) CloseFrame("code");
                    }
                    break;
                case "pre":
                    _preDepth++;
                    var pre = OpenFrame("pre");
                    VisitChildren(node);
                    _preDepth--;
                    if (pre) CloseFrame("pre");
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

        private static bool IsCollapsible(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f' or '\u00A0';

        // Runs of [ \t\r\n\f] and no-break spaces become one space, which is dropped at the start of a line.
        private void Text(string raw)
        {
            if (_preDepth > 0)
            {
                Emit(raw);
                return;
            }
            var atLineStart = Current.Length == 0 || Current[^1] is ' ' or '\n';
            var space = false;
            var i = 0;
            while (i < raw.Length)
            {
                if (IsCollapsible(raw[i]))
                {
                    space = true;
                    i++;
                    continue;
                }
                var wordStart = i;
                while (i < raw.Length && !IsCollapsible(raw[i])) i++;
                if (space && !atLineStart) Current.Append(' ');
                Current.Append(raw, wordStart, i - wordStart);
                space = false;
                atLineStart = false;
            }
            if (space && !atLineStart) Current.Append(' ');
        }

        public string Finish(string title)
        {
            while (_frames.Count > 1)
            {
                var inner = _frames[^1];
                _frames.RemoveAt(_frames.Count - 1);
                _frames[^1].Text.Append(inner.Text);
            }
            var text = _frames[0].Text;
            var spaces = new StringBuilder();
            var measure = new Tidy(Span<char>.Empty);
            measure.Run(text, spaces);
            var cleanTitle = title.Trim();
            var prefix = cleanTitle.Length > 0 && measure.Head != "# " ? cleanTitle + "\n\n" : "";
            return string.Create(prefix.Length + measure.Length, (prefix, text, spaces), static (destination, state) =>
            {
                state.prefix.CopyTo(destination);
                new Tidy(destination[state.prefix.Length..]).Run(state.text, state.spaces.Clear());
            });
        }
    }
}
