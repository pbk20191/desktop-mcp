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

    /// The caller's whitespace-separated words, escaped; throws when there are none.
    private static func escapedWords(_ query: String) throws -> [String] {
        let words = query.split(whereSeparator: \.isWhitespace)
        guard !words.isEmpty else { throw ToolError("query is required.") }
        return words.map(escape)
    }

    /// Each whitespace-separated word must appear in the display name or the text content.
    static func wordsQuery(_ query: String) throws -> String {
        try escapedWords(query).map { w in
            #"(kMDItemDisplayName == "*\#(w)*"cdw || kMDItemTextContent == "\#(w)"cdw)"#
        }.joined(separator: " && ")
    }

    /// Each word must appear in the display name: the matches ranked first.
    static func nameQuery(_ query: String) throws -> String {
        try escapedWords(query).map { w in #"kMDItemDisplayName == "*\#(w)*"cdw"# }.joined(separator: " && ")
    }

    /// The words query minus the name matches: at least one word is missing from the name, so
    /// this and `nameQuery` never return the same item.
    static func contentOnlyQuery(_ query: String) throws -> String {
        let notInName = try escapedWords(query).map { w in #"kMDItemDisplayName != "*\#(w)*"cdw"# }
        return try wordsQuery(query) + " && (" + notInName.joined(separator: " || ") + ")"
    }

    /// The folders searched when the caller names none: the application folders (recursive, so
    /// Utilities is included) for `application`, home otherwise. `~` is left unexpanded.
    static func defaultScope(kind: String) -> [String] {
        kind == "application" ? ["/Applications", "/System/Applications", "~/Applications"] : ["~"]
    }

    /// The caller's folders, or the default for `kind` when there are none, with `~` expanded.
    static func scope(_ paths: [String], kind: String) -> [URL] {
        (paths.isEmpty ? defaultScope(kind: kind) : paths)
            .map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }
    }

    /// The first query's items, then the second's, up to `limit`; never re-sorted across the two.
    /// The total counts both, so "N more not shown" covers rows the second query was not read for.
    static func merge(first: [Item], firstTotal: Int, second: [Item], secondTotal: Int,
                      limit: Int) -> (items: [Item], total: Int) {
        (Array((first + second).prefix(max(limit, 0))), firstTotal + secondTotal)
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

    /// Runs `query` over `scope` sorted newest first and reads the first `limit` results (none
    /// for a limit of 0, which only counts). The whole call, reads included, stops at `timeout`
    /// seconds. `content` adds a preview of items that are text files. `check`, when given, is validated first, on the same thread.
    static func search(_ query: String, scope: [URL], limit: Int, timeout: Double, content: Bool = false,
                       check: (query: String, shown: String)? = nil) async throws -> Found {
        try await offload { () throws -> Found in
            if let check { try validate(check.query, shown: check.shown) }
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

    /// One `<path>\t<kind>\t<ISO-8601>\t<size>` line per item, in the order given (the query
    /// sorts), then how many more matched and whether the search was stopped.
    static func format(_ items: [Item], total: Int, content: Bool, stopped: Bool) -> String {
        let dates = ISO8601DateFormatter()
        var lines: [String] = []
        for item in items {
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
            description: "Searches the Spotlight index for files. By default 'query' is words that must each appear in the file name or text, name matches first, then content-only matches; raw=true passes 'query' as a Spotlight query (e.g. kMDItemContentModificationDate > $time.today(-7)). 'scope' is a list of folders (default: home; kind=application without scope searches the app folders /Applications, /System/Applications, ~/Applications). 'kind' narrows the type. Returns up to 'limit' (max 200) lines '<path>\\t<kind>\\t<modified>\\t<size>', newest first within each group; content=true adds the first 300 characters of each plain-text file.",
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
                let folders = scope(try args.stringList("scope") ?? [], kind: kind)
                let clause = kindClause(kind)
                func narrowed(_ query: String) -> String { clause.map { "(\(query)) && \($0)" } ?? query }

                if raw {
                    let query = text.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !query.isEmpty else { throw ToolError("query is required.") }
                    // The user's query is validated alone, so the error quotes what they wrote.
                    let found = try await search(narrowed(query), scope: folders, limit: limit, timeout: deadlineSeconds,
                                                 content: content, check: (query, query))
                    return [.text(format(found.items, total: found.total, content: content, stopped: found.stopped))]
                }

                // Name matches first, then matches by content only; both share one deadline.
                let start = Date()
                let names = try nameQuery(text)
                let first = try await search(narrowed(names), scope: folders, limit: limit, timeout: deadlineSeconds,
                                             content: content, check: (names, text))
                let remaining = deadlineSeconds - Date().timeIntervalSince(start)
                guard !first.stopped, remaining > 0 else {
                    return [.text(format(first.items, total: first.total, content: content, stopped: true))]
                }
                // When the name matches filled the limit, the second query only counts.
                let contentOnly = try contentOnlyQuery(text)
                let second = try await search(narrowed(contentOnly), scope: folders, limit: limit - first.items.count,
                                              timeout: remaining, content: content, check: (contentOnly, text))
                let merged = merge(first: first.items, firstTotal: first.total, second: second.items,
                                   secondTotal: second.total, limit: limit)
                return [.text(format(merged.items, total: merged.total, content: content, stopped: second.stopped))]
            })
    }
}
