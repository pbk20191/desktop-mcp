# Native desktop tools: Open, Clipboard, RunShortcut, Spotlight, Window, Menu, OCR

Date: 2026-10-20
Status: approved 2026-10-20 (open questions resolved as recommended)
Builds on: master 2dceca7

## Intent

The current tools (App, Shell, Snapshot, Click, Type, Scroll, Move, Shortcut, Wait, Scrape,
Desktop, Notification) drive the Mac mostly through coordinates and keystrokes. Several common
jobs have a direct system API that is faster, more exact and cheaper in tokens than clicking:
opening a file, reading what the user copied, running a user's Shortcut, finding a file,
arranging windows, choosing a menu command, and reading text off a UI that has no accessibility
tree. Each tool below wraps one such API. They are ordered from simplest to most involved, and
each is an independent unit that can ship on its own.

## Goals

- Seven new tools, each one system framework or one system binary, no new dependencies, no new
  runtime installs.
- Every tool is excludable with `--exclude-tools` / `[tools] exclude` like the existing ones.
- `OCR` is opt-in (owner, 2026-10-20: it is slow): it is registered only when named in
  `[tools] include`, `--include-tools` or `DESKTOP_MCP_INCLUDE_TOOLS` (comma-separated, merged like
  `exclude`). Every other tool stays on by default. `exclude` wins over `include`.
- Outputs are compact text the model can act on: coordinates in the same screen space `Click`
  uses (points, origin top-left of the main display), paths as absolute POSIX paths.
- Each tool is testable in-process without granting Accessibility or Screen Recording to the test
  runner, by keeping the system call behind a thin seam and testing the logic around it.

## Non-goals

- Changing existing tools. `App` keeps its `resize`/`move` modes even though `Window` covers them.
- EventKit (Calendar, Reminders), Contacts, Mail, Photos: each needs its own TCC grant and has
  dedicated MCP servers elsewhere.
- Writing to Spotlight's index (CoreSpotlight `CSSearchableIndex`). The search tool reads the
  system index through `MDQuery`; CoreSpotlight's `CSSearchQuery` only sees an app's own items.
- Long-running observation (watching the clipboard, live Spotlight updates, window events).

## Anti-goals

- A tool that silently overwrites user data. Writing the clipboard, closing a window and running a
  Shortcut are marked `destructive` in their annotations.
- Opening network locations or arbitrary URL schemes the user did not ask for: `Open` takes what
  the model passes, nothing more, and refuses an empty target.

## Order and rationale

| # | Tool | API | New TCC | Why this place |
|---|---|---|---|---|
| 1 | `Open` | NSWorkspace | none | one call per mode |
| 2 | `Clipboard` | NSPasteboard | none | read/write of three types |
| 3 | `RunShortcut` | `/usr/bin/shortcuts` | none (Shortcuts may prompt per action) | subprocess, reuses `Shell.run` |
| 4 | `Spotlight` | CoreServices `MDQuery` | none | query building + result formatting |
| 5 | `Window` | AX | Accessibility (already required) | window enumeration and identity |
| 6 | `Menu` | AX menu bar | Accessibility (already required) | tree walk, title matching |
| 7 | `OCR` | Vision + existing capture | Screen Recording (as `Snapshot use_vision`) | coordinate conversion, multi-display |

## Decisions

### 1. `Open`

- Input: `target` (string, required: absolute path, `~`-path, or URL), `mode` (`open` default |
  `reveal`), `app` (string, optional: app name or bundle id, only with `open`).
- `open`: a path that does not exist → error `"No such file: <path>"`; a URL is opened with
  `NSWorkspace.open(_:configuration:)`, a path with `open(_:withApplicationAt:configuration:)`
  when `app` is given (resolved by bundle id first, then by name through
  `NSWorkspace.urlForApplication(withBundleIdentifier:)` / the same lookup `App launch` uses).
- `reveal`: `NSWorkspace.activateFileViewerSelecting([url])`; only for existing paths.
- Output: `"Opened <target> with <App name>."` / `"Revealed <path> in Finder."`
- Annotations: readOnly false, destructive false, idempotent false, openWorld true.

### 2. `Clipboard`

- Input: `mode` (`read` default | `write`), `text` (string, for `write`), `paths` (array of
  strings, for `write`, mutually exclusive with `text`).
- `read`: returns, in this order, whatever the general pasteboard holds:
  - text (`.string`), truncated at 100 000 characters with a `"… (truncated, N characters)"` tail;
  - file URLs as one absolute path per line under `"Files:"`;
  - an image (`.png`, else `.tiff` converted to PNG) as an MCP image content block, scaled down so
    the longer side is at most 1568 px.
  An empty pasteboard → `"The clipboard is empty."`
- `write`: clears the pasteboard and writes `text` as `.string`, or `paths` as file URLs (each
  must exist). Output `"Copied N characters."` / `"Copied N files."`.
- Seam: the tool takes an `NSPasteboard`; production passes `.general`, tests a uniquely named
  pasteboard (`NSPasteboard(name:)`), released afterwards, so tests never touch the user's clipboard.
