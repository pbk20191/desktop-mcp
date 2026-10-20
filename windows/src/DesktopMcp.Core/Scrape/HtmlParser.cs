using System.Collections.Frozen;
using System.Net;
using System.Text;

namespace DesktopMcp.Core.Scrape;

/// <summary>An element or text node of a parsed HTML document.</summary>
internal sealed class HtmlNode(string name)
{
    public const string TextName = "#text";

    // Nodes are kept small, since hostile input can make one per few input characters: a text node's string or an
    // element's attributes share one field, and the children field holds nothing, the only child, or a list.
    private object? _data;
    private object? _children;

    public string Name { get; } = name;

    public string Text
    {
        get => _data as string ?? "";
        init => _data = value;
    }

    public IReadOnlyDictionary<string, string> Attributes
    {
        get => _data as IReadOnlyDictionary<string, string> ?? FrozenDictionary<string, string>.Empty;
        init => _data = value;
    }

    public int ChildCount => _children switch
    {
        null => 0,
        HtmlNode => 1,
        _ => ((List<HtmlNode>)_children).Count,
    };

    public HtmlNode ChildAt(int index) => _children as HtmlNode ?? ((List<HtmlNode>)_children!)[index];

    /// <summary>A copy of the children; walks use <see cref="ChildCount"/> and <see cref="ChildAt"/>.</summary>
    public IReadOnlyList<HtmlNode> Children => [.. Enumerable.Range(0, ChildCount).Select(ChildAt)];

    public void Add(HtmlNode child)
    {
        switch (_children)
        {
            case null:
                _children = child;
                break;
            case HtmlNode only:
                _children = new List<HtmlNode> { only, child };
                break;
            default:
                ((List<HtmlNode>)_children).Add(child);
                break;
        }
    }

    public string Attribute(string attributeName) => Attributes.GetValueOrDefault(attributeName, "");

    // Recursion is safe: HtmlParser caps the tree depth at HtmlParser.MaxDepth.
    public string InnerText() => Name == TextName ? Text : string.Concat(Enumerable.Range(0, ChildCount).Select(i => ChildAt(i).InnerText()));

    /// <summary>The first descendant named <paramref name="tag"/> in document order, not looking inside foreign
    /// content (svg, math), whose elements are not HTML elements of that name.</summary>
    public HtmlNode? Find(string tag)
    {
        for (var i = 0; i < ChildCount; i++)
        {
            var child = ChildAt(i);
            if (child.Name == tag) return child;
            if (child.Name is "svg" or "math") continue;
            if (child.Find(tag) is { } found) return found;
        }
        return null;
    }
}

/// <summary>
/// A tolerant HTML parser standing in for the libxml2 tidy parser the macOS port uses: just enough tree for
/// <see cref="HtmlToMarkdown"/>. Tag and attribute names are lower-cased; entities are decoded in text and attributes.
/// It never throws, and its time and memory are linear in the input: elements nest at most <see cref="MaxDepth"/>
/// deep (deeper start tags still add their element, but its content goes to the deepest open element, and their end
/// tags are absorbed), which bounds both the recursion of the tree walks and every scan of the open-element stack.
/// </summary>
internal static class HtmlParser
{
    public const int MaxDepth = 256;

    private static readonly HashSet<string> VoidElements =
        ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"];
    private static readonly HashSet<string> RawTextElements =
        ["script", "style", "xmp", "iframe", "noembed", "noframes", "noscript", "textarea", "title"];
    private static readonly HashSet<string> EscapableRawText = ["textarea", "title"];
    private static readonly HashSet<string> ClosesParagraph =
    [
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "dd", "dt", "fieldset",
        "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "li", "main",
        "menu", "nav", "ol", "p", "pre", "section", "summary", "table", "ul",
    ];
    private static readonly HashSet<string> Headings = ["h1", "h2", "h3", "h4", "h5", "h6"];
    private static readonly HashSet<string> ScopeBoundaries =
        ["table", "td", "th", "caption", "button", "html", "template", "object", "marquee", "applet"];
    private static readonly HashSet<string> ListItemBoundaries = [.. ScopeBoundaries, "ul", "ol"];
    private static readonly HashSet<string> DefinitionBoundaries = [.. ScopeBoundaries, "dl"];
    private static readonly HashSet<string> RowBoundaries = ["table", "thead", "tbody", "tfoot", "html"];
    private static readonly HashSet<string> CellBoundaries = ["tr", "table", "html"];
    private static readonly HashSet<string> SectionBoundaries = ["table", "html"];
    private static readonly HashSet<string> OptionBoundaries = ["select", "datalist", "html"];

