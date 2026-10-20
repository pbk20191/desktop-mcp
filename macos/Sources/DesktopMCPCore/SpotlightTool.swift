import CoreServices
import Foundation
import MCP

/// Searches the system Spotlight index through `MDQuery`. Building the query string and
/// formatting the results are pure; only `search` touches the index.
enum SpotlightTool {
    struct Item: Sendable, Equatable {
        var path: String
        var kind: String
        var modified: Date?
        var size: Int?
        var text: String?
    }

    /// The items read (newest first, at most the limit), how many the query matched in all, and
    /// whether the deadline cut the search short.
    struct Found: Sendable, Equatable {
        var items: [Item]
        var total: Int
        var stopped: Bool
    }

    static let kinds = ["any", "document", "pdf", "image", "folder", "application"]
    static let deadlineSeconds = 10.0
    static let stoppedLine = "(search stopped after 10 s; results may be incomplete)"
    private static let previewLimit = 300
    private static let previewBytes = 4096

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

    private static func invalid(_ shown: String) -> ToolError {
        ToolError("Invalid Spotlight query: \(shown)", telemetry: "Invalid Spotlight query")
    }

    /// Throws `Invalid Spotlight query: <shown>` when the system rejects `query`.
    static func validate(_ query: String, shown: String) throws {
        guard MDQueryCreate(kCFAllocatorDefault, query as CFString, nil, nil) != nil else { throw invalid(shown) }
    }

    /// The first 300 characters of a text file, from at most its first 4 KiB, decoded as UTF-8
    /// with replacement and with line breaks turned into spaces; nil when unreadable or blank.
    static func preview(path: String) -> String? {
        guard let handle = FileHandle(forReadingAtPath: path) else { return nil }
        defer { try? handle.close() }
        guard let data = try? handle.read(upToCount: previewBytes), !data.isEmpty else { return nil }
        let flat = String(decoding: data, as: UTF8.self).components(separatedBy: .newlines).joined(separator: " ")
        guard !flat.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
        return String(flat.prefix(previewLimit))
    }

    private static func attribute(_ item: MDItem, _ name: CFString) -> CFTypeRef? {
        MDItemCopyAttribute(item, name)
    }

    /// Runs `query` over `scope` sorted newest first and reads the first `limit` results. The
    /// whole call, reads included, stops at `timeout` seconds. `content` adds a preview of
    /// items that are text files.
    static func search(_ query: String, scope: [URL], limit: Int, timeout: Double, content: Bool = false) async throws -> Found {
        try await offload { () throws -> Found in
            let sorting = [kMDItemContentModificationDate] as CFArray
            guard let mdQuery = MDQueryCreate(kCFAllocatorDefault, query as CFString, nil, sorting) else {
                throw invalid(query)
            }
            MDQuerySetSortOptionFlagsForAttribute(mdQuery, kMDItemContentModificationDate,
                                                  UInt32(kMDQueryReverseSortOrderFlag.rawValue))
            if !scope.isEmpty {
                MDQuerySetSearchScope(mdQuery, scope.map { $0.path as CFString } as CFArray, 0)
            }

            // Observed behaviour: MDQueryStop from another thread makes a synchronous
            // MDQueryExecute return with what it gathered so far. The watchdog only calls that,
            // and the box is emptied once the execute returns so it stops pinning the query.
            // The execute gets 80% of the time, so a stopped search still has time left to read
            // its first results before the deadline.
            let deadline = Date().addingTimeInterval(timeout)
            let executeBudget = timeout * 0.8
            let box = Locked<MDQuery?>(mdQuery)
            let stoppedExecute = Locked(false)
            let watchdog = DispatchWorkItem {
                box.withLock { if let q = $0 { MDQueryStop(q); stoppedExecute.current = true } }
            }
            DispatchQueue.global().asyncAfter(deadline: .now() + executeBudget, execute: watchdog)
            let executed = MDQueryExecute(mdQuery, CFOptionFlags(kMDQuerySynchronous.rawValue))
            watchdog.cancel()
            box.withLock { $0 = nil }
            guard executed else { throw invalid(query) }
            MDQueryDisableUpdates(mdQuery)

            var stopped = stoppedExecute.current
            let total = MDQueryGetResultCount(mdQuery)
            var items: [Item] = []
            for index in 0..<min(total, max(limit, 0)) {
                if Date() >= deadline { stopped = true; break }
                guard let raw = MDQueryGetResultAtIndex(mdQuery, index) else { continue }
                let mdItem = Unmanaged<MDItem>.fromOpaque(raw).takeUnretainedValue()
                guard let path = attribute(mdItem, kMDItemPath) as? String else { continue }
                let tree = attribute(mdItem, kMDItemContentTypeTree) as? [String] ?? []
                let modified = attribute(mdItem, kMDItemContentModificationDate) as? Date
                    ?? attribute(mdItem, kMDItemFSContentChangeDate) as? Date
                let size = tree.contains("public.folder") ? nil : (attribute(mdItem, kMDItemFSSize) as? NSNumber)?.intValue
                items.append(Item(
                    path: path,
                    kind: attribute(mdItem, kMDItemKind) as? String ?? "",
                    modified: modified,
                    size: size,
                    text: content && tree.contains("public.text") ? preview(path: path) : nil))
            }
            return Found(items: items, total: total, stopped: stopped)
        }
    }

    /// One `<path>\t<kind>\t<ISO-8601>\t<size>` line per item, newest first, then how many more
    /// matched and whether the search was stopped.
    static func format(_ items: [Item], total: Int, content: Bool, stopped: Bool) -> String {
        let sorted = items.sorted { a, b in
            switch (a.modified, b.modified) {
            case let (x?, y?) where x != y: return x > y
            case (_?, nil): return true
            case (nil, _?): return false
            default: return a.path < b.path
            }
        }
        let dates = ISO8601DateFormatter()
        var lines: [String] = []
        for item in sorted {
            lines.append([item.path, item.kind, item.modified.map(dates.string(from:)) ?? "-",
                          item.size.map(String.init) ?? "-"].joined(separator: "\t"))
            if content, let text = item.text { lines.append("  " + text) }
        }
        if lines.isEmpty { lines.append("No matches.") }
        if total > items.count { lines.append("\(total - items.count) more not shown") }
        if stopped { lines.append(stoppedLine) }
        return lines.joined(separator: "\n")
    }

    static func tool() -> Tool {
        Tool(
            name: "Spotlight",
            description: "Searches the Spotlight index for files. By default 'query' is words that must each appear in the file name or text; raw=true passes 'query' as a Spotlight query (e.g. kMDItemContentModificationDate > $time.today(-7)). 'scope' is a list of folders (default: home). 'kind' narrows the type. Returns up to 'limit' (max 200) lines '<path>\\t<kind>\\t<modified>\\t<size>', newest first; content=true adds the first 300 characters of each plain-text file.",
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
                let paths = try args.stringList("scope") ?? []
                let scope = (paths.isEmpty ? ["~"] : paths)
                    .map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }

                var query: String
                if raw {
                    let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !trimmed.isEmpty else { throw ToolError("query is required.") }
                    query = trimmed
                } else {
                    query = try wordsQuery(text)
                }
                try validate(query, shown: raw ? query : text)
                if let clause = kindClause(kind) { query = "(\(query)) && \(clause)" }

                let found = try await search(query, scope: scope, limit: limit, timeout: deadlineSeconds, content: content)
                return [.text(format(found.items, total: found.total, content: content, stopped: found.stopped))]
            })
    }
}