- Annotations: destructive true (write replaces the user's clipboard), readOnly false.

### 3. `RunShortcut`

Named so it is not confused with the existing `Shortcut` (keyboard shortcuts).

- Input: `mode` (`run` default | `list`), `name` (string, for `run`), `input` (string,
  optional), `timeout` (integer seconds, default 60, capped by `--max-timeout` like `Shell`).
- `list`: `shortcuts list` → names, one per line; optional `folder` input passes `--folder-name`.
- `run`: `shortcuts run <name>` with `--input-path <tmp file>` when `input` is given (the file
  holds the text, is created `0600` in `FileManager.temporaryDirectory`, removed afterwards) and
  `--output-path <tmp file>`; the output file's text (or `"(no output)"`) is returned with the
  exit status. A name not in `shortcuts list` → error listing up to 20 close matches
  (case-insensitive prefix/substring), without running anything.
- Runs through the existing `Shell.run` (posix_spawn, timeout, kills the process group on
  timeout/cancel).
- Annotations: destructive true, openWorld true.

### 4. `Spotlight`

- Input: `query` (string, required), `raw` (boolean, default false), `scope` (array of paths,
  default `[~]`), `kind` (optional enum: `any` default, `document`, `pdf`, `image`, `folder`,
  `application`), `limit` (integer, default 20, max 200), `content` (boolean, default false).
- Query building: with `raw` false, `query` is matched as words: each whitespace-separated word
  becomes `(kMDItemDisplayName == "*word*"cdw || kMDItemTextContent == "word"cdw)` joined with
  `&&`; quotes and backslashes in words are escaped. With `raw` true the string is passed as a
  Spotlight query as is (for `kMDItemContentModificationDate > $time.today(-7)` and the like).
  `kind` adds a `kMDItemContentTypeTree == "<UTI>"` clause (`public.content`, `com.adobe.pdf`,
  `public.image`, `public.folder`, `com.apple.application`).
- Execution: `MDQueryCreate` sorted by `kMDItemContentModificationDate` (newest first),
  `MDQuerySetSearchScope` with `scope`, `MDQueryExecute` with `kMDQuerySynchronous`, run off the
  cooperative pool (it blocks). The whole call, attribute reads included, is bounded by a 10 s
  wall-clock deadline: `MDQueryStop` ends the execute, and only the first `limit` results are read;
  the "more" count comes from `MDQueryGetResultCount`. A search that hit the deadline adds the line
  `"(search stopped after 10 s; results may be incomplete)"`. A query the system rejects
  (`MDQueryCreate` returns nil) → error `"Invalid Spotlight query: <query>"`, quoting what the
  caller passed (the raw string or words), not the combined query with the `kind` clause.
  An empty `scope` means the default (home).
- Output: up to `limit` lines, newest modification first:
  `<path>\t<kind>\t<modified ISO-8601>\t<size>` (`-` for a missing date or size); then
  `"N more not shown"` when truncated. With `content` true, each item whose type conforms to
  `public.text` is followed by the first 300 characters of the file itself (at most 4 KiB read,
  decoded as UTF-8 with replacement): Spotlight indexes `kMDItemTextContent` but does not return
  it, so the attribute cannot serve. Other types get no content line. No results → `"No matches."`
- Seam: query string building and result formatting are pure functions with unit tests; one
  test runs a real raw query for an always-indexed system item (`kMDItemFSName == "Safari.app"`
  in `/Applications`) and asserts path, kind and date, skipping only when Spotlight returns nothing
  (indexing disabled). The temporary directory is not indexed, so a test file there cannot serve.
- Annotations: readOnly true, idempotent true.

### 5. `Window`

- Input: `mode` (`list` default | `focus` | `move` | `resize` | `minimize` | `restore` |
  `close`), `app` (optional name or bundle id; `list` without it lists every regular app),
  `id` (string, from `list`; required for every mode except `list`), `loc`, `size` (int pairs).
- `list` output, one window per line:
  `<id>\t<app>\t"<title>"\t[x,y,w,h]\t<flags>` where flags are any of `focused`, `minimized`,
  `fullscreen`. `id` is `<pid>:w<number>` where `<number>` is the window server's window number
  (`CGWindowID`, read with `_AXUIElementGetWindow`, looked up with `dlsym` so its absence is not
  a link failure). The number is stable for the window's lifetime, unlike its position in
  `AXWindows`, which follows z-order and changes when a window is focused or restored. Only when
  the number cannot be read is the id `<pid>:<index>` (index in `AXWindows` at listing time).
- Identity: a `w<number>` id is resolved at use by finding the app's window with that number;
  no longer present → error `"Window <id> no longer exists; call Window list again."`. Its title
  and frame may have changed (the user moved it, it was restored) without making it a different
  window, so they are not compared. An index id (fallback) is re-resolved by pid and index and
  checked against the title and frame captured at listing time (a small per-server cache, last
  64 listings); a mismatch → error `"Window <id> changed since it was listed; call Window list again."`
  rather than acting on the wrong window. Either way the check is repeated inside the same
  blocking call that acts, on the element that is acted on.
- `focus`: activate the app, `AXRaise` the window, set `AXMain`. `minimize`/`restore`: set
  `AXMinimized`. `move`/`resize`: as `App` does, on the resolved window. `close`: press the
  window's `AXCloseButton`; reports `"Close requested"` (the app may ask to save).
- Annotations: destructive true (close), readOnly false.

### 6. `Menu`

- Input: `mode` (`list` default | `select`), `app` (optional; default the frontmost app),
  `path` (array of strings for `select`, e.g. `["File", "Export…"]`; for `list`, an optional
  prefix to list below), `depth` (integer, default 2, max 4, for `list`).
- Matching a path element against an item's `AXTitle`, in this order: exact; case-insensitive;
  case-insensitive with `…` ≡ `...` and trailing whitespace ignored. More than one match at the
  same rule → error naming them. The Apple menu is item 0 and can be addressed as `"Apple"`.
- `select` walks the menu bar: for each element, `AXPress` on the bar item / item opens its
  submenu (via `AXChildren` of the `AXMenu`), the last element is pressed. A disabled item
  (`AXEnabled` false) → error `"Menu item <path> is disabled."` without pressing. A missing item
  → error listing its siblings' titles. Menus opened on the way are closed (`AXCancel` /
  Escape) on any error.