    // Names shared by every element of that name instead of one string per tag.
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> KnownNames = new HashSet<string>(
    [
        .. VoidElements, .. RawTextElements, .. ClosesParagraph, .. ScopeBoundaries, .. RowBoundaries, .. OptionBoundaries,
        "a", "abbr", "b", "body", "cite", "code", "em", "font", "head", "html", "i", "label", "math", "option", "q", "s",
        "small", "span", "strong", "sub", "sup", "svg", "tbody", "td", "tfoot", "th", "thead", "time", "tr", "u",
        "alt", "class", "href", "id", "name", "rel", "src", "style", "title", "type",
    ]).GetAlternateLookup<ReadOnlySpan<char>>();

    public static HtmlNode Parse(string html)
    {
        var document = new HtmlNode("#document");
        var builder = new Builder(document);
        var position = 0;
        while (position < html.Length)
        {
            var lt = html.IndexOf('<', position);
            if (lt < 0)
            {
                Decode(html.AsSpan(position), builder.PendingText);
                break;
            }
            Decode(html.AsSpan(position, lt - position), builder.PendingText);
            position = lt;
            var next = position + 1 < html.Length ? html[position + 1] : '\0';
            if (string.CompareOrdinal(html, position, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", position + 4, StringComparison.Ordinal);
                position = end < 0 ? html.Length : end + 3;
            }
            else if (next is '!' or '?')
            {
                position = SkipPast(html, position, '>');
            }
            else if (next == '/' && position + 2 < html.Length && char.IsAsciiLetter(html[position + 2]))
            {
                var tag = ReadTag(html, position + 2);
                builder.End(tag.Name);
                position = tag.Next;
            }
            else if (char.IsAsciiLetter(next))
            {
                var tag = ReadTag(html, position + 1);
                position = tag.Next;
                if (RawTextElements.Contains(tag.Name) && !tag.SelfClosing)
                {
                    var close = IndexOfCloseTag(html, position, tag.Name);
                    var content = html.AsSpan(position, close - position);
                    builder.RawText(tag.Name, tag.Attributes, EscapableRawText.Contains(tag.Name) ? Decode(content) : content.ToString());
                    position = close >= html.Length ? html.Length : SkipPast(html, close, '>');
                }
                else
                {
                    builder.Start(tag.Name, tag.Attributes, tag.SelfClosing);
                }
            }
            else
            {
                builder.PendingText.Append('<');
                position++;
            }
        }
        builder.FlushText();
        return document;
    }

    /// <summary>
    /// Decodes character references. Named ones need their semicolon and go through <see cref="WebUtility.HtmlDecode"/>;
    /// numeric ones may omit it, and like a browser, zero, surrogates and values past U+10FFFF become U+FFFD.
    /// </summary>
    internal static string Decode(ReadOnlySpan<char> text)
    {
        if (!text.Contains('&')) return text.ToString();
        var decoded = new StringBuilder(text.Length);
        Decode(text, decoded);
        return decoded.ToString();
    }

    private static void Decode(ReadOnlySpan<char> text, StringBuilder decoded)
    {
        while (true)
        {
            var amp = text.IndexOf('&');
            if (amp < 0)
            {
                decoded.Append(text);
                return;
            }
            decoded.Append(text[..amp]);
            text = text[amp..];
            var length = text.Length > 1 && text[1] == '#' ? DecodeNumeric(text, decoded) : DecodeNamed(text, decoded);
            if (length < 0)
            {
                decoded.Append('&');
                length = 1;
            }
            text = text[length..];
        }
    }

    // Appends the reference at the start of text ("&#...") and returns its length, or -1 when it has no digits.
    private static int DecodeNumeric(ReadOnlySpan<char> text, StringBuilder decoded)
    {
        var i = 2;
        var hex = i < text.Length && text[i] is 'x' or 'X';
        if (hex) i++;
        var digitsStart = i;
        var value = 0;
        for (; i < text.Length; i++)
        {
            int digit;
            if (char.IsAsciiDigit(text[i])) digit = text[i] - '0';
            else if (hex && char.IsAsciiHexDigitLower(text[i])) digit = text[i] - 'a' + 10;
            else if (hex && char.IsAsciiHexDigitUpper(text[i])) digit = text[i] - 'A' + 10;
            else break;
            value = Math.Min(value * (hex ? 16 : 10) + digit, 0x110000);
        }
        if (i == digitsStart) return -1;
        if (i < text.Length && text[i] == ';') i++;
        if (value is 0 or (>= 0xD800 and <= 0xDFFF) or > 0x10FFFF) decoded.Append('\uFFFD');
        else if (value <= 0xFFFF) decoded.Append((char)value);
        else decoded.Append(char.ConvertFromUtf32(value));
        return i;
    }

    // Appends the named reference at the start of text ("&name;") and returns its length, or -1 when it is not one.
    private static int DecodeNamed(ReadOnlySpan<char> text, StringBuilder decoded)
    {
        const int longestName = 32;
        var i = 1;
        while (i < text.Length && i <= longestName && char.IsAsciiLetterOrDigit(text[i])) i++;
        if (i == 1 || i >= text.Length || text[i] != ';') return -1;
        var reference = text[..(i + 1)].ToString();
        var value = WebUtility.HtmlDecode(reference);
        if (value == reference) return -1;
        decoded.Append(value);
        return i + 1;
    }

    private readonly record struct Tag(string Name, Dictionary<string, string>? Attributes, bool SelfClosing, int Next);

    private static string LowerName(ReadOnlySpan<char> name)
    {
        const int longestKnown = 16;
        if (name.Length > longestKnown) return name.ToString().ToLowerInvariant();
        Span<char> lower = stackalloc char[longestKnown];
        var length = name.ToLowerInvariant(lower);
        return KnownNames.TryGetValue(lower[..length], out var known) ? known : lower[..length].ToString();
    }

    private static Tag ReadTag(string html, int start)
    {
        var i = start;
        while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('/' or '>')) i++;
        var name = LowerName(html.AsSpan(start, i - start));
        Dictionary<string, string>? attributes = null;
        var selfClosing = false;
        while (i < html.Length)
        {
            while (i < html.Length && char.IsWhiteSpace(html[i])) i++;
            if (i >= html.Length) break;
            if (html[i] == '>')
            {
                i++;
                break;
            }
            if (html[i] == '/')
            {
                selfClosing = i + 1 < html.Length && html[i + 1] == '>';
                i++;
                continue;
            }
            var nameStart = i;
            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('=' or '>' or '/')) i++;
            if (i == nameStart)
            {
                i++;
                continue;
            }
            var attributeName = LowerName(html.AsSpan(nameStart, i - nameStart));
            while (i < html.Length && char.IsWhiteSpace(html[i])) i++;
            var value = "";
            if (i < html.Length && html[i] == '=')
            {
                i++;
                while (i < html.Length && char.IsWhiteSpace(html[i])) i++;
                if (i < html.Length && html[i] is '"' or '\'')
                {
                    var quote = html[i];
                    var end = html.IndexOf(quote, i + 1);
                    if (end < 0) end = html.Length;
                    value = Decode(html.AsSpan(i + 1, end - i - 1));
                    i = Math.Min(end + 1, html.Length);
                }
                else
                {
                    var valueStart = i;
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>') i++;
                    value = Decode(html.AsSpan(valueStart, i - valueStart));
                }
            }
            attributes ??= new Dictionary<string, string>(StringComparer.Ordinal);
            attributes.TryAdd(attributeName, value);
        }
        return new Tag(name, attributes, selfClosing, i);
    }

    // The next "</name" followed by whitespace, "/", ">" or the end; html.Length when there is none.
    private static int IndexOfCloseTag(string html, int from, string name)
    {
        var i = from;
        while (true)
        {
            var at = html.IndexOf("</", i, StringComparison.Ordinal);
            if (at < 0) return html.Length;
            var end = at + 2 + name.Length;
            if (end <= html.Length
                && string.Compare(html, at + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (end == html.Length || html[end] is '>' or '/' || char.IsWhiteSpace(html[end])))
                return at;
            i = at + 2;
        }
    }

    private static int SkipPast(string html, int from, char c)
    {
        var at = html.IndexOf(c, from);
        return at < 0 ? html.Length : at + 1;
    }

    private static readonly string[] OneCharStrings = [.. Enumerable.Range(0, 128).Select(c => ((char)c).ToString())];

    private sealed class Builder(HtmlNode document)
    {
        // The document plus at most MaxDepth open elements, so every scan below is bounded; scans for an element
        // that is not open at all are skipped, which keeps the common case constant-time.
        private readonly List<HtmlNode> _stack = [document];
        private readonly Dictionary<string, int> _openCounts = new(StringComparer.Ordinal);
        // Start tags past MaxDepth that were not left open, by name: their end tags close nothing.
        private readonly Dictionary<string, int> _droppedCounts = new(StringComparer.Ordinal);

        private HtmlNode Current => _stack[^1];

        /// <summary>Text read since the last tag that changed the tree; adjacent text becomes one node.</summary>
        public StringBuilder PendingText { get; } = new();

        public void FlushText()
        {
            if (PendingText.Length == 0) return;
            var text = PendingText.Length == 1 && PendingText[0] < OneCharStrings.Length ? OneCharStrings[PendingText[0]] : PendingText.ToString();
            Current.Add(new HtmlNode(HtmlNode.TextName) { Text = text });
            PendingText.Clear();
        }

        public void Start(string tag, Dictionary<string, string>? attributes, bool selfClosing)
        {
            var element = Add(tag, attributes);
            if (selfClosing || VoidElements.Contains(tag)) return;
            if (_stack.Count > MaxDepth)
            {
                _droppedCounts[tag] = _droppedCounts.GetValueOrDefault(tag) + 1;
                return;
            }
            _stack.Add(element);
            _openCounts[tag] = _openCounts.GetValueOrDefault(tag) + 1;
        }

        // A raw-text element is complete when it is read, so it is never left open.
        public void RawText(string tag, Dictionary<string, string>? attributes, string text)
        {
            var element = Add(tag, attributes);
            if (text.Length > 0) element.Add(new HtmlNode(HtmlNode.TextName) { Text = text });
        }

        private HtmlNode Add(string tag, Dictionary<string, string>? attributes)
        {
            FlushText();
            if (ClosesParagraph.Contains(tag)) CloseInScope("p", ScopeBoundaries);
            if (tag == "li") CloseInScope("li", ListItemBoundaries);
            if (tag is "dt" or "dd")
            {
                CloseInScope("dt", DefinitionBoundaries);
                CloseInScope("dd", DefinitionBoundaries);
            }
            if (tag == "tr") CloseInScope("tr", RowBoundaries);
            if (tag is "td" or "th")
            {
                CloseInScope("td", CellBoundaries);
                CloseInScope("th", CellBoundaries);
            }
            if (tag is "thead" or "tbody" or "tfoot")
            {
                CloseInScope("thead", SectionBoundaries);
                CloseInScope("tbody", SectionBoundaries);
                CloseInScope("tfoot", SectionBoundaries);
            }
            if (tag == "option") CloseInScope("option", OptionBoundaries);
            if (Headings.Contains(tag) && Headings.Contains(Current.Name)) PopFrom(_stack.Count - 1);

            var element = attributes is null ? new HtmlNode(tag) : new HtmlNode(tag) { Attributes = attributes };
            Current.Add(element);
            return element;
        }

        public void End(string tag)
        {
            if (tag == "br")
            {
                Start("br", null, selfClosing: true);
                return;
            }
            if (_droppedCounts.GetValueOrDefault(tag) is > 0 and var dropped)
            {
                _droppedCounts[tag] = dropped - 1;
                return;
            }
            if (!IsOpen(tag)) return;
            FlushText();
            for (var i = _stack.Count - 1; i > 0; i--)
            {
                if (_stack[i].Name != tag) continue;
                PopFrom(i);
                return;
            }
        }

        private void CloseInScope(string tag, HashSet<string> boundaries)
        {
            if (!IsOpen(tag)) return;
            for (var i = _stack.Count - 1; i > 0; i--)
            {
                if (_stack[i].Name == tag)
                {
                    PopFrom(i);
                    return;
                }
                if (boundaries.Contains(_stack[i].Name)) return;
            }
        }

        private bool IsOpen(string tag) => _openCounts.GetValueOrDefault(tag) > 0;

        // Closes the open element at index and everything opened after it.
        private void PopFrom(int index)
        {
            for (var i = index; i < _stack.Count; i++) _openCounts[_stack[i].Name]--;
            _stack.RemoveRange(index, _stack.Count - index);
        }
    }
}
