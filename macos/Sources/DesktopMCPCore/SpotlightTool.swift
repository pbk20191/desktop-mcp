import CoreServices
import Foundation
import MCP

/// Searches the system Spotlight index through `MDQuery`. Building the query string and
/// formatting the results are pure; only `search` touches the index.
enum SpotlightTool {
    struct Item: Sendable, Equatable {
        var path: String
        var kind: String
        var modified: Date
        var size: Int?
        var text: String?
    }

    static let kinds = ["any", "document", "pdf", "image", "folder", "application"]
    static let watchdogSeconds = 10.0
    private static let contentLimit = 300

    /// Escapes a word for use inside a quoted Spotlight string, where `*` would otherwise be a
    /// wildcard: the user's words are matched literally.
    private static func escape(_ word: Substring) -> String {
        var escaped = ""
        for character in word {
            if character == "\\" || character == "\"" || character == "*" { escaped.append("\\") }
            escaped.append(character)
        }
        return escaped
    }

    /// Each whitespace-separated word must appear in the display name or the text content.
    static func wordsQuery(_ query: String) throws -> String {
        let words = query.split(whereSeparator: \.isWhitespace)
        guard !words.isEmpty else { throw ToolError("query is required.") }
        return words.map { word in
            let w = escape(word)
            return #"(kMDItemDisplayName == "*\#(w)*"cdw || kMDItemTextContent == "\#(w)"cdw)"#
        }.joined(separator: " && ")
    }

    static func kindClause(_ kind: String) -> String? {
        let uti: String
        switch kind {
        case "document": uti = "public.content"
        case "pdf": uti = "com.adobe.pdf"
        case "image": uti = "public.image"
        case "folder": uti = "public.folder"
        case "application": uti = "com.apple.application"
        default: return nil
        }
        return #"kMDItemContentTypeTree == "\#(uti)""#
    }

    private static func attribute(_ item: MDItem, _ name: CFString) -> CFTypeRef? {
        MDItemCopyAttribute(item, name)
    }

    /// Runs `query` synchronously over `scope`, stopping it after `timeout` seconds; a stopped
    /// query yields what it gathered so far. `content` also reads `kMDItemTextContent`.
    static func search(_ query: String, scope: [URL], timeout: Double, content: Bool = false) async throws -> [Item] {
        try await offload { () throws -> [Item] in
            guard let mdQuery = MDQueryCreate(kCFAllocatorDefault, query as CFString, nil, nil) else {
                throw ToolError("Invalid Spotlight query: \(query)", telemetry: "Invalid Spotlight query")
            }
            if !scope.isEmpty {
                MDQuerySetSearchScope(mdQuery, scope.map { $0.path as CFString } as CFArray, 0)
            }
            // MDQueryStop is the documented way to end a synchronous MDQueryExecute from another
            // thread; the watchdog only ever calls that one function on the query.
            let shared = UncheckedSendable(mdQuery)
            let watchdog = DispatchWorkItem { MDQueryStop(shared.value) }
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: watchdog)
            let executed = MDQueryExecute(mdQuery, CFOptionFlags(kMDQuerySynchronous.rawValue))
            watchdog.cancel()
            guard executed else {
                throw ToolError("Invalid Spotlight query: \(query)", telemetry: "Invalid Spotlight query")
            }
            MDQueryDisableUpdates(mdQuery)

            var items: [Item] = []
            for index in 0..<MDQueryGetResultCount(mdQuery) {
                guard let raw = MDQueryGetResultAtIndex(mdQuery, index) else { continue }
                let mdItem = Unmanaged<MDItem>.fromOpaque(raw).takeUnretainedValue()
                guard let path = attribute(mdItem, kMDItemPath) as? String else { continue }
                let tree = attribute(mdItem, kMDItemContentTypeTree) as? [String] ?? []
                let modified = attribute(mdItem, kMDItemContentModificationDate) as? Date
                    ?? attribute(mdItem, kMDItemFSContentChangeDate) as? Date
                    ?? .distantPast
                let size = tree.contains("public.folder") ? nil : (attribute(mdItem, kMDItemFSSize) as? NSNumber)?.intValue
                items.append(Item(
                    path: path,
                    kind: attribute(mdItem, kMDItemKind) as? String ?? "",
                    modified: modified,
                    size: size,
                    text: content ? attribute(mdItem, kMDItemTextContent) as? String : nil))
            }
            return items
        }
    }

    /// Newest first, at most `limit` lines of `<path>\t<kind>\t<ISO-8601>\t<size>`.
    static func format(_ items: [Item], limit: Int, content: Bool) -> String {
        guard !items.isEmpty else { return "No matches." }
        let sorted = items.sorted { $0.modified != $1.modified ? $0.modified > $1.modified : $0.path < $1.path }
        let shown = sorted.prefix(max(limit, 1))
        let dates = ISO8601DateFormatter()
        var lines: [String] = []
        for item in shown {
            lines.append([item.path, item.kind, dates.string(from: item.modified), item.size.map(String.init) ?? "-"]
                .joined(separator: "\t"))
            if content, let text = item.text {
                let flat = text.components(separatedBy: .newlines).joined(separator: " ")
                if !flat.trimmingCharacters(in: .whitespaces).isEmpty {
                    lines.append("  " + String(flat.prefix(contentLimit)))
                }
            }
        }
        if sorted.count > shown.count { lines.append("\(sorted.count - shown.count) more not shown") }
        return lines.joined(separator: "\n")
    }

    static func tool() -> Tool {
        Tool(
            name: "Spotlight",
            description: "Searches the Spotlight index for files. By default 'query' is words that must each appear in the file name or text; raw=true passes 'query' as a Spotlight query (e.g. kMDItemContentModificationDate > $time.today(-7)). 'scope' is a list of folders (default: home). 'kind' narrows the type. Returns up to 'limit' (max 200) lines '<path>\\t<kind>\\t<modified>\\t<size>', newest first; content=true adds the start of each file's indexed text.",
            inputSchema: DesktopMCP.schema([
                "query": DesktopMCP.string(),
                "raw": DesktopMCP.boolean(default: false),
                "scope": DesktopMCP.stringArray(),
                "kind": DesktopMCP.string(default: "any", enum: kinds),
                "limit": DesktopMCP.integer(default: 20),
                "content": DesktopMCP.boolean(default: false),
            ], required: ["query"]),
            annotations: ToolAnnotations(title: "Spotlight", readOnly: true, destructive: false, idempotent: true, openWorld: false),
            handler: { args in
                let text = try args.string("query") ?? ""
                let raw = try args.bool("raw") ?? false
                let kind = try args.choice("kind", in: kinds, default: "any")
                let limit = min(max(try args.int("limit") ?? 20, 1), 200)
                let content = try args.bool("content") ?? false
                let paths = try args.stringList("scope") ?? ["~"]
                let scope = paths.map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }

                var query: String
                if raw {
                    let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !trimmed.isEmpty else { throw ToolError("query is required.") }
                    query = trimmed
                } else {
                    query = try wordsQuery(text)
                }
                if let clause = kindClause(kind) { query = "(\(query)) && \(clause)" }

                let items = try await search(query, scope: scope, timeout: watchdogSeconds, content: content)
                return [.text(format(items, limit: limit, content: content))]
            })
    }
}