- `list` output: an indented tree of titles with `[disabled]` and the item's keyboard shortcut
  when it has one (`AXMenuItemCmdChar` + modifiers, rendered `⌘⇧E`); separators omitted.
- Seam: the walker takes a protocol over AX (title, enabled, children, press) so path matching
  and error copy are tested with an in-memory tree.
- Annotations: destructive true (it runs arbitrary commands), readOnly false.

### 7. `OCR`

- Opt-in: `DesktopMCP.optInTools = ["OCR"]`; `makeRegistry(excluding:including:)` skips an opt-in
  tool unless it is in `including`. Naming a tool in `include` that is not opt-in is a no-op with
  the log line `"Tool '<name>' is on by default; include ignored"`; an unknown name logs
  `"Could not include tool '<name>' — not found"`. `install` passes nothing new: the agent reads
  `[tools] include` from config.toml like `exclude`.

- Input: `region` (optional `[x, y, w, h]` in screen points; default the focused window's frame,
  else the main display), `languages` (array, default `["ko-KR", "en-US"]`), `level`
  (`accurate` default | `fast`), `min_confidence` (number 0–1, default 0.3).
- Capture: reuse `Screenshot`'s ScreenCaptureKit path for the displays the region intersects;
  crop to the region. No Screen Recording permission → the same message `Snapshot use_vision` gives.
- Recognition: `VNRecognizeTextRequest` with `recognitionLanguages`, `recognitionLevel`,
  `usesLanguageCorrection` true; observations below `min_confidence` dropped.
- Coordinates: Vision's normalized, bottom-left-origin boxes are converted to image pixels, then
  to screen points with the capturing display's scale and origin, so a returned centre can be
  passed to `Click` unchanged. Multi-display regions are recognized per display and merged.
- Output: one line per text line, top-to-bottom then left-to-right:
  `[cx,cy] "<text>" (w×h, confidence 0.93)`; at most 500 lines, then `"N more lines not shown"`.
  Nothing found → `"No text found."`
- Seam: the recognizer takes a `CGImage` and a pixel→point mapping; tests render known text into
  an image with Core Text, recognize it (Vision needs no TCC), and assert both the text and that
  the converted centres land inside the rendered boxes.
- Annotations: readOnly true, idempotent false (the screen changes).

## Constraints

- Swift 6 strict concurrency; no `@unchecked Sendable`; platform `.macOS(.v13)` (every API above
  exists on 13: `VNRecognizeTextRequest` with Korean since 13, `MDQuery`, `shortcuts` CLI);
  SDK 0.12.1; no new dependencies.
- Blocking calls (MDQuery, AX, Vision, pasteboard) run off the cooperative pool, as the existing
  `offload` helper does.
- Tests: `DEVELOPER_DIR=/Applications/Xcode-16.4.app/Contents/Developer`,
  `ANONYMIZED_TELEMETRY=false`; never touch the general pasteboard, never run a real Shortcut,
  never press a real menu item, never move a real window in tests.
- One feature branch, one commit series per tool, in the order above.

## Left to the builder

- File layout (one file per tool in Core is the expectation).
- Exact wording of tool descriptions, within the outputs fixed above.
- How the `Window` listing cache is keyed and evicted.

## Resolved questions (owner: "as recommended", 2026-10-20)

1. Names stay as written: `Open`, `Clipboard`, `RunShortcut`, `Spotlight`, `Window`, `Menu`, `OCR`.
2. `Clipboard write` replaces the user's clipboard; no save/restore mode (YAGNI; annotated destructive).
3. `Window close` and `Menu select` ship enabled; anyone who does not want them uses
   `--exclude-tools` / `[tools] exclude` as for every other tool.
4. `OCR` is off unless included (owner, later on 2026-10-20: too slow to offer by default).
