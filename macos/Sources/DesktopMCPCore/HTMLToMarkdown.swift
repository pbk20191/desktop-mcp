import Foundation

/// HTML to Markdown, standing in for Python's markdownify.
///
/// Parsing is done by the system's HTML parser (libxml2, through Foundation's `XMLDocument` in
/// tidy-HTML mode), which repairs unclosed and misnested tags and decodes entities the way a
/// browser would. This type only walks the resulting tree.
///
/// Unlike the original (which passed `strip=["script", "style"]` and so kept their text), the
/// contents of script, style and similar non-content elements are dropped.
public enum HTMLToMarkdown {
    static let skipped: Set<String> = ["script", "style", "noscript", "template", "svg", "head", "iframe", "object", "canvas"]
    static let blocks: Set<String> = [
        "p", "div", "section", "article", "header", "footer", "main", "nav", "aside", "form",
        "table", "thead", "tbody", "tfoot", "figure", "figcaption", "details", "summary", "dl", "dt", "dd",
        "fieldset", "address", "center",
    ]

    public static func convert(_ html: String) -> String {
        guard let root = parse(html)?.rootElement() else { return "" }
        var renderer = Renderer()
        renderer.visit(root)
        let title = root.elements(forName: "head").first?.elements(forName: "title").first?.stringValue ?? ""
        return renderer.finish(title: title)
    }

    /// The system HTML parser. The text has already been decoded (per the HTTP headers), so the
    /// parser must not re-decode it from a charset the page declares: the string initialiser
    /// fixes the encoding to UTF-8, which makes it ignore `<meta charset>`; an XML declaration
    /// would still override that, so it is dropped.
    static func parse(_ html: String) -> XMLDocument? {
        let text = html.replacingOccurrences(of: #"^\s*<\?xml\b[^>]*\?>"#, with: "", options: [.regularExpression, .caseInsensitive])
        return try? XMLDocument(xmlString: text, options: [.documentTidyHTML, .nodeLoadExternalEntitiesNever])
    }

    /// An element whose rendered text is collected separately and then wrapped.
    private struct Frame {
        var tag: String
        var text = ""
        var href = ""
    }

    private struct ListState {
        var ordered: Bool
        var counter = 0
    }

    private struct Renderer {
        var frames: [Frame] = [Frame(tag: "#root")]
        var lists: [ListState] = []
        var preDepth = 0

        // MARK: Output helpers

        private var current: String { frames[frames.count - 1].text }

        private mutating func emit(_ s: String) { frames[frames.count - 1].text += s }

        private mutating func ensureBlankLine() {
            if current.isEmpty || current.hasSuffix("\n\n") { return }
            emit(current.hasSuffix("\n") ? "\n" : "\n\n")
        }

        private mutating func ensureNewline() {
            if !current.isEmpty && !current.hasSuffix("\n") { emit("\n") }
        }

        private mutating func trimTrailingSpaces() {
            while frames[frames.count - 1].text.hasSuffix(" ") { frames[frames.count - 1].text.removeLast() }
        }

        private mutating func openFrame(_ tag: String, href: String = "") {
            frames.append(Frame(tag: tag, text: "", href: href))
        }

        private mutating func closeFrame(_ tag: String) {
            guard frames.count > 1, frames[frames.count - 1].tag == tag else { return }
            let frame = frames.removeLast()
            let inner = frame.text
            let trimmed = inner.trimmingCharacters(in: .whitespacesAndNewlines)
            switch tag {
            case "a":
                if trimmed.isEmpty { return }
                let href = frame.href
                if href.isEmpty || href.hasPrefix("javascript:") || href.hasPrefix("#") {
                    emit(trimmed)
                } else {
                    emit("[\(trimmed.replacingOccurrences(of: "\n", with: " "))](\(href))")
                }
            case "strong", "b":
                if !trimmed.isEmpty { emit("**\(trimmed)**") }
            case "em", "i":
                if !trimmed.isEmpty { emit("*\(trimmed)*") }
            case "code":
                if !trimmed.isEmpty { emit("`\(trimmed)`") }
            case "pre":
                ensureBlankLine()
                emit("```\n" + inner.trimmingCharacters(in: .newlines) + "\n```")
                ensureBlankLine()
            default:
                emit(inner)
            }
        }

        // MARK: Tree walk

        mutating func visit(_ node: XMLNode) {
            switch node.kind {
            case .text:
                text(node.stringValue ?? "")
            case .element:
                if let element = node as? XMLElement { visit(element) }
            default:
                break  // comments, processing instructions, DTD
            }
        }

        private mutating func visitChildren(of element: XMLElement) {
            for child in element.children ?? [] { visit(child) }
        }

        private mutating func text(_ raw: String) {
            if preDepth > 0 {
                emit(raw)
                return
            }
            let collapsed = raw.replacingOccurrences(of: "\u{00A0}", with: " ")
                .replacingOccurrences(of: "[ \\t\\r\\n\\f]+", with: " ", options: .regularExpression)
            let atLineStart = current.isEmpty || current.hasSuffix(" ") || current.hasSuffix("\n")
            if collapsed == " " && atLineStart { return }
            if collapsed.hasPrefix(" ") && atLineStart {
                emit(String(collapsed.dropFirst()))
            } else {
                emit(collapsed)
            }
        }

        private mutating func visit(_ element: XMLElement) {
            let tag = element.name?.lowercased() ?? ""
            if HTMLToMarkdown.skipped.contains(tag) { return }
            func attribute(_ name: String) -> String { element.attribute(forName: name)?.stringValue ?? "" }

            switch tag {
            case "br":
                emit("  \n")
            case "hr":
                ensureBlankLine()
                emit("---")
                ensureBlankLine()
            case "img":
                let src = attribute("src")
                if !src.isEmpty { emit("![\(attribute("alt"))](\(src))") }
            case "h1", "h2", "h3", "h4", "h5", "h6":
                ensureBlankLine()
                emit(String(repeating: "#", count: Int(String(tag.last!))!) + " ")
                visitChildren(of: element)
                ensureBlankLine()
            case "ul", "ol":
                if lists.isEmpty { ensureBlankLine() } else { ensureNewline() }
                lists.append(ListState(ordered: tag == "ol"))
                visitChildren(of: element)
                lists.removeLast()
                if lists.isEmpty { ensureBlankLine() } else { ensureNewline() }
            case "li":
                ensureNewline()
                let depth = max(lists.count, 1)
                let indent = String(repeating: "  ", count: depth - 1)
                if !lists.isEmpty, lists[lists.count - 1].ordered {
                    lists[lists.count - 1].counter += 1
                    emit("\(indent)\(lists[lists.count - 1].counter). ")
                } else {
                    emit("\(indent)- ")
                }
                visitChildren(of: element)
                ensureNewline()
            case "blockquote":
                ensureBlankLine()
                emit("> ")
                visitChildren(of: element)
                ensureBlankLine()
            case "a":
                openFrame("a", href: attribute("href"))
                visitChildren(of: element)
                closeFrame("a")
            case "strong", "b", "em", "i":
                openFrame(tag)
                visitChildren(of: element)
                closeFrame(tag)
            case "code":
                if preDepth > 0 {
                    visitChildren(of: element)
                } else {
                    openFrame("code")
                    visitChildren(of: element)
                    closeFrame("code")
                }
            case "pre":
                preDepth += 1
                openFrame("pre")
                visitChildren(of: element)
                preDepth -= 1
                closeFrame("pre")
            case "tr":
                ensureNewline()
                emit("| ")
                visitChildren(of: element)
                ensureNewline()
            case "td", "th":
                visitChildren(of: element)
                trimTrailingSpaces()
                emit(" | ")
            default:
                let block = HTMLToMarkdown.blocks.contains(tag)
                if block { ensureBlankLine() }
                visitChildren(of: element)
                if block { ensureBlankLine() }
            }
        }

        // MARK: Finish

        mutating func finish(title: String) -> String {
            while frames.count > 1 {
                let inner = frames.removeLast()
                frames[frames.count - 1].text += inner.text
            }
            var body = frames[0].text
            // Tidy: trailing spaces (except Markdown line breaks), runs of blank lines.
            body = body.components(separatedBy: "\n").map { line -> String in
                let stripped = line.replacingOccurrences(of: "\\s+$", with: "", options: .regularExpression)
                return line.hasSuffix("  ") && !stripped.isEmpty ? stripped + "  " : stripped
            }.joined(separator: "\n")
            body = body.replacingOccurrences(of: "\n{3,}", with: "\n\n", options: .regularExpression)
            body = body.trimmingCharacters(in: .whitespacesAndNewlines)

            let cleanTitle = title.trimmingCharacters(in: .whitespacesAndNewlines)
            if !cleanTitle.isEmpty && !body.hasPrefix("# ") {
                return cleanTitle + "\n\n" + body
            }
            return body
        }
    }
}
