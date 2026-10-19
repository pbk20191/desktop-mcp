# Windows port P1: server core and headless tools, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the P0b skeleton into a useful headless server: the `serve`/`install`/`uninstall`/`auth` CLI, `config.toml`, the validated `ServePlan`, stdio serving, and the Wait, PowerShell, FileSystem, Clipboard, Process, Registry, Notification and Scrape (without sampling) tools, all producing upstream's output text.

**Architecture:**
- Platform-light logic goes in `DesktopMcp.Core`: coercion, Python-compatible text formatting, the tabulate-style table, fuzzy matching, config, ServePlan, the PowerShell runner, FileSystem, Registry, Processes, and Scrape with its SSRF guard and HTML-to-Markdown converter. Everything there is unit-tested without a desktop.
- `DesktopMcp.Desktop` gains only the Clipboard (WinRT first, Win32 fallback) and Toast services. Neither touches the Windows App SDK, so headless runs never initialise it.
- `DesktopMcp.Server` gains the CLI (System.CommandLine), the stdio host and one `SpecTool` subclass per P1 tool, wired through `ToolRegistry` and `ToolServices`. The exe's `Program.cs` only calls `CliApp.RunAsync`.

**Tech Stack:** .NET 10 NativeAOT, ModelContextProtocol 2.2.0, System.CommandLine 2.0.12, Tomlyn 2.10.1 (source-generated), CsWinRT (Windows SDK 22621 projection: `Windows.ApplicationModel.DataTransfer`, `Windows.UI.Notifications`), hand-declared `LibraryImport`s, xUnit 2.9.3.

**Spec:** `docs/superpowers/specs/2026-10-19-windows-port-design.md`, mainly §4 (result shapes, coercion), §5 (cancellation, Job Object), §8 (CLI, config, core tools), §9 (testing), §10 (differences) and §11 (P1 scope). The upstream reference is CursorTouch/Windows-MCP at `128f2960c79b74bd2630f4dbc9d4f664f4ce89b3`; paths below such as `__main__.py:589` are relative to `src/windows_mcp/` in that tree. Every output string, error message, default and limit is copied from there unless the "Decisions" section says otherwise.

## Global Constraints

- TFM `net10.0-windows10.0.22621.0`, `SupportedOSPlatformVersion` **10.0.22621.0**.
- RIDs `win-x64` and `win-arm64`; the default RID comes from the dotnet process architecture (already in `windows/Directory.Build.props`).
- `TreatWarningsAsErrors`, `IsAotCompatible`, no warning suppression. Package versions published at least two weeks before 2026-10-19 (System.CommandLine 2.0.12 was published 2026-09-08; Tomlyn 2.10.1 on 2026-07-02).
- **The publish folder for each RID contains only `desktop-mcp.exe`** (plus `.pdb`), and that exe alone runs. The probe exe has the same rule. `scripts/build-release.ps1` enforces it and must keep passing.
- Windows App SDK initialisation stays lazy: the server starts and headless tools work without the runtime, and desktop features fail with an install hint. No P1 code calls `AppRuntime.EnsureInitialized()`.
- `scripts/smoke-stdio.ps1` keeps failing if a headless run has d2d1, d3d11, dwrite or d3dcompiler_47 loaded.
- Naming follows the macOS port: command `desktop-mcp`, config directory `~/.desktop-mcp`, environment variables `DESKTOP_MCP_*` in place of `WINDOWS_MCP_*`. `ANONYMIZED_TELEMETRY`, `POSTHOG_API_KEY` and `POSTHOG_HOST` keep their upstream names.
- The tool contract `spec/windows/tools.json` does not change. Tool output text and behaviour match upstream except where spec §10 lists a deliberate difference or the Decisions section below records one.
- Result shapes (spec §4): text tools return one `TextContent` plus `structuredContent: {"result": <text>}`; an unexpected exception becomes `isError: true` with text `Error calling tool '<Name>': <message>` (already implemented by `SpecTool` and `ToolResults`).
- Desktop tests carry `[Trait("Category", "Desktop")]` and run with `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath=`; the default `dotnet test DesktopMcp.slnx` skips them.
- Commit messages end with a blank line and `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Work happens on branch `windows-p1`.

All `dotnet` and `pwsh` commands below run from `windows/` unless stated otherwise; `git` commands run from the repository root.

## Decisions on upstream ambiguities

Upstream is Python on FastMCP, so some behaviour has no exact C# equivalent. Each decision below is binding for the tasks; the citation names the upstream code it departs from or interprets.

1. **Default command** (`__main__.py:546-548`). Upstream's click group has no `invoke_without_command`, so a bare `windows-mcp` fails with "Missing command." The spec makes `serve` the default: a bare `desktop-mcp` (no arguments at all) runs `serve`. Any argument list that starts with a serve flag still gets upstream's legacy usage error (`__main__.py:420-444`).
2. **Environment variables** (`__main__.py:589-698`, `config.py:4-6`). Exactly the options that have an `envvar` upstream get one, renamed: `--auth-key`, `--ip-allowlist`, `--tools`, `--exclude-tools`, `--cors-origins`, `--ssl-certfile`, `--ssl-keyfile`, `--oauth-client-id`, `--oauth-client-secret`, `--stateless-http`, `--control/--no-control`, `--control-idle-timeout`, `--control-policy`, plus `DESKTOP_MCP_DEBUG` (upstream's `is_debug`). `--transport`, `--host`, `--port`, `--config` and `--allow-insecure-remote` have none, as upstream. Environment values are parsed with click's rules (booleans `1/true/t/yes/y/on` and `0/false/f/no/n/off`; an empty value means unset).
3. **Port range** (`__main__.py:568-574`, `infrastructure/config.py:154-156`). Upstream checks the range only for TOML. Spec §8 puts it in `ServePlan`, so every source gets upstream's TOML message `server.port must be between 0 and 65535`.
4. **TOML errors** (`infrastructure/config.py:138`, `__main__.py:735-738`). Upstream prints Python's `TOMLDecodeError` text. We print `Invalid TOML in <path>: <Tomlyn's reason>`. Tomlyn 2.x reads TOML 1.1 while Python 3.14's `tomllib` reads TOML 1.0; every 1.0 document is valid 1.1. The transport message lists the set in a fixed order, `{'stdio', 'sse', 'streamable-http'}` (Python's set order varies between runs, `infrastructure/config.py:148-149`). A section that is not a table (for example `server = 5`) fails with `server must be a TOML table` (upstream raises a Python `TypeError` there).
5. **Argument validation messages** (FastMCP/pydantic, e.g. `tools/input.py:434`). Pydantic's messages are framework artefacts. Our messages: `Missing required argument: <name>`, `<name> must be an integer` (accepts JSON integers, integral floats and integer strings; rejects booleans), `<name> must be a string`. Coercion messages that upstream writes itself are copied exactly (`desktop/utils.py:44`: `<name> must be true or false`). JSON `null` for an optional argument means "use the default".
6. **Loose booleans**: Scrape `use_dom`/`use_sampling` (`tools/scrape.py:29-33`) and Process `force` (`tools/process.py:37`) use upstream's loose rule (`True` or a string whose lower case is `"true"`, everything else false). Spec §4 names both, as upstream treats `force` loosely too. Every other `bool | str` parameter uses the strict rule.
7. **PowerShell Ctrl+Break** (`powershell/utils.py:380-401`). The child runs with `CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW` (.NET 10 `ProcessStartInfo.CreateNewProcessGroup`), so it owns a hidden console and `GenerateConsoleCtrlEvent` from the server usually fails; upstream has the same limitation. We still send it, wait 2 s, then kill the tree with `Process.Kill(entireProcessTree: true)` instead of `taskkill /T /F`.
8. **PowerShell details** (`powershell/service.py`). `PYTHONHOME` is not removed (line 117; it only matters for uv). The executable lookup searches the child's `PATH` with `PATHEXT` and never the current directory (lines 181-209). The elevation hint says `desktop-mcp server` instead of `Windows-MCP server` (lines 263-266). Like upstream's `communicate()`, the timeout covers both process exit and the end of its output streams.
9. **FileSystem paths** (`filesystem/service.py:46` and the other `Path(...).resolve()` calls). Python's `resolve()` on Windows returns the OS final path (links resolved, 8.3 names expanded, on-disk case). `PathResolver.Resolve` does the same with `GetFinalPathNameByHandleW` on the longest existing prefix. Because of it, `Type: Symlink` and `Link target:` are unreachable upstream (lines 306 and 329) and are not produced.
10. **FileSystem text mode** (`filesystem/service.py:56, 89`). Python opens files in text mode: reading turns `\r\n` and `\r` into `\n`, writing turns `\n` into `\r\n`, and `errors='replace'` turns undecodable bytes into U+FFFD. We do the same. Python codec names map to .NET encodings (`utf-8`, `utf-8-sig`, `utf-16`, `latin-1`, `ascii`, `cpNNN`, and any name .NET knows); an unknown name gives Python's `unknown encoding: <name>`. A BOM is written for `utf-8-sig`/`utf-16`/`utf-32` only when the file is new or empty.
    - Names are looked up trimmed first, then normalised, then as `iso8859-N`. Python's aliases `latin`, `mbcs`/`ansi`/`dbcs` (the ANSI code page), `oem` and `utf-7` (a port of CPython's codec) are added. `unicode` is rejected, as Python does. Names only .NET knows are still accepted.
    - Write errors: an unencodable character gives .NET's message, not Python's, and the file is left untouched where Python would truncate it.
11. **FileSystem move onto itself** (`filesystem/service.py:149-154`). With `overwrite=True`, upstream deletes the destination before moving, which deletes the source when both resolve to the same file (a case-only rename). We skip that delete when the two resolved paths are equal ignoring case. This prevents data loss. Extended in review (user-approved):
    - copy and move refuse with an `Error:` when the destination equals the source, contains it, or is inside it, before anything is deleted;
    - a move onto itself is a no-op for files and directories;
    - an empty or whitespace destination counts as missing;
    - a directory loop in a copied tree is an error, checked before anything is created or deleted;
    - deletes remove symlinks and junctions as links and never enter them;
    - other reparse points, such as cloud placeholders, are handled as plain entries, as `rmtree` does.
12. **FileSystem glob** (`filesystem/service.py:210, 265`). Python 3.14 `pathlib` rules: `/` or `\` separate segments, `**` matches zero or more directories (and, as the last segment, every file and directory below), wildcards are fnmatch (`*`, `?`, `[seq]`, `[!seq]`), matching is case-insensitive, symlinked directories are not followed, unreadable directories are skipped, and an absolute pattern fails with `Non-relative patterns are unsupported`. Glob does not follow junctions either; Python 3.14's `**` does, so this is stricter. The other known differences are `..` segments, a trailing separator, the on-disk spelling of matches and repeated `**`.
13. **Registry** (`registry/service.py`). Upstream drives PowerShell cmdlets; we use `Microsoft.Win32.Registry` and reproduce what PowerShell 7 prints (pwsh is upstream's first choice): cmdlet-prefixed errors such as `Get-ItemProperty: Property <name> does not exist at path HKEY_CURRENT_USER\...`, DWORD/QWORD shown unsigned, `Set-ItemProperty`'s conversions (a DWord accepts `-1`, `42`, `0x10`; Binary accepts a single byte), and `Format-List` layout for `list` (names padded to the longest, arrays as `{a, b}`, cut after 4 items with `…`, no line wrapping). Output uses `\n` line endings (upstream's PowerShell output mixes `\r\n` and `\n`). Paths: `HKCU:`/`HKLM:` drives and `Registry::<hive>\...` provider paths. Messages were checked against pwsh 7.6.7 on 2026-10-19. Also:
    - Upstream's real error output is CLIXML text, because it captures pwsh's stderr. The port prints the clean cmdlet text instead.
    - Non-registry drives are refused.
    - Wildcards in paths and names are taken literally.
    - DWord, QWord and Binary accept only plain integer and hex forms; pwsh also accepts `1e3`, `10kb` and an empty value.
    - Control characters in a path are refused.
    - A key delete never targets a hive.
    - An empty value name gets pwsh's texts; only `(default)` means the default value.
14. **Notification failure text** (`notifications/service.py:271-273`). Upstream reports `Notification may have been sent. PowerShell output: <first 200 chars>`. We use no PowerShell, so the text is `Notification may have been sent. Error: <first 200 chars of the exception message>`.
15. **Process names and CPU** (`process/service.py:22-33`). psutil names come from the process snapshot, `chrome.exe` with the extension. We read them from the system process snapshot, mapping PID 0 to psutil's `System Idle Process`. CPU% is measured over two samples 250 ms apart (spec §10); like psutil it is a share of one CPU and can exceed 100. Amended in review:
    - CPU, names and memory come from `NtQuerySystemInformation(SystemProcessInformation)` (not Toolhelp and `TotalProcessorTime`), so processes that deny `OpenProcess` still get real CPU.
    - CPU is rounded to 1 decimal before sorting.
    - Kill checks the creation time against PID reuse and uses exit code 15.
    - "Unknown" appears only in the list output.
16. **Scrape fetch** (`desktop/service.py:1033-1052`, `infrastructure/security.py:190-207`). As upstream, at most 5 requests are made (so 4 redirects are followed) before `Too many redirects while fetching URL`. Departures:
    - **System proxy (user decision, 2026-10-19).** Each request asks `HttpClient.DefaultProxy` (on Windows: the WinINet/WinHTTP settings, including auto-config, then `HTTP(S)_PROXY`/`NO_PROXY`) for the target URI. When it returns a proxy that is not bypassed, the request goes through that proxy and the address cannot be pinned. The URL and every resolved address are still validated locally first, as upstream does before its proxy skips the pin. When no proxy applies, the connection is pinned to the validated address. Every redirect is validated again either way.
    - The timeout is 10 s per request in total, not per socket read.
    - The User-Agent is `desktop-mcp/<version>`, not `python-requests/x`.
    - The body is capped at 8 MB, as the macOS port does.
    - Text without a charset in `Content-Type` is decoded from a `<meta charset>` in the first 1024 bytes, else as UTF-8. requests would use ISO-8859-1 for `text/*`, which garbles UTF-8 pages.
    - The text after the colon in `Failed to connect to <url>: ...` and `Request timed out for <url>: ...` is .NET's.
    - `AddressPolicy` also blocks `100.64.0.0/10` (CGNAT, which holds Alibaba Cloud's metadata service at 100.100.100.200, and tailnets) and `fec0::/10`. This is stricter than upstream; the cost is that Scrape cannot fetch tailnet pages.
    - On the proxy path, `System.Uri`'s host is validated again when it differs from urlsplit's.
    - IP-literal hosts skip DNS.
    - `http://0/` gives `Could not resolve hostname`.
17. **Scrape without sampling in P1** (`tools/scrape.py:28-73`). `use_dom=True` returns upstream's `No DOM information found. Please open <url> in browser first.` until the browser DOM lands (P2 snapshot, P6 DOM). Sampling arrives in P5; until then the result is the raw `URL: <url>\nContent:\n<markdown>`, which is upstream's own fallback when the client cannot sample.
    - **HTML to Markdown (Task 15).** The converter keeps at most 256 open elements; deeper tags are flattened and their end tags absorbed. It adds no new inline Markdown wrappers past 16 levels, list indentation stops growing at 10 levels, invalid numeric entities become U+FFFD, and memory is bounded at about 40 B per input character. These differ from the macOS converter only on pathological pages.
18. **Clipboard text cut** (`tools/clipboard.py:30`). `text[:100]` counts code points, as Python does, so a surrogate pair is never split.
19. **Job Object** (spec §5). At startup `serve` puts the process in a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. Every process the server starts is then killed when the server exits, including GUI apps a user starts through the PowerShell tool (`Start-Process notepad`). Upstream has no such job. This follows the spec and is a visible behaviour change; the user confirmed it on 2026-10-19 (no breakaway).
20. **CLI location.** Spec §3 places the CLI in the exe project. It lives in `DesktopMcp.Server/Cli` so `DesktopMcp.Server.Tests` can test it; the exe's `Program.cs` is a one-line call.

## File map

```
windows/
  Directory.Build.props                       (TomlynIsReflectionEnabledByDefault=false for every project)
  Directory.Build.targets                     (AllowedReferenceRelatedFileExtensions only when publishing)
  Directory.Packages.props                    (Tomlyn 2.10.1, System.CommandLine 2.0.12)
  scripts/build-release.ps1                   (AOT activation check through the probe's imaging step)
  scripts/smoke-stdio.ps1                     (full contract compare, Wait isError, --help, --config, headless tool calls)
  probes/DesktopMcp.Probe/Program.cs          (clipboard and toast probes)
  probes/DesktopMcp.Probe/ClipboardProbe.cs   (new)
  src/DesktopMcp/Program.cs                   (calls CliApp.RunAsync)
  src/DesktopMcp.Core/DesktopMcp.Core.csproj  (Tomlyn)
  src/DesktopMcp.Core/Elevation.cs
  src/DesktopMcp.Core/Interop/CoreNative.cs   (Toolhelp, Job Object, console control, final path names)
  src/DesktopMcp.Core/Text/PyText.cs          (Python number/string formatting rules)
  src/DesktopMcp.Core/Text/TableFormatter.cs  (tabulate "simple")
  src/DesktopMcp.Core/Text/Fuzzy.cs           (thefuzz partial_ratio)
  src/DesktopMcp.Core/Text/Coercion.cs        (spec §4 argument coercion)
  src/DesktopMcp.Core/Net/IpAddressText.cs    (Python-strict IP parsing)
  src/DesktopMcp.Core/Config/ConfigException.cs
  src/DesktopMcp.Core/Config/DesktopMcpConfig.cs
  src/DesktopMcp.Core/Config/ConfigFile.cs
  src/DesktopMcp.Core/Config/ConfigTomlContext.cs
  src/DesktopMcp.Core/Config/IdleTimeout.cs
  src/DesktopMcp.Core/Config/IpNetwork.cs
  src/DesktopMcp.Core/Config/ServeArguments.cs
  src/DesktopMcp.Core/Config/ServePlan.cs
  src/DesktopMcp.Core/Config/ToolFilter.cs
  src/DesktopMcp.Core/PowerShell/ShellEnvironment.cs
  src/DesktopMcp.Core/PowerShell/PowerShellRunner.cs
  src/DesktopMcp.Core/PowerShell/ProcessJob.cs
  src/DesktopMcp.Core/FileSystem/TextEncodings.cs
  src/DesktopMcp.Core/FileSystem/TextFiles.cs
  src/DesktopMcp.Core/FileSystem/PathResolver.cs
  src/DesktopMcp.Core/FileSystem/Fnmatch.cs
  src/DesktopMcp.Core/FileSystem/GlobMatcher.cs
  src/DesktopMcp.Core/FileSystem/FileSystemService.cs
  src/DesktopMcp.Core/WindowsRegistry/RegistryLocation.cs
  src/DesktopMcp.Core/WindowsRegistry/RegistryService.cs
  src/DesktopMcp.Core/Processes/ProcessSnapshot.cs
  src/DesktopMcp.Core/Processes/ProcessService.cs
  src/DesktopMcp.Core/Scrape/ScrapeException.cs
  src/DesktopMcp.Core/Scrape/UrlParts.cs
  src/DesktopMcp.Core/Scrape/AddressPolicy.cs
  src/DesktopMcp.Core/Scrape/HtmlParser.cs
  src/DesktopMcp.Core/Scrape/HtmlToMarkdown.cs
  src/DesktopMcp.Core/Scrape/ScrapeClient.cs
  src/DesktopMcp.Desktop/DesktopMcp.Desktop.csproj   (InternalsVisibleTo the probe)
  src/DesktopMcp.Desktop/Interop/Native.cs            (clipboard entry points)
  src/DesktopMcp.Desktop/Threading/StaThread.cs
  src/DesktopMcp.Desktop/ClipboardAccess/IClipboard.cs
  src/DesktopMcp.Desktop/ClipboardAccess/WinRtClipboard.cs
  src/DesktopMcp.Desktop/ClipboardAccess/Win32Clipboard.cs
  src/DesktopMcp.Desktop/ClipboardAccess/SystemClipboard.cs
  src/DesktopMcp.Desktop/Toasts/IToastSender.cs
  src/DesktopMcp.Desktop/Toasts/ToastSender.cs
  src/DesktopMcp.Server/DesktopMcp.Server.csproj      (System.CommandLine)
  src/DesktopMcp.Server/Cli/CliApp.cs
  src/DesktopMcp.Server/Cli/CliEnvironment.cs
  src/DesktopMcp.Server/Cli/CliUsageException.cs
  src/DesktopMcp.Server/Cli/EnvValues.cs
  src/DesktopMcp.Server/Cli/ServeOptions.cs
  src/DesktopMcp.Server/Hosting/StderrLoggerProvider.cs
  src/DesktopMcp.Server/Hosting/StdioServer.cs
  src/DesktopMcp.Server/Tools/ToolSpecCatalog.cs      (shared Tool type info, duplicate names)
  src/DesktopMcp.Server/Tools/ToolRegistry.cs         (real tools by name)
  src/DesktopMcp.Server/Tools/ToolServices.cs
  src/DesktopMcp.Server/Tools/ToolArguments.cs
  src/DesktopMcp.Server/Tools/WaitTool.cs
  src/DesktopMcp.Server/Tools/PowerShellTool.cs
  src/DesktopMcp.Server/Tools/FileSystemTool.cs
  src/DesktopMcp.Server/Tools/RegistryTool.cs
  src/DesktopMcp.Server/Tools/ProcessTool.cs
  src/DesktopMcp.Server/Tools/ClipboardTool.cs
  src/DesktopMcp.Server/Tools/NotificationTool.cs
  src/DesktopMcp.Server/Tools/ScrapeTool.cs
  tests/DesktopMcp.Core.Tests/DesktopMcp.Core.Tests.csproj   (ExpectedVersion metadata)
  tests/DesktopMcp.Core.Tests/ServerTextTests.cs
  tests/DesktopMcp.Core.Tests/TempDirectory.cs
  tests/DesktopMcp.Core.Tests/Text/{PyTextTests,TableFormatterTests,FuzzyTests,CoercionTests}.cs
  tests/DesktopMcp.Core.Tests/Config/{ConfigFileTests,IpNetworkTests,ServePlanTests,ToolFilterTests}.cs
  tests/DesktopMcp.Core.Tests/PowerShell/{ShellEnvironmentTests,PowerShellRunnerTests,ProcessJobTests}.cs
  tests/DesktopMcp.Core.Tests/FileSystem/{TextFilesTests,PathResolverTests,GlobMatcherTests,FileSystemServiceTests}.cs
  tests/DesktopMcp.Core.Tests/WindowsRegistry/RegistryServiceTests.cs
  tests/DesktopMcp.Core.Tests/Processes/ProcessServiceTests.cs
  tests/DesktopMcp.Core.Tests/Scrape/{UrlPartsTests,AddressPolicyTests,HtmlToMarkdownTests,LoopbackHttpServer,ScrapeClientTests}.cs
  tests/DesktopMcp.Server.Tests/ContractTests.cs            (shared type info; PendingTool test moved out)
  tests/DesktopMcp.Server.Tests/ToolSpecCatalogTests.cs     (duplicate names)
  tests/DesktopMcp.Server.Tests/ToolCalls.cs                (invokes a tool through SpecTool)
  tests/DesktopMcp.Server.Tests/Tools/{PendingToolTests,WaitToolTests,PowerShellToolTests,FileSystemToolTests,RegistryToolTests,ProcessToolTests,ClipboardToolTests,NotificationToolTests,ScrapeToolTests}.cs
  tests/DesktopMcp.Server.Tests/Cli/CliAppTests.cs
  tests/DesktopMcp.Desktop.Tests/ClipboardTests.cs          (Category=Desktop: changes the user's clipboard, then restores it)
  tests/DesktopMcp.Desktop.Tests/ToastSenderTests.cs        (one Category=Desktop test shows a toast)
docs/superpowers/specs/2026-10-19-windows-port-design.md      (P1 results, Task 17)
docs/superpowers/plans/2026-10-19-windows-p0-followups.md     (P1 items closed, Task 17)
```

## Tasks

1. Build and smoke hardening
2. Test hygiene: version, contract type info, duplicate tool names
3. Core text helpers: Python formatting, tabulate table, fuzzy matching
4. Argument coercion, tool arguments and the Wait tool
5. Config file (Tomlyn)
6. ServePlan, IP allowlist and tool filter
7. CLI and stdio host
8. PowerShell
9. FileSystem
10. Registry
11. Process
12. Clipboard
13. Notification
14. Scrape: URL parsing and address rules
15. Scrape: HTML to Markdown
16. Scrape: pinned fetch and the Scrape tool
17. Release verification and documentation

---

### Task 1: Build and smoke hardening

Closes four P1 follow-ups: the automated AOT activation check, `AllowedReferenceRelatedFileExtensions` only for publishes, the full contract comparison in the smoke test, and keeping the timing-dependent dispatcher test out of the default run.

**Files:**
- Modify: `windows/Directory.Build.targets`
- Modify: `windows/scripts/build-release.ps1`
- Replace: `windows/scripts/smoke-stdio.ps1`

**Interfaces:**
- Consumes: the probe's `imaging` command (`probes/DesktopMcp.Probe/Program.cs`, exit code 0 on success, 1 when Win2D's DLL loaded under AOT or rendering failed).
- Produces: `smoke-stdio.ps1 -Exe <path> [-Spec <path>]`, which later tasks extend. It records the raw JSON line of the last response in `$script:lastLine`.

- [ ] **Step 1: Confirm the default test run skips desktop tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --logger "console;verbosity=normal"`
Expected: `Passed!` with `Failed: 0`. The listed test names are only `Win2DStaticActivationTests.*`; no `DispatcherQueueThreadTests` line appears (that class carries `[Trait("Category", "Desktop")]`, and `tests/default.runsettings` filters `Category!=Desktop`). If a `DispatcherQueueThreadTests` test runs, stop and add the trait.

- [ ] **Step 2: Show that a JIT build drops the library pdbs today**

Run:
```powershell
dotnet build src/DesktopMcp -c Debug
Test-Path src/DesktopMcp/bin/Debug/net10.0-windows10.0.22621.0/win-x64/DesktopMcp.Core.pdb
```
Expected: the build succeeds and `Test-Path` prints `False` (on an arm64 host the folder is `win-arm64`).

- [ ] **Step 3: Restrict the related-file list to publishes**

In `windows/Directory.Build.targets` replace the second comment and `PropertyGroup` with:

```xml
  <!--
    NativeAOT exes ship one file (plus its pdb). When publishing, referenced projects are compiled into the exe, so
    their .pdb/.pri must not be copied next to it: keep only .xml as a related file (the property cannot be empty,
    which would restore the default) and do not publish referenced documentation files either. dotnet build/run keep
    the default list, so the JIT exe has the Core/Desktop/Server pdbs and stack traces keep their line numbers.
    The .NET SDK sets _IsPublishing for dotnet publish.
  -->
  <PropertyGroup Condition="'$(PublishAot)' == 'true' and '$(_IsPublishing)' == 'true'">
    <AllowedReferenceRelatedFileExtensions>.xml</AllowedReferenceRelatedFileExtensions>
    <PublishReferencesDocumentationFiles>false</PublishReferencesDocumentationFiles>
  </PropertyGroup>
```

- [ ] **Step 4: Verify the JIT build keeps the pdbs**

Run the two commands from Step 2 again.
Expected: `True`.

- [ ] **Step 5: Add the AOT activation check to the release script**

Append to `windows/scripts/build-release.ps1` (after the existing smoke block):

```powershell
# NativeAOT activation check. xUnit runs under the JIT, so only an AOT run proves that the statically linked Win2D
# activates. The probe's imaging step renders through Win2D and fails if Microsoft.Graphics.Canvas.dll loads.
# It needs an x64 host and the Windows App SDK runtime (a developer machine).
if ($Runtimes -contains 'win-x64' -and [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'X64') {
    $probeOut = Join-Path $root 'probes/out'
    if (Test-Path $probeOut) { Remove-Item $probeOut -Recurse -Force }
    dotnet publish (Join-Path $root 'probes/DesktopMcp.Probe/DesktopMcp.Probe.csproj') -c Release -r win-x64 -o $probeOut
    if ($LASTEXITCODE -ne 0) { throw 'probe publish failed' }
    $probeFiles = @(Get-ChildItem $probeOut -Recurse -Force | ForEach-Object { [IO.Path]::GetRelativePath($probeOut, $_.FullName) })
    $probeExtra = @($probeFiles | Where-Object { $_ -notin 'desktop-mcp-probe.exe', 'desktop-mcp-probe.pdb' })
    if ($probeExtra.Count -gt 0) { throw "probes/out must contain only desktop-mcp-probe.exe and its pdb; extra: [$($probeExtra -join ', ')]" }
    $png = Join-Path ([IO.Path]::GetTempPath()) "desktop-mcp-activation-$PID.png"
    try {
        & (Join-Path $probeOut 'desktop-mcp-probe.exe') imaging $png
        if ($LASTEXITCODE -ne 0) { throw "AOT activation check failed: desktop-mcp-probe imaging exited with $LASTEXITCODE" }
        Write-Host 'AOT activation check OK: the NativeAOT probe activated the statically linked Win2D'
    }
    finally { Remove-Item $png -ErrorAction SilentlyContinue }
}
```

- [ ] **Step 6: Compare the whole tool list with the contract in the smoke test**

Replace `windows/scripts/smoke-stdio.ps1` with:

```powershell
#Requires -Version 7
# Starts desktop-mcp.exe over stdio and checks initialize, that tools/list equals spec/windows/tools.json, one tool
# call, and that this headless run did not load the (delay-loaded) graphics stack: d2d1, d3d11, dwrite, d3dcompiler_47.
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Spec = (Join-Path $PSScriptRoot '../../spec/windows/tools.json')
)
$ErrorActionPreference = 'Stop'

$utf8 = [System.Text.UTF8Encoding]::new($false)
$psi = [System.Diagnostics.ProcessStartInfo]::new((Resolve-Path $Exe).Path)
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardInputEncoding = $utf8
$psi.StandardOutputEncoding = $utf8
$psi.UseShellExecute = $false
$psi.Environment['ANONYMIZED_TELEMETRY'] = 'false'
$process = [System.Diagnostics.Process]::Start($psi)
# Drain stderr from the start so a chatty server never blocks on a full pipe.
$stderrTask = $process.StandardError.ReadToEndAsync()
$script:lastLine = $null

function Send($message) {
    $process.StandardInput.WriteLine(($message | ConvertTo-Json -Compress -Depth 20))
    $process.StandardInput.Flush()
}
function Receive([int]$ExpectedId) {
    while ($true) {
        $line = $process.StandardOutput.ReadLineAsync()
        if (-not $line.Wait(15000)) { throw "no response within 15 s (expected id $ExpectedId)" }
        if ($null -eq $line.Result) {
            $process.WaitForExit(2000) | Out-Null
            $text = if ($stderrTask.Wait(2000)) { $stderrTask.Result } else { '(stderr still open)' }
            throw "server exited: $text"
        }
        $message = $line.Result | ConvertFrom-Json -Depth 50
        if ($null -eq $message.PSObject.Properties['id']) { continue } # notification
        if ($message.id -ne $ExpectedId) { throw "expected response id $ExpectedId, got $($message.id)" }
        $script:lastLine = $line.Result
        return $message
    }
}
# tools/list must equal the contract file as JSON values; the MCP SDK owns the serialisation, so not as bytes.
function Assert-ToolsMatchSpec([string]$ResponseLine) {
    $actual = @{}
    foreach ($tool in [System.Text.Json.Nodes.JsonNode]::Parse($ResponseLine)['result']['tools'].AsArray()) { $actual[$tool['name'].ToString()] = $tool }
    $expected = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText((Resolve-Path $Spec).Path))['tools'].AsArray()
    if ($actual.Count -ne $expected.Count) { throw "tools/list has $($actual.Count) tools; spec/windows/tools.json has $($expected.Count)" }
    foreach ($tool in $expected) {
        $name = $tool['name'].ToString()
        if (-not $actual.ContainsKey($name)) { throw "tools/list lacks $name" }
        if (-not [System.Text.Json.Nodes.JsonNode]::DeepEquals($tool, $actual[$name])) {
            throw "$name differs from spec/windows/tools.json`nspec:   $($tool.ToJsonString())`nserver: $($actual[$name].ToJsonString())"
        }
    }
}

try {
    Send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'smoke'; version = '1' } } }
    $init = Receive 1
    if ($init.result.serverInfo.name -ne 'desktop-mcp') { throw "unexpected serverInfo: $($init | ConvertTo-Json -Depth 5)" }
    $expectedInstructions = "Windows MCP server provides tools to interact directly with the Windows desktop,`nthus enabling to operate the desktop on the user's behalf."
    if ($init.result.instructions -cne $expectedInstructions) { throw "unexpected instructions: $($init.result.instructions)" }
    Send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    Send @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
    $list = Receive 2
    Assert-ToolsMatchSpec $script:lastLine

    Send @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'Wait'; arguments = @{ duration = 0 } } }
    $call = Receive 3
    if ($null -eq $call.result) { throw "tools/call failed: $($call | ConvertTo-Json -Depth 5)" }

    # The graphics stack is delay-loaded (Win2DStatic.targets): a run that never renders must not load it.
    $process.Refresh()
    $graphics = @($process.Modules | Where-Object { $_.ModuleName -in 'd2d1.dll', 'd3d11.dll', 'dwrite.dll', 'd3dcompiler_47.dll' } | ForEach-Object ModuleName)
    if ($graphics.Count -gt 0) { throw "headless run loaded the graphics stack: $($graphics -join ', ')" }

    Write-Host "smoke OK: $($init.result.serverInfo.name) $($init.result.serverInfo.version), $($list.result.tools.Count) tools match spec/windows/tools.json, graphics stack not loaded ($($process.Modules.Count) modules)"
}
finally {
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(5000)) { $process.Kill($true); throw 'server did not exit after stdin closed' }
}
```

- [ ] **Step 7: Run the release script for x64**

Run: `pwsh scripts/build-release.ps1 -Runtimes win-x64`
Expected, in order: `win-x64: <size> MB, folder OK (desktop-mcp.exe, desktop-mcp.pdb)`, then `smoke OK: desktop-mcp 0.1.0, 21 tools match spec/windows/tools.json, graphics stack not loaded (<n> modules)`, then the probe's `imaging: ... nativeAot=True, Microsoft.Graphics.Canvas.dll loaded=False` lines and `AOT activation check OK: the NativeAOT probe activated the statically linked Win2D`. Exit code 0.

- [ ] **Step 8: Commit**

```bash
git add windows/Directory.Build.targets windows/scripts/build-release.ps1 windows/scripts/smoke-stdio.ps1
git commit -m "Check AOT Win2D activation in the release script and compare tools/list with the contract" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Test hygiene: version, contract type info, duplicate tool names

Closes three P1 follow-ups: `ServerTextTests` no longer hardcodes `0.1.0`, `ToolSpecCatalog` and `ContractTests` share one `JsonTypeInfo<Tool>`, and a duplicate tool name in the contract gives a clear error (needed before `--tools` filtering in Task 6).

**Files:**
- Modify: `windows/tests/DesktopMcp.Core.Tests/DesktopMcp.Core.Tests.csproj`
- Modify: `windows/tests/DesktopMcp.Core.Tests/ServerTextTests.cs`
- Modify: `windows/src/DesktopMcp.Server/Tools/ToolSpecCatalog.cs`
- Modify: `windows/tests/DesktopMcp.Server.Tests/ContractTests.cs`
- Modify: `windows/tests/DesktopMcp.Server.Tests/ToolSpecCatalogTests.cs`

**Interfaces:**
- Produces: `internal static JsonTypeInfo<Tool> ToolSpecCatalog.ToolTypeInfo`; `ToolSpecCatalog.Parse` throws `InvalidDataException("The tool contract lists '<name>' more than once.")`.

- [ ] **Step 1: Write the failing tests**

Replace `VersionHasNoBuildMetadata` in `windows/tests/DesktopMcp.Core.Tests/ServerTextTests.cs` with the test below and add `using System.Reflection;` at the top:

```csharp
    [Fact]
    public void VersionIsTheBuildVersionWithoutMetadata()
    {
        var expected = typeof(ServerTextTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "ExpectedVersion").Value;
        Assert.False(string.IsNullOrEmpty(expected));
        Assert.Equal(expected, ServerText.Version);
    }
```

Add to `windows/tests/DesktopMcp.Server.Tests/ToolSpecCatalogTests.cs`:

```csharp
    [Fact]
    public void DuplicateToolNamesAreRejected()
    {
        const string duplicated = """{"tools":[{"name":"Wait","inputSchema":{"type":"object"}},{"name":"Wait","inputSchema":{"type":"object"}}]}""";
        var error = Assert.Throws<InvalidDataException>(() => ToolSpecCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(duplicated))));
        Assert.Equal("The tool contract lists 'Wait' more than once.", error.Message);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~ServerTextTests"`
Expected: FAIL in `VersionIsTheBuildVersionWithoutMetadata` with `System.InvalidOperationException : Sequence contains no matching element`.

Run: `dotnet test tests/DesktopMcp.Server.Tests --filter "FullyQualifiedName~ToolSpecCatalogTests"`
Expected: FAIL in `DuplicateToolNamesAreRejected`: `Assert.Throws() Failure`, expected `InvalidDataException`, actual `ArgumentException` (from `ToDictionary`).

- [ ] **Step 3: Pass the build's Version to the Core tests**

Add to `windows/tests/DesktopMcp.Core.Tests/DesktopMcp.Core.Tests.csproj`:

```xml
  <ItemGroup>
    <!-- ServerTextTests compares ServerText.Version with the build's Version property. -->
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>ExpectedVersion</_Parameter1>
      <_Parameter2>$(Version)</_Parameter2>
    </AssemblyAttribute>
  </ItemGroup>
```

- [ ] **Step 4: Share the type info and reject duplicates**

Replace `windows/src/DesktopMcp.Server/Tools/ToolSpecCatalog.cs` with:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DesktopMcp.Server.Tools;

/// <summary>The tool contract (spec/windows/tools.json) as protocol <see cref="Tool"/> objects.</summary>
public sealed class ToolSpecCatalog
{
    private const string ResourceName = "DesktopMcp.Server.tools.json";
    private readonly Dictionary<string, Tool> _tools = new(StringComparer.Ordinal);

    private ToolSpecCatalog(List<Tool> tools)
    {
        foreach (var tool in tools)
        {
            if (!_tools.TryAdd(tool.Name, tool))
                throw new InvalidDataException($"The tool contract lists '{tool.Name}' more than once.");
        }
        Names = tools.Select(t => t.Name).ToArray();
    }

    /// <summary>The SDK's serialisation contract for <see cref="Tool"/>; ContractTests serialises with the same instance.</summary>
    internal static JsonTypeInfo<Tool> ToolTypeInfo { get; } =
        (JsonTypeInfo<Tool>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool));

    public IReadOnlyList<string> Names { get; }

    public Tool Get(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool : throw new KeyNotFoundException($"Tool '{name}' is not in the contract.");

    public static ToolSpecCatalog LoadEmbedded()
    {
        using var stream = typeof(ToolSpecCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        return Parse(stream);
    }

    public static ToolSpecCatalog Parse(Stream utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var tools = new List<Tool>();
        foreach (var element in document.RootElement.GetProperty("tools").EnumerateArray())
        {
            tools.Add(element.Deserialize(ToolTypeInfo) ?? throw new JsonException("null tool entry"));
        }
        return new ToolSpecCatalog(tools);
    }
}
```

In `windows/tests/DesktopMcp.Server.Tests/ContractTests.cs`, in `RegisteredToolsMatchTheContract`, replace the two lines that build `typeInfo` and `actual` with:

```csharp
        var actual = tools.ToDictionary(t => t.ProtocolTool.Name, t => JsonSerializer.SerializeToNode(t.ProtocolTool, ToolSpecCatalog.ToolTypeInfo)!);
```

and remove the now-unused `using ModelContextProtocol;`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each test project.

- [ ] **Step 6: Commit**

```bash
git add windows/tests/DesktopMcp.Core.Tests windows/src/DesktopMcp.Server/Tools/ToolSpecCatalog.cs windows/tests/DesktopMcp.Server.Tests
git commit -m "Derive the expected version from the build, share the Tool type info and reject duplicate tool names" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Core text helpers: Python formatting, tabulate table, fuzzy matching

Upstream's output depends on Python's number formatting (`f"{n:,}"`, `f"{x:.1f}"`, which round exact binary ties to even), `repr()`, `shlex.join`, tabulate 0.10's `simple` table (`process/service.py:49-54`) and thefuzz `partial_ratio` (`process/service.py:39`, rapidfuzz 3.14 underneath). These helpers reproduce them deterministically. The expected values in the tests were produced with CPython (`f"{x:.1f}"`, `format(x, "g")`, `shlex.join`, `repr`).

**Files:**
- Create: `windows/src/DesktopMcp.Core/Text/PyText.cs`
- Create: `windows/src/DesktopMcp.Core/Text/TableFormatter.cs`
- Create: `windows/src/DesktopMcp.Core/Text/Fuzzy.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Text/PyTextTests.cs`, `TableFormatterTests.cs`, `FuzzyTests.cs`

**Interfaces:**
- Produces (namespace `DesktopMcp.Core.Text`):
  - `public static class PyText`: `string Thousands(long value)`, `string Fixed(double value, int decimals)`, `string FormatG(double value)`, `string Repr(string value)`, `string ShellQuote(string value)`, `string ShellJoin(IEnumerable<string> args)`, `int CodePointLength(string value)`, `string Prefix(string value, int codePoints)`
  - `public static class TableFormatter`: `string Simple(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)`
  - `public static class Fuzzy`: `int PartialRatio(string s1, string s2)`

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Core.Tests/Text/PyTextTests.cs`:

```csharp
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Tests;

public class PyTextTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(999L, "999")]
    [InlineData(1234567L, "1,234,567")]
    [InlineData(-1234L, "-1,234")]
    public void ThousandsMatchesPythonCommaFormat(long value, string expected) => Assert.Equal(expected, PyText.Thousands(value));

    [Theory]
    [InlineData(0.25, 1, "0.2")]
    [InlineData(0.35, 1, "0.3")]
    [InlineData(0.45, 1, "0.5")]
    [InlineData(2.675, 2, "2.67")]
    [InlineData(1.05, 1, "1.1")]
    [InlineData(300.45, 1, "300.4")]
    [InlineData(0.15, 1, "0.1")]
    [InlineData(12.25, 1, "12.2")]
    [InlineData(12.25, 2, "12.25")]
    [InlineData(2.5, 0, "2")]
    [InlineData(3.5, 0, "4")]
    [InlineData(-0.25, 1, "-0.2")]
    [InlineData(1024.0, 1, "1024.0")]
    public void FixedRoundsTheExactBinaryValueHalfToEven(double value, int decimals, string expected) =>
        Assert.Equal(expected, PyText.Fixed(value, decimals));

    [Theory]
    [InlineData(1.5, "1.5")]
    [InlineData(10.0, "10")]
    [InlineData(2.25, "2.25")]
    [InlineData(1e6, "1e+06")]
    [InlineData(123456.0, "123456")]
    [InlineData(1234567.0, "1.23457e+06")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(-0.0, "-0")]
    [InlineData(3.14159265, "3.14159")]
    public void FormatGMatchesPythonGeneralFormat(double value, string expected) => Assert.Equal(expected, PyText.FormatG(value));

    [Theory]
    [InlineData("it's", "\"it's\"")]
    [InlineData("a\"b", "'a\"b'")]
    [InlineData(@"x\y", @"'x\\y'")]
    [InlineData("tab\t", @"'tab\t'")]
    [InlineData("plain", "'plain'")]
    public void ReprMatchesPython(string value, string expected) => Assert.Equal(expected, PyText.Repr(value));

    [Fact]
    public void ShellJoinMatchesShlex() =>
        Assert.Equal("desktop-mcp serve --port 9000 'a b' 'it'\"'\"'s' ''",
            PyText.ShellJoin(["desktop-mcp", "serve", "--port", "9000", "a b", "it's", ""]));

    [Fact]
    public void PrefixCountsCodePoints()
    {
        var text = "a\U0001F437b";   // the pig is a surrogate pair
        Assert.Equal(3, PyText.CodePointLength(text));
        Assert.Equal("a\U0001F437", PyText.Prefix(text, 2));
        Assert.Equal("a", PyText.Prefix(text, 1));
        Assert.Equal(text, PyText.Prefix(text, 10));
    }
}
```

`windows/tests/DesktopMcp.Core.Tests/Text/TableFormatterTests.cs`:

```csharp
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Tests;

public class TableFormatterTests
{
    [Fact]
    public void NumbersAlignRightTextAlignsLeftAndLinesAreRightStripped()
    {
        var table = TableFormatter.Simple(
            ["PID", "Name", "CPU%", "Memory"],
            [["4", "System", "0.0%", "0.1 MB"], ["1234", "chrome.exe", "12.5%", "300.5 MB"]]);
        Assert.Equal(
            "  PID  Name        CPU%    Memory\n" +
            "-----  ----------  ------  --------\n" +
            "    4  System      0.0%    0.1 MB\n" +
            " 1234  chrome.exe  12.5%   300.5 MB",
            table);
    }

    [Fact]
    public void FloatColumnsAlignOnTheDecimalPoint()
    {
        var table = TableFormatter.Simple(["x"], [["1.5"], ["10"], ["2.25"]]);
        Assert.Equal("    x\n-----\n 1.5\n10\n 2.25", table);
    }

    [Fact]
    public void IntegerCellsAreNormalisedAndCellsTrimmed()
    {
        var table = TableFormatter.Simple(["n", "s"], [["007", " left "]]);
        Assert.Equal("  n  s\n---  ----\n  7  left", table);
    }

    [Fact]
    public void HeadersOnly()
    {
        Assert.Equal("Name\n------", TableFormatter.Simple(["Name"], []));
    }
}
```

`windows/tests/DesktopMcp.Core.Tests/Text/FuzzyTests.cs`:

```csharp
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Tests;

public class FuzzyTests
{
    [Theory]
    [InlineData("chrome", "chrome.exe", 100)]
    [InlineData("note", "notepad.exe", 100)]
    [InlineData("chorme", "chrome.exe", 83)]
    [InlineData("abc", "xyz", 0)]
    [InlineData("this is a test", "this is a test!", 100)]
    [InlineData("abcd", "dcba", 40)]
    [InlineData("ab", "", 0)]
    [InlineData("", "", 100)]
    public void PartialRatioMatchesRapidfuzz(string s1, string s2, int expected) => Assert.Equal(expected, Fuzzy.PartialRatio(s1, s2));

    [Fact]
    public void PartialRatioIsSymmetricInTheShorterString() =>
        Assert.Equal(Fuzzy.PartialRatio("chrome.exe", "chorme"), Fuzzy.PartialRatio("chorme", "chrome.exe"));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests`
Expected: build FAILS with `error CS0234: The type or namespace name 'Text' does not exist in the namespace 'DesktopMcp.Core'`.

- [ ] **Step 3: Implement PyText**

`windows/src/DesktopMcp.Core/Text/PyText.cs`:

```csharp
using System.Globalization;
using System.Numerics;
using System.Text;

namespace DesktopMcp.Core.Text;

/// <summary>
/// Python's formatting rules that upstream's output strings depend on. Python formats the exact binary value of a
/// double and rounds exact ties half to even; .NET rounds ties away from zero, so the rounding is done here on the
/// exact decimal expansion.
/// </summary>
public static class PyText
{
    /// <summary>Python's <c>f"{n:,}"</c>.</summary>
    public static string Thousands(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>Python's <c>f"{x:.Nf}"</c>.</summary>
    public static string Fixed(double value, int decimals)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        var (digits, scale) = Exact(value);
        var rounded = RoundToDecimals(digits, scale, decimals).ToString(CultureInfo.InvariantCulture);
        rounded = rounded.PadLeft(decimals + 1, '0');
        var text = decimals == 0 ? rounded : rounded[..^decimals] + "." + rounded[^decimals..];
        return double.IsNegative(value) ? "-" + text : text;
    }

    /// <summary>Python's <c>format(x, "g")</c>: six significant digits, trailing zeros removed.</summary>
    public static string FormatG(double value)
    {
        const int precision = 6;
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        var sign = double.IsNegative(value) ? "-" : "";
        if (value == 0) return sign + "0";
        var (digits, scale) = Exact(value);
        var exponent = digits.Length - 1 - scale;
        var significant = RoundToDecimals(digits, scale, precision - 1 - exponent);
        if (significant.ToString(CultureInfo.InvariantCulture).Length > precision)
        {
            exponent++;
            significant /= 10;
        }
        if (exponent >= -4 && exponent < precision)
            return StripZeros(Fixed(value, precision - 1 - exponent));
        var mantissa = significant.ToString(CultureInfo.InvariantCulture);
        var text = StripZeros(mantissa[..1] + "." + mantissa[1..]);
        return $"{sign}{text}e{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent):00}";
    }

    /// <summary>Python's <c>repr(str)</c> for the strings our messages quote.</summary>
    public static string Repr(string value)
    {
        var quote = value.Contains('\'') && !value.Contains('"') ? '"' : '\'';
        var builder = new StringBuilder().Append(quote);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append(@"\\"); break;
                case '\n': builder.Append(@"\n"); break;
                case '\r': builder.Append(@"\r"); break;
                case '\t': builder.Append(@"\t"); break;
                default:
                    if (c == quote) builder.Append('\\').Append(c);
                    else if (c < 0x20 || c == 0x7f) builder.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:x2}");
                    else builder.Append(c);
                    break;
            }
        }
        return builder.Append(quote).ToString();
    }

    /// <summary>Python's <c>shlex.quote</c>.</summary>
    public static string ShellQuote(string value)
    {
        if (value.Length == 0) return "''";
        if (value.All(c => char.IsAsciiLetterOrDigit(c) || "@%+=:,./-_".Contains(c))) return value;
        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    /// <summary>Python's <c>shlex.join</c>.</summary>
    public static string ShellJoin(IEnumerable<string> args) => string.Join(' ', args.Select(ShellQuote));

    /// <summary>Python's <c>len(str)</c>: code points, so a surrogate pair counts once.</summary>
    public static int CodePointLength(string value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++, count++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
        }
        return count;
    }

    /// <summary>Python's <c>value[:codePoints]</c>.</summary>
    public static string Prefix(string value, int codePoints)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++, count++)
        {
            if (count == codePoints) return value[..i];
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
        }
        return value;
    }

    private static string StripZeros(string text) =>
        text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;

    // |value| == digits * 10^-scale, exactly.
    private static (string Digits, int Scale) Exact(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent = 1;
        else mantissa |= 1L << 52;
        exponent -= 1075;
        if (exponent >= 0)
            return ((new BigInteger(mantissa) << exponent).ToString(CultureInfo.InvariantCulture), 0);
        // mantissa / 2^e == mantissa * 5^e / 10^e
        return ((new BigInteger(mantissa) * BigInteger.Pow(5, -exponent)).ToString(CultureInfo.InvariantCulture), -exponent);
    }

    // digits * 10^-scale rounded half to even to `decimals` places, as an integer count of 10^-decimals.
    private static BigInteger RoundToDecimals(string digits, int scale, int decimals)
    {
        var number = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        if (scale <= decimals) return number * BigInteger.Pow(10, decimals - scale);
        var divisor = BigInteger.Pow(10, scale - decimals);
        var quotient = BigInteger.DivRem(number, divisor, out var remainder);
        var twice = remainder * 2;
        if (twice > divisor || (twice == divisor && !quotient.IsEven)) quotient += 1;
        return quotient;
    }
}
```

- [ ] **Step 4: Implement the table formatter**

`windows/src/DesktopMcp.Core/Text/TableFormatter.cs`:

```csharp
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace DesktopMcp.Core.Text;

/// <summary>
/// tabulate 0.10's "simple" format with headers (spec §6: a deterministic implementation). A column whose non-empty
/// cells all parse as integers is an int column (cells re-printed as integers, right-aligned); one whose cells all
/// parse as numbers is a float column (cells printed with Python's "g" format, aligned on the decimal point);
/// anything else is text (left-aligned). Each column is at least two characters wider than its header, columns are
/// separated by two spaces, widths count code points, and every line is right-stripped.
/// </summary>
public static partial class TableFormatter
{
    private const int MinPadding = 2;

    public static string Simple(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var columns = new List<(string[] Cells, bool Numeric, int Width)>();
        for (var c = 0; c < headers.Count; c++)
        {
            var cells = rows.Select(r => c < r.Count ? r[c].Trim() : "").ToArray();
            var kind = ColumnKind(cells);
            if (kind != Kind.Text) cells = AlignDecimal(cells.Select(s => Format(s, kind)).ToArray());
            var width = Math.Max(PyText.CodePointLength(headers[c]) + MinPadding, cells.Length == 0 ? 0 : cells.Max(PyText.CodePointLength));
            columns.Add((cells, kind != Kind.Text, width));
        }

        var lines = new List<string>
        {
            Join(headers.Select((h, c) => Pad(h, columns[c].Width, columns[c].Numeric))),
            Join(columns.Select(col => new string('-', col.Width))),
        };
        for (var r = 0; r < rows.Count; r++)
            lines.Add(Join(columns.Select(col => Pad(col.Cells[r], col.Width, col.Numeric))));
        return string.Join("\n", lines);
    }

    private enum Kind { Text, Int, Float }

    private static Kind ColumnKind(string[] cells)
    {
        var present = cells.Where(s => s.Length > 0).ToArray();
        if (present.Length == 0) return Kind.Text;
        if (present.All(IsInt)) return Kind.Int;
        return present.All(IsNumber) ? Kind.Float : Kind.Text;
    }

    private static bool IsInt(string s) => IntPattern().IsMatch(s);

    private static bool IsNumber(string s) =>
        IsInt(s) || s.ToLowerInvariant() is "inf" or "+inf" or "-inf" or "infinity" or "+infinity" or "-infinity" or "nan" or "+nan" or "-nan"
        || (FloatPattern().IsMatch(s) && double.TryParse(s.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out _));

    private static string Format(string cell, Kind kind)
    {
        if (cell.Length == 0) return cell;
        if (kind == Kind.Int) return BigInteger.Parse(cell.Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        var lower = cell.ToLowerInvariant().TrimStart('+');
        var value = lower switch
        {
            "inf" or "infinity" => double.PositiveInfinity,
            "-inf" or "-infinity" => double.NegativeInfinity,
            "nan" or "-nan" => double.NaN,
            _ => double.Parse(cell.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture),
        };
        return PyText.FormatG(value);
    }

    // tabulate's _afterpoint: digits after the decimal point (or after the exponent marker), -1 for integers.
    private static string[] AlignDecimal(string[] cells)
    {
        int AfterPoint(string s)
        {
            if (s.Length == 0 || IsInt(s)) return -1;
            var point = s.LastIndexOf('.');
            if (point < 0) point = s.ToLowerInvariant().LastIndexOf('e');
            return point < 0 ? -1 : s.Length - point - 1;
        }
        var after = cells.Select(AfterPoint).ToArray();
        var max = after.Length == 0 ? -1 : after.Max();
        return cells.Select((s, i) => s.Length == 0 ? s : s + new string(' ', max - after[i])).ToArray();
    }

    private static string Pad(string text, int width, bool right)
    {
        var fill = new string(' ', Math.Max(0, width - PyText.CodePointLength(text)));
        return right ? fill + text : text + fill;
    }

    private static string Join(IEnumerable<string> cells) => string.Join("  ", cells).TrimEnd();

    [GeneratedRegex(@"^[+-]?[0-9]+(_[0-9]+)*$")]
    private static partial Regex IntPattern();

    [GeneratedRegex(@"^[+-]?([0-9]+(_[0-9]+)*)?(\.[0-9]*)?([eE][+-]?[0-9]+)?$")]
    private static partial Regex FloatPattern();
}
```

- [ ] **Step 5: Implement partial_ratio**

`windows/src/DesktopMcp.Core/Text/Fuzzy.cs`:

```csharp
namespace DesktopMcp.Core.Text;

/// <summary>
/// thefuzz 0.22's <c>fuzz.partial_ratio</c> (rapidfuzz 3.14 underneath): the best <c>ratio</c> between the shorter
/// string and every window of the longer one, including the partial windows at both ends, rounded half to even.
/// <c>ratio</c> is the normalised Indel similarity, 200 * LCS / (len1 + len2).
/// </summary>
public static class Fuzzy
{
    public static int PartialRatio(string s1, string s2)
    {
        if (s1.Length == 0 || s2.Length == 0) return s1.Length == s2.Length ? 100 : 0;
        var score = s1.Length <= s2.Length ? Best(s1, s2) : Best(s2, s1);
        if (s1.Length == s2.Length) score = Math.Max(score, Best(s2, s1));
        return (int)Math.Round(score, MidpointRounding.ToEven);
    }

    private static double Best(string needle, string haystack)
    {
        int n = needle.Length, m = haystack.Length;
        var best = 0.0;
        for (var i = 1; i < n; i++) best = Math.Max(best, Ratio(needle, haystack.AsSpan(0, i)));
        for (var i = 0; i <= m - n; i++) best = Math.Max(best, Ratio(needle, haystack.AsSpan(i, n)));
        for (var i = m - n + 1; i < m; i++) best = Math.Max(best, Ratio(needle, haystack.AsSpan(i)));
        return best;
    }

    private static double Ratio(ReadOnlySpan<char> a, ReadOnlySpan<char> b) =>
        200.0 * LongestCommonSubsequence(a, b) / (a.Length + b.Length);

    private static int LongestCommonSubsequence(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
                current[j] = a[i - 1] == b[j - 1] ? previous[j - 1] + 1 : Math.Max(previous[j], current[j - 1]);
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~PyTextTests|FullyQualifiedName~TableFormatterTests|FullyQualifiedName~FuzzyTests"`
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add windows/src/DesktopMcp.Core/Text windows/tests/DesktopMcp.Core.Tests/Text
git commit -m "Add Python formatting, tabulate's simple table and partial_ratio to Core" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Argument coercion, tool arguments and the Wait tool

Establishes the pattern every later tool follows: Core holds the coercion rules (spec §4), `ToolArguments` reads a `tools/call` request with them, the tool is a `SpecTool` subclass chosen by name in `ToolRegistry`, and Server tests call it through `SpecTool.InvokeAsync`. Closes three P1 follow-ups: the `SpecTool` cancellation test, moving the `PendingTool` test out of `ContractTests`, and requiring `isError: false` for Wait in the smoke test.

Wait upstream (`tools/input.py:433-436`): `time.sleep(duration)` then `Waited for {duration} seconds.`; a negative duration raises Python's `sleep length must be non-negative`, and one too large for the OS timer raises `sleep length is too large`.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Text/Coercion.cs`
- Create: `windows/src/DesktopMcp.Server/Tools/ToolArguments.cs`, `ToolServices.cs`, `WaitTool.cs`
- Modify: `windows/src/DesktopMcp.Server/Tools/ToolRegistry.cs`
- Modify: `windows/tests/DesktopMcp.Server.Tests/ContractTests.cs` (remove `PendingToolReturnsAnErrorResult`)
- Modify: `windows/scripts/smoke-stdio.ps1`
- Test: `windows/tests/DesktopMcp.Core.Tests/Text/CoercionTests.cs`, `windows/tests/DesktopMcp.Server.Tests/ToolCalls.cs`, `windows/tests/DesktopMcp.Server.Tests/Tools/PendingToolTests.cs`, `windows/tests/DesktopMcp.Server.Tests/Tools/WaitToolTests.cs`

**Interfaces:**
- Consumes: `SpecTool(Tool)`, `ToolResults.Text(string)`, `ToolSpecCatalog`.
- Produces:
  - `DesktopMcp.Core.Text.Coercion`: `bool StrictBool(JsonElement value, string name)`, `bool LooseBool(JsonElement value)`, `long Integer(JsonElement value, string name)`, `string String(JsonElement value, string name)`, `JsonElement JsonList(JsonElement value, string name)`, `IReadOnlyList<string> StringList(JsonElement value, string name)`. All throw `ArgumentException` whose `Message` is the user-facing text.
  - `DesktopMcp.Server.Tools.ToolArguments` (internal): `From(RequestContext<CallToolRequestParams>)`, `JsonElement? Get(string)`, `RequiredString`, `OptionalString`, `String(name, default)`, `RequiredInteger`, `OptionalInteger`, `Integer(name, default)`, `StrictBool(name, default)`, `LooseBool(name, default)`.
  - `public sealed record ToolServices` with `static ToolServices CreateDefault()`; later tasks add one `required` property per service.
  - `ToolRegistry.CreateAll(ToolSpecCatalog catalog, ToolServices? services = null)` and `ToolRegistry.Create(Tool protocolTool, ToolServices services)`.
  - Test helper `ToolCalls`: `McpServerTool Create(string name, ToolServices? services = null)`, `Task<CallToolResult> CallAsync(McpServerTool tool, string argumentsJson, CancellationToken cancellationToken = default)`, `string Text(CallToolResult)`, `string OkText(CallToolResult)`.

- [ ] **Step 1: Write the failing Core tests**

`windows/tests/DesktopMcp.Core.Tests/Text/CoercionTests.cs`:

```csharp
using System.Text.Json;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Tests;

public class CoercionTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\" TRUE \"", true)]
    [InlineData("\"False\"", false)]
    public void StrictBoolAcceptsBooleansAndTheTwoLiterals(string json, bool expected) =>
        Assert.Equal(expected, Coercion.StrictBool(Json(json), "recursive"));

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void StrictBoolRejectsEverythingElse(string json)
    {
        var error = Assert.Throws<ArgumentException>(() => Coercion.StrictBool(Json(json), "recursive"));
        Assert.Equal("recursive must be true or false", error.Message);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("\"TRUE\"", true)]
    [InlineData("\"true\"", true)]
    [InlineData("\" true\"", false)]
    [InlineData("\"yes\"", false)]
    [InlineData("1", false)]
    [InlineData("false", false)]
    public void LooseBoolIsTrueOnlyForTrueOrTheStringTrue(string json, bool expected) =>
        Assert.Equal(expected, Coercion.LooseBool(Json(json)));

    [Theory]
    [InlineData("5", 5L)]
    [InlineData("-3", -3L)]
    [InlineData("5.0", 5L)]
    [InlineData("\" 42 \"", 42L)]
    [InlineData("\"+7\"", 7L)]
    public void IntegerAcceptsIntegralNumbersAndIntegerStrings(string json, long expected) =>
        Assert.Equal(expected, Coercion.Integer(Json(json), "duration"));

    [Theory]
    [InlineData("1.5")]
    [InlineData("\"1.5\"")]
    [InlineData("true")]
    [InlineData("\"abc\"")]
    [InlineData("[]")]
    public void IntegerRejectsTheRest(string json)
    {
        var error = Assert.Throws<ArgumentException>(() => Coercion.Integer(Json(json), "duration"));
        Assert.Equal("duration must be an integer", error.Message);
    }

    [Fact]
    public void StringRequiresAJsonString()
    {
        Assert.Equal("x", Coercion.String(Json("\"x\""), "command"));
        Assert.Equal("command must be a string", Assert.Throws<ArgumentException>(() => Coercion.String(Json("5"), "command")).Message);
    }

    [Fact]
    public void JsonListAcceptsAnArrayOrAJsonString()
    {
        Assert.Equal(JsonValueKind.Array, Coercion.JsonList(Json("[1,2]"), "loc").ValueKind);
        var parsed = Coercion.JsonList(Json("\"[100, 200]\""), "loc");
        Assert.Equal(2, parsed.GetArrayLength());
        Assert.Equal(200, parsed[1].GetInt32());
    }

    [Fact]
    public void JsonListReportsInvalidJson()
    {
        var error = Assert.Throws<ArgumentException>(() => Coercion.JsonList(Json("\"[1,\""), "loc"));
        Assert.StartsWith("loc is not valid JSON: ", error.Message);
    }

    [Fact]
    public void StringListAcceptsStringsOnly()
    {
        Assert.Equal(["a", "b"], Coercion.StringList(Json("\"[\\\"a\\\", \\\"b\\\"]\""), "args"));
        Assert.Equal("args must be a list of strings", Assert.Throws<ArgumentException>(() => Coercion.StringList(Json("[1]"), "args")).Message);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~CoercionTests"`
Expected: build FAILS with `error CS0103: The name 'Coercion' does not exist in the current context`.

- [ ] **Step 3: Implement Coercion**

`windows/src/DesktopMcp.Core/Text/Coercion.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopMcp.Core.Text;

/// <summary>
/// Upstream's argument coercion (spec §4). MCP clients that strip anyOf schemas send "true" for true and "[1, 2]" for a
/// list, so bool | str and list parameters accept those strings. Each method throws <see cref="ArgumentException"/>
/// whose message is the text the tool reports.
/// </summary>
public static partial class Coercion
{
    /// <summary>upstream desktop/utils.py:18-44 as_bool: a bool, or "true"/"false" after strip and casefold.</summary>
    public static bool StrictBool(JsonElement value, string name)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.String:
                var text = value.GetString()!.Trim().ToLowerInvariant();
                if (text == "true") return true;
                if (text == "false") return false;
                break;
        }
        throw new ArgumentException($"{name} must be true or false");
    }

    /// <summary>upstream tools/scrape.py:29 and tools/process.py:37: true, or a string whose lower case is "true".</summary>
    public static bool LooseBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => value.GetString()!.ToLowerInvariant() == "true",
        _ => false,
    };

    /// <summary>A JSON integer, an integral JSON number, or a string holding a signed decimal integer.</summary>
    public static long Integer(JsonElement value, string name)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var whole)) return whole;
                if (value.TryGetDouble(out var real) && real == Math.Floor(real) && Math.Abs(real) < 9.2e18) return (long)real;
                break;
            case JsonValueKind.String:
                var text = value.GetString()!.Trim();
                if (SignedInteger().IsMatch(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                break;
        }
        throw new ArgumentException($"{name} must be an integer");
    }

    public static string String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException($"{name} must be a string");

    /// <summary>upstream _as_loc / _as_region (tools/input.py:40-48): a list, or a string holding JSON.</summary>
    public static JsonElement JsonList(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Array) return value;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"{name} must be a list");
        try
        {
            using var document = JsonDocument.Parse(value.GetString()!);
            return document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"{name} is not valid JSON: {e.Message}");
        }
    }

    /// <summary>upstream _as_args (tools/app.py:13-22).</summary>
    public static IReadOnlyList<string> StringList(JsonElement value, string name)
    {
        var list = JsonList(value, name);
        if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new ArgumentException($"{name} must be a list of strings");
        return list.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    [GeneratedRegex("^[+-]?[0-9]+$")]
    private static partial Regex SignedInteger();
}
```

- [ ] **Step 4: Run the Core tests to verify they pass**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~CoercionTests"`
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 5: Write the failing Server tests**

`windows/tests/DesktopMcp.Server.Tests/ToolCalls.cs`:

```csharp
using System.Text.Json;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tests;

/// <summary>Calls a tool the way the SDK does, through <see cref="SpecTool"/>'s InvokeAsync.</summary>
internal static class ToolCalls
{
    public static McpServerTool Create(string name, ToolServices? services = null) =>
        ToolRegistry.Create(ToolSpecCatalog.LoadEmbedded().Get(name), services ?? ToolServices.CreateDefault());

    public static async Task<CallToolResult> CallAsync(McpServerTool tool, string argumentsJson, CancellationToken cancellationToken = default)
    {
        Dictionary<string, JsonElement> arguments;
        using (var document = JsonDocument.Parse(argumentsJson))
            arguments = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        await using var server = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), new McpServerOptions());
        var request = new RequestContext<CallToolRequestParams>(server, new JsonRpcRequest { Method = "tools/call" },
            new CallToolRequestParams { Name = tool.ProtocolTool.Name, Arguments = arguments });
        return await tool.InvokeAsync(request, cancellationToken);
    }

    public static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    /// <summary>The text of a result that must not be an error.</summary>
    public static string OkText(CallToolResult result)
    {
        var text = Text(result);
        Assert.False(result.IsError == true, text);
        return text;
    }
}
```

`windows/tests/DesktopMcp.Server.Tests/Tools/PendingToolTests.cs`:

```csharp
namespace DesktopMcp.Server.Tests;

public class PendingToolTests
{
    [Fact]
    public async Task PendingToolReturnsAnErrorResult()
    {
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Click"), "{}");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Click': not available in this build yet", ToolCalls.Text(result));
    }
}
```

`windows/tests/DesktopMcp.Server.Tests/Tools/WaitToolTests.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace DesktopMcp.Server.Tests;

public class WaitToolTests
{
    [Fact]
    public async Task WaitsAndReportsTheDuration()
    {
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Wait"), """{"duration": 0}""");
        Assert.Equal("Waited for 0 seconds.", ToolCalls.OkText(result));
        Assert.Equal("Waited for 0 seconds.", Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("result").GetString());
    }

    [Fact]
    public async Task AcceptsAnIntegerString() =>
        Assert.Equal("Waited for 1 seconds.", ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Wait"), """{"duration": "1"}""")));

    [Theory]
    [InlineData("""{"duration": -1}""", "Error calling tool 'Wait': sleep length must be non-negative")]
    [InlineData("""{"duration": 99999999999}""", "Error calling tool 'Wait': sleep length is too large")]
    [InlineData("""{"duration": 1.5}""", "Error calling tool 'Wait': duration must be an integer")]
    [InlineData("{}", "Error calling tool 'Wait': Missing required argument: duration")]
    public async Task InvalidDurationsAreErrorResults(string arguments, string expected)
    {
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Wait"), arguments);
        Assert.True(result.IsError);
        Assert.Equal(expected, ToolCalls.Text(result));
    }

    // SpecTool rethrows OperationCanceledException when the SDK's token is cancelled (P1 follow-up).
    [Fact]
    public async Task CancellationPropagatesThroughSpecTool()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ToolCalls.CallAsync(ToolCalls.Create("Wait"), """{"duration": 30}""", cancellation.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"cancellation took {clock.Elapsed}");
    }
}
```

In `windows/tests/DesktopMcp.Server.Tests/ContractTests.cs` delete the `PendingToolReturnsAnErrorResult` test and the `using ModelContextProtocol.Server;` line that only it used.

- [ ] **Step 6: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Server.Tests`
Expected: build FAILS with `error CS0246: The type or namespace name 'ToolServices' could not be found` and `error CS0117: 'ToolRegistry' does not contain a definition for 'Create'`.

- [ ] **Step 7: Implement ToolArguments, ToolServices, WaitTool and the registry**

`windows/src/DesktopMcp.Server/Tools/ToolArguments.cs`:

```csharp
using System.Text.Json;
using DesktopMcp.Core.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// Typed access to a tools/call request's arguments with upstream's coercion rules. A missing argument and JSON null
/// both mean "not given", so optional parameters take their defaults.
/// </summary>
internal sealed class ToolArguments(IDictionary<string, JsonElement> values)
{
    public static ToolArguments From(RequestContext<CallToolRequestParams> request) =>
        new(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());

    public JsonElement? Get(string name) =>
        values.TryGetValue(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value : null;

    public string RequiredString(string name) => Coercion.String(Get(name) ?? throw Missing(name), name);

    public string? OptionalString(string name) => Get(name) is { } value ? Coercion.String(value, name) : null;

    public string String(string name, string defaultValue) => OptionalString(name) ?? defaultValue;

    public long RequiredInteger(string name) => Coercion.Integer(Get(name) ?? throw Missing(name), name);

    public long? OptionalInteger(string name) => Get(name) is { } value ? Coercion.Integer(value, name) : null;

    public long Integer(string name, long defaultValue) => OptionalInteger(name) ?? defaultValue;

    public bool StrictBool(string name, bool defaultValue) => Get(name) is { } value ? Coercion.StrictBool(value, name) : defaultValue;

    public bool LooseBool(string name, bool defaultValue) => Get(name) is { } value ? Coercion.LooseBool(value) : defaultValue;

    private static ArgumentException Missing(string name) => new($"Missing required argument: {name}");
}
```

`windows/src/DesktopMcp.Server/Tools/ToolServices.cs`:

```csharp
namespace DesktopMcp.Server.Tools;

/// <summary>
/// The services the tools call. Tests replace the ones with side effects (clipboard, toasts) with fakes through
/// <c>with</c>. Creating the defaults touches nothing: no Windows App SDK, no desktop.
/// </summary>
public sealed record ToolServices
{
    public static ToolServices CreateDefault() => new();
}
```

`windows/src/DesktopMcp.Server/Tools/WaitTool.cs`:

```csharp
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/input.py:422-436.</summary>
public sealed class WaitTool(Tool protocolTool) : SpecTool(protocolTool)
{
    // Task.Delay accepts at most uint.MaxValue - 1 milliseconds.
    private const long MaxSeconds = (uint.MaxValue - 1L) / 1000;

    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var duration = ToolArguments.From(request).RequiredInteger("duration");
        if (duration < 0) throw new ArgumentException("sleep length must be non-negative");
        if (duration > MaxSeconds) throw new ArgumentException("sleep length is too large");
        await Task.Delay(TimeSpan.FromSeconds(duration), cancellationToken).ConfigureAwait(false);
        return ToolResults.Text($"Waited for {duration} seconds.");
    }
}
```

Replace `windows/src/DesktopMcp.Server/Tools/ToolRegistry.cs` with:

```csharp
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

public static class ToolRegistry
{
    /// <summary>One tool per contract entry, in contract order.</summary>
    public static IReadOnlyList<McpServerTool> CreateAll(ToolSpecCatalog catalog, ToolServices? services = null)
    {
        services ??= ToolServices.CreateDefault();
        return catalog.Names.Select(name => Create(catalog.Get(name), services)).ToArray();
    }

    /// <summary>The implementation for a contract entry; entries of later phases stay <see cref="PendingTool"/>.</summary>
    public static McpServerTool Create(Tool protocolTool, ToolServices services) => protocolTool.Name switch
    {
        "Wait" => new WaitTool(protocolTool),
        _ => new PendingTool(protocolTool),
    };
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each project, including `WaitToolTests`, `PendingToolTests` and `ContractTests`.

- [ ] **Step 9: Require a successful Wait in the smoke test**

In `windows/scripts/smoke-stdio.ps1` replace

```powershell
    if ($null -eq $call.result) { throw "tools/call failed: $($call | ConvertTo-Json -Depth 5)" }
```

with

```powershell
    if ($null -eq $call.result -or $call.result.isError) { throw "Wait failed: $($call | ConvertTo-Json -Depth 5)" }
    if ($call.result.content[0].text -cne 'Waited for 0 seconds.') { throw "unexpected Wait text: $($call.result.content[0].text)" }
```

Run: `pwsh scripts/build-release.ps1 -Runtimes win-x64`
Expected: `smoke OK: desktop-mcp 0.1.0, 21 tools match spec/windows/tools.json, graphics stack not loaded (...)` and `AOT activation check OK: ...`.

- [ ] **Step 10: Commit**

```bash
git add windows/src/DesktopMcp.Core/Text/Coercion.cs windows/src/DesktopMcp.Server/Tools windows/tests/DesktopMcp.Core.Tests/Text/CoercionTests.cs windows/tests/DesktopMcp.Server.Tests windows/scripts/smoke-stdio.ps1
git commit -m "Add argument coercion, typed tool arguments and the Wait tool" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Config file (Tomlyn)

`~/.desktop-mcp/config.toml` (or `--config`) with upstream's sections, defaults, strict types and messages (`infrastructure/config.py:22-212`). Tomlyn parses into an untyped `TomlTable` through a source-generated context; the rules and messages are applied by hand so they match upstream exactly. Reflection is switched off in every build, so the JIT tests exercise the same path as the AOT exe.

**Files:**
- Modify: `windows/Directory.Packages.props`, `windows/Directory.Build.props`, `windows/src/DesktopMcp.Core/DesktopMcp.Core.csproj`
- Create: `windows/src/DesktopMcp.Core/Config/ConfigException.cs`, `DesktopMcpConfig.cs`, `IdleTimeout.cs`, `ConfigTomlContext.cs`, `ConfigFile.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/TempDirectory.cs`, `windows/tests/DesktopMcp.Core.Tests/Config/ConfigFileTests.cs`

**Interfaces:**
- Consumes: `PyText.Repr`.
- Produces (namespace `DesktopMcp.Core.Config`):
  - `public sealed class ConfigException(string message) : Exception`
  - `DesktopMcpConfig { ServerConfig Server; SecurityConfig Security; ToolsConfig Tools; ControlConfig Control; string? SourcePath }` with `ServerConfig { string Transport; string Host; long Port; bool AllowInsecureRemote; string? AuthKey; string? SslCertfile; string? SslKeyfile; bool StatelessHttp }`, `SecurityConfig { List<string> IpAllowlist; List<string> CorsOrigins; string? OAuthClientId; string? OAuthClientSecret }`, `ToolsConfig { List<string> Exclude }`, `ControlConfig { long MouseTakeoverUnits; long MouseTakeoverPixels; bool Enabled; double IdleTimeoutSeconds; string Policy }`
  - `ConfigFile`: `DirectoryName`, `FileName`, `Transports`, `Policies`, `string DefaultPath(string home)`, `string? Discover(string? explicitPath, string home)`, `string ExpandUser(string path, string home)`, `DesktopMcpConfig Load(string? path)`, `DesktopMcpConfig Parse(string toml, string path)`
  - `IdleTimeout.Message`, `double IdleTimeout.Validate(object? raw)`
  - Test helper `TempDirectory : IDisposable` with `Path`, `Combine(params string[])`, `WriteFile(string relative, string content)`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Core.Tests/TempDirectory.cs`:

```csharp
namespace DesktopMcp.Core.Tests;

/// <summary>A fresh folder under %TEMP%, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dmcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteFile(string relative, string content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
```

`windows/tests/DesktopMcp.Core.Tests/Config/ConfigFileTests.cs`:

```csharp
using DesktopMcp.Core.Config;

namespace DesktopMcp.Core.Tests;

public sealed class ConfigFileTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private DesktopMcpConfig Load(string toml) => ConfigFile.Load(_temp.WriteFile("config.toml", toml));

    private string Error(string toml) => Assert.Throws<ConfigException>(() => Load(toml)).Message;

    [Fact]
    public void NoPathGivesUpstreamDefaults()
    {
        var config = ConfigFile.Load(null);
        Assert.Equal("stdio", config.Server.Transport);
        Assert.Equal("localhost", config.Server.Host);
        Assert.Equal(8000, config.Server.Port);
        Assert.False(config.Server.AllowInsecureRemote);
        Assert.Null(config.Server.AuthKey);
        Assert.False(config.Server.StatelessHttp);
        Assert.Empty(config.Security.IpAllowlist);
        Assert.Empty(config.Tools.Exclude);
        Assert.True(config.Control.Enabled);
        Assert.Equal(10.0, config.Control.IdleTimeoutSeconds);
        Assert.Equal("relaxed", config.Control.Policy);
        Assert.Equal(500, config.Control.MouseTakeoverUnits);
        Assert.Equal(500, config.Control.MouseTakeoverPixels);
        Assert.Null(config.SourcePath);
    }

    [Fact]
    public void MissingFileIsAnError()
    {
        var path = _temp.Combine("nope.toml");
        Assert.Equal($"Config file not found: {path}", Assert.Throws<ConfigException>(() => ConfigFile.Load(path)).Message);
    }

    [Fact]
    public void ReadsEverySection()
    {
        var config = Load("""
            [server]
            transport = "sse"
            host = "0.0.0.0"
            port = 9000
            allow_insecure_remote = true
            auth_key = "secret"
            ssl_certfile = "cert.pem"
            ssl_keyfile = "C:/keys/key.pem"
            stateless_http = true

            [security]
            ip_allowlist = ["10.0.0.0/8", "192.168.1.5"]
            cors_origins = ["https://a.example"]
            oauth_client_id = "id"
            oauth_client_secret = "shh"

            [tools]
            exclude = ["PowerShell", "Registry"]

            [control]
            policy = "strict"
            enabled = false
            idle_timeout_seconds = 2.5
            mouse_takeover_units = 40
            mouse_takeover_pixels = 2000
            """);
        Assert.Equal("sse", config.Server.Transport);
        Assert.Equal("0.0.0.0", config.Server.Host);
        Assert.Equal(9000, config.Server.Port);
        Assert.True(config.Server.AllowInsecureRemote);
        Assert.Equal("secret", config.Server.AuthKey);
        Assert.Equal(_temp.Combine("cert.pem"), config.Server.SslCertfile);
        Assert.Equal(@"C:\keys\key.pem", config.Server.SslKeyfile);
        Assert.True(config.Server.StatelessHttp);
        Assert.Equal(["10.0.0.0/8", "192.168.1.5"], config.Security.IpAllowlist);
        Assert.Equal(["https://a.example"], config.Security.CorsOrigins);
        Assert.Equal("id", config.Security.OAuthClientId);
        Assert.Equal("shh", config.Security.OAuthClientSecret);
        Assert.Equal(["PowerShell", "Registry"], config.Tools.Exclude);
        Assert.Equal("strict", config.Control.Policy);
        Assert.False(config.Control.Enabled);
        Assert.Equal(2.5, config.Control.IdleTimeoutSeconds);
        Assert.Equal(40, config.Control.MouseTakeoverUnits);
        Assert.Equal(2000, config.Control.MouseTakeoverPixels);
        Assert.Equal(_temp.Combine("config.toml"), config.SourcePath);
    }

    [Fact]
    public void IntegerIdleTimeoutIsAccepted() => Assert.Equal(3.0, Load("[control]\nidle_timeout_seconds = 3").Control.IdleTimeoutSeconds);

    [Fact]
    public void EmptyStringsMeanUnset()
    {
        var config = Load("[server]\nauth_key = \"\"\nssl_certfile = \"\"\n[security]\noauth_client_id = \"\"");
        Assert.Null(config.Server.AuthKey);
        Assert.Null(config.Server.SslCertfile);
        Assert.Null(config.Security.OAuthClientId);
    }

    [Theory]
    [InlineData("[server]\nport = true", "server.port must be a TOML integer, not bool")]
    [InlineData("[server]\nport = \"80\"", "server.port must be a TOML integer, not str")]
    [InlineData("[server]\nport = 1.5", "server.port must be a TOML integer, not float")]
    [InlineData("[server]\nport = 70000", "server.port must be between 0 and 65535")]
    [InlineData("[server]\ntransport = \"tcp\"", "server.transport must be one of {'stdio', 'sse', 'streamable-http'}, got 'tcp'")]
    [InlineData("[server]\ntransport = 1", "server.transport must be a TOML string, not int")]
    [InlineData("[server]\nhost = [\"a\"]", "server.host must be a TOML string, not list")]
    [InlineData("[server]\nallow_insecure_remote = 1", "server.allow_insecure_remote must be a TOML boolean, not int")]
    [InlineData("[server]\nstateless_http = \"true\"", "server.stateless_http must be a TOML boolean, not str")]
    [InlineData("[security]\nip_allowlist = [1]", "security.ip_allowlist must be an array of strings")]
    [InlineData("[security]\ncors_origins = \"x\"", "security.cors_origins must be an array of strings")]
    [InlineData("[security]\noauth_client_secret = 5", "security.oauth_client_secret must be a TOML string, not int")]
    [InlineData("[tools]\nexclude = \"PowerShell\"", "tools.exclude must be an array of strings")]
    [InlineData("[control]\npolicy = \"lax\"", "control.policy must be relaxed, balanced, or strict")]
    [InlineData("[control]\npolicy = 1", "control.policy must be relaxed, balanced, or strict")]
    [InlineData("[control]\nenabled = \"no\"", "control.enabled must be a TOML boolean, not str")]
    [InlineData("[control]\nidle_timeout_seconds = 0", "control.idle_timeout_seconds must be a finite positive number")]
    [InlineData("[control]\nidle_timeout_seconds = true", "control.idle_timeout_seconds must be a finite positive number")]
    [InlineData("[control]\nidle_timeout_seconds = inf", "control.idle_timeout_seconds must be a finite positive number")]
    [InlineData("[control]\nmouse_takeover_units = 39", "control.mouse_takeover_units must be between 40 and 2000")]
    [InlineData("[control]\nmouse_takeover_pixels = true", "control.mouse_takeover_pixels must be a TOML integer, not bool")]
    [InlineData("server = 5", "server must be a TOML table")]
    public void StrictTypesUseUpstreamMessages(string toml, string expected) => Assert.Equal(expected, Error(toml));

    [Fact]
    public void SyntaxErrorsNameTheFile() =>
        Assert.StartsWith($"Invalid TOML in {_temp.Combine("config.toml")}: (1,8)", Error("[server\nport = 1\n"));

    [Fact]
    public void DiscoverPrefersTheExplicitPathAndExpandsTilde()
    {
        Assert.Equal(_temp.Combine("x.toml"), ConfigFile.Discover("~/x.toml", _temp.Path));
        Assert.Equal(_temp.Combine("y.toml"), ConfigFile.Discover(@"~\y.toml", _temp.Path));
        Assert.Equal(@"D:\cfg.toml", ConfigFile.Discover(@"D:\cfg.toml", _temp.Path));
    }

    [Fact]
    public void DiscoverFindsTheDefaultFileOnlyWhenItExists()
    {
        Assert.Null(ConfigFile.Discover(null, _temp.Path));
        var path = _temp.WriteFile(Path.Combine(".desktop-mcp", "config.toml"), "");
        Assert.Equal(path, ConfigFile.Discover(null, _temp.Path));
        Assert.Equal("stdio", ConfigFile.Load(path).Server.Transport);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~ConfigFileTests"`
Expected: build FAILS with `error CS0234: The type or namespace name 'Config' does not exist in the namespace 'DesktopMcp.Core'`.

- [ ] **Step 3: Add Tomlyn with reflection off**

In `windows/Directory.Packages.props` add inside the `ItemGroup`:

```xml
    <!-- Published 2026-07-02. Source-generated, NativeAOT ready; reads TOML 1.1. -->
    <PackageVersion Include="Tomlyn" Version="2.10.1" />
```

In `windows/Directory.Build.props`, inside the first `PropertyGroup`, add:

```xml
    <!-- Tomlyn: no reflection fallback in any build (Tomlyn's targets only switch it off for PublishAot), so the JIT
         tests run the same source-generated path as the NativeAOT exe. -->
    <TomlynIsReflectionEnabledByDefault>false</TomlynIsReflectionEnabledByDefault>
```

In `windows/src/DesktopMcp.Core/DesktopMcp.Core.csproj` add:

```xml
  <ItemGroup>
    <PackageReference Include="Tomlyn" />
  </ItemGroup>
```

- [ ] **Step 4: Implement the config model and loader**

`windows/src/DesktopMcp.Core/Config/ConfigException.cs`:

```csharp
namespace DesktopMcp.Core.Config;

/// <summary>A configuration problem; the message is printed as <c>Error: &lt;message&gt;</c> (upstream's ClickException).</summary>
public sealed class ConfigException(string message) : Exception(message);
```

`windows/src/DesktopMcp.Core/Config/DesktopMcpConfig.cs`:

```csharp
namespace DesktopMcp.Core.Config;

/// <summary>~/.desktop-mcp/config.toml with upstream's sections and defaults (infrastructure/config.py:22-69).</summary>
public sealed class DesktopMcpConfig
{
    public ServerConfig Server { get; } = new();
    public SecurityConfig Security { get; } = new();
    public ToolsConfig Tools { get; } = new();
    public ControlConfig Control { get; } = new();
    public string? SourcePath { get; set; }
}

public sealed class ServerConfig
{
    public string Transport { get; set; } = "stdio";
    public string Host { get; set; } = "localhost";
    public long Port { get; set; } = 8000;
    public bool AllowInsecureRemote { get; set; }
    public string? AuthKey { get; set; }
    public string? SslCertfile { get; set; }
    public string? SslKeyfile { get; set; }
    public bool StatelessHttp { get; set; }
}

public sealed class SecurityConfig
{
    public List<string> IpAllowlist { get; set; } = [];
    public List<string> CorsOrigins { get; set; } = [];
    public string? OAuthClientId { get; set; }
    public string? OAuthClientSecret { get; set; }
}

public sealed class ToolsConfig
{
    public List<string> Exclude { get; set; } = [];
}

public sealed class ControlConfig
{
    public long MouseTakeoverUnits { get; set; } = 500;
    public long MouseTakeoverPixels { get; set; } = 500;
    public bool Enabled { get; set; } = true;
    public double IdleTimeoutSeconds { get; set; } = 10.0;
    public string Policy { get; set; } = "relaxed";
}
```

`windows/src/DesktopMcp.Core/Config/IdleTimeout.cs`:

```csharp
namespace DesktopMcp.Core.Config;

/// <summary>upstream validate_idle_timeout (infrastructure/config.py:95-105), shared by TOML and the CLI.</summary>
public static class IdleTimeout
{
    public const string Message = "control.idle_timeout_seconds must be a finite positive number";

    public static double Validate(object? raw) => raw switch
    {
        long seconds when seconds > 0 => seconds,
        double seconds when double.IsFinite(seconds) && seconds > 0 => seconds,
        _ => throw new ConfigException(Message),
    };
}
```

`windows/src/DesktopMcp.Core/Config/ConfigTomlContext.cs`:

```csharp
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace DesktopMcp.Core.Config;

/// <summary>
/// Source-generated Tomlyn metadata. The file is read as an untyped table and checked by <see cref="ConfigFile"/>, so
/// the strict type rules and their messages are upstream's.
/// </summary>
[TomlSerializable(typeof(TomlTable))]
internal partial class ConfigTomlContext : TomlSerializerContext;
```

`windows/src/DesktopMcp.Core/Config/ConfigFile.cs`:

```csharp
using System.Text;
using DesktopMcp.Core.Text;
using Tomlyn;
using Tomlyn.Model;

namespace DesktopMcp.Core.Config;

/// <summary>Finds and reads config.toml with upstream's rules and messages (infrastructure/config.py:72-212).</summary>
public static class ConfigFile
{
    public const string DirectoryName = ".desktop-mcp";
    public const string FileName = "config.toml";
    public static IReadOnlyList<string> Transports { get; } = ["stdio", "sse", "streamable-http"];
    public static IReadOnlyList<string> Policies { get; } = ["relaxed", "balanced", "strict"];

    public static string DefaultPath(string home) => Path.Combine(home, DirectoryName, FileName);

    /// <summary>The explicit path (with ~ expanded), else the default file when it exists, else null.</summary>
    public static string? Discover(string? explicitPath, string home)
    {
        if (!string.IsNullOrEmpty(explicitPath)) return ExpandUser(explicitPath, home);
        var path = DefaultPath(home);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Python's expanduser for "~", "~/..." and "~\...".</summary>
    public static string ExpandUser(string path, string home)
    {
        if (path == "~") return home;
        return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal)
            ? Path.Combine(home, path[2..])
            : path;
    }

    /// <summary>Defaults when <paramref name="path"/> is null; otherwise the parsed, validated file.</summary>
    public static DesktopMcpConfig Load(string? path)
    {
        if (path is null) return new DesktopMcpConfig();
        if (!File.Exists(path)) throw new ConfigException($"Config file not found: {path}");
        return Parse(File.ReadAllText(path, Encoding.UTF8), path);
    }

    public static DesktopMcpConfig Parse(string toml, string path)
    {
        TomlTable document;
        try
        {
            document = TomlSerializer.Deserialize(toml, ConfigTomlContext.Default.TomlTable) ?? new TomlTable();
        }
        catch (TomlException e)
        {
            throw new ConfigException($"Invalid TOML in {path}: {Reason(e)}");
        }

        var config = new DesktopMcpConfig { SourcePath = path };
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
        var server = Section(document, "server");
        var security = Section(document, "security");
        var tools = Section(document, "tools");
        var control = Section(document, "control");

        if (server.TryGetValue("transport", out var transport))
        {
            var value = StrictString(transport, "server.transport");
            if (!Transports.Contains(value))
                throw new ConfigException($"server.transport must be one of {{'stdio', 'sse', 'streamable-http'}}, got {PyText.Repr(value)}");
            config.Server.Transport = value;
        }
        if (server.TryGetValue("host", out var host)) config.Server.Host = StrictString(host, "server.host");
        if (server.TryGetValue("port", out var port))
        {
            config.Server.Port = StrictInt(port, "server.port");
            if (config.Server.Port is < 0 or > 65535) throw new ConfigException("server.port must be between 0 and 65535");
        }
        if (server.TryGetValue("allow_insecure_remote", out var insecure))
            config.Server.AllowInsecureRemote = StrictBool(insecure, "server.allow_insecure_remote");
        if (server.TryGetValue("auth_key", out var authKey)) config.Server.AuthKey = Blank(StrictString(authKey, "server.auth_key"));
        // TLS paths are relative to the config file's folder.
        if (server.TryGetValue("ssl_certfile", out var cert) && Blank(StrictString(cert, "server.ssl_certfile")) is { } certPath)
            config.Server.SslCertfile = Path.GetFullPath(certPath, baseDirectory);
        if (server.TryGetValue("ssl_keyfile", out var key) && Blank(StrictString(key, "server.ssl_keyfile")) is { } keyPath)
            config.Server.SslKeyfile = Path.GetFullPath(keyPath, baseDirectory);
        if (server.TryGetValue("stateless_http", out var stateless))
            config.Server.StatelessHttp = StrictBool(stateless, "server.stateless_http");

        if (security.TryGetValue("ip_allowlist", out var allowlist)) config.Security.IpAllowlist = ListOfStrings(allowlist, "security.ip_allowlist");
        if (security.TryGetValue("cors_origins", out var cors)) config.Security.CorsOrigins = ListOfStrings(cors, "security.cors_origins");
        if (security.TryGetValue("oauth_client_id", out var clientId))
            config.Security.OAuthClientId = Blank(StrictString(clientId, "security.oauth_client_id"));
        if (security.TryGetValue("oauth_client_secret", out var clientSecret))
            config.Security.OAuthClientSecret = Blank(StrictString(clientSecret, "security.oauth_client_secret"));

        if (tools.TryGetValue("exclude", out var exclude)) config.Tools.Exclude = ListOfStrings(exclude, "tools.exclude");

        if (control.TryGetValue("policy", out var policy))
        {
            if (policy is not string name || !Policies.Contains(name))
                throw new ConfigException("control.policy must be relaxed, balanced, or strict");
            config.Control.Policy = name;
        }
        if (control.TryGetValue("enabled", out var enabled)) config.Control.Enabled = StrictBool(enabled, "control.enabled");
        if (control.TryGetValue("idle_timeout_seconds", out var idle)) config.Control.IdleTimeoutSeconds = IdleTimeout.Validate(idle);
        if (control.TryGetValue("mouse_takeover_units", out var units)) config.Control.MouseTakeoverUnits = TakeoverDistance(units, "control.mouse_takeover_units");
        if (control.TryGetValue("mouse_takeover_pixels", out var pixels)) config.Control.MouseTakeoverPixels = TakeoverDistance(pixels, "control.mouse_takeover_pixels");
        return config;
    }

    private static TomlTable Section(TomlTable document, string name)
    {
        if (!document.TryGetValue(name, out var value)) return new TomlTable();
        return value as TomlTable ?? throw new ConfigException($"{name} must be a TOML table");
    }

    private static long TakeoverDistance(object? raw, string key)
    {
        var value = StrictInt(raw, key);
        if (value is < 40 or > 2000) throw new ConfigException($"{key} must be between 40 and 2000");
        return value;
    }

    private static string StrictString(object? raw, string key) =>
        raw as string ?? throw new ConfigException($"{key} must be a TOML string, not {PyTypeName(raw)}");

    private static bool StrictBool(object? raw, string key) =>
        raw is bool value ? value : throw new ConfigException($"{key} must be a TOML boolean, not {PyTypeName(raw)}");

    // TOML booleans are a subclass of int in Python; upstream rejects them explicitly.
    private static long StrictInt(object? raw, string key) =>
        raw is long value ? value : throw new ConfigException($"{key} must be a TOML integer, not {PyTypeName(raw)}");

    private static List<string> ListOfStrings(object? raw, string key)
    {
        if (raw is not TomlArray array) throw new ConfigException($"{key} must be an array of strings");
        var items = new List<string>();
        foreach (var item in array)
        {
            if (item is not string text) throw new ConfigException($"{key} must be an array of strings");
            items.Add(text);
        }
        return items;
    }

    private static string? Blank(string value) => value.Length == 0 ? null : value;

    // The names Python's type(raw).__name__ prints for TOML values.
    private static string PyTypeName(object? value) => value switch
    {
        null => "NoneType",
        bool => "bool",
        long => "int",
        double => "float",
        string => "str",
        TomlArray or TomlTableArray => "list",
        TomlTable => "dict",
        _ => "datetime",
    };

    private static string Reason(TomlException e)
    {
        const string marker = "Reason: ";
        var at = e.Message.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? e.Message : e.Message[(at + marker.Length)..];
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~ConfigFileTests"`
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add windows/Directory.Packages.props windows/Directory.Build.props windows/src/DesktopMcp.Core windows/tests/DesktopMcp.Core.Tests
git commit -m "Read config.toml with Tomlyn's source generator and upstream's strict types" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: ServePlan, IP allowlist and tool filter

`ServePlan` is the validated, immutable result of CLI-or-environment values, then `config.toml`, then defaults (`__main__.py:722-834`, `_choose_value` at lines 123-133). The tool filter is upstream's `_apply_tool_filter` (`__main__.py:447-493`): `--tools` keeps only the named tools plus ControlStatus, otherwise `--exclude-tools` removes tools but never ControlStatus.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Net/IpAddressText.cs`
- Create: `windows/src/DesktopMcp.Core/Config/IpNetwork.cs`, `ServeArguments.cs`, `ServePlan.cs`, `ToolFilter.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Config/IpNetworkTests.cs`, `ServePlanTests.cs`, `ToolFilterTests.cs`

**Interfaces:**
- Consumes: `ConfigException`, `DesktopMcpConfig`, `PyText.Repr`.
- Produces:
  - `DesktopMcp.Core.Net.IpAddressText`: `bool TryParse(string text, out IPAddress? address)` (Python-strict), `bool IsLoopbackHost(string host)`.
  - `DesktopMcp.Core.Config.IpNetwork(IPAddress Network, int PrefixLength)` with `static IReadOnlyList<IpNetwork> ParseAllowlist(IEnumerable<string> entries)` (throws `FormatException`).
  - `ServeArguments` record: `string? Transport, Host; long? Port; bool Debug; string? AuthKey; bool? AllowInsecureRemote; string? IpAllowlist, Tools, ExcludeTools, CorsOrigins, SslCertfile, SslKeyfile, OAuthClientId, OAuthClientSecret; bool? StatelessHttp, Control; double? ControlIdleTimeout; string? ControlPolicy`. Null means "not given on the command line or in the environment".
  - `ServePlan` record: `Transport, Host, int Port, AuthKey, AllowInsecureRemote, StatelessHttp, IpAllowlist, CorsOrigins, SslCertfile, SslKeyfile, OAuthClientId, OAuthClientSecret, ExplicitTools, ExcludeTools, AllowedHosts, ControlSettings Control, Debug, Warnings, Scheme`, and `static ServePlan Resolve(ServeArguments arguments, DesktopMcpConfig config)` (throws `ConfigException`).
  - `ControlSettings(bool Enabled, string Policy, double IdleTimeoutSeconds, long MouseTakeoverUnits, long MouseTakeoverPixels)`.
  - `ToolFilter.AlwaysKept` (`"ControlStatus"`) and `IReadOnlyList<string> ToolFilter.Apply(IReadOnlyList<string> registered, IReadOnlyList<string> explicitTools, IReadOnlyList<string> excludeTools)`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Core.Tests/Config/IpNetworkTests.cs`:

```csharp
using DesktopMcp.Core.Config;
using DesktopMcp.Core.Net;

namespace DesktopMcp.Core.Tests;

public class IpNetworkTests
{
    [Fact]
    public void ParsesAddressesAndNetworksAndClearsHostBits()
    {
        var networks = IpNetwork.ParseAllowlist(["10.1.2.3/8", " 192.168.1.5 ", "", "2001:db8::1/32", "::1"]);
        Assert.Equal(["10.0.0.0/8", "192.168.1.5/32", "2001:db8::/32", "::1/128"], networks.Select(n => n.ToString()));
    }

    [Fact]
    public void ReportsEveryBadEntryWithPythonsMessages()
    {
        var error = Assert.Throws<FormatException>(() => IpNetwork.ParseAllowlist(["abc", "10.0.0.0/33", " 010.0.0.1", "1.2.3"]));
        Assert.Equal(
            "Invalid IP allowlist entries: " +
            "'abc': 'abc' does not appear to be an IPv4 or IPv6 address; " +
            "'10.0.0.0/33': '10.0.0.0/33' does not appear to be an IPv4 or IPv6 network; " +
            "' 010.0.0.1': '010.0.0.1' does not appear to be an IPv4 or IPv6 address; " +
            "'1.2.3': '1.2.3' does not appear to be an IPv4 or IPv6 address",
            error.Message);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.6.7", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("example.com", false)]
    [InlineData("[::1]", false)]
    public void IsLoopbackHostMatchesUpstream(string host, bool expected) => Assert.Equal(expected, IpAddressText.IsLoopbackHost(host));
}
```

`windows/tests/DesktopMcp.Core.Tests/Config/ServePlanTests.cs`:

```csharp
using DesktopMcp.Core.Config;

namespace DesktopMcp.Core.Tests;

public class ServePlanTests
{
    private const string RefusalForAll =
        "Refusing to bind HTTP transport to '0.0.0.0' without authentication.\n" +
        "  Use --auth-key <token> or --oauth-client-id/--oauth-client-secret.\n" +
        "  Or pass --allow-insecure-remote to explicitly allow unauthenticated access (not recommended).";

    private static ServePlan Resolve(ServeArguments arguments, DesktopMcpConfig? config = null) =>
        ServePlan.Resolve(arguments, config ?? new DesktopMcpConfig());

    private static string Error(ServeArguments arguments, DesktopMcpConfig? config = null) =>
        Assert.Throws<ConfigException>(() => Resolve(arguments, config)).Message;

    [Fact]
    public void DefaultsAreStdioOnLocalhost()
    {
        var plan = Resolve(new ServeArguments());
        Assert.Equal("stdio", plan.Transport);
        Assert.Equal("localhost", plan.Host);
        Assert.Equal(8000, plan.Port);
        Assert.Null(plan.AuthKey);
        Assert.Empty(plan.ExplicitTools);
        Assert.Empty(plan.ExcludeTools);
        Assert.Null(plan.AllowedHosts);
        Assert.Empty(plan.Warnings);
        Assert.Equal(new ControlSettings(true, "relaxed", 10.0, 500, 500), plan.Control);
        Assert.Equal("http", plan.Scheme);
    }

    [Fact]
    public void ExplicitValuesBeatTheConfigWhichBeatsDefaults()
    {
        var config = new DesktopMcpConfig();
        config.Server.Port = 9100;
        config.Server.Host = "127.0.0.1";
        config.Control.Policy = "strict";
        config.Control.Enabled = false;
        var plan = Resolve(new ServeArguments { Port = 9200, ControlPolicy = "balanced" }, config);
        Assert.Equal(9200, plan.Port);
        Assert.Equal("127.0.0.1", plan.Host);
        Assert.Equal("balanced", plan.Control.Policy);
        Assert.False(plan.Control.Enabled);
    }

    [Fact]
    public void ExplicitExcludeToolsReplaceTheConfigList()
    {
        var config = new DesktopMcpConfig();
        config.Tools.Exclude = ["Click"];
        Assert.Equal(["Click"], Resolve(new ServeArguments(), config).ExcludeTools);
        Assert.Equal(["PowerShell", "Registry"], Resolve(new ServeArguments { ExcludeTools = " PowerShell, ,Registry " }, config).ExcludeTools);
    }

    [Fact]
    public void ToolsListIsSplitAndTrimmed() =>
        Assert.Equal(["Wait", "Scrape"], Resolve(new ServeArguments { Tools = "Wait, Scrape,," }).ExplicitTools);

    [Fact]
    public void EmptyExplicitAuthKeyOverridesTheConfigKey()
    {
        var config = new DesktopMcpConfig();
        config.Server.AuthKey = "from-config";
        Assert.Equal("from-config", Resolve(new ServeArguments(), config).AuthKey);
        Assert.Null(Resolve(new ServeArguments { AuthKey = "" }, config).AuthKey);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(65536L)]
    public void PortOutsideTheRangeIsRejected(long port) =>
        Assert.Equal("server.port must be between 0 and 65535", Error(new ServeArguments { Port = port }));

    [Fact]
    public void TlsFilesComeInPairs() =>
        Assert.Equal("--ssl-certfile and --ssl-keyfile must be provided together.", Error(new ServeArguments { SslCertfile = "c.pem" }));

    [Fact]
    public void OAuthCredentialsComeInPairs() =>
        Assert.Equal("OAuth requires both --oauth-client-id and --oauth-client-secret.", Error(new ServeArguments { OAuthClientSecret = "s" }));

    [Fact]
    public void BadAllowlistEntriesAreReported() =>
        Assert.Equal("Invalid ip_allowlist: Invalid IP allowlist entries: 'nope': 'nope' does not appear to be an IPv4 or IPv6 address",
            Error(new ServeArguments { IpAllowlist = "10.0.0.0/8,nope" }));

    [Fact]
    public void RemoteHttpBindNeedsAuthentication() =>
        Assert.Equal(RefusalForAll, Error(new ServeArguments { Transport = "sse", Host = "0.0.0.0" }));

    [Fact]
    public void AuthKeyOAuthOrInsecureFlagAllowARemoteBind()
    {
        Assert.Equal("0.0.0.0", Resolve(new ServeArguments { Transport = "sse", Host = "0.0.0.0", AuthKey = "k" }).Host);
        Assert.Equal("0.0.0.0", Resolve(new ServeArguments { Transport = "sse", Host = "0.0.0.0", OAuthClientId = "i", OAuthClientSecret = "s" }).Host);
        var insecure = Resolve(new ServeArguments { Transport = "streamable-http", Host = "0.0.0.0", AllowInsecureRemote = true });
        Assert.Null(insecure.AllowedHosts);
    }

    [Fact]
    public void AllowedHostsFollowTheBindAddress()
    {
        Assert.Equal(["localhost", "127.0.0.1", "[::1]"], Resolve(new ServeArguments { Transport = "sse" }).AllowedHosts);
        Assert.Equal(["10.0.0.5"], Resolve(new ServeArguments { Transport = "sse", Host = "10.0.0.5", AuthKey = "k" }).AllowedHosts);
        Assert.Null(Resolve(new ServeArguments { Transport = "sse", Host = "::", AuthKey = "k" }).AllowedHosts);
        Assert.Null(Resolve(new ServeArguments { Transport = "stdio" }).AllowedHosts);
    }

    [Fact]
    public void AuthOrAllowlistOnStdioIsAWarning()
    {
        Assert.Equal(["--auth-key / --ip-allowlist have no effect on stdio transport"], Resolve(new ServeArguments { AuthKey = "k" }).Warnings);
        Assert.Equal(["--auth-key / --ip-allowlist have no effect on stdio transport"], Resolve(new ServeArguments { IpAllowlist = "10.0.0.1" }).Warnings);
    }

    [Fact]
    public void TlsMakesTheSchemeHttps() =>
        Assert.Equal("https", Resolve(new ServeArguments { SslCertfile = "c.pem", SslKeyfile = "k.pem" }).Scheme);
}
```

`windows/tests/DesktopMcp.Core.Tests/Config/ToolFilterTests.cs`:

```csharp
using DesktopMcp.Core.Config;

namespace DesktopMcp.Core.Tests;

public class ToolFilterTests
{
    private static readonly string[] Registered = ["ControlStatus", "App", "PowerShell", "Wait", "Registry"];

    [Fact]
    public void NoFilterKeepsEverything() => Assert.Equal(Registered, ToolFilter.Apply(Registered, [], []));

    [Fact]
    public void ExplicitToolsKeepOnlyThoseAndControlStatusInRegistryOrder() =>
        Assert.Equal(["ControlStatus", "Wait", "Registry"], ToolFilter.Apply(Registered, ["Registry", "Wait", "Nope"], []));

    [Fact]
    public void ExcludeNeverRemovesControlStatus() =>
        Assert.Equal(["ControlStatus", "App", "Wait", "Registry"], ToolFilter.Apply(Registered, [], ["PowerShell", "ControlStatus"]));

    [Fact]
    public void ExplicitToolsOverrideExclude() =>
        Assert.Equal(["ControlStatus", "PowerShell"], ToolFilter.Apply(Registered, ["PowerShell"], ["PowerShell"]));

    [Fact]
    public void NamesAreCaseSensitive() => Assert.Equal(["ControlStatus"], ToolFilter.Apply(Registered, ["wait"], []));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~IpNetworkTests|FullyQualifiedName~ServePlanTests|FullyQualifiedName~ToolFilterTests"`
Expected: build FAILS with `error CS0246: The type or namespace name 'ServeArguments' could not be found` (and the same for `IpNetwork`, `ToolFilter`, `IpAddressText`).

- [ ] **Step 3: Implement strict IP parsing and the allowlist**

`windows/src/DesktopMcp.Core/Net/IpAddressText.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DesktopMcp.Core.Net;

/// <summary>
/// Python's <c>ipaddress.ip_address</c> parsing: IPv4 as exactly four decimal octets without leading zeros, IPv6
/// without brackets. .NET's IPAddress.TryParse alone is more lenient ("1.2.3" and "010.0.0.1" parse there).
/// </summary>
public static class IpAddressText
{
    public static bool TryParse(string text, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        if (text.Contains(':'))
        {
            if (text.Contains('[') || text.Contains(']') || text.Contains('/')) return false;
            return IPAddress.TryParse(text, out address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        }
        var parts = text.Split('.');
        if (parts.Length != 4) return false;
        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit) || (part.Length > 1 && part[0] == '0')) return false;
            var value = int.Parse(part, CultureInfo.InvariantCulture);
            if (value > 255) return false;
            bytes[i] = (byte)value;
        }
        address = new IPAddress(bytes);
        return true;
    }

    /// <summary>upstream is_loopback_host (infrastructure/auth.py:82-89).</summary>
    public static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!TryParse(host, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }
}
```

`windows/src/DesktopMcp.Core/Config/IpNetwork.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DesktopMcp.Core.Net;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Config;

/// <summary>An IPv4 or IPv6 network for the IP allowlist (enforced by the HTTP pipeline in P5).</summary>
public sealed record IpNetwork(IPAddress Network, int PrefixLength)
{
    public override string ToString() => $"{Network}/{PrefixLength}";

    /// <summary>upstream parse_ip_allowlist (infrastructure/security.py:210-236): blank entries are skipped, host bits
    /// are cleared, and every bad entry is reported at once.</summary>
    public static IReadOnlyList<IpNetwork> ParseAllowlist(IEnumerable<string> entries)
    {
        var networks = new List<IpNetwork>();
        var errors = new List<string>();
        foreach (var entry in entries)
        {
            var value = entry.Trim();
            if (value.Length == 0) continue;
            if (TryParse(value, out var network, out var error)) networks.Add(network);
            else errors.Add($"{PyText.Repr(entry)}: {error}");
        }
        if (errors.Count > 0) throw new FormatException("Invalid IP allowlist entries: " + string.Join("; ", errors));
        return networks;
    }

    private static bool TryParse(string value, [NotNullWhen(true)] out IpNetwork? network, [NotNullWhen(false)] out string? error)
    {
        network = null;
        error = null;
        var slash = value.IndexOf('/');
        if (slash < 0)
        {
            if (IpAddressText.TryParse(value, out var address))
            {
                network = new IpNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
                return true;
            }
            error = $"{PyText.Repr(value)} does not appear to be an IPv4 or IPv6 address";
            return false;
        }
        if (IpAddressText.TryParse(value[..slash], out var baseAddress))
        {
            var max = baseAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var prefixText = value[(slash + 1)..];
            if (prefixText.Length is > 0 and <= 3 && prefixText.All(char.IsAsciiDigit))
            {
                var prefix = int.Parse(prefixText, CultureInfo.InvariantCulture);
                if (prefix <= max)
                {
                    network = new IpNetwork(Mask(baseAddress, prefix), prefix);
                    return true;
                }
            }
        }
        error = $"{PyText.Repr(value)} does not appear to be an IPv4 or IPv6 network";
        return false;
    }

    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }
        return new IPAddress(bytes);
    }
}
```

- [ ] **Step 4: Implement ServeArguments, ServePlan and ToolFilter**

`windows/src/DesktopMcp.Core/Config/ServeArguments.cs`:

```csharp
namespace DesktopMcp.Core.Config;

/// <summary>
/// The serve settings given on the command line or through their DESKTOP_MCP_* variables. A null member was given in
/// neither place, so <see cref="ServePlan.Resolve"/> falls back to config.toml and then to the default
/// (upstream _choose_value, __main__.py:123-133).
/// </summary>
public sealed record ServeArguments
{
    public string? Transport { get; init; }
    public string? Host { get; init; }
    public long? Port { get; init; }
    public bool Debug { get; init; }
    public string? AuthKey { get; init; }
    public bool? AllowInsecureRemote { get; init; }
    public string? IpAllowlist { get; init; }
    public string? Tools { get; init; }
    public string? ExcludeTools { get; init; }
    public string? CorsOrigins { get; init; }
    public string? SslCertfile { get; init; }
    public string? SslKeyfile { get; init; }
    public string? OAuthClientId { get; init; }
    public string? OAuthClientSecret { get; init; }
    public bool? StatelessHttp { get; init; }
    public bool? Control { get; init; }
    public double? ControlIdleTimeout { get; init; }
    public string? ControlPolicy { get; init; }
}
```

`windows/src/DesktopMcp.Core/Config/ServePlan.cs`:

```csharp
using DesktopMcp.Core.Net;

namespace DesktopMcp.Core.Config;

public sealed record ControlSettings(bool Enabled, string Policy, double IdleTimeoutSeconds, long MouseTakeoverUnits, long MouseTakeoverPixels);

/// <summary>The validated settings serve runs with (spec §8; upstream serve, __main__.py:722-834).</summary>
public sealed record ServePlan
{
    public required string Transport { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? AuthKey { get; init; }
    public bool AllowInsecureRemote { get; init; }
    public bool StatelessHttp { get; init; }
    public IReadOnlyList<IpNetwork> IpAllowlist { get; init; } = [];
    public IReadOnlyList<string> CorsOrigins { get; init; } = [];
    public string? SslCertfile { get; init; }
    public string? SslKeyfile { get; init; }
    public string? OAuthClientId { get; init; }
    public string? OAuthClientSecret { get; init; }
    public IReadOnlyList<string> ExplicitTools { get; init; } = [];
    public IReadOnlyList<string> ExcludeTools { get; init; } = [];
    /// <summary>Hosts the Host header may name (DNS-rebinding protection), or null for no check.</summary>
    public IReadOnlyList<string>? AllowedHosts { get; init; }
    public required ControlSettings Control { get; init; }
    public bool Debug { get; init; }
    /// <summary>Non-fatal notes for the log.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string Scheme => SslCertfile is null ? "http" : "https";

    public static ServePlan Resolve(ServeArguments arguments, DesktopMcpConfig config)
    {
        var transport = arguments.Transport ?? config.Server.Transport;
        var host = arguments.Host ?? config.Server.Host;
        var port = arguments.Port ?? config.Server.Port;
        if (port is < 0 or > 65535) throw new ConfigException("server.port must be between 0 and 65535");
        var authKey = Blank(arguments.AuthKey ?? config.Server.AuthKey);
        var statelessHttp = arguments.StatelessHttp ?? config.Server.StatelessHttp;
        var allowInsecureRemote = arguments.AllowInsecureRemote ?? config.Server.AllowInsecureRemote;
        var sslCertfile = Blank(arguments.SslCertfile ?? config.Server.SslCertfile);
        var sslKeyfile = Blank(arguments.SslKeyfile ?? config.Server.SslKeyfile);
        var oauthClientId = Blank(arguments.OAuthClientId ?? config.Security.OAuthClientId);
        var oauthClientSecret = Blank(arguments.OAuthClientSecret ?? config.Security.OAuthClientSecret);

        var explicitTools = SplitList(arguments.Tools);
        var excludeTools = string.IsNullOrEmpty(arguments.ExcludeTools) ? config.Tools.Exclude.ToArray() : SplitList(arguments.ExcludeTools);
        // Upstream keeps empty allowlist entries here; ParseAllowlist skips them.
        var allowlistEntries = string.IsNullOrEmpty(arguments.IpAllowlist)
            ? config.Security.IpAllowlist.ToArray()
            : arguments.IpAllowlist.Split(',').Select(entry => entry.Trim()).ToArray();
        var corsOrigins = string.IsNullOrEmpty(arguments.CorsOrigins) ? config.Security.CorsOrigins.ToArray() : SplitList(arguments.CorsOrigins);

        if ((sslCertfile is null) != (sslKeyfile is null))
            throw new ConfigException("--ssl-certfile and --ssl-keyfile must be provided together.");
        if ((oauthClientId is null) != (oauthClientSecret is null))
            throw new ConfigException("OAuth requires both --oauth-client-id and --oauth-client-secret.");

        IReadOnlyList<IpNetwork> allowlist = [];
        if (allowlistEntries.Length > 0)
        {
            try { allowlist = IpNetwork.ParseAllowlist(allowlistEntries); }
            catch (FormatException e) { throw new ConfigException($"Invalid ip_allowlist: {e.Message}"); }
        }

        var oauth = oauthClientId is not null && oauthClientSecret is not null;
        var loopback = IpAddressText.IsLoopbackHost(host);
        if (transport != "stdio" && !loopback && authKey is null && !oauth && !allowInsecureRemote)
        {
            throw new ConfigException(
                $"Refusing to bind HTTP transport to '{host}' without authentication.\n" +
                "  Use --auth-key <token> or --oauth-client-id/--oauth-client-secret.\n" +
                "  Or pass --allow-insecure-remote to explicitly allow unauthenticated access (not recommended).");
        }

        var warnings = new List<string>();
        if ((authKey is not null || allowlistEntries.Length > 0) && transport == "stdio")
            warnings.Add("--auth-key / --ip-allowlist have no effect on stdio transport");

        // DNS-rebinding protection: loopback binds accept the loopback names, a specific address accepts itself, and
        // 0.0.0.0, :: and --allow-insecure-remote turn the check off.
        IReadOnlyList<string>? allowedHosts = null;
        if (transport != "stdio" && !allowInsecureRemote)
        {
            if (loopback) allowedHosts = ["localhost", "127.0.0.1", "[::1]"];
            else if (host is not ("0.0.0.0" or "::" or "")) allowedHosts = [host];
        }

        return new ServePlan
        {
            Transport = transport,
            Host = host,
            Port = (int)port,
            AuthKey = authKey,
            AllowInsecureRemote = allowInsecureRemote,
            StatelessHttp = statelessHttp,
            IpAllowlist = allowlist,
            CorsOrigins = corsOrigins,
            SslCertfile = sslCertfile,
            SslKeyfile = sslKeyfile,
            OAuthClientId = oauthClientId,
            OAuthClientSecret = oauthClientSecret,
            ExplicitTools = explicitTools,
            ExcludeTools = excludeTools,
            AllowedHosts = allowedHosts,
            Control = new ControlSettings(
                arguments.Control ?? config.Control.Enabled,
                arguments.ControlPolicy ?? config.Control.Policy,
                arguments.ControlIdleTimeout ?? config.Control.IdleTimeoutSeconds,
                config.Control.MouseTakeoverUnits,
                config.Control.MouseTakeoverPixels),
            Debug = arguments.Debug,
            Warnings = warnings,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string[] SplitList(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();
}
```

`windows/src/DesktopMcp.Core/Config/ToolFilter.cs`:

```csharp
namespace DesktopMcp.Core.Config;

/// <summary>upstream _apply_tool_filter (__main__.py:447-493).</summary>
public static class ToolFilter
{
    public const string AlwaysKept = "ControlStatus";

    /// <summary>The registered names that stay, in registry order.</summary>
    public static IReadOnlyList<string> Apply(IReadOnlyList<string> registered, IReadOnlyList<string> explicitTools, IReadOnlyList<string> excludeTools)
    {
        if (explicitTools.Count > 0)
        {
            var keep = new HashSet<string>(explicitTools, StringComparer.Ordinal) { AlwaysKept };
            return registered.Where(keep.Contains).ToArray();
        }
        if (excludeTools.Count > 0)
        {
            var drop = new HashSet<string>(excludeTools.Where(name => name != AlwaysKept), StringComparer.Ordinal);
            return registered.Where(name => !drop.Contains(name)).ToArray();
        }
        return registered;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~IpNetworkTests|FullyQualifiedName~ServePlanTests|FullyQualifiedName~ToolFilterTests"`
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add windows/src/DesktopMcp.Core/Net windows/src/DesktopMcp.Core/Config windows/tests/DesktopMcp.Core.Tests/Config
git commit -m "Validate serve settings into a ServePlan and filter tools as upstream does" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: CLI and stdio host

`System.CommandLine` 2.0.12 (stable, 2026-09-08). A NativeAOT probe on 2026-10-19 parsed options, defaults, `AcceptOnlyFromAmong` and `OptionResult.Implicit` with zero trim/AOT warnings. The CLI has `serve` (the default), `install`, `uninstall` and `auth` with every upstream flag and help text (`__main__.py:546-1168`), renamed for desktop-mcp. Errors follow click's shapes: `Error: <message>` with exit code 1 for configuration problems, and a `Usage:` block with exit code 2 for usage errors. `serve` runs the existing stdio host, now with tool filtering and stderr logging.

**Files:**
- Modify: `windows/Directory.Packages.props`, `windows/src/DesktopMcp.Server/DesktopMcp.Server.csproj`
- Create: `windows/src/DesktopMcp.Server/Cli/CliEnvironment.cs`, `CliUsageException.cs`, `EnvValues.cs`, `ServeOptions.cs`, `CliApp.cs`
- Create: `windows/src/DesktopMcp.Server/Hosting/StderrLoggerProvider.cs`, `StdioServer.cs`
- Replace: `windows/src/DesktopMcp/Program.cs`
- Modify: `windows/scripts/smoke-stdio.ps1`
- Test: `windows/tests/DesktopMcp.Server.Tests/Cli/CliAppTests.cs`

**Interfaces:**
- Consumes: `ConfigFile`, `ServePlan`, `ServeArguments`, `IdleTimeout`, `ToolFilter`, `ToolRegistry.CreateAll`, `ToolServices.CreateDefault`, `PyText.Repr`, `PyText.ShellJoin`, `ServerText`.
- Produces:
  - `CliApp.Name` (`"desktop-mcp"`), `Task<int> CliApp.RunAsync(string[] args, CancellationToken)`, `Task<int> CliApp.RunAsync(IReadOnlyList<string> args, CliEnvironment environment, CancellationToken)`.
  - `CliEnvironment { TextWriter Out; TextWriter Error; Func<string, string?> GetVariable; string HomeDirectory; Func<ServePlan, CancellationToken, Task<int>> Serve }` and `CliEnvironment.CreateDefault()`.
  - `StdioServer.RunAsync(ServePlan plan, CancellationToken cancellationToken) : Task<int>`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Server.Tests/Cli/CliAppTests.cs`:

```csharp
using DesktopMcp.Core.Config;
using DesktopMcp.Server.Cli;

namespace DesktopMcp.Server.Tests;

public sealed class CliAppTests : IDisposable
{
    private static readonly string[] ServeFlags =
    [
        "--transport", "--host", "--port", "--debug", "--config", "--auth-key", "--allow-insecure-remote", "--ip-allowlist",
        "--tools", "--exclude-tools", "--cors-origins", "--ssl-certfile", "--ssl-keyfile", "--oauth-client-id",
        "--oauth-client-secret", "--stateless-http", "--control", "--no-control", "--control-idle-timeout", "--control-policy",
    ];

    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dmcp-cli-" + Guid.NewGuid().ToString("N"));
    private ServePlan? _served;

    public CliAppTests() => Directory.CreateDirectory(_home);

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private Task<int> RunAsync(params string[] args) => CliApp.RunAsync(args, new CliEnvironment
    {
        Out = _out,
        Error = _error,
        GetVariable = name => _variables.GetValueOrDefault(name),
        HomeDirectory = _home,
        Serve = (plan, _) =>
        {
            _served = plan;
            return Task.FromResult(0);
        },
    }, CancellationToken.None);

    [Fact]
    public async Task NoArgumentsServesStdioWithDefaults()
    {
        Assert.Equal(0, await RunAsync());
        Assert.NotNull(_served);
        Assert.Equal("stdio", _served.Transport);
        Assert.Equal("localhost", _served.Host);
        Assert.Equal(8000, _served.Port);
        Assert.Equal(new ControlSettings(true, "relaxed", 10.0, 500, 500), _served.Control);
        Assert.Empty(_served.ExplicitTools);
    }

    [Fact]
    public async Task ServeFlagsReachThePlan()
    {
        Assert.Equal(0, await RunAsync("serve", "--port", "9001", "--tools", "Wait, Scrape", "--debug", "--control-policy", "strict", "--no-control"));
        Assert.Equal(9001, _served!.Port);
        Assert.Equal(["Wait", "Scrape"], _served.ExplicitTools);
        Assert.True(_served.Debug);
        Assert.Equal("strict", _served.Control.Policy);
        Assert.False(_served.Control.Enabled);
    }

    [Fact]
    public async Task TheLastControlFlagWins()
    {
        Assert.Equal(0, await RunAsync("serve", "--no-control", "--control"));
        Assert.True(_served!.Control.Enabled);
    }

    [Fact]
    public async Task EnvironmentVariablesFillOptionsThatWereNotGiven()
    {
        _variables["DESKTOP_MCP_EXCLUDE_TOOLS"] = "PowerShell";
        _variables["DESKTOP_MCP_DEBUG"] = "1";
        _variables["DESKTOP_MCP_CONTROL"] = "off";
        _variables["DESKTOP_MCP_CONTROL_IDLE_TIMEOUT"] = "2.5";
        _variables["DESKTOP_MCP_STATELESS_HTTP"] = "yes";
        _variables["DESKTOP_MCP_CONTROL_POLICY"] = "balanced";
        Assert.Equal(0, await RunAsync("serve"));
        Assert.Equal(["PowerShell"], _served!.ExcludeTools);
        Assert.True(_served.Debug);
        Assert.False(_served.Control.Enabled);
        Assert.Equal(2.5, _served.Control.IdleTimeoutSeconds);
        Assert.True(_served.StatelessHttp);
        Assert.Equal("balanced", _served.Control.Policy);
    }

    [Fact]
    public async Task TheCommandLineBeatsTheEnvironment()
    {
        _variables["DESKTOP_MCP_EXCLUDE_TOOLS"] = "PowerShell";
        Assert.Equal(0, await RunAsync("serve", "--exclude-tools", "Registry"));
        Assert.Equal(["Registry"], _served!.ExcludeTools);
    }

    [Fact]
    public async Task AnExplicitConfigFileIsRead()
    {
        var path = Path.Combine(_home, "custom.toml");
        File.WriteAllText(path, "[server]\nport = 8123\n[tools]\nexclude = [\"Click\"]\n");
        Assert.Equal(0, await RunAsync("serve", "--config", path));
        Assert.Equal(8123, _served!.Port);
        Assert.Equal(["Click"], _served.ExcludeTools);
    }

    [Fact]
    public async Task TheDefaultConfigFileIsDiscovered()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".desktop-mcp"));
        File.WriteAllText(Path.Combine(_home, ".desktop-mcp", "config.toml"), "[control]\npolicy = \"strict\"\n");
        Assert.Equal(0, await RunAsync());
        Assert.Equal("strict", _served!.Control.Policy);
    }

    [Fact]
    public async Task AMissingConfigFileIsAnError()
    {
        var path = Path.Combine(_home, "missing.toml");
        Assert.Equal(1, await RunAsync("serve", "--config", path));
        Assert.Contains($"Error: Config file not found: {path}", _error.ToString());
        Assert.Null(_served);
    }

    [Theory]
    [InlineData("sse")]
    [InlineData("streamable-http")]
    public async Task HttpTransportsArriveInP5(string transport)
    {
        Assert.Equal(1, await RunAsync("serve", "--transport", transport));
        Assert.Contains($"Error: The {transport} transport is not available in this build yet; it arrives in phase P5. Use --transport stdio.", _error.ToString());
        Assert.Null(_served);
    }

    [Fact]
    public async Task ARemoteBindWithoutAuthenticationIsRefusedFirst()
    {
        Assert.Equal(1, await RunAsync("serve", "--transport", "streamable-http", "--host", "0.0.0.0"));
        Assert.Contains("Error: Refusing to bind HTTP transport to '0.0.0.0' without authentication.", _error.ToString());
    }

    [Fact]
    public async Task ServeFlagsBeforeASubcommandGetUpstreamsUsageError()
    {
        Assert.Equal(2, await RunAsync("--port", "9000", "--tools", "a b"));
        var error = _error.ToString();
        Assert.Contains("Usage: desktop-mcp [OPTIONS] COMMAND [ARGS]...", error);
        Assert.Contains("Try 'desktop-mcp --help' for help.", error);
        Assert.Contains(
            "Error: `desktop-mcp` is now a command group. Did you mean:\n    desktop-mcp serve --port 9000 --tools 'a b'\n" +
            "These flags belong on the `serve` subcommand. See `desktop-mcp serve --help`.",
            error);
        Assert.Null(_served);
    }

    [Fact]
    public async Task ABadEnvironmentBooleanIsAUsageError()
    {
        _variables["DESKTOP_MCP_STATELESS_HTTP"] = "maybe";
        Assert.Equal(2, await RunAsync("serve"));
        Assert.Contains("Usage: desktop-mcp serve [OPTIONS]", _error.ToString());
        Assert.Contains("Error: Invalid value for '--stateless-http': 'maybe' is not a valid boolean.", _error.ToString());
    }

    [Fact]
    public async Task ABadIdleTimeoutIsAUsageError()
    {
        Assert.Equal(2, await RunAsync("serve", "--control-idle-timeout", "0"));
        Assert.Contains("Error: Invalid value for '--control-idle-timeout': control.idle_timeout_seconds must be a finite positive number", _error.ToString());
    }

    [Fact]
    public async Task ABadPolicyInTheEnvironmentIsAUsageError()
    {
        _variables["DESKTOP_MCP_CONTROL_POLICY"] = "lax";
        Assert.Equal(2, await RunAsync("serve"));
        Assert.Contains("Error: Invalid value for '--control-policy': 'lax' is not one of 'relaxed', 'balanced', 'strict'.", _error.ToString());
    }

    [Fact]
    public async Task AnUnknownOptionIsAUsageError()
    {
        Assert.Equal(2, await RunAsync("serve", "--bogus"));
        Assert.Contains("Usage: desktop-mcp serve [OPTIONS]", _error.ToString());
        Assert.Contains("--bogus", _error.ToString());
    }

    [Theory]
    [InlineData("install --force")]
    [InlineData("uninstall")]
    [InlineData("auth --with-tls --port 9000")]
    public async Task InstallUninstallAndAuthArriveInP6(string commandLine)
    {
        var args = commandLine.Split(' ');
        Assert.Equal(1, await RunAsync(args));
        Assert.Contains($"Error: `desktop-mcp {args[0]}` is not available in this build yet; it arrives in phase P6.", _error.ToString());
    }

    [Fact]
    public async Task ServeHelpListsEveryFlag()
    {
        Assert.Equal(0, await RunAsync("serve", "--help"));
        var help = _out.ToString();
        Assert.All(ServeFlags, flag => Assert.Contains(flag, help));
        Assert.Contains("Comma-separated list of tools to remove from the active set", help);
    }

    [Fact]
    public async Task RootAndSubcommandHelpIsComplete()
    {
        Assert.Equal(0, await RunAsync("--help"));
        Assert.All(new[] { "serve", "install", "uninstall", "auth" }, command => Assert.Contains(command, _out.ToString()));
        Assert.Equal(0, await RunAsync("install", "--help"));
        Assert.Contains("--force", _out.ToString());
        Assert.Equal(0, await RunAsync("auth", "--help"));
        Assert.Contains("--with-tls", _out.ToString());
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Server.Tests --filter "FullyQualifiedName~CliAppTests"`
Expected: build FAILS with `error CS0234: The type or namespace name 'Cli' does not exist in the namespace 'DesktopMcp.Server'`.

- [ ] **Step 3: Add System.CommandLine**

In `windows/Directory.Packages.props` add:

```xml
    <!-- Published 2026-09-08 (2.0.13 is newer than two weeks). AOT probe on 2026-10-19: zero warnings. -->
    <PackageVersion Include="System.CommandLine" Version="2.0.12" />
```

In `windows/src/DesktopMcp.Server/DesktopMcp.Server.csproj`, add to the first `ItemGroup`:

```xml
    <PackageReference Include="System.CommandLine" />
```

- [ ] **Step 4: Implement the CLI**

`windows/src/DesktopMcp.Server/Cli/CliUsageException.cs`:

```csharp
namespace DesktopMcp.Server.Cli;

/// <summary>A bad option value; reported in click's usage-error shape with exit code 2.</summary>
internal sealed class CliUsageException(string message) : Exception(message);
```

`windows/src/DesktopMcp.Server/Cli/CliEnvironment.cs`:

```csharp
using DesktopMcp.Core.Config;
using DesktopMcp.Server.Hosting;

namespace DesktopMcp.Server.Cli;

/// <summary>Where the CLI reads and writes. Tests replace every member; production uses the console and the process.</summary>
public sealed class CliEnvironment
{
    public required TextWriter Out { get; init; }
    public required TextWriter Error { get; init; }
    public required Func<string, string?> GetVariable { get; init; }
    public required string HomeDirectory { get; init; }
    public required Func<ServePlan, CancellationToken, Task<int>> Serve { get; init; }

    public static CliEnvironment CreateDefault() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        GetVariable = Environment.GetEnvironmentVariable,
        HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Serve = StdioServer.RunAsync,
    };
}
```

`windows/src/DesktopMcp.Server/Cli/EnvValues.cs`:

```csharp
using System.Globalization;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Server.Cli;

/// <summary>click's parsing of option values that come from environment variables. An empty value means unset.</summary>
internal static class EnvValues
{
    private static readonly string[] TrueWords = ["1", "true", "t", "yes", "y", "on"];
    private static readonly string[] FalseWords = ["0", "false", "f", "no", "n", "off"];

    public static bool? Bool(string? raw, string option)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var value = raw.Trim().ToLowerInvariant();
        if (TrueWords.Contains(value)) return true;
        if (FalseWords.Contains(value)) return false;
        throw new CliUsageException($"Invalid value for '{option}': {PyText.Repr(raw)} is not a valid boolean.");
    }

    /// <summary>Python's float(): surrounding blanks, underscores, inf and nan are accepted.</summary>
    public static double? Float(string? raw, string option)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var text = raw.Trim().ToLowerInvariant().Replace("_", "");
        double? value = text switch
        {
            "inf" or "+inf" or "infinity" or "+infinity" => double.PositiveInfinity,
            "-inf" or "-infinity" => double.NegativeInfinity,
            "nan" or "+nan" or "-nan" => double.NaN,
            _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
        };
        return value ?? throw new CliUsageException($"Invalid value for '{option}': {PyText.Repr(raw)} is not a valid float.");
    }

    public static string? Choice(string? raw, string option, IReadOnlyList<string> choices)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (choices.Contains(raw)) return raw;
        throw new CliUsageException(
            $"Invalid value for '{option}': {PyText.Repr(raw)} is not one of {string.Join(", ", choices.Select(PyText.Repr))}.");
    }

    /// <summary>upstream is_debug (config.py:4-6).</summary>
    public static bool IsDebug(string? raw) => raw is not null && raw.ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
```

`windows/src/DesktopMcp.Server/Cli/ServeOptions.cs`:

```csharp
using System.CommandLine;
using DesktopMcp.Core.Config;

namespace DesktopMcp.Server.Cli;

/// <summary>The serve command's options with upstream's help texts (__main__.py:551-698), renamed for desktop-mcp.</summary>
internal sealed class ServeOptions
{
    public ServeOptions()
    {
        Transport.AcceptOnlyFromAmong([.. ConfigFile.Transports]);
        ControlPolicy.AcceptOnlyFromAmong([.. ConfigFile.Policies]);
    }

    public Option<string> Transport { get; } = new("--transport") { Description = "The transport layer used by the MCP server.", DefaultValueFactory = _ => "stdio" };
    public Option<string> Host { get; } = new("--host") { Description = "Host to bind the SSE/Streamable HTTP server.", DefaultValueFactory = _ => "localhost" };
    public Option<int> Port { get; } = new("--port") { Description = "Port to bind the SSE/Streamable HTTP server.", DefaultValueFactory = _ => 8000 };
    public Option<bool> Debug { get; } = new("--debug") { Description = "Enable debug mode to provide verbose logging for troubleshooting.", Arity = ArgumentArity.Zero };
    public Option<string> Config { get; } = new("--config") { Description = "Path to desktop-mcp config file (default: ~/.desktop-mcp/config.toml)." };
    public Option<string> AuthKey { get; } = new("--auth-key") { Description = "Bearer token required on all HTTP requests. Can also be set via DESKTOP_MCP_AUTH_KEY." };
    public Option<bool> AllowInsecureRemote { get; } = new("--allow-insecure-remote") { Description = "Allow binding to non-loopback addresses without authentication (not recommended).", Arity = ArgumentArity.Zero };
    public Option<string> IpAllowlist { get; } = new("--ip-allowlist") { Description = "Comma-separated list of allowed client IPs or CIDR ranges (e.g. '10.0.0.0/8,192.168.1.5'). IPv4 and IPv6 supported." };
    public Option<string> Tools { get; } = new("--tools") { Description = "Comma-separated explicit list of tools to enable (e.g. 'Screenshot,Click,Snapshot'). Overrides --exclude-tools." };
    public Option<string> ExcludeTools { get; } = new("--exclude-tools") { Description = "Comma-separated list of tools to remove from the active set (e.g. 'PowerShell,Registry')." };
    public Option<string> CorsOrigins { get; } = new("--cors-origins") { Description = "Comma-separated list of allowed CORS origins (e.g. 'https://my-client.example.com'). Defaults to none — no CORS headers are emitted, so browsers block cross-origin requests via Same-Origin Policy." };
    public Option<string> SslCertfile { get; } = new("--ssl-certfile") { Description = "Path to TLS certificate file (.pem) for HTTPS. Requires --ssl-keyfile." };
    public Option<string> SslKeyfile { get; } = new("--ssl-keyfile") { Description = "Path to TLS private key file (.pem) for HTTPS. Requires --ssl-certfile." };
    public Option<string> OAuthClientId { get; } = new("--oauth-client-id") { Description = "OAuth client ID (pre-provisioned confidential client). Requires --oauth-client-secret." };
    public Option<string> OAuthClientSecret { get; } = new("--oauth-client-secret") { Description = "OAuth client secret. Requires --oauth-client-id." };
    public Option<bool> StatelessHttp { get; } = new("--stateless-http") { Description = "Run the streamable-http transport in stateless mode (no Mcp-Session-Id header). Lets reconnecting clients survive a server restart without re-handshaking, and removes the per-connection state that prevents horizontal scaling. Has no effect on stdio/sse transports.", Arity = ArgumentArity.Zero };
    public Option<bool> Control { get; } = new("--control") { Description = "Enable desktop ownership, input protection and control indicator (the default). Restart required.", Arity = ArgumentArity.Zero };
    public Option<bool> NoControl { get; } = new("--no-control") { Description = "Disable desktop ownership, input protection and control indicator. Restart required.", Arity = ArgumentArity.Zero };
    public Option<double> ControlIdleTimeout { get; } = new("--control-idle-timeout") { Description = "Positive finite user idle wait in seconds before control resumes. Restart required.", DefaultValueFactory = _ => 10.0 };
    public Option<string> ControlPolicy { get; } = new("--control-policy") { Description = "Desktop gate: relaxed (direct actions), balanced (also possible interference), strict (all tools). Restart required.", DefaultValueFactory = _ => "relaxed" };

    public IEnumerable<Option> All =>
    [
        Transport, Host, Port, Debug, Config, AuthKey, AllowInsecureRemote, IpAllowlist, Tools, ExcludeTools, CorsOrigins,
        SslCertfile, SslKeyfile, OAuthClientId, OAuthClientSecret, StatelessHttp, Control, NoControl, ControlIdleTimeout, ControlPolicy,
    ];

    /// <summary>The values given on the command line, else through their DESKTOP_MCP_* variable (Decisions 2).</summary>
    public ServeArguments Read(ParseResult result, Func<string, string?> environment)
    {
        var idle = Given(result, ControlIdleTimeout)
            ? result.GetValue(ControlIdleTimeout)
            : EnvValues.Float(environment("DESKTOP_MCP_CONTROL_IDLE_TIMEOUT"), "--control-idle-timeout");
        if (idle is { } seconds)
        {
            try { IdleTimeout.Validate(seconds); }
            catch (ConfigException e) { throw new CliUsageException($"Invalid value for '--control-idle-timeout': {e.Message}"); }
        }

        return new ServeArguments
        {
            Transport = Given(result, Transport) ? result.GetValue(Transport) : null,
            Host = Given(result, Host) ? result.GetValue(Host) : null,
            Port = Given(result, Port) ? result.GetValue(Port) : null,
            Debug = result.GetValue(Debug) || EnvValues.IsDebug(environment("DESKTOP_MCP_DEBUG")),
            AuthKey = StringOrVariable(result, AuthKey, environment, "DESKTOP_MCP_AUTH_KEY"),
            AllowInsecureRemote = Given(result, AllowInsecureRemote) ? true : null,
            IpAllowlist = StringOrVariable(result, IpAllowlist, environment, "DESKTOP_MCP_IP_ALLOWLIST"),
            Tools = StringOrVariable(result, Tools, environment, "DESKTOP_MCP_TOOLS"),
            ExcludeTools = StringOrVariable(result, ExcludeTools, environment, "DESKTOP_MCP_EXCLUDE_TOOLS"),
            CorsOrigins = StringOrVariable(result, CorsOrigins, environment, "DESKTOP_MCP_CORS_ORIGINS"),
            SslCertfile = StringOrVariable(result, SslCertfile, environment, "DESKTOP_MCP_SSL_CERTFILE"),
            SslKeyfile = StringOrVariable(result, SslKeyfile, environment, "DESKTOP_MCP_SSL_KEYFILE"),
            OAuthClientId = StringOrVariable(result, OAuthClientId, environment, "DESKTOP_MCP_OAUTH_CLIENT_ID"),
            OAuthClientSecret = StringOrVariable(result, OAuthClientSecret, environment, "DESKTOP_MCP_OAUTH_CLIENT_SECRET"),
            StatelessHttp = Given(result, StatelessHttp) ? true : EnvValues.Bool(environment("DESKTOP_MCP_STATELESS_HTTP"), "--stateless-http"),
            Control = LastControlFlag(result) ?? EnvValues.Bool(environment("DESKTOP_MCP_CONTROL"), "--control"),
            ControlIdleTimeout = idle,
            ControlPolicy = Given(result, ControlPolicy)
                ? result.GetValue(ControlPolicy)
                : EnvValues.Choice(environment("DESKTOP_MCP_CONTROL_POLICY"), "--control-policy", ConfigFile.Policies),
        };
    }

    private static bool Given(ParseResult result, Option option) => result.GetResult(option) is { Implicit: false };

    private static string? StringOrVariable(ParseResult result, Option<string> option, Func<string, string?> environment, string variable) =>
        Given(result, option) ? result.GetValue(option) : environment(variable) is { Length: > 0 } value ? value : null;

    // click's --control/--no-control is one option; the last occurrence wins.
    private static bool? LastControlFlag(ParseResult result)
    {
        bool? value = null;
        foreach (var token in result.Tokens)
        {
            if (token.Value == "--control") value = true;
            else if (token.Value == "--no-control") value = false;
        }
        return value;
    }
}
```

`windows/src/DesktopMcp.Server/Cli/CliApp.cs`:

```csharp
using System.CommandLine;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Server.Cli;

/// <summary>
/// The desktop-mcp command line (spec §8): serve (the default), install, uninstall and auth with upstream's flags and
/// messages (__main__.py:397-1168). In P1, install, uninstall, auth and the HTTP transports parse their options and
/// report the phase that brings them.
/// </summary>
public static class CliApp
{
    public const string Name = "desktop-mcp";

    private static readonly string[] SubcommandNames = ["serve", "install", "uninstall", "auth"];

    // __main__.py:397-417
    private static readonly HashSet<string> LegacyServeFlags =
    [
        "--transport", "--host", "--port", "--auth-key", "--stateless-http", "--ip-allowlist", "--cors-origins",
        "--ssl-certfile", "--ssl-keyfile", "--oauth-client-id", "--oauth-client-secret", "--tools", "--exclude-tools",
        "--allow-insecure-remote", "--debug", "--control", "--no-control", "--control-idle-timeout", "--control-policy",
    ];

    public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default) =>
        RunAsync(args, CliEnvironment.CreateDefault(), cancellationToken);

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CliEnvironment environment, CancellationToken cancellationToken)
    {
        // upstream _LegacyAwareGroup (__main__.py:420-444)
        if (FirstLegacyArgument(args) is not null)
        {
            var suggested = PyText.ShellJoin([Name, "serve", .. args]);
            return UsageError(environment, Name, "[OPTIONS] COMMAND [ARGS]...",
                $"`{Name}` is now a command group. Did you mean:\n    {suggested}\n" +
                $"These flags belong on the `serve` subcommand. See `{Name} serve --help`.");
        }

        // A bare desktop-mcp serves (Decisions 1).
        IReadOnlyList<string> effective = args.Count == 0 ? ["serve"] : args;
        var root = BuildRootCommand(environment);
        var result = root.Parse(effective, new ParserConfiguration { ResponseFileTokenReplacer = null });
        if (result.Errors.Count > 0)
        {
            var command = result.CommandResult.Command;
            var isRoot = ReferenceEquals(command, root);
            return UsageError(environment, isRoot ? Name : $"{Name} {command.Name}", isRoot ? "[OPTIONS] COMMAND [ARGS]..." : "[OPTIONS]",
                string.Join("\n", result.Errors.Select(e => e.Message)));
        }
        return await result.InvokeAsync(new InvocationConfiguration { Output = environment.Out, Error = environment.Error }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? FirstLegacyArgument(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (SubcommandNames.Contains(arg)) return null;
            if (LegacyServeFlags.Contains(arg.Split('=', 2)[0])) return arg;
        }
        return null;
    }

    private static RootCommand BuildRootCommand(CliEnvironment environment)
    {
        var root = new RootCommand($"{Name}: MCP server for Windows desktop automation.");

        var serveOptions = new ServeOptions();
        var serve = new Command("serve", "Run the MCP server (the default command).");
        foreach (var option in serveOptions.All) serve.Options.Add(option);
        serve.SetAction((result, cancellationToken) => ServeAsync(result, serveOptions, environment, cancellationToken));
        root.Subcommands.Add(serve);

        root.Subcommands.Add(NotYetAvailable(environment, "install", "Install desktop-mcp as a scheduled task that starts at login.", "P6",
            Choice("--transport", "Transport for the background server (stdio not supported as a service).", "streamable-http", "sse", "streamable-http"),
            Text("--host", "Host to bind.", "127.0.0.1"),
            Number("--port", "Port to bind.", 8000),
            Flag("--force", "Reinstall even if already installed.")));
        root.Subcommands.Add(NotYetAvailable(environment, "uninstall", "Remove the desktop-mcp scheduled task and stop the background server.", "P6"));
        root.Subcommands.Add(NotYetAvailable(environment, "auth", "Generate an auth key (and optionally TLS certs) and save to ~/.desktop-mcp/config.toml.", "P6",
            Choice("--transport", "Transport mode to configure. Saves the choice to config.toml.", "sse", "stdio", "sse", "streamable-http"),
            Text("--host", "Host to bind the server to.", "0.0.0.0"),
            Number("--port", "Port to bind the server to.", 8000),
            Flag("--with-tls", "Generate a self-signed TLS certificate and key."),
            Flag("--force", "Overwrite existing credentials without prompting.")));
        return root;
    }

    private static async Task<int> ServeAsync(ParseResult result, ServeOptions options, CliEnvironment environment, CancellationToken cancellationToken)
    {
        ServeArguments arguments;
        try { arguments = options.Read(result, environment.GetVariable); }
        catch (CliUsageException e) { return UsageError(environment, $"{Name} serve", "[OPTIONS]", e.Message); }

        ServePlan plan;
        try
        {
            var config = ConfigFile.Load(ConfigFile.Discover(result.GetValue(options.Config), environment.HomeDirectory));
            plan = ServePlan.Resolve(arguments, config);
        }
        catch (ConfigException e)
        {
            environment.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }

        if (plan.Transport != "stdio")
        {
            environment.Error.WriteLine($"Error: The {plan.Transport} transport is not available in this build yet; it arrives in phase P5. Use --transport stdio.");
            return 1;
        }
        return await environment.Serve(plan, cancellationToken).ConfigureAwait(false);
    }

    private static Command NotYetAvailable(CliEnvironment environment, string name, string description, string phase, params Option[] options)
    {
        var command = new Command(name, description);
        foreach (var option in options) command.Options.Add(option);
        command.SetAction(_ =>
        {
            environment.Error.WriteLine($"Error: `{Name} {name}` is not available in this build yet; it arrives in phase {phase}.");
            return 1;
        });
        return command;
    }

    private static Option<string> Choice(string name, string description, string defaultValue, params string[] choices)
    {
        var option = new Option<string>(name) { Description = description, DefaultValueFactory = _ => defaultValue };
        option.AcceptOnlyFromAmong(choices);
        return option;
    }

    private static Option<string> Text(string name, string description, string defaultValue) =>
        new(name) { Description = description, DefaultValueFactory = _ => defaultValue };

    private static Option<int> Number(string name, string description, int defaultValue) =>
        new(name) { Description = description, DefaultValueFactory = _ => defaultValue };

    private static Option<bool> Flag(string name, string description) =>
        new(name) { Description = description, Arity = ArgumentArity.Zero };

    // click's UsageError output (exit code 2).
    private static int UsageError(CliEnvironment environment, string commandPath, string usageArguments, string message)
    {
        environment.Error.WriteLine($"Usage: {commandPath} {usageArguments}");
        environment.Error.WriteLine($"Try '{commandPath} --help' for help.");
        environment.Error.WriteLine();
        environment.Error.WriteLine($"Error: {message}");
        return 2;
    }
}
```

- [ ] **Step 5: Implement the stdio host**

`windows/src/DesktopMcp.Server/Hosting/StderrLoggerProvider.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace DesktopMcp.Server.Hosting;

/// <summary>
/// Plain log lines on stderr; stdout carries the stdio protocol. Hand-written instead of the console logger so there is
/// no options binding to worry about under NativeAOT.
/// </summary>
internal sealed class StderrLoggerProvider : ILoggerProvider
{
    private static readonly Lock Gate = new();

    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);

    public void Dispose() { }

    private sealed class StderrLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{Level(logLevel)}: {category}: {formatter(state, exception)}";
            lock (Gate)
            {
                Console.Error.WriteLine(line);
                if (exception is not null) Console.Error.WriteLine(exception);
            }
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            _ => "crit",
        };
    }
}
```

`windows/src/DesktopMcp.Server/Hosting/StdioServer.cs`:

```csharp
using DesktopMcp.Core;
using DesktopMcp.Core.Config;
using DesktopMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DesktopMcp.Server.Hosting;

/// <summary>serve --transport stdio: Generic Host + the SDK's stdio transport; logs on stderr only (spec §8).</summary>
public static class StdioServer
{
    public static async Task<int> RunAsync(ServePlan plan, CancellationToken cancellationToken)
    {
        // upstream serve sets NO_COLOR for stdio (__main__.py:725-726).
        if (Environment.GetEnvironmentVariable("NO_COLOR") is null) Environment.SetEnvironmentVariable("NO_COLOR", "1");

        var catalog = ToolSpecCatalog.LoadEmbedded();
        var kept = ToolFilter.Apply(catalog.Names, plan.ExplicitTools, plan.ExcludeTools).ToHashSet(StringComparer.Ordinal);
        var tools = ToolRegistry.CreateAll(catalog, ToolServices.CreateDefault()).Where(tool => kept.Contains(tool.ProtocolTool.Name)).ToArray();

        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new StderrLoggerProvider());
        builder.Logging.SetMinimumLevel(plan.Debug ? LogLevel.Debug : LogLevel.Warning);
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = ServerText.Name, Version = ServerText.Version };
                options.ServerInstructions = ServerText.Instructions;
            })
            .WithStdioServerTransport()
            .WithTools(tools);

        var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("desktop-mcp");
        foreach (var warning in plan.Warnings) logger.LogWarning("{Warning}", warning);
        logger.LogDebug("Starting desktop-mcp (transport=stdio, tools={Tools}, exclude={Exclude})",
            plan.ExplicitTools.Count > 0 ? string.Join(",", plan.ExplicitTools) : "all",
            plan.ExcludeTools.Count > 0 ? string.Join(",", plan.ExcludeTools) : "none");
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
```

Replace `windows/src/DesktopMcp/Program.cs` with:

```csharp
using DesktopMcp.Server.Cli;

// The CLI (spec §8) lives in DesktopMcp.Server so it can be tested; this exe is only the composition root.
return await CliApp.RunAsync(args);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each project, including all `CliAppTests`.

- [ ] **Step 7: Exercise --help and config.toml in the AOT smoke test**

In `windows/scripts/smoke-stdio.ps1` replace the two lines

```powershell
$utf8 = [System.Text.UTF8Encoding]::new($false)
$psi = [System.Diagnostics.ProcessStartInfo]::new((Resolve-Path $Exe).Path)
```

with

```powershell
$exePath = (Resolve-Path $Exe).Path
# The CLI and Tomlyn only run under NativeAOT here (xUnit runs them under the JIT).
$help = (& $exePath serve --help) -join "`n"
if ($LASTEXITCODE -ne 0 -or -not $help.Contains('--exclude-tools')) { throw "serve --help failed ($LASTEXITCODE): $help" }
$config = Join-Path ([IO.Path]::GetTempPath()) "desktop-mcp-smoke-$PID.toml"
[IO.File]::WriteAllText($config, "[server]`ntransport = `"stdio`"`n`n[control]`npolicy = `"relaxed`"`n")

$utf8 = [System.Text.UTF8Encoding]::new($false)
$psi = [System.Diagnostics.ProcessStartInfo]::new($exePath)
foreach ($argument in 'serve', '--config', $config) { $psi.ArgumentList.Add($argument) }
```

and in the `finally` block add `Remove-Item $config -ErrorAction SilentlyContinue` as its last line.

Run: `pwsh scripts/build-release.ps1 -Runtimes win-x64`
Expected: the publish reports no warnings; `win-x64: ... folder OK (desktop-mcp.exe, desktop-mcp.pdb)`; `smoke OK: desktop-mcp 0.1.0, 21 tools match spec/windows/tools.json, graphics stack not loaded (...)`; `AOT activation check OK: ...`.

Run: `dist/win-x64/desktop-mcp.exe --port 9000; $LASTEXITCODE`
Expected: stderr shows the `Usage: desktop-mcp [OPTIONS] COMMAND [ARGS]...` block ending in ``These flags belong on the `serve` subcommand. See `desktop-mcp serve --help`.``, and `$LASTEXITCODE` prints `2`.

- [ ] **Step 8: Commit**

```bash
git add windows/Directory.Packages.props windows/src/DesktopMcp.Server windows/src/DesktopMcp/Program.cs windows/tests/DesktopMcp.Server.Tests/Cli windows/scripts/smoke-stdio.ps1
git commit -m "Add the serve, install, uninstall and auth CLI and serve stdio through it" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: PowerShell

upstream `PowerShellExecutor` (`powershell/service.py:212-273`, `powershell/utils.py:310-448`, environment at `service.py:25-175`) and the tool (`tools/shell.py:22-27`): `Response: {output}\nStatus Code: {rc}`. The process runs `pwsh` from the child's `PATH`, else `powershell`, else the System32 path. It gets `-NoProfile` (plus `-OutputFormat Text` for Windows PowerShell) and `-EncodedCommand` of the UTF-8 preamble plus the command. Stdin is closed, the flags are `CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW`, the working directory is the user profile, the environment is rebuilt from HKLM/HKCU, and `NO_COLOR=1` is set. Output is stdout, or stderr when stdout is empty. A timeout returns `Command execution timed out` with status 1, and `Access is denied` gets the elevation hint when the server is not elevated. Cancellation and timeouts send Ctrl+Break, wait 2 s, then kill the tree (spec §5). This task also adds the startup Job Object (spec §5, Decisions 19).

The tests run the real `pwsh` (or `powershell`). They never depend on a profile (`-NoProfile`) and keep their timeouts short.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Interop/CoreNative.cs`
- Create: `windows/src/DesktopMcp.Core/Elevation.cs`
- Create: `windows/src/DesktopMcp.Core/PowerShell/ShellEnvironment.cs`, `PowerShellRunner.cs`, `ProcessJob.cs`
- Create: `windows/src/DesktopMcp.Server/Tools/PowerShellTool.cs`
- Modify: `windows/src/DesktopMcp.Server/Tools/ToolServices.cs`, `ToolRegistry.cs`, `windows/src/DesktopMcp.Server/Hosting/StdioServer.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/PowerShell/ShellEnvironmentTests.cs`, `PowerShellRunnerTests.cs`, `ProcessJobTests.cs`; `windows/tests/DesktopMcp.Server.Tests/Tools/PowerShellToolTests.cs`

**Interfaces:**
- Consumes: `ToolArguments`, `ToolResults.Text`, `TempDirectory` (tests).
- Produces:
  - `DesktopMcp.Core.Elevation.IsElevated() : bool`
  - `DesktopMcp.Core.PowerShell.PowerShellResult(string Output, int StatusCode)`
  - `PowerShellRunner()`; `Task<PowerShellResult> RunAsync(string command, long timeoutSeconds, CancellationToken cancellationToken, string? shell = null)`; internal `PowerShellRunner(Func<Dictionary<string, string>> environment, Func<bool> isElevated)`, `Preamble`, `AccessDeniedHint`, `Encode(string)`, `ResolveShell(string?, IReadOnlyDictionary<string, string>)`.
  - `ShellEnvironment.Build() : Dictionary<string, string>`; internal `Build(IReadOnlyDictionary<string, string> inherited, RegistryEnvironment? machine, RegistryEnvironment? user, string computerName, string userName, string userProfile)`, `DedupPath(params string[])`, `FallbackPathExt`, `record RegistryEnvironment(Dictionary<string, string> Variables, string Path, string PathExt)`.
  - `ProcessJob.TryAssignKillOnClose() : bool`
  - `internal static unsafe partial class CoreNative` (Task 9 and Task 11 add entry points to it).
  - `ToolServices.PowerShell` (`PowerShellRunner`).

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Core.Tests/PowerShell/ShellEnvironmentTests.cs`:

```csharp
using DesktopMcp.Core.PowerShell;

namespace DesktopMcp.Core.Tests;

public class ShellEnvironmentTests
{
    private static Dictionary<string, string> Variables(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void DedupPathKeepsTheFirstSpellingAndDropsEmptyEntries() =>
        Assert.Equal(@"C:\A;C:\B\;D:\c", ShellEnvironment.DedupPath(@"C:\A;C:\B\", @"c:\a\;;C:\b", @"D:\c"));

    [Fact]
    public void BuildFillsMissingVariablesAndAppendsRegistryPaths()
    {
        var inherited = Variables(("PATH", @"C:\inherited"), ("TEMP", @"C:\keep"), ("EMPTY", ""), ("PATHEXT", ".COM"));
        var machine = new RegistryEnvironment(Variables(("TEMP", @"C:\machine"), ("EMPTY", "machine"), ("ONLYMACHINE", "m"), ("SHARED", "machine")),
            @"C:\Windows;C:\inherited", ".COM;.EXE;.BAT");
        var user = new RegistryEnvironment(Variables(("SHARED", "user")), @"C:\Users\me\bin", "");

        var environment = ShellEnvironment.Build(inherited, machine, user, "PC1", "me", @"C:\Users\me");

        Assert.Equal(@"C:\inherited;C:\Windows;C:\Users\me\bin", environment["PATH"]);
        Assert.Equal(@"C:\keep", environment["TEMP"]);
        Assert.Equal("machine", environment["EMPTY"]);
        Assert.Equal("m", environment["ONLYMACHINE"]);
        Assert.Equal("user", environment["SHARED"]);
        Assert.Equal(".COM;.EXE;.BAT", environment["PATHEXT"]);
        Assert.Equal("PC1", environment["COMPUTERNAME"]);
        Assert.Equal("me", environment["USERNAME"]);
        Assert.Equal(@"C:\Users\me", environment["USERPROFILE"]);
        Assert.Equal("C:", environment["HOMEDRIVE"]);
        Assert.Equal(@"\Users\me", environment["HOMEPATH"]);
        Assert.Equal("PC1", environment["USERDOMAIN"]);
    }

    [Fact]
    public void BuildNeverOverwritesInheritedValues()
    {
        var inherited = Variables(("USERPROFILE", @"X:\p"), ("COMPUTERNAME", "A"), ("PATHEXT", ".EXE"));
        var machine = new RegistryEnvironment(Variables(), "", ".BAT");
        var environment = ShellEnvironment.Build(inherited, machine, new RegistryEnvironment(Variables(), "", ""), "PC1", "me", @"C:\Users\me");
        Assert.Equal(@"X:\p", environment["USERPROFILE"]);
        Assert.Equal("A", environment["COMPUTERNAME"]);
        Assert.Equal(".EXE", environment["PATHEXT"]);
        Assert.Equal("A", environment["USERDOMAIN"]);
    }

    [Fact]
    public void BuildWithoutTheRegistryFallsBackForPathExt()
    {
        var environment = ShellEnvironment.Build(Variables(), null, null, "PC", "me", @"C:\Users\me");
        Assert.Equal(ShellEnvironment.FallbackPathExt, environment["PATHEXT"]);
    }

    [Fact]
    public void BuildReadsTheRealEnvironment()
    {
        var environment = ShellEnvironment.Build();
        Assert.False(string.IsNullOrEmpty(environment["PATH"]));
        Assert.Contains(".EXE", environment["PATHEXT"], StringComparison.OrdinalIgnoreCase);
    }
}
```

`windows/tests/DesktopMcp.Core.Tests/PowerShell/PowerShellRunnerTests.cs`:

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DesktopMcp.Core.PowerShell;

namespace DesktopMcp.Core.Tests;

/// <summary>Runs the real pwsh (or Windows PowerShell) with -NoProfile and short timeouts.</summary>
public class PowerShellRunnerTests
{
    private static readonly PowerShellRunner Runner = new();

    private static Dictionary<string, string> Variables(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task ReturnsStdoutAsUtf8AndTheExitCode()
    {
        var result = await Runner.RunAsync("Write-Output 'héllo ✓'; exit 3", 30, CancellationToken.None);
        Assert.Equal(3, result.StatusCode);
        Assert.Equal("héllo ✓", result.Output.TrimEnd());
    }

    [Fact]
    public async Task FallsBackToStderrWhenStdoutIsEmpty()
    {
        var result = await Runner.RunAsync("[Console]::Error.WriteLine('only stderr'); exit 1", 30, CancellationToken.None);
        Assert.Equal(1, result.StatusCode);
        Assert.Equal("only stderr", result.Output.TrimEnd());
    }

    [Fact]
    public async Task TimeoutStopsTheWholeProcessTree()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"dmcp-ps-{Guid.NewGuid():N}.txt");
        var command = "$p = Start-Process -FilePath ping.exe -ArgumentList '-n','120','127.0.0.1' -WindowStyle Hidden -PassThru; " +
                      $"Set-Content -LiteralPath '{pidFile}' -Value $p.Id; Start-Sleep -Seconds 120";
        var clock = Stopwatch.StartNew();
        var result = await Runner.RunAsync(command, 8, CancellationToken.None);
        clock.Stop();
        try
        {
            Assert.Equal(new PowerShellResult("Command execution timed out", 1), result);
            Assert.InRange(clock.Elapsed.TotalSeconds, 8, 30);
            var childPid = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);
            Assert.True(HasExited(childPid), $"grandchild {childPid} survived the timeout");
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task CancellationStopsTheCommandAndThrows()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner.RunAsync("Start-Sleep -Seconds 60", 60, cancellation.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task AMissingShellIsReportedWithItsName()
    {
        var result = await Runner.RunAsync("Write-Output x", 10, CancellationToken.None, shell: @"C:\no\such\dir\shell.exe");
        Assert.Equal(1, result.StatusCode);
        Assert.StartsWith("Command execution failed: Win32Exception: ", result.Output);
        Assert.EndsWith(@"(shell: C:\no\such\dir\shell.exe)", result.Output);
    }

    [Fact]
    public async Task AccessDeniedGetsTheElevationHintWhenNotElevated()
    {
        var runner = new PowerShellRunner(ShellEnvironment.Build, () => false);
        var result = await runner.RunAsync("Write-Output 'Access is denied'; exit 5", 30, CancellationToken.None);
        Assert.Equal(5, result.StatusCode);
        Assert.EndsWith(PowerShellRunner.AccessDeniedHint, result.Output);
    }

    [Fact]
    public async Task NoHintWhenElevated()
    {
        var runner = new PowerShellRunner(ShellEnvironment.Build, () => true);
        var result = await runner.RunAsync("Write-Output 'Access is denied'; exit 5", 30, CancellationToken.None);
        Assert.DoesNotContain("HINT", result.Output);
    }

    [Fact]
    public void EncodeIsUtf16LeBase64OfThePreambleAndCommand() =>
        Assert.Equal(PowerShellRunner.Preamble + "Get-Date", Encoding.Unicode.GetString(Convert.FromBase64String(PowerShellRunner.Encode("Get-Date"))));

    [Fact]
    public void ResolveShellPrefersPwshOnTheChildPath()
    {
        using var temp = new TempDirectory();
        var pwsh = temp.WriteFile("pwsh.exe", "");
        Assert.Equal(pwsh, PowerShellRunner.ResolveShell(null, Variables(("PATH", temp.Path), ("PATHEXT", ".COM;.EXE"))));
    }

    [Fact]
    public void ResolveShellFallsBackToSystem32WindowsPowerShell() =>
        Assert.Equal(@"X:\Win\System32\WindowsPowerShell\v1.0\powershell.exe",
            PowerShellRunner.ResolveShell(null, Variables(("PATH", ""), ("SystemRoot", @"X:\Win"))));

    [Fact]
    public void ResolveShellKeepsAnUnknownExplicitShell() =>
        Assert.Equal("nushell", PowerShellRunner.ResolveShell("nushell", Variables(("PATH", ""))));

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
```

`windows/tests/DesktopMcp.Core.Tests/PowerShell/ProcessJobTests.cs`:

```csharp
using DesktopMcp.Core.Interop;
using DesktopMcp.Core.PowerShell;

namespace DesktopMcp.Core.Tests;

/// <summary>Side effect: the test host joins a kill-on-close job, so its children die when it exits.</summary>
public class ProcessJobTests
{
    [Fact]
    public void PutsTheCurrentProcessInAKillOnCloseJob()
    {
        Assert.True(ProcessJob.TryAssignKillOnClose());
        Assert.True(ProcessJob.TryAssignKillOnClose());
        Assert.True(CoreNative.IsProcessInJob(CoreNative.GetCurrentProcess(), 0, out var inJob));
        Assert.True(inJob);
    }
}
```

`windows/tests/DesktopMcp.Server.Tests/Tools/PowerShellToolTests.cs`:

```csharp
namespace DesktopMcp.Server.Tests;

public class PowerShellToolTests
{
    [Fact]
    public async Task ReturnsTheResponseAndStatusCode()
    {
        var text = ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("PowerShell"), """{"command": "Write-Output ok", "timeout": "20"}"""));
        Assert.Equal("Response: ok\r\n\nStatus Code: 0", text);
    }

    [Fact]
    public async Task ATimeoutIsAResultNotAnError()
    {
        var text = ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("PowerShell"), """{"command": "Start-Sleep -Seconds 30", "timeout": 1}"""));
        Assert.Equal("Response: Command execution timed out\nStatus Code: 1", text);
    }

    [Fact]
    public async Task AMissingCommandIsAnError()
    {
        var result = await ToolCalls.CallAsync(ToolCalls.Create("PowerShell"), "{}");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'PowerShell': Missing required argument: command", ToolCalls.Text(result));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~ShellEnvironmentTests|FullyQualifiedName~PowerShellRunnerTests|FullyQualifiedName~ProcessJobTests"`
Expected: build FAILS with `error CS0234: The type or namespace name 'PowerShell' does not exist in the namespace 'DesktopMcp.Core'`.

- [ ] **Step 3: Add the Core interop and elevation check**

`windows/src/DesktopMcp.Core/Interop/CoreNative.cs`:

```csharp
using System.Runtime.InteropServices;

namespace DesktopMcp.Core.Interop;

/// <summary>Hand-declared Win32 entry points for Core. Layouts assume a 64-bit process (win-x64, win-arm64).</summary>
internal static unsafe partial class CoreNative
{
    // kernel32: console control events (the PowerShell graceful stop)
    internal const uint CtrlBreakEvent = 1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    // kernel32: Job Objects (spec §5)
    internal const uint JobObjectLimitKillOnJobClose = 0x2000;
    internal const int JobObjectExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    internal static partial nint CreateJobObject(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(nint job, int infoClass, JobObjectExtendedLimitInformation* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
```

`windows/src/DesktopMcp.Core/Elevation.cs`:

```csharp
using System.Security.Principal;

namespace DesktopMcp.Core;

/// <summary>upstream is_elevated (desktop/utils.py:47-54, IsUserAnAdmin): true only for an elevated administrator token.</summary>
public static class Elevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
```

- [ ] **Step 4: Implement the environment, the runner and the job**

`windows/src/DesktopMcp.Core/PowerShell/ShellEnvironment.cs`:

```csharp
using System.Collections;
using System.Security;
using Microsoft.Win32;

namespace DesktopMcp.Core.PowerShell;

/// <summary>The variables of one registry Environment key: PATH and PATHEXT apart, other string values by name.</summary>
internal sealed record RegistryEnvironment(Dictionary<string, string> Variables, string Path, string PathExt);

/// <summary>
/// upstream _prepare_env (powershell/service.py:98-175). MCP hosts may start the server with a stripped environment,
/// so the child's environment starts from the server's and fills in what is missing from HKLM and HKCU (HKCU wins),
/// appends missing PATH entries, repairs PATHEXT and adds the session variables. Inherited values are never replaced.
/// </summary>
public static class ShellEnvironment
{
    internal const string FallbackPathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC;.CPL;.PY;.PYW";
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public static Dictionary<string, string> Build()
    {
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            inherited[(string)entry.Key] = entry.Value as string ?? "";
        RegistryEnvironment? machine = null, user = null;
        try
        {
            machine = ReadRegistry(Registry.LocalMachine, MachineKey);
            user = ReadRegistry(Registry.CurrentUser, "Environment");
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException)
        {
            machine = user = null;
        }
        return Build(inherited, machine, user, Environment.MachineName, Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    internal static Dictionary<string, string> Build(IReadOnlyDictionary<string, string> inherited, RegistryEnvironment? machine,
        RegistryEnvironment? user, string computerName, string userName, string userProfile)
    {
        var environment = new Dictionary<string, string>(inherited, StringComparer.OrdinalIgnoreCase);
        if (machine is not null && user is not null)
        {
            var merged = new Dictionary<string, string>(machine.Variables, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in user.Variables) merged[name] = value;
            foreach (var (name, value) in merged)
            {
                if (string.IsNullOrEmpty(environment.GetValueOrDefault(name))) environment[name] = value;
            }
            if (machine.Path.Length > 0 || user.Path.Length > 0)
                environment["PATH"] = DedupPath(environment.GetValueOrDefault("PATH") ?? "", machine.Path, user.Path);
            var pathExt = user.PathExt.Length > 0 ? user.PathExt : machine.PathExt;
            if (pathExt.Length > 0 && !HasExe(environment)) environment["PATHEXT"] = pathExt;
        }
        else if (!HasExe(environment))
        {
            environment["PATHEXT"] = FallbackPathExt;
        }

        if (string.IsNullOrEmpty(environment.GetValueOrDefault("COMPUTERNAME")) && computerName.Length > 0) environment["COMPUTERNAME"] = computerName;
        if (string.IsNullOrEmpty(environment.GetValueOrDefault("USERNAME")) && userName.Length > 0) environment["USERNAME"] = userName;
        environment.TryAdd("USERPROFILE", userProfile);
        // os.path.splitdrive: "C:" and "\Users\me", or "\\server\share" and the rest.
        var drive = Path.GetPathRoot(userProfile)?.TrimEnd('\\') ?? "";
        environment.TryAdd("HOMEDRIVE", drive);
        environment.TryAdd("HOMEPATH", userProfile[drive.Length..]);
        environment.TryAdd("USERDOMAIN", environment.GetValueOrDefault("COMPUTERNAME") ?? "");
        return environment;
    }

    /// <summary>upstream _dedup_path: join, then drop empty and repeated entries (case-insensitive, trailing \ ignored).</summary>
    internal static string DedupPath(params string[] segments)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var entry in string.Join(';', segments.Where(s => s.Length > 0)).Split(';'))
        {
            var normalized = entry.ToLowerInvariant().TrimEnd('\\');
            if (normalized.Length > 0 && seen.Add(normalized)) kept.Add(entry);
        }
        return string.Join(';', kept);
    }

    // upstream _read_reg_env: REG_SZ and REG_EXPAND_SZ values (the latter expanded against this process's environment);
    // a missing or unreadable key gives nothing.
    internal static RegistryEnvironment ReadRegistry(RegistryKey hive, string subKey)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string path = "", pathExt = "";
        try
        {
            using var key = hive.OpenSubKey(subKey);
            if (key is not null)
            {
                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValueKind(name) is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;
                    var value = key.GetValue(name) as string ?? "";
                    if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase)) path = value;
                    else if (name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase)) pathExt = value;
                    else variables[name] = value;
                }
            }
        }
        catch (IOException)
        {
        }
        return new RegistryEnvironment(variables, path, pathExt);
    }

    private static bool HasExe(Dictionary<string, string> environment) =>
        (environment.GetValueOrDefault("PATHEXT") ?? "").Contains(".EXE", StringComparison.OrdinalIgnoreCase);
}
```

`windows/src/DesktopMcp.Core/PowerShell/PowerShellRunner.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DesktopMcp.Core.Interop;

namespace DesktopMcp.Core.PowerShell;

public sealed record PowerShellResult(string Output, int StatusCode);

/// <summary>upstream PowerShellExecutor.execute_command and run_with_graceful_timeout (Decisions 7 and 8).</summary>
public sealed class PowerShellRunner
{
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);

    // $OutputEncoding: how Windows PowerShell encodes its own output; [Console]::OutputEncoding: how it decodes native
    // programs' output. Both UTF-8, so the pipe carries UTF-8 either way.
    internal const string Preamble =
        "$OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; ";

    internal const string AccessDeniedHint =
        "\n\nHINT: This command may require an elevated (Administrator) terminal. " +
        "The desktop-mcp server is currently running at a lower integrity level.";

    // A CancellationTokenSource delay is at most uint.MaxValue - 1 milliseconds; longer timeouts mean no timeout.
    private const long MaxTimeoutSeconds = (uint.MaxValue - 1L) / 1000;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<Dictionary<string, string>> _environment;
    private readonly Func<bool> _isElevated;

    public PowerShellRunner() : this(ShellEnvironment.Build, Elevation.IsElevated) { }

    internal PowerShellRunner(Func<Dictionary<string, string>> environment, Func<bool> isElevated)
    {
        _environment = environment;
        _isElevated = isElevated;
    }

    public async Task<PowerShellResult> RunAsync(string command, long timeoutSeconds, CancellationToken cancellationToken, string? shell = null)
    {
        var environment = _environment();
        // NO_COLOR suppresses ANSI escape sequences in pwsh 7.2+; Windows PowerShell ignores it.
        environment["NO_COLOR"] = "1";
        var resolved = ResolveShell(shell, environment);
        Process process;
        try
        {
            process = Start(resolved, command, environment);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            // "The system cannot find the file specified" does not say which file, so name the shell.
            return new PowerShellResult($"Command execution failed: {e.GetType().Name}: {e.Message} (shell: {resolved})", 1);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = timeoutSeconds > MaxTimeoutSeconds
                ? new CancellationTokenSource()
                : new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(0, timeoutSeconds)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            try
            {
                // Like Python's communicate(): the exit and the end of both streams.
                await Task.WhenAll(process.WaitForExitAsync(linked.Token), stdout.WaitAsync(linked.Token), stderr.WaitAsync(linked.Token))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await StopAsync(process).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new PowerShellResult("Command execution timed out", 1);
            }

            var output = await stdout.ConfigureAwait(false);
            if (output.Length == 0) output = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 && output.Contains("Access is denied", StringComparison.Ordinal) && !_isElevated())
                output += AccessDeniedHint;
            return new PowerShellResult(output, process.ExitCode);
        }
    }

    internal static string Encode(string command) => Convert.ToBase64String(Encoding.Unicode.GetBytes(Preamble + command));

    /// <summary>upstream _resolve_shell: the shell (or pwsh, then powershell) on the child's PATH, else Windows
    /// PowerShell's System32 path, else an explicit non-PowerShell name unchanged so the launch error names it.</summary>
    internal static string ResolveShell(string? shell, IReadOnlyDictionary<string, string> environment)
    {
        var path = environment.GetValueOrDefault("PATH");
        var pathExt = environment.GetValueOrDefault("PATHEXT");
        string[] names = shell is not null ? [shell] : ["pwsh", "powershell"];
        foreach (var name in names)
        {
            if (Which(name, path, pathExt) is { } found) return found;
        }
        if (shell is not null && !Path.GetFileNameWithoutExtension(shell).Equals("powershell", StringComparison.OrdinalIgnoreCase)) return shell;
        var systemRoot = environment.GetValueOrDefault("SystemRoot") ?? Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        return Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    // shutil.which over the child's PATH with PATHEXT (never the current directory).
    private static string? Which(string name, string? path, string? pathExt)
    {
        var extensions = (string.IsNullOrEmpty(pathExt) ? ShellEnvironment.FallbackPathExt : pathExt).Split(';', StringSplitOptions.RemoveEmptyEntries);
        string[] candidates = extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? [name] : extensions.Select(e => name + e).ToArray();
        if (!string.IsNullOrEmpty(Path.GetDirectoryName(name))) return candidates.FirstOrDefault(File.Exists);
        foreach (var directory in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                var full = Path.Combine(directory.Trim('"'), candidate);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    private static Process Start(string shell, string command, Dictionary<string, string> environment)
    {
        var info = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,          // CREATE_NO_WINDOW: no console flashes up and steals focus
            CreateNewProcessGroup = true,   // CREATE_NEW_PROCESS_GROUP: the target of Ctrl+Break
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        info.ArgumentList.Add("-NoProfile");
        // Only Windows PowerShell 5.1 takes -OutputFormat Text here.
        if (Path.GetFileNameWithoutExtension(shell).Equals("powershell", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add("-OutputFormat");
            info.ArgumentList.Add("Text");
        }
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Encode(command));
        info.Environment.Clear();
        foreach (var (name, value) in environment) info.Environment[name] = value;
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {shell}");
        // The child must not read the MCP pipe.
        process.StandardInput.Close();
        return process;
    }

    private static async Task StopAsync(Process process)
    {
        // Ctrl+Break first. The child has its own hidden console (CREATE_NO_WINDOW), so this usually fails, as it does
        // upstream; the result is ignored.
        _ = CoreNative.GenerateConsoleCtrlEvent(CoreNative.CtrlBreakEvent, (uint)process.Id);
        if (await WaitForExitAsync(process, GracePeriod).ConfigureAwait(false)) return;
        try { process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
        await WaitForExitAsync(process, GracePeriod).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
```

`windows/src/DesktopMcp.Core/PowerShell/ProcessJob.cs`:

```csharp
using DesktopMcp.Core.Interop;

namespace DesktopMcp.Core.PowerShell;

/// <summary>
/// spec §5: the server puts itself in a Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE so the processes it starts
/// (PowerShell children, and anything they start) cannot outlive it. Nested jobs are fine on Windows 8 and later.
/// </summary>
public static unsafe class ProcessJob
{
    private static readonly Lock Gate = new();
    private static nint _job;

    public static bool TryAssignKillOnClose()
    {
        lock (Gate)
        {
            if (_job != 0) return true;
            var job = CoreNative.CreateJobObject(0, 0);
            if (job == 0) return false;
            var info = new CoreNative.JobObjectExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = CoreNative.JobObjectLimitKillOnJobClose;
            if (!CoreNative.SetInformationJobObject(job, CoreNative.JobObjectExtendedLimitInformationClass, &info,
                    (uint)sizeof(CoreNative.JobObjectExtendedLimitInformation))
                || !CoreNative.AssignProcessToJobObject(job, CoreNative.GetCurrentProcess()))
            {
                CoreNative.CloseHandle(job);
                return false;
            }
            // Never closed: the handle closes when this process exits, and the job then kills what is left in it.
            _job = job;
            return true;
        }
    }
}
```

- [ ] **Step 5: Run the Core tests to verify they pass**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~ShellEnvironmentTests|FullyQualifiedName~PowerShellRunnerTests|FullyQualifiedName~ProcessJobTests"`
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 6: Add the tool and wire it**

`windows/src/DesktopMcp.Server/Tools/PowerShellTool.cs`:

```csharp
using DesktopMcp.Core.PowerShell;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/shell.py:22-27.</summary>
public sealed class PowerShellTool(Tool protocolTool, PowerShellRunner runner) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var command = arguments.RequiredString("command");
        var timeout = arguments.Integer("timeout", 30);
        var result = await runner.RunAsync(command, timeout, cancellationToken).ConfigureAwait(false);
        return ToolResults.Text($"Response: {result.Output}\nStatus Code: {result.StatusCode}");
    }
}
```

Replace `windows/src/DesktopMcp.Server/Tools/ToolServices.cs` with:

```csharp
using DesktopMcp.Core.PowerShell;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// The services the tools call. Tests replace the ones with side effects (clipboard, toasts) with fakes through
/// <c>with</c>. Creating the defaults touches nothing: no Windows App SDK, no desktop.
/// </summary>
public sealed record ToolServices
{
    public required PowerShellRunner PowerShell { get; init; }

    public static ToolServices CreateDefault() => new()
    {
        PowerShell = new PowerShellRunner(),
    };
}
```

In `windows/src/DesktopMcp.Server/Tools/ToolRegistry.cs` replace the switch body with:

```csharp
    public static McpServerTool Create(Tool protocolTool, ToolServices services) => protocolTool.Name switch
    {
        "Wait" => new WaitTool(protocolTool),
        "PowerShell" => new PowerShellTool(protocolTool, services.PowerShell),
        _ => new PendingTool(protocolTool),
    };
```

In `windows/src/DesktopMcp.Server/Hosting/StdioServer.cs` add `using DesktopMcp.Core.PowerShell;` and replace

```csharp
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("desktop-mcp");
```

with

```csharp
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("desktop-mcp");
        // spec §5: every process the server starts dies with it (Decisions 19).
        if (!ProcessJob.TryAssignKillOnClose())
            logger.LogWarning("Could not put the server in a kill-on-close Job Object; PowerShell children may outlive it.");
```

- [ ] **Step 7: Run all tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each project, including `PowerShellToolTests`.

- [ ] **Step 8: Commit**

```bash
git add windows/src/DesktopMcp.Core windows/src/DesktopMcp.Server windows/tests/DesktopMcp.Core.Tests/PowerShell windows/tests/DesktopMcp.Server.Tests/Tools/PowerShellToolTests.cs
git commit -m "Run PowerShell as upstream does and put the server in a kill-on-close job" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: FileSystem

upstream `filesystem/service.py`, `filesystem/views.py` and `tools/filesystem.py`: eight modes, the 10 MB read limit and 500-result limit, relative paths resolved from the user's Desktop folder, and upstream's output formats and `Error: ...` texts. Decisions 9 to 12 cover path resolution, text mode, the move guard and glob rules.

**Files:**
- Modify: `windows/src/DesktopMcp.Core/Interop/CoreNative.cs` (final path names)
- Create: `windows/src/DesktopMcp.Core/FileSystem/TextEncodings.cs`, `TextFiles.cs`, `PathResolver.cs`, `Fnmatch.cs`, `GlobMatcher.cs`, `FileSystemService.cs`
- Create: `windows/src/DesktopMcp.Server/Tools/FileSystemTool.cs`
- Modify: `windows/src/DesktopMcp.Server/Tools/ToolServices.cs`, `ToolRegistry.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/FileSystem/TextFilesTests.cs`, `PathResolverTests.cs`, `GlobMatcherTests.cs`, `FileSystemServiceTests.cs`; `windows/tests/DesktopMcp.Server.Tests/Tools/FileSystemToolTests.cs`

**Interfaces:**
- Consumes: `CoreNative`, `Elevation.IsElevated`, `PyText.Thousands`, `PyText.Fixed`, `PyText.Repr`, `ToolArguments`, `TempDirectory`.
- Produces (namespace `DesktopMcp.Core.FileSystem`):
  - internal `TextEncodings.Get(string name) : (Encoding Encoding, bool Signature)` (throws `ArgumentException("unknown encoding: <name>")`), `TextEncodings.Configure(Encoding)`.
  - internal `TextFiles.Read(string path, string encoding)`, `TextFiles.SplitLines(string)`, `TextFiles.Write(string path, string content, bool append, string encoding)`.
  - internal `PathResolver.Resolve(string path) : string`.
  - internal `Fnmatch.ToRegex(string pattern) : Regex`; internal `GlobMatcher.Glob(string root, string pattern, bool recursive, CancellationToken) : IEnumerable<string>`.
  - `public sealed class FileSystemService(string defaultDirectory, Func<bool> isElevated)` plus a parameterless constructor (Desktop folder, real elevation). Constants `MaxReadSize`, `MaxResults`. Methods: `string ResolveArgument(string path)`, `ReadFile(string path, long? offset, long? limit, string encoding)`, `WriteFile(string path, string content, bool append, string encoding)`, `CopyPath(string source, string destination, bool overwrite, CancellationToken)`, `MovePath(string source, string destination, bool overwrite)`, `DeletePath(string path, bool recursive)`, `ListDirectory(string path, string? pattern, bool recursive, bool showHidden, CancellationToken)`, `SearchFiles(string path, string pattern, bool recursive, CancellationToken)`, `GetFileInfo(string path)`, `static string FormatSize(long bytes)`, internal `static string Suffix(string name)`. All return upstream's text.
  - `ToolServices.FileSystem` (`FileSystemService`).

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Core.Tests/FileSystem/TextFilesTests.cs`:

```csharp
using System.Text;
using DesktopMcp.Core.FileSystem;

namespace DesktopMcp.Core.Tests;

public sealed class TextFilesTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ReadTranslatesNewlinesAndReplacesBadBytes()
    {
        var path = _temp.Combine("a.txt");
        File.WriteAllBytes(path, [(byte)'a', 0x0D, 0x0A, (byte)'b', 0x0D, (byte)'c', 0xFF]);
        Assert.Equal("a\nb\nc\uFFFD", TextFiles.Read(path, "utf-8"));
    }

    [Fact]
    public void Utf8KeepsTheBomAndUtf8SigStripsIt()
    {
        var path = _temp.Combine("bom.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'x']);
        Assert.Equal("\uFEFFx", TextFiles.Read(path, "utf-8"));
        Assert.Equal("x", TextFiles.Read(path, "UTF_8_SIG"));
    }

    [Fact]
    public void Utf16HonoursABigEndianBom()
    {
        var path = _temp.Combine("be.txt");
        File.WriteAllBytes(path, [0xFE, 0xFF, 0x00, (byte)'y']);
        Assert.Equal("y", TextFiles.Read(path, "utf-16"));
    }

    [Theory]
    [InlineData("latin-1", "é", new byte[] { 0xE9 })]
    [InlineData("cp1252", "€", new byte[] { 0x80 })]
    [InlineData("cp949", "가", new byte[] { 0xB0, 0xA1 })]
    public void PythonCodecNamesMapToEncodings(string name, string text, byte[] bytes)
    {
        var path = _temp.Combine("enc.txt");
        File.WriteAllBytes(path, bytes);
        Assert.Equal(text, TextFiles.Read(path, name));
    }

    [Fact]
    public void UnknownEncodingUsesPythonsText() =>
        Assert.Equal("unknown encoding: nope", Assert.Throws<ArgumentException>(() => TextEncodings.Get("nope")).Message);

    [Fact]
    public void WriteTranslatesNewlinesAndWritesABomOnlyIntoAnEmptyFile()
    {
        var path = _temp.Combine("w.txt");
        TextFiles.Write(path, "a\nb", append: false, "utf-16");
        TextFiles.Write(path, "\nc", append: true, "utf-16");
        Assert.Equal([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("a\r\nb\r\nc")], File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteRejectsUnencodableText() =>
        Assert.Throws<EncoderFallbackException>(() => TextFiles.Write(_temp.Combine("x.txt"), "é", append: false, "ascii"));

    [Fact]
    public void SplitLinesKeepsLineEnds() => Assert.Equal(["1\n", "2\n", "3"], TextFiles.SplitLines("1\n2\n3"));
}
```

`windows/tests/DesktopMcp.Core.Tests/FileSystem/PathResolverTests.cs`:

```csharp
using DesktopMcp.Core.FileSystem;

namespace DesktopMcp.Core.Tests;

public sealed class PathResolverTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ExistingPathsGetTheirOnDiskSpelling()
    {
        Directory.CreateDirectory(_temp.Combine("MixedCase"));
        var resolved = PathResolver.Resolve(_temp.Combine("mixedcase"));
        Assert.Equal("MixedCase", Path.GetFileName(resolved));
        Assert.False(resolved.StartsWith(@"\\?\", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingTailIsAppendedToTheResolvedPrefix()
    {
        Directory.CreateDirectory(_temp.Combine("MixedCase"));
        var root = PathResolver.Resolve(_temp.Combine("MixedCase"));
        Assert.Equal(Path.Combine(root, "missing", "x.txt"), PathResolver.Resolve(_temp.Combine("mixedcase", "missing", "x.txt")));
    }

    [Fact]
    public void EmptyMeansTheCurrentDirectory() =>
        Assert.Equal(PathResolver.Resolve(Directory.GetCurrentDirectory()), PathResolver.Resolve(""));
}
```

`windows/tests/DesktopMcp.Core.Tests/FileSystem/GlobMatcherTests.cs`:

```csharp
using DesktopMcp.Core.FileSystem;

namespace DesktopMcp.Core.Tests;

public sealed class GlobMatcherTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _root;

    public GlobMatcherTests()
    {
        _root = PathResolver.Resolve(_temp.Path);
        foreach (var file in new[] { "a.txt", "B.TXT", ".hidden.txt", "c.log", @"sub\d.txt", @"sub\deep\e.txt" })
            _temp.WriteFile(file, "x");
    }

    public void Dispose() => _temp.Dispose();

    private string[] Relative(string pattern, bool recursive) =>
        GlobMatcher.Glob(_root, pattern, recursive, CancellationToken.None).Select(p => Path.GetRelativePath(_root, p)).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    [Theory]
    [InlineData("*.txt", "a.txt", true)]
    [InlineData("*.txt", "A.TXT", true)]
    [InlineData("?.log", "c.log", true)]
    [InlineData("[ab].txt", "b.txt", true)]
    [InlineData("[!ab].txt", "c.txt", true)]
    [InlineData("[!ab].txt", "a.txt", false)]
    [InlineData("*", ".hidden", true)]
    [InlineData("a.txt", "a.txtx", false)]
    public void FnmatchIsCaseInsensitiveAndMatchesDotFiles(string pattern, string name, bool expected) =>
        Assert.Equal(expected, Fnmatch.ToRegex(pattern).IsMatch(name));

    [Fact]
    public void GlobMatchesOneLevel() => Assert.Equal([".hidden.txt", "a.txt", "B.TXT"], Relative("*.txt", recursive: false));

    [Fact]
    public void RecursiveGlobIsDoubleStarSlashPattern() =>
        Assert.Equal([".hidden.txt", "a.txt", "B.TXT", @"sub\d.txt", @"sub\deep\e.txt"], Relative("*.txt", recursive: true));

    [Fact]
    public void SegmentsAndDoubleStarCombine() => Assert.Equal([@"sub\deep\e.txt"], Relative("sub/**/e.txt", recursive: false));

    [Fact]
    public void TrailingDoubleStarMatchesTheDirectoryAndEverythingBelow() =>
        Assert.Equal(["sub", @"sub\d.txt", @"sub\deep", @"sub\deep\e.txt"], Relative(@"sub\**", recursive: false));

    [Fact]
    public void AbsolutePatternsAreRejected() =>
        Assert.Equal("Non-relative patterns are unsupported",
            Assert.Throws<NotSupportedException>(() => GlobMatcher.Glob(_root, @"C:\*.txt", false, CancellationToken.None).ToList()).Message);
}
```

`windows/tests/DesktopMcp.Core.Tests/FileSystem/FileSystemServiceTests.cs`:

```csharp
using System.Globalization;
using DesktopMcp.Core.FileSystem;

namespace DesktopMcp.Core.Tests;

public sealed class FileSystemServiceTests : IDisposable
{
    private const string Hint = "\n\nHINT: This operation may require an elevated (Administrator) terminal.";
    private readonly TempDirectory _temp = new();
    private readonly string _root;
    private readonly FileSystemService _fs;

    public FileSystemServiceTests()
    {
        _root = PathResolver.Resolve(_temp.Path);
        _fs = new FileSystemService(_root, () => true);
    }

    public void Dispose() => _temp.Dispose();

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private string Write(string relative, string content)
    {
        var path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void RelativeArgumentsResolveFromTheDefaultDirectory()
    {
        Assert.Equal(P("a.txt"), _fs.ResolveArgument("a.txt"));
        Assert.Equal(@"D:\x.txt", _fs.ResolveArgument(@"D:\x.txt"));
    }

    [Fact]
    public void ReadWholeFile() =>
        Assert.Equal($"File: {P("a.txt")}\na\nb\nc", _fs.ReadFile(Write("a.txt", "a\r\nb\rc"), null, null, "utf-8"));

    [Theory]
    [InlineData(2L, 2L, "Lines 2-3 of 4:\n2\n3\n")]
    [InlineData(3L, 10L, "Lines 3-4 of 4:\n3\n4\n")]
    [InlineData(0L, 0L, "Lines 1-4 of 4:\n1\n2\n3\n4\n")]
    [InlineData(-5L, 1L, "Lines 1-1 of 4:\n1\n")]
    [InlineData(1L, -1L, "Lines 1--1 of 4:\n1\n2\n3\n")]
    public void ReadWithOffsetAndLimitFollowsPythonSlicing(long offset, long limit, string expected)
    {
        var path = Write("n.txt", "1\n2\n3\n4\n");
        Assert.Equal($"File: {path}\n{expected}", _fs.ReadFile(path, offset, limit, "utf-8"));
    }

    [Fact]
    public void ReadErrors()
    {
        Assert.Equal($"Error: File not found: {P("nope.txt")}", _fs.ReadFile(P("nope.txt"), null, null, "utf-8"));
        Assert.Equal($"Error: Path is not a file: {_root}", _fs.ReadFile(_root, null, null, "utf-8"));
        Assert.Equal("Error reading file: unknown encoding: nope", _fs.ReadFile(Write("a.txt", "x"), null, null, "nope"));
    }

    [Fact]
    public void ReadRefusesLargeFiles()
    {
        var path = P("big.bin");
        using (var stream = File.Create(path)) stream.SetLength(FileSystemService.MaxReadSize + 1);
        Assert.Equal("Error: File too large (10,485,761 bytes). Maximum is 10,485,760 bytes. Use offset/limit parameters or the Shell tool for large files.",
            _fs.ReadFile(path, null, null, "utf-8"));
    }

    [Fact]
    public void ALockedFileIsPermissionDeniedWithTheHintWhenNotElevated()
    {
        var path = Write("locked.txt", "x");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal($"Error: Permission denied: {path}{Hint}", new FileSystemService(_root, () => false).ReadFile(path, null, null, "utf-8"));
    }

    [Fact]
    public void WriteAndAppendReportTheSize()
    {
        Assert.Equal($"Written to {P("w.txt")} (7 bytes)", _fs.WriteFile(P("w.txt"), "a\nb\nc", false, "utf-8"));
        Assert.Equal($"Appended to {P("w.txt")} (9 bytes)", _fs.WriteFile(P("w.txt"), "\n", true, "utf-8"));
        Assert.Equal("a\r\nb\r\nc\r\n", File.ReadAllText(P("w.txt")));
    }

    [Fact]
    public void WriteCreatesParentDirectories()
    {
        _fs.WriteFile(P("sub", "dir", "f.txt"), "x", false, "utf-8");
        Assert.True(File.Exists(P("sub", "dir", "f.txt")));
    }

    [Fact]
    public void CopyFileDirectoryAndOverwrite()
    {
        var source = Write("a.txt", "x");
        Assert.Equal($"Copied file: {source} -> {P("b.txt")}", _fs.CopyPath(source, P("b.txt"), false, CancellationToken.None));
        Assert.Equal($"Error: Destination already exists: {P("b.txt")}. Set overwrite=True to replace.", _fs.CopyPath(source, P("b.txt"), false, CancellationToken.None));
        Directory.CreateDirectory(P("into"));
        Assert.Equal($"Copied file: {source} -> {P("into")}", _fs.CopyPath(source, P("into"), true, CancellationToken.None));
        Assert.True(File.Exists(P("into", "a.txt")));
        Write(@"tree\x\y.txt", "y");
        Assert.Equal($"Copied directory: {P("tree")} -> {P("tree2")}", _fs.CopyPath(P("tree"), P("tree2"), false, CancellationToken.None));
        Assert.Equal("y", File.ReadAllText(P("tree2", "x", "y.txt")));
        Assert.Equal($"Error: Source not found: {P("none")}", _fs.CopyPath(P("none"), P("z"), false, CancellationToken.None));
    }

    [Fact]
    public void MoveAndOverwrite()
    {
        var source = Write("a.txt", "x");
        Write("b.txt", "old");
        Assert.Equal($"Error: Destination already exists: {P("b.txt")}. Set overwrite=True to replace.", _fs.MovePath(source, P("b.txt"), false));
        Assert.Equal($"Moved: {source} -> {P("b.txt")}", _fs.MovePath(source, P("b.txt"), true));
        Assert.Equal("x", File.ReadAllText(P("b.txt")));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void MoveOntoItselfWithOverwriteKeepsTheFile()
    {
        var path = Write("a.txt", "x");
        Assert.Equal($"Moved: {path} -> {path}", _fs.MovePath(path, P("A.TXT"), true));
        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public void Delete()
    {
        var file = Write("a.txt", "x");
        Assert.Equal($"Deleted file: {file}", _fs.DeletePath(file, false));
        Write(@"d\x.txt", "x");
        Assert.Equal($"Error: Directory is not empty: {P("d")}. Set recursive=True to delete non-empty directories.", _fs.DeletePath(P("d"), false));
        Assert.Equal($"Deleted directory: {P("d")}", _fs.DeletePath(P("d"), true));
        Directory.CreateDirectory(P("e"));
        Assert.Equal($"Deleted directory: {P("e")}", _fs.DeletePath(P("e"), false));
        Assert.Equal($"Error: Path not found: {P("e")}", _fs.DeletePath(P("e"), false));
    }

    [Fact]
    public void ListPutsDirectoriesFirstAndHidesDotNames()
    {
        Write("b.txt", "12345");
        Write(".hidden", "x");
        Write("a.log", new string('x', 2048));
        Directory.CreateDirectory(P("Alpha"));
        Assert.Equal($"Directory: {_root}\n  [DIR ] Alpha  \n  [FILE] a.log  2.0 KB\n  [FILE] b.txt  5 B",
            _fs.ListDirectory(_root, null, false, false, CancellationToken.None));
        Assert.Equal($"Directory: {_root}\n  [DIR ] Alpha  \n  [FILE] .hidden  1 B\n  [FILE] a.log  2.0 KB\n  [FILE] b.txt  5 B",
            _fs.ListDirectory(_root, null, false, true, CancellationToken.None));
        Assert.Equal($"Directory: {_root} (filter: *.txt)\n  [FILE] b.txt  5 B",
            _fs.ListDirectory(_root, "*.txt", false, false, CancellationToken.None));
        Assert.Equal($"Directory {_root} is empty matching \"*.zz\".", _fs.ListDirectory(_root, "*.zz", false, false, CancellationToken.None));
    }

    [Fact]
    public void ListRecursiveShowsRelativePaths()
    {
        Write("z.txt", "1");
        Write(@"sub\a.txt", "1");
        Assert.Equal($"Directory: {_root}\n  [DIR ] sub  \n  [FILE] sub\\a.txt  1 B\n  [FILE] z.txt  1 B",
            _fs.ListDirectory(_root, null, true, false, CancellationToken.None));
    }

    [Fact]
    public void ListEdgeCases()
    {
        Directory.CreateDirectory(P("e"));
        Assert.Equal($"Directory {P("e")} is empty.", _fs.ListDirectory(P("e"), null, false, false, CancellationToken.None));
        Assert.Equal($"Error: Directory not found: {P("none")}", _fs.ListDirectory(P("none"), null, false, false, CancellationToken.None));
        var file = Write("f.txt", "x");
        Assert.Equal($"Error: Path is not a directory: {file}", _fs.ListDirectory(file, null, false, false, CancellationToken.None));
    }

    [Fact]
    public void ListStopsAfterFiveHundredItems()
    {
        for (var i = 0; i < 501; i++) Write($"f{i:D3}.txt", "");
        var lines = _fs.ListDirectory(_root, null, false, false, CancellationToken.None).Split('\n');
        Assert.Equal(502, lines.Length);
        Assert.Equal("... (truncated, 500+ items)", lines[^1]);
    }

    [Fact]
    public void Search()
    {
        Write("a.txt", "1");
        Write(@"sub\b.txt", "1");
        Write(@"sub\c.log", "1");
        Assert.Equal($"Search: \"*.txt\" in {_root} (2 matches)\n  [FILE] a.txt  1 B\n  [FILE] sub\\b.txt  1 B",
            _fs.SearchFiles(_root, "*.txt", true, CancellationToken.None));
        Assert.Equal($"Search: \"*.txt\" in {_root} (1 matches)\n  [FILE] a.txt  1 B", _fs.SearchFiles(_root, "*.txt", false, CancellationToken.None));
        Assert.Equal($"No matches found for \"*.zz\" in {_root}", _fs.SearchFiles(_root, "*.zz", true, CancellationToken.None));
        Assert.Equal("Error searching: Non-relative patterns are unsupported", _fs.SearchFiles(_root, @"C:\*.txt", true, CancellationToken.None));
        Assert.Equal($"Error: Search path not found: {P("none")}", _fs.SearchFiles(P("none"), "*", true, CancellationToken.None));
    }

    [Fact]
    public void InfoForAFile()
    {
        var path = Write("info.txt", "hello");
        var info = new FileInfo(path);
        string Stamp(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.Equal(
            $"Path: {path}\nType: File\nSize: 5 B (5 bytes)\nCreated: {Stamp(info.CreationTime)}\nModified: {Stamp(info.LastWriteTime)}\n" +
            $"Accessed: {Stamp(info.LastAccessTime)}\nRead-only: False\nExtension: .txt",
            _fs.GetFileInfo(path));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Assert.Contains("\nRead-only: True\n", _fs.GetFileInfo(path));
    }

    [Fact]
    public void InfoForADirectory()
    {
        Write(@"d\a.txt", "x");
        Directory.CreateDirectory(P("d", "s"));
        var text = _fs.GetFileInfo(P("d"));
        Assert.StartsWith($"Path: {P("d")}\nType: Directory\nSize: 0 B (0 bytes)\n", text);
        Assert.EndsWith("\nRead-only: False\nContents: 1 files, 1 directories", text);
        Assert.Equal($"Error: Path not found: {P("none")}", _fs.GetFileInfo(P("none")));
    }

    [Theory]
    [InlineData("a.txt", ".txt")]
    [InlineData(".env", "(none)")]
    [InlineData("noext", "(none)")]
    [InlineData("a.tar.gz", ".gz")]
    [InlineData("a.", ".")]
    public void SuffixFollowsPathlib(string name, string expected) => Assert.Equal(expected, FileSystemService.Suffix(name));

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(1610612736L, "1.5 GB")]
    public void FormatSizeMatchesUpstream(long bytes, string expected) => Assert.Equal(expected, FileSystemService.FormatSize(bytes));
}
```

`windows/tests/DesktopMcp.Server.Tests/Tools/FileSystemToolTests.cs`:

```csharp
using DesktopMcp.Core.FileSystem;
using DesktopMcp.Server.Tools;

namespace DesktopMcp.Server.Tests;

public sealed class FileSystemToolTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dmcp-fs-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(string json) =>
        ToolCalls.CallAsync(ToolCalls.Create("FileSystem", ToolServices.CreateDefault() with { FileSystem = new FileSystemService(_root, () => true) }), json);

    [Fact]
    public async Task RelativePathsResolveFromTheDefaultDirectory()
    {
        var written = ToolCalls.OkText(await CallAsync("""{"mode": "write", "path": "notes.txt", "content": "hi"}"""));
        Assert.StartsWith("Written to ", written);
        Assert.EndsWith(@"\notes.txt (2 bytes)", written);
        Assert.EndsWith("\nhi", ToolCalls.OkText(await CallAsync("""{"mode": "read", "path": "notes.txt"}""")));
    }

    [Fact]
    public async Task BooleanStringsAreStrict()
    {
        Assert.StartsWith("Directory ", ToolCalls.OkText(await CallAsync("""{"mode": "list", "path": ".", "recursive": "TRUE"}""")));
        var result = await CallAsync("""{"mode": "list", "path": ".", "recursive": "yes"}""");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'FileSystem': recursive must be true or false", ToolCalls.Text(result));
    }

    [Theory]
    [InlineData("""{"mode": "write", "path": "a.txt"}""", "Error: content parameter is required for write mode.")]
    [InlineData("""{"mode": "copy", "path": "a.txt"}""", "Error: destination parameter is required for copy mode.")]
    [InlineData("""{"mode": "move", "path": "a.txt"}""", "Error: destination parameter is required for move mode.")]
    [InlineData("""{"mode": "search", "path": "."}""", "Error: pattern parameter is required for search mode.")]
    [InlineData("""{"mode": "zip", "path": "."}""", "Error: Unknown mode \"zip\". Use: read, write, copy, move, delete, list, search, info.")]
    public async Task MissingParametersUseUpstreamMessages(string json, string expected) =>
        Assert.Equal(expected, ToolCalls.OkText(await CallAsync(json)));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~TextFilesTests|FullyQualifiedName~PathResolverTests|FullyQualifiedName~GlobMatcherTests|FullyQualifiedName~FileSystemServiceTests"`
Expected: build FAILS with `error CS0234: The type or namespace name 'FileSystem' does not exist in the namespace 'DesktopMcp.Core'`.

- [ ] **Step 3: Add the final-path entry points**

In `windows/src/DesktopMcp.Core/Interop/CoreNative.cs` add `using Microsoft.Win32.SafeHandles;` at the top and, inside the class, after the Job Object block:

```csharp
    // kernel32: final path names (FileSystem path resolution, like Python's os.path.realpath)
    internal const uint FileShareAll = 0x7;                      // read | write | delete
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;    // required to open a directory

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* buffer, uint bufferLength, uint flags);
```

- [ ] **Step 4: Implement encodings, text files, path resolution and glob**

`windows/src/DesktopMcp.Core/FileSystem/TextEncodings.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace DesktopMcp.Core.FileSystem;

/// <summary>Python codec names as .NET encodings (Decisions 10).</summary>
internal static class TextEncodings
{
    static TextEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// The encoding for a Python codec name, decoding with U+FFFD for bad bytes (errors='replace') and encoding strictly.
    /// <c>Signature</c> marks codecs that write a BOM (utf-8-sig, utf-16, utf-32).
    /// </summary>
    public static (Encoding Encoding, bool Signature) Get(string name)
    {
        var key = name.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        var (encoding, signature) = key switch
        {
            "utf-8" or "utf8" or "u8" or "utf" or "cp65001" => ((Encoding)new UTF8Encoding(false), false),
            "utf-8-sig" or "utf8-sig" => (new UTF8Encoding(true), true),
            "utf-16" or "utf16" => (new UnicodeEncoding(false, true), true),
            "utf-16-le" or "utf-16le" => (new UnicodeEncoding(false, false), false),
            "utf-16-be" or "utf-16be" => (new UnicodeEncoding(true, false), false),
            "utf-32" or "utf32" => (new UTF32Encoding(false, true), true),
            "utf-32-le" or "utf-32le" => (new UTF32Encoding(false, false), false),
            "utf-32-be" or "utf-32be" => (new UTF32Encoding(true, false), false),
            "latin-1" or "latin1" or "l1" or "iso-8859-1" or "iso8859-1" => (Encoding.Latin1, false),
            "ascii" or "us-ascii" => (Encoding.ASCII, false),
            _ => (Lookup(key, name), false),
        };
        return (Configure(encoding), signature);
    }

    /// <summary>A writable copy with Python's error handling.</summary>
    public static Encoding Configure(Encoding encoding)
    {
        var configured = (Encoding)encoding.Clone();
        configured.EncoderFallback = EncoderFallback.ExceptionFallback;
        configured.DecoderFallback = new DecoderReplacementFallback("\uFFFD");
        return configured;
    }

    private static Encoding Lookup(string key, string name)
    {
        try
        {
            if (key.StartsWith("cp", StringComparison.Ordinal)
                && int.TryParse(key.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var codePage))
                return Encoding.GetEncoding(codePage);
            return Encoding.GetEncoding(key);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            throw new ArgumentException($"unknown encoding: {name}");
        }
    }
}
```

`windows/src/DesktopMcp.Core/FileSystem/TextFiles.cs`:

```csharp
using System.Text;

namespace DesktopMcp.Core.FileSystem;

/// <summary>Python's text-mode file I/O on Windows (Decisions 10).</summary>
internal static class TextFiles
{
    /// <summary>open(path, encoding=..., errors='replace').read(): BOM handling for signature codecs, universal newlines.</summary>
    public static string Read(string path, string encodingName)
    {
        var (encoding, signature) = TextEncodings.Get(encodingName);
        var bytes = File.ReadAllBytes(path);
        var offset = 0;
        if (signature)
        {
            var preamble = encoding.GetPreamble();
            if (bytes.AsSpan().StartsWith(preamble))
            {
                offset = preamble.Length;
            }
            else if (encoding is UnicodeEncoding && bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encoding = TextEncodings.Configure(new UnicodeEncoding(bigEndian: true, byteOrderMark: false));
                offset = 2;
            }
        }
        return encoding.GetString(bytes, offset, bytes.Length - offset).Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>readlines(): every line keeps its "\n".</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>open(path, 'w' or 'a', encoding=...).write(content): "\n" becomes "\r\n"; a BOM only into an empty file.
    /// The text is encoded before the file is opened, so an encoding error leaves the file untouched.</summary>
    public static void Write(string path, string content, bool append, string encodingName)
    {
        var (encoding, signature) = TextEncodings.Get(encodingName);
        var bytes = encoding.GetBytes(content.Replace("\n", "\r\n"));
        using var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
        if (signature && stream.Length == 0) stream.Write(encoding.GetPreamble());
        stream.Write(bytes);
    }
}
```

`windows/src/DesktopMcp.Core/FileSystem/PathResolver.cs`:

```csharp
using DesktopMcp.Core.Interop;

namespace DesktopMcp.Core.FileSystem;

/// <summary>
/// Python's Path.resolve() on Windows (Decisions 9): the longest existing prefix becomes the OS final path (links
/// resolved, 8.3 names expanded, on-disk case) and the missing rest is appended unchanged.
/// </summary>
internal static class PathResolver
{
    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path.Length == 0 ? "." : path);
        var existing = full;
        var tail = new Stack<string>();
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null) return full;
            tail.Push(Path.GetFileName(existing));
            existing = parent;
        }
        var resolved = FinalPath(existing) ?? existing;
        foreach (var part in tail) resolved = Path.Combine(resolved, part);
        return resolved;
    }

    private static unsafe string? FinalPath(string path)
    {
        using var handle = CoreNative.CreateFile(path, 0, CoreNative.FileShareAll, 0, CoreNative.OpenExisting, CoreNative.FileFlagBackupSemantics, 0);
        if (handle.IsInvalid) return null;
        var buffer = new char[1024];
        while (true)
        {
            uint length;
            fixed (char* pointer = buffer) length = CoreNative.GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, 0);
            if (length == 0) return null;
            if (length < buffer.Length) return StripPrefix(new string(buffer, 0, (int)length));
            buffer = new char[length];
        }
    }

    // GetFinalPathNameByHandle returns \\?\C:\... or \\?\UNC\server\share\...; realpath strips both prefixes.
    private static string StripPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}
```

`windows/src/DesktopMcp.Core/FileSystem/Fnmatch.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopMcp.Core.FileSystem;

/// <summary>Python's fnmatch.translate, matched case-insensitively (ntpath.normcase).</summary>
internal static class Fnmatch
{
    public static Regex ToRegex(string pattern) =>
        new("^" + Translate(pattern) + @"\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    internal static string Translate(string pattern)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    builder.Append(".*");
                    while (i + 1 < pattern.Length && pattern[i + 1] == '*') i++;
                    break;
                case '?':
                    builder.Append('.');
                    break;
                case '[':
                    var j = i + 1;
                    if (j < pattern.Length && pattern[j] == '!') j++;
                    if (j < pattern.Length && pattern[j] == ']') j++;
                    while (j < pattern.Length && pattern[j] != ']') j++;
                    if (j >= pattern.Length)
                    {
                        builder.Append(@"\[");
                        break;
                    }
                    var body = pattern[(i + 1)..j].Replace(@"\", @"\\");
                    i = j;
                    if (body.StartsWith('!')) body = "^" + body[1..];
                    else if (body.StartsWith('^')) body = @"\" + body;
                    builder.Append('[').Append(body).Append(']');
                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return builder.ToString();
    }
}
```

`windows/src/DesktopMcp.Core/FileSystem/GlobMatcher.cs`:

```csharp
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.FileSystem;

/// <summary>Python 3.14 Path(root).glob(pattern), or rglob(pattern) when recursive (Decisions 12).</summary>
internal static class GlobMatcher
{
    public static IEnumerable<string> Glob(string root, string pattern, bool recursive, CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(pattern)) throw new NotSupportedException("Non-relative patterns are unsupported");
        if (pattern.Length == 0) throw new ArgumentException($"Unacceptable pattern: {PyText.Repr(pattern)}");
        var segments = pattern.Split('/', '\\').Where(s => s.Length > 0 && s != ".").ToList();
        if (recursive) segments.Insert(0, "**");
        if (segments.Count == 0) return [root];
        return Walk(root, segments, 0, cancellationToken).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Walk(string directory, IReadOnlyList<string> segments, int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (index == segments.Count)
        {
            yield return directory;
            yield break;
        }
        var segment = segments[index];
        var last = index == segments.Count - 1;
        if (segment == "**")
        {
            if (last)
            {
                // A trailing ** matches the directory itself and everything below it.
                yield return directory;
                foreach (var entry in Descendants(directory, cancellationToken)) yield return entry;
                yield break;
            }
            foreach (var match in Walk(directory, segments, index + 1, cancellationToken)) yield return match;
            foreach (var subdirectory in Entries(directory, directoriesOnly: true))
            {
                if (IsLink(subdirectory)) continue;
                foreach (var match in Walk(subdirectory, segments, index, cancellationToken)) yield return match;
            }
            yield break;
        }
        var regex = Fnmatch.ToRegex(segment);
        foreach (var entry in Entries(directory, directoriesOnly: !last))
        {
            if (!regex.IsMatch(Path.GetFileName(entry))) continue;
            foreach (var match in Walk(entry, segments, index + 1, cancellationToken)) yield return match;
        }
    }

    private static IEnumerable<string> Descendants(string directory, CancellationToken cancellationToken)
    {
        foreach (var entry in Entries(directory, directoriesOnly: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
            if (!Directory.Exists(entry) || IsLink(entry)) continue;
            foreach (var child in Descendants(entry, cancellationToken)) yield return child;
        }
    }

    // Unreadable directories are skipped, as Python's glob does.
    private static List<string> Entries(string directory, bool directoriesOnly)
    {
        try
        {
            return (directoriesOnly ? Directory.EnumerateDirectories(directory) : Directory.EnumerateFileSystemEntries(directory)).ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static bool IsLink(string path) => new DirectoryInfo(path).LinkTarget is not null;
}
```

- [ ] **Step 5: Implement the service**

`windows/src/DesktopMcp.Core/FileSystem/FileSystemService.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.FileSystem;

/// <summary>upstream filesystem/service.py and views.py. Every method returns upstream's text, errors included.</summary>
public sealed class FileSystemService(string defaultDirectory, Func<bool> isElevated)
{
    public const long MaxReadSize = 10 * 1024 * 1024;
    public const int MaxResults = 500;
    private const string ElevationHint = "\n\nHINT: This operation may require an elevated (Administrator) terminal.";
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    /// <summary>The user's Desktop folder (platformdirs user_desktop_dir) and the real elevation state.</summary>
    public FileSystemService() : this(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Elevation.IsElevated) { }

    /// <summary>tools/filesystem.py:41-46: relative paths are joined to the Desktop folder.</summary>
    public string ResolveArgument(string path) => Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path, defaultDirectory);

    public string ReadFile(string path, long? offset, long? limit, string encoding)
    {
        var file = PathResolver.Resolve(path);
        if (!Exists(file)) return $"Error: File not found: {file}";
        if (!File.Exists(file)) return $"Error: Path is not a file: {file}";
        var size = new FileInfo(file).Length;
        if (size > MaxReadSize)
            return $"Error: File too large ({PyText.Thousands(size)} bytes). Maximum is {PyText.Thousands(MaxReadSize)} bytes. Use offset/limit parameters or the Shell tool for large files.";
        try
        {
            var text = TextFiles.Read(file, encoding);
            if (offset is null && limit is null) return $"File: {file}\n{text}";
            var lines = TextFiles.SplitLines(text);
            var start = Math.Max(0, (offset is null or 0 ? 1 : offset.Value) - 1);
            var end = limit is null or 0 ? lines.Count : start + limit.Value;
            return $"File: {file}\nLines {start + 1}-{Math.Min(end, lines.Count)} of {lines.Count}:\n{string.Concat(Slice(lines, start, end))}";
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return PermissionDenied(file);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error reading file: {e.Message}";
        }
    }

    public string WriteFile(string path, string content, bool append, string encoding)
    {
        var file = PathResolver.Resolve(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            TextFiles.Write(file, content, append, encoding);
            var size = new FileInfo(file).Length;
            return $"{(append ? "Appended to" : "Written to")} {file} ({PyText.Thousands(size)} bytes)";
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return PermissionDenied(file);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error writing file: {e.Message}";
        }
    }

    public string CopyPath(string source, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        var src = PathResolver.Resolve(source);
        var dst = PathResolver.Resolve(destination);
        if (!Exists(src)) return $"Error: Source not found: {src}";
        if (Exists(dst) && !overwrite) return $"Error: Destination already exists: {dst}. Set overwrite=True to replace.";
        try
        {
            if (File.Exists(src))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                // shutil.copy2 into an existing directory copies the file inside it.
                var target = Directory.Exists(dst) ? Path.Combine(dst, Path.GetFileName(src)) : dst;
                File.Copy(src, target, overwrite: true);
                return $"Copied file: {src} -> {dst}";
            }
            if (Exists(dst)) Directory.Delete(dst, recursive: true);
            CopyDirectory(src, dst, cancellationToken);
            return $"Copied directory: {src} -> {dst}";
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return "Error: Permission denied." + Hint();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error copying: {e.Message}";
        }
    }

    public string MovePath(string source, string destination, bool overwrite)
    {
        var src = PathResolver.Resolve(source);
        var dst = PathResolver.Resolve(destination);
        if (!Exists(src)) return $"Error: Source not found: {src}";
        if (Exists(dst) && !overwrite) return $"Error: Destination already exists: {dst}. Set overwrite=True to replace.";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            // Decisions 11: never delete the destination when it is the source itself.
            if (Exists(dst) && !string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
                else File.Delete(dst);
            }
            if (Directory.Exists(src)) MoveDirectory(src, dst);
            else File.Move(src, dst);
            return $"Moved: {src} -> {dst}";
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return "Error: Permission denied." + Hint();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error moving: {e.Message}";
        }
    }

    public string DeletePath(string path, bool recursive)
    {
        var target = PathResolver.Resolve(path);
        if (!Exists(target)) return $"Error: Path not found: {target}";
        try
        {
            if (File.Exists(target))
            {
                File.Delete(target);
                return $"Deleted file: {target}";
            }
            if (!recursive)
            {
                if (Directory.EnumerateFileSystemEntries(target).Any())
                    return $"Error: Directory is not empty: {target}. Set recursive=True to delete non-empty directories.";
                Directory.Delete(target);
            }
            else
            {
                Directory.Delete(target, recursive: true);
            }
            return $"Deleted directory: {target}";
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return PermissionDenied(target);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error deleting: {e.Message}";
        }
    }

    public string ListDirectory(string path, string? pattern, bool recursive, bool showHidden, CancellationToken cancellationToken)
    {
        var directory = PathResolver.Resolve(path);
        if (!Exists(directory)) return $"Error: Directory not found: {directory}";
        if (!Directory.Exists(directory)) return $"Error: Path is not a directory: {directory}";
        var hasPattern = !string.IsNullOrEmpty(pattern);
        try
        {
            var candidates = recursive
                ? GlobMatcher.Glob(directory, hasPattern ? pattern! : "*", recursive: true, cancellationToken)
                : Directory.EnumerateFileSystemEntries(directory);
            var sorted = candidates.Select(Entry.Of)
                .OrderBy(e => e.IsDirectory ? 0 : 1)
                .ThenBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
                .ToList();
            Regex? filter = !recursive && hasPattern ? Fnmatch.ToRegex(pattern!) : null;
            var lines = new List<string>();
            var count = 0;
            foreach (var entry in sorted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!showHidden && entry.Name.StartsWith('.')) continue;
                if (filter is not null && !filter.IsMatch(entry.Name)) continue;
                if (++count > MaxResults)
                {
                    lines.Add($"... (truncated, {MaxResults}+ items)");
                    break;
                }
                lines.Add(FormatEntry(recursive ? Path.GetRelativePath(directory, entry.FullPath) : entry.Name, entry));
            }
            if (lines.Count == 0) return $"Directory {directory} is empty{(hasPattern ? $" matching \"{pattern}\"" : "")}.";
            var header = $"Directory: {directory}" + (hasPattern ? $" (filter: {pattern})" : "");
            return header + "\n" + string.Join("\n", lines);
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return PermissionDenied(directory);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error listing directory: {e.Message}";
        }
    }

    public string SearchFiles(string path, string pattern, bool recursive, CancellationToken cancellationToken)
    {
        var root = PathResolver.Resolve(path);
        if (!Exists(root)) return $"Error: Search path not found: {root}";
        if (!Directory.Exists(root)) return $"Error: Search path is not a directory: {root}";
        try
        {
            var matches = GlobMatcher.Glob(root, pattern, recursive, cancellationToken)
                .Select(Entry.Of)
                .OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
                .ToList();
            var lines = new List<string>();
            var count = 0;
            foreach (var match in matches)
            {
                if (++count > MaxResults)
                {
                    lines.Add($"... (truncated, {MaxResults}+ matches)");
                    break;
                }
                lines.Add(FormatEntry(Path.GetRelativePath(root, match.FullPath), match));
            }
            if (lines.Count == 0) return $"No matches found for \"{pattern}\" in {root}";
            return $"Search: \"{pattern}\" in {root} ({Math.Min(count, MaxResults)} matches)\n" + string.Join("\n", lines);
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return PermissionDenied(root);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error searching: {e.Message}";
        }
    }

    public string GetFileInfo(string path)
    {
        var target = PathResolver.Resolve(path);
        if (!Exists(target)) return $"Error: Path not found: {target}";
        try
        {
            var isDirectory = Directory.Exists(target);
            FileSystemInfo info = isDirectory ? new DirectoryInfo(target) : new FileInfo(target);
            var size = info is FileInfo file ? file.Length : 0;
            var lines = new List<string>
            {
                $"Path: {target}",
                $"Type: {(isDirectory ? "Directory" : "File")}",
                $"Size: {FormatSize(size)} ({PyText.Thousands(size)} bytes)",
                $"Created: {Stamp(info.CreationTime)}",
                $"Modified: {Stamp(info.LastWriteTime)}",
                $"Accessed: {Stamp(info.LastAccessTime)}",
                // os.access(W_OK) on Windows: only a file's read-only attribute counts.
                $"Read-only: {(!isDirectory && info.Attributes.HasFlag(FileAttributes.ReadOnly) ? "True" : "False")}",
            };
            if (isDirectory)
            {
                try
                {
                    var entries = Directory.EnumerateFileSystemEntries(target).ToList();
                    lines.Add($"Contents: {entries.Count(File.Exists)} files, {entries.Count(Directory.Exists)} directories");
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            else
            {
                lines.Add($"Extension: {Suffix(Path.GetFileName(target))}");
            }
            return string.Join("\n", lines);
        }
        catch (Exception e) when (IsPermissionError(e))
        {
            return PermissionDenied(target);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error getting file info: {e.Message}";
        }
    }

    /// <summary>upstream format_size (filesystem/views.py:72-81).</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{PyText.Fixed(bytes / 1024.0, 1)} KB",
        < 1024L * 1024 * 1024 => $"{PyText.Fixed(bytes / (1024.0 * 1024), 1)} MB",
        _ => $"{PyText.Fixed(bytes / (1024.0 * 1024 * 1024), 1)} GB",
    };

    /// <summary>Path.suffix (Python 3.14): the last dot part, none for a leading-dot name, "." for a trailing dot.</summary>
    internal static string Suffix(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "(none)" : name[dot..];
    }

    private sealed record Entry(string FullPath, string Name, bool IsDirectory, long Size)
    {
        public static Entry Of(string path)
        {
            var isDirectory = Directory.Exists(path);
            long size = 0;
            if (!isDirectory)
            {
                try { size = new FileInfo(path).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return new Entry(path, Path.GetFileName(path), isDirectory, size);
        }
    }

    // upstream Directory.to_string (filesystem/views.py:63-69): directories keep the two trailing spaces.
    private static string FormatEntry(string display, Entry entry) =>
        $"  [{(entry.IsDirectory ? "DIR " : "FILE")}] {display}  {(entry.IsDirectory ? "" : FormatSize(entry.Size))}";

    private static string Stamp(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    // Python maps sharing and lock violations to PermissionError as well.
    private static bool IsPermissionError(Exception e) =>
        e is UnauthorizedAccessException || e is IOException { HResult: ErrorSharingViolation or ErrorLockViolation };

    private string Hint() => isElevated() ? "" : ElevationHint;

    private string PermissionDenied(string path) => $"Error: Permission denied: {path}{Hint()}";

    // Python's list[start:end] with start >= 0.
    private static IEnumerable<string> Slice(List<string> items, long start, long end)
    {
        long count = items.Count;
        var from = Math.Min(start, count);
        var to = end < 0 ? Math.Max(0, count + end) : Math.Min(end, count);
        return to <= from ? [] : items.Skip((int)from).Take((int)(to - from));
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyDirectory(entry, target, cancellationToken);
            else File.Copy(entry, target);
        }
        Directory.SetLastWriteTimeUtc(destination, Directory.GetLastWriteTimeUtc(source));
    }

    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException) when (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            // shutil.move across volumes: copy, then delete.
            CopyDirectory(source, destination, CancellationToken.None);
            Directory.Delete(source, recursive: true);
        }
    }
}
```

- [ ] **Step 6: Run the Core tests to verify they pass**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~TextFilesTests|FullyQualifiedName~PathResolverTests|FullyQualifiedName~GlobMatcherTests|FullyQualifiedName~FileSystemServiceTests"`
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 7: Add the tool and wire it**

`windows/src/DesktopMcp.Server/Tools/FileSystemTool.cs`:

```csharp
using DesktopMcp.Core.FileSystem;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/filesystem.py:26-82: paths are resolved first, then the strict booleans, then the mode.</summary>
public sealed class FileSystemTool(Tool protocolTool, FileSystemService fileSystem) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var mode = arguments.RequiredString("mode");
        var path = fileSystem.ResolveArgument(arguments.RequiredString("path"));
        var destination = arguments.OptionalString("destination");
        if (!string.IsNullOrEmpty(destination)) destination = fileSystem.ResolveArgument(destination);
        var content = arguments.OptionalString("content");
        var pattern = arguments.OptionalString("pattern");
        var offset = arguments.OptionalInteger("offset");
        var limit = arguments.OptionalInteger("limit");
        var encoding = arguments.String("encoding", "utf-8");
        var recursive = arguments.StrictBool("recursive", false);
        var append = arguments.StrictBool("append", false);
        var overwrite = arguments.StrictBool("overwrite", false);
        var showHidden = arguments.StrictBool("show_hidden", false);

        var text = await Task.Run(() => mode switch
        {
            "read" => fileSystem.ReadFile(path, offset, limit, encoding),
            "write" => content is null ? "Error: content parameter is required for write mode." : fileSystem.WriteFile(path, content, append, encoding),
            "copy" => destination is null ? "Error: destination parameter is required for copy mode." : fileSystem.CopyPath(path, destination, overwrite, cancellationToken),
            "move" => destination is null ? "Error: destination parameter is required for move mode." : fileSystem.MovePath(path, destination, overwrite),
            "delete" => fileSystem.DeletePath(path, recursive),
            "list" => fileSystem.ListDirectory(path, pattern, recursive, showHidden, cancellationToken),
            "search" => pattern is null ? "Error: pattern parameter is required for search mode." : fileSystem.SearchFiles(path, pattern, recursive, cancellationToken),
            "info" => fileSystem.GetFileInfo(path),
            _ => $"Error: Unknown mode \"{mode}\". Use: read, write, copy, move, delete, list, search, info.",
        }, cancellationToken).ConfigureAwait(false);
        return ToolResults.Text(text);
    }
}
```

Replace `windows/src/DesktopMcp.Server/Tools/ToolServices.cs` with:

```csharp
using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.PowerShell;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// The services the tools call. Tests replace the ones with side effects (clipboard, toasts) with fakes through
/// <c>with</c>. Creating the defaults touches nothing: no Windows App SDK, no desktop.
/// </summary>
public sealed record ToolServices
{
    public required PowerShellRunner PowerShell { get; init; }
    public required FileSystemService FileSystem { get; init; }

    public static ToolServices CreateDefault() => new()
    {
        PowerShell = new PowerShellRunner(),
        FileSystem = new FileSystemService(),
    };
}
```

In `windows/src/DesktopMcp.Server/Tools/ToolRegistry.cs` replace the switch body with:

```csharp
    public static McpServerTool Create(Tool protocolTool, ToolServices services) => protocolTool.Name switch
    {
        "Wait" => new WaitTool(protocolTool),
        "PowerShell" => new PowerShellTool(protocolTool, services.PowerShell),
        "FileSystem" => new FileSystemTool(protocolTool, services.FileSystem),
        _ => new PendingTool(protocolTool),
    };
```

- [ ] **Step 8: Run all tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each project, including `FileSystemToolTests`.

- [ ] **Step 9: Commit**

```bash
git add windows/src/DesktopMcp.Core windows/src/DesktopMcp.Server windows/tests/DesktopMcp.Core.Tests/FileSystem windows/tests/DesktopMcp.Server.Tests/Tools/FileSystemToolTests.cs
git commit -m "Add the FileSystem tool with upstream's modes, limits and texts" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: Registry

upstream `registry/service.py` and `tools/registry.py` drive PowerShell cmdlets; spec §8 replaces them with `Microsoft.Win32.Registry` while keeping the modes, value types and output strings. Decisions 13 lists what is reproduced from pwsh 7: the cmdlet-prefixed errors, unsigned DWORD/QWORD, `Set-ItemProperty`'s conversions and the `Format-List` layout. The tests use a throwaway key `HKCU\Software\desktop-mcp-tests\<guid>` and delete it afterwards.

Reference strings (pwsh 7.6.7, English resources):
- missing value: `Property <name> does not exist at path HKEY_CURRENT_USER\<subkey>.`
- missing key: `Cannot find path '<path as given>' because it does not exist.`
- unknown drive: `Cannot find drive. A drive with the name '<drive>' does not exist.`
- conversion: `Cannot convert value "<v>" to type "System.UInt32". Error: "The input string '<v>' was not in a correct format."` or `... Error: "Value was either too large or too small for a UInt32."`; Binary nests a `System.Byte` conversion inside a `System.Byte[]` one, and its range text is `Value was either too large or too small for an unsigned byte.`

**Files:**
- Create: `windows/src/DesktopMcp.Core/WindowsRegistry/RegistryLocation.cs`, `RegistryService.cs`
- Create: `windows/src/DesktopMcp.Server/Tools/RegistryTool.cs`
- Modify: `windows/src/DesktopMcp.Server/Tools/ToolServices.cs`, `ToolRegistry.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/WindowsRegistry/RegistryServiceTests.cs`, `windows/tests/DesktopMcp.Server.Tests/Tools/RegistryToolTests.cs`

**Interfaces:**
- Consumes: `ToolArguments`, `ToolResults`.
- Produces (namespace `DesktopMcp.Core.WindowsRegistry`):
  - internal `RegistryCommandException(string message)`; internal `RegistryLocation(RegistryHive Hive, string HiveName, string SubKey)` with `ProviderPath`, `static Parse(string path)`, `Open(string originalPath, bool writable)`, `CreateOrOpen()`.
  - `public sealed class RegistryService` with `static IReadOnlyList<string> AllowedTypes`, `string GetValue(string path, string name)`, `string SetValue(string path, string name, string value, string type)`, `string DeleteEntry(string path, string? name)`, `string ListKey(string path)`.
  - `ToolServices.Registry` (`RegistryService`).

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Core.Tests/WindowsRegistry/RegistryServiceTests.cs`:

```csharp
using DesktopMcp.Core.WindowsRegistry;
using Microsoft.Win32;

namespace DesktopMcp.Core.Tests;

/// <summary>Works in a throwaway key HKCU\Software\desktop-mcp-tests\&lt;guid&gt; that Dispose deletes.</summary>
public sealed class RegistryServiceTests : IDisposable
{
    private const string Parent = @"Software\desktop-mcp-tests";
    private readonly string _subKey = Parent + @"\" + Guid.NewGuid().ToString("N");
    private readonly string _path;
    private readonly RegistryService _registry = new();

    public RegistryServiceTests() => _path = @"HKCU:\" + _subKey;

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false);
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(Parent);
            if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException) { }
    }

    [Fact]
    public void SetCreatesTheKeyAndGetReadsTheValue()
    {
        Assert.Equal($"Registry value [{_path}] \"s\" set to \"hello\" (type: String).", _registry.SetValue(_path, "s", "hello", "String"));
        Assert.Equal($"Registry value [{_path}] \"s\" = hello", _registry.GetValue(_path, "s"));
    }

    [Theory]
    [InlineData("DWord", "42", "42")]
    [InlineData("DWord", "-1", "4294967295")]
    [InlineData("DWord", "0x10", "16")]
    [InlineData("DWord", "4294967295", "4294967295")]
    [InlineData("QWord", "-5", "18446744073709551611")]
    [InlineData("QWord", "1234567890123", "1234567890123")]
    [InlineData("Binary", "7", "7")]
    [InlineData("Binary", "0x1F", "31")]
    [InlineData("MultiString", "a,b", "a,b")]
    [InlineData("String", "  padded  ", "padded")]
    public void ValuesReadBackAsPowerShellShowsThem(string type, string value, string shown)
    {
        _registry.SetValue(_path, "v", value, type);
        Assert.Equal($"Registry value [{_path}] \"v\" = {shown}", _registry.GetValue(_path, "v"));
    }

    [Fact]
    public void ExpandStringIsExpandedOnRead()
    {
        _registry.SetValue(_path, "e", @"%USERNAME%\x", "ExpandString");
        Assert.Equal($@"Registry value [{_path}] ""e"" = {Environment.GetEnvironmentVariable("USERNAME")}\x", _registry.GetValue(_path, "e"));
    }

    [Fact]
    public void MultiStringAndBinaryWithSeveralItemsPrintOneItemPerLine()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey))
        {
            key.SetValue("m", new[] { "a", "b" }, RegistryValueKind.MultiString);
            key.SetValue("b", new byte[] { 1, 2 }, RegistryValueKind.Binary);
        }
        Assert.Equal($"Registry value [{_path}] \"m\" = a\nb", _registry.GetValue(_path, "m"));
        Assert.Equal($"Registry value [{_path}] \"b\" = 1\n2", _registry.GetValue(_path, "b"));
    }

    [Theory]
    [InlineData("DWord", "abc", "Cannot convert value \"abc\" to type \"System.UInt32\". Error: \"The input string 'abc' was not in a correct format.\"")]
    [InlineData("DWord", "5000000000", "Cannot convert value \"5000000000\" to type \"System.UInt32\". Error: \"Value was either too large or too small for a UInt32.\"")]
    [InlineData("QWord", "x1", "Cannot convert value \"x1\" to type \"System.UInt64\". Error: \"The input string 'x1' was not in a correct format.\"")]
    [InlineData("Binary", "1,2,255", "Cannot convert value \"1,2,255\" to type \"System.Byte[]\". Error: \"Cannot convert value \"1,2,255\" to type \"System.Byte\". Error: \"The input string '1,2,255' was not in a correct format.\"\"")]
    [InlineData("Binary", "300", "Cannot convert value \"300\" to type \"System.Byte[]\". Error: \"Cannot convert value \"300\" to type \"System.Byte\". Error: \"Value was either too large or too small for an unsigned byte.\"\"")]
    public void ConversionErrorsUsePowerShellsText(string type, string value, string message) =>
        Assert.Equal($"Error writing registry: Set-ItemProperty: {message}", _registry.SetValue(_path, "v", value, type));

    [Fact]
    public void AnInvalidTypeIsRejectedBeforeAnyWrite()
    {
        Assert.Equal("Error: invalid registry type 'Text'. Allowed: Binary, DWord, ExpandString, MultiString, QWord, String",
            _registry.SetValue(_path, "v", "x", "Text"));
        using var key = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Null(key);
    }

    [Fact]
    public void GetErrors()
    {
        _registry.SetValue(_path, "s", "x", "String");
        Assert.Equal($@"Error reading registry: Get-ItemProperty: Property nope does not exist at path HKEY_CURRENT_USER\{_subKey}.", _registry.GetValue(_path, "nope"));
        Assert.Equal($@"Error reading registry: Get-ItemProperty: Cannot find path '{_path}\none' because it does not exist.", _registry.GetValue(_path + @"\none", "x"));
        Assert.Equal("Error reading registry: Get-ItemProperty: Cannot find drive. A drive with the name 'HKXX' does not exist.", _registry.GetValue(@"HKXX:\Software", "x"));
    }

    [Fact]
    public void TheDefaultValueIsNamedDefault()
    {
        _registry.SetValue(_path, "(default)", "d", "String");
        Assert.Equal($"Registry value [{_path}] \"(default)\" = d", _registry.GetValue(_path, "(default)"));
        Assert.Contains("(default) : d", _registry.ListKey(_path));
    }

    [Fact]
    public void ListShowsValuesInFormatListLayoutAndSubKeys()
    {
        _registry.SetValue(_path, "s", "hello", "String");
        _registry.SetValue(_path, "dword", "42", "DWord");
        _registry.SetValue(_path, "m", "a", "MultiString");
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey))
        {
            key.SetValue("bytes", new byte[] { 1, 2, 3, 4, 5 }, RegistryValueKind.Binary);
            key.CreateSubKey("Beta").Dispose();
            key.CreateSubKey("Alpha").Dispose();
        }
        Assert.Equal(
            $"Registry key [{_path}]:\nValues:\ns     : hello\ndword : 42\nm     : {{a}}\nbytes : {{1, 2, 3, 4…}}\n\nSub-Keys:\nAlpha\nBeta",
            _registry.ListKey(_path));
    }

    [Fact]
    public void ListOfEmptyAndSubKeyOnlyKeys()
    {
        using (Registry.CurrentUser.CreateSubKey(_subKey)) { }
        Assert.Equal($"Registry key [{_path}]:\nNo values or sub-keys found.", _registry.ListKey(_path));
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey)) key.CreateSubKey("child").Dispose();
        Assert.Equal($"Registry key [{_path}]:\nSub-Keys:\nchild", _registry.ListKey(_path));
        Assert.Equal($@"Error listing registry: Get-ItemProperty: Cannot find path '{_path}\none' because it does not exist.", _registry.ListKey(_path + @"\none"));
    }

    [Fact]
    public void DeleteValuesAndKeys()
    {
        _registry.SetValue(_path, "s", "x", "String");
        Assert.Equal($"Registry value [{_path}] \"s\" deleted.", _registry.DeleteEntry(_path, "s"));
        Assert.Equal($@"Error deleting registry value: Remove-ItemProperty: Property s does not exist at path HKEY_CURRENT_USER\{_subKey}.", _registry.DeleteEntry(_path, "s"));
        Assert.Equal($"Registry key [{_path}] deleted.", _registry.DeleteEntry(_path, null));
        Assert.Equal($"Error deleting registry key: Remove-Item: Cannot find path '{_path}' because it does not exist.", _registry.DeleteEntry(_path, ""));
    }

    [Fact]
    public void ProviderPathsWork()
    {
        var provider = @"Registry::HKEY_CURRENT_USER\" + _subKey;
        _registry.SetValue(provider, "p", "1", "String");
        Assert.Equal($"Registry value [{provider}] \"p\" = 1", _registry.GetValue(provider, "p"));
        Assert.Equal($"Registry value [{_path}] \"p\" = 1", _registry.GetValue(_path.Replace('\\', '/'), "p").Replace('/', '\\'));
    }
}
```

`windows/tests/DesktopMcp.Server.Tests/Tools/RegistryToolTests.cs`:

```csharp
using Microsoft.Win32;

namespace DesktopMcp.Server.Tests;

public sealed class RegistryToolTests : IDisposable
{
    private readonly string _subKey = @"Software\desktop-mcp-tests\" + Guid.NewGuid().ToString("N");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false);

    private static Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(string json) => ToolCalls.CallAsync(ToolCalls.Create("Registry"), json);

    [Fact]
    public async Task SetThenGetThroughTheTool()
    {
        var path = (@"HKCU:\" + _subKey).Replace(@"\", @"\\");
        Assert.EndsWith("\"n\" set to \"7\" (type: DWord).", ToolCalls.OkText(await CallAsync($$"""{"mode": "set", "path": "{{path}}", "name": "n", "value": "7", "type": "DWord"}""")));
        Assert.EndsWith("\"n\" = 7", ToolCalls.OkText(await CallAsync($$"""{"mode": "get", "path": "{{path}}", "name": "n"}""")));
    }

    [Theory]
    [InlineData("""{"mode": "get", "path": "HKCU:\\Software"}""", "Error: name parameter is required for get mode.")]
    [InlineData("""{"mode": "set", "path": "HKCU:\\Software", "value": "x"}""", "Error: name parameter is required for set mode.")]
    [InlineData("""{"mode": "set", "path": "HKCU:\\Software", "name": "x"}""", "Error: value parameter is required for set mode.")]
    [InlineData("""{"mode": "rename", "path": "HKCU:\\Software"}""", "Error: mode must be \"get\", \"set\", \"delete\", or \"list\".")]
    public async Task MissingParametersUseUpstreamMessages(string json, string expected) =>
        Assert.Equal(expected, ToolCalls.OkText(await CallAsync(json)));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~RegistryServiceTests"`
Expected: build FAILS with `error CS0234: The type or namespace name 'WindowsRegistry' does not exist in the namespace 'DesktopMcp.Core'`.

- [ ] **Step 3: Implement registry paths**

`windows/src/DesktopMcp.Core/WindowsRegistry/RegistryLocation.cs`:

```csharp
using Microsoft.Win32;

namespace DesktopMcp.Core.WindowsRegistry;

/// <summary>A failure carrying the text PowerShell's registry provider prints, without the cmdlet prefix.</summary>
internal sealed class RegistryCommandException(string message) : Exception(message);

/// <summary>
/// A PowerShell registry path: the HKCU: and HKLM: drives (the only registry drives PowerShell defines), or a
/// Registry::&lt;hive&gt;\... provider path. "/" is accepted as a separator, as PowerShell does.
/// </summary>
internal sealed record RegistryLocation(RegistryHive Hive, string HiveName, string SubKey)
{
    private static readonly (string Short, string Long, RegistryHive Hive)[] Hives =
    [
        ("HKCU", "HKEY_CURRENT_USER", RegistryHive.CurrentUser),
        ("HKLM", "HKEY_LOCAL_MACHINE", RegistryHive.LocalMachine),
        ("HKCR", "HKEY_CLASSES_ROOT", RegistryHive.ClassesRoot),
        ("HKU", "HKEY_USERS", RegistryHive.Users),
        ("HKCC", "HKEY_CURRENT_CONFIG", RegistryHive.CurrentConfig),
    ];

    /// <summary>The path PowerShell shows in "does not exist at path" errors.</summary>
    public string ProviderPath => SubKey.Length == 0 ? HiveName : $@"{HiveName}\{SubKey}";

    public static RegistryLocation Parse(string path)
    {
        var normalized = path.Replace('/', '\\');
        foreach (var prefix in new[] { @"Microsoft.PowerShell.Core\Registry::", "Registry::" })
        {
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = normalized[prefix.Length..].Trim('\\');
            var separator = rest.IndexOf('\\');
            var hiveName = separator < 0 ? rest : rest[..separator];
            var index = Array.FindIndex(Hives, h => h.Long.Equals(hiveName, StringComparison.OrdinalIgnoreCase) || h.Short.Equals(hiveName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new RegistryCommandException($"Cannot find path '{path}' because it does not exist.");
            return new RegistryLocation(Hives[index].Hive, Hives[index].Long, separator < 0 ? "" : rest[(separator + 1)..].TrimEnd('\\'));
        }
        var colon = normalized.IndexOf(':');
        if (colon <= 0) throw new RegistryCommandException($"Cannot find path '{path}' because it does not exist.");
        var drive = normalized[..colon];
        var driveIndex = Array.FindIndex(Hives, 0, 2, h => h.Short.Equals(drive, StringComparison.OrdinalIgnoreCase));
        if (driveIndex < 0) throw new RegistryCommandException($"Cannot find drive. A drive with the name '{drive}' does not exist.");
        return new RegistryLocation(Hives[driveIndex].Hive, Hives[driveIndex].Long, normalized[(colon + 1)..].Trim('\\'));
    }

    /// <summary>The key, or PowerShell's "Cannot find path" error naming the path as the caller gave it.</summary>
    public RegistryKey Open(string originalPath, bool writable)
    {
        var root = RegistryKey.OpenBaseKey(Hive, RegistryView.Default);
        if (SubKey.Length == 0) return root;
        using (root)
        {
            return root.OpenSubKey(SubKey, writable)
                ?? throw new RegistryCommandException($"Cannot find path '{originalPath}' because it does not exist.");
        }
    }

    /// <summary>upstream's "if (-not (Test-Path ...)) { New-Item ... -Force }": the key, created when missing.</summary>
    public RegistryKey CreateOrOpen()
    {
        var root = RegistryKey.OpenBaseKey(Hive, RegistryView.Default);
        if (SubKey.Length == 0) return root;
        using (root) return root.CreateSubKey(SubKey, writable: true);
    }
}
```

- [ ] **Step 4: Implement the service**

`windows/src/DesktopMcp.Core/WindowsRegistry/RegistryService.cs`:

```csharp
using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DesktopMcp.Core.WindowsRegistry;

/// <summary>upstream registry/service.py on Microsoft.Win32.Registry, printing what pwsh 7 printed (Decisions 13).</summary>
public sealed partial class RegistryService
{
    /// <summary>upstream ALLOWED_REGISTRY_TYPES (registry/views.py:7-16).</summary>
    public static IReadOnlyList<string> AllowedTypes { get; } = ["String", "ExpandString", "Binary", "DWord", "MultiString", "QWord"];

    public string GetValue(string path, string name)
    {
        try
        {
            var location = RegistryLocation.Parse(path);
            using var key = location.Open(path, writable: false);
            var valueName = ValueName(name);
            if (!HasValue(key, valueName)) throw new RegistryCommandException($"Property {name} does not exist at path {location.ProviderPath}.");
            // Get-ItemProperty | Select-Object -ExpandProperty: one line per item, then Python's strip().
            return $"Registry value [{path}] \"{name}\" = {ExpandedText(key.GetValue(valueName)).Trim()}";
        }
        catch (Exception e) when (IsRegistryError(e))
        {
            return $"Error reading registry: Get-ItemProperty: {e.Message}";
        }
    }

    public string SetValue(string path, string name, string value, string type)
    {
        if (!AllowedTypes.Contains(type))
            return $"Error: invalid registry type '{type}'. Allowed: {string.Join(", ", AllowedTypes.Order(StringComparer.Ordinal))}";
        try
        {
            var location = RegistryLocation.Parse(path);
            // Upstream creates the key first, then converts and sets the value.
            using var key = location.CreateOrOpen();
            var (data, kind) = Convert(value, type);
            key.SetValue(ValueName(name), data, kind);
            return $"Registry value [{path}] \"{name}\" set to \"{value}\" (type: {type}).";
        }
        catch (Exception e) when (IsRegistryError(e))
        {
            return $"Error writing registry: Set-ItemProperty: {e.Message}";
        }
    }

    /// <summary>With a name, deletes that value; with no name (or an empty one), deletes the key and its subkeys.</summary>
    public string DeleteEntry(string path, string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            try
            {
                var location = RegistryLocation.Parse(path);
                using var key = location.Open(path, writable: true);
                var valueName = ValueName(name);
                if (!HasValue(key, valueName)) throw new RegistryCommandException($"Property {name} does not exist at path {location.ProviderPath}.");
                key.DeleteValue(valueName);
                return $"Registry value [{path}] \"{name}\" deleted.";
            }
            catch (Exception e) when (IsRegistryError(e))
            {
                return $"Error deleting registry value: Remove-ItemProperty: {e.Message}";
            }
        }
        try
        {
            var location = RegistryLocation.Parse(path);
            using (location.Open(path, writable: false)) { }
            using var root = RegistryKey.OpenBaseKey(location.Hive, RegistryView.Default);
            root.DeleteSubKeyTree(location.SubKey);
            return $"Registry key [{path}] deleted.";
        }
        catch (Exception e) when (IsRegistryError(e) || e is ArgumentException)
        {
            return $"Error deleting registry key: Remove-Item: {e.Message}";
        }
    }

    public string ListKey(string path)
    {
        try
        {
            var location = RegistryLocation.Parse(path);
            using var key = location.Open(path, writable: false);
            var values = FormatList(key.GetValueNames().Select(n => (n.Length == 0 ? "(default)" : n, key.GetValue(n))).ToList());
            var subKeys = string.Join("\n", key.GetSubKeyNames());
            var parts = new List<string>();
            if (values.Length > 0) parts.Add($"Values:\n{values}");
            if (subKeys.Length > 0) parts.Add($"\nSub-Keys:\n{subKeys}");
            if (parts.Count == 0) parts.Add("No values or sub-keys found.");
            return $"Registry key [{path}]:\n{string.Join("\n", parts).Trim()}";
        }
        catch (Exception e) when (IsRegistryError(e))
        {
            return $"Error listing registry: Get-ItemProperty: {e.Message}";
        }
    }

    // Set-ItemProperty -Type conversions as pwsh 7 performs them.
    internal static (object Data, RegistryValueKind Kind) Convert(string value, string type) => type switch
    {
        "String" => (value, RegistryValueKind.String),
        "ExpandString" => (value, RegistryValueKind.ExpandString),
        "MultiString" => (new[] { value }, RegistryValueKind.MultiString),
        "DWord" => (unchecked((int)ParseUnsigned(value, 32, "System.UInt32", "a UInt32")), RegistryValueKind.DWord),
        "QWord" => (unchecked((long)ParseUnsigned(value, 64, "System.UInt64", "a UInt64")), RegistryValueKind.QWord),
        "Binary" => (new[] { ParseByte(value) }, RegistryValueKind.Binary),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "not a registry type"),
    };

    // Decimal (a negative value wraps, so -1 becomes 0xFFFFFFFF) or 0x hex.
    private static ulong ParseUnsigned(string value, int bits, string typeName, string rangeName)
    {
        var text = value.Trim();
        var max = bits == 32 ? uint.MaxValue : ulong.MaxValue;
        var min = bits == 32 ? int.MinValue : long.MinValue;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) && hex <= max) return hex;
        }
        else if (text.StartsWith('-'))
        {
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var negative) && negative >= min)
                return unchecked((ulong)negative) & max;
        }
        else if (ulong.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var positive) && positive <= max)
        {
            return positive;
        }
        var inner = NumberPattern().IsMatch(text)
            ? $"Value was either too large or too small for {rangeName}."
            : $"The input string '{value}' was not in a correct format.";
        throw new RegistryCommandException($"Cannot convert value \"{value}\" to type \"{typeName}\". Error: \"{inner}\"");
    }

    // A string becomes a one-byte array: PowerShell converts it to a single Byte.
    private static byte ParseByte(string value)
    {
        var text = value.Trim();
        byte parsed;
        var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out parsed)
            : byte.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out parsed);
        if (ok) return parsed;
        var inner = NumberPattern().IsMatch(text)
            ? "Value was either too large or too small for an unsigned byte."
            : $"The input string '{value}' was not in a correct format.";
        throw new RegistryCommandException(
            $"Cannot convert value \"{value}\" to type \"System.Byte[]\". Error: \"Cannot convert value \"{value}\" to type \"System.Byte\". Error: \"{inner}\"\"");
    }

    // Get-ItemProperty shows DWORD and QWORD unsigned, and each array item on its own line.
    private static string ExpandedText(object? value) => value switch
    {
        byte[] bytes => string.Join("\n", bytes.Select(b => b.ToString(CultureInfo.InvariantCulture))),
        string[] strings => string.Join("\n", strings),
        _ => ScalarText(value),
    };

    // Format-List: names padded to the longest, arrays as {a, b} cut after $FormatEnumerationLimit (4) items with "…".
    private static string FormatList(IReadOnlyList<(string Name, object? Value)> items)
    {
        if (items.Count == 0) return "";
        var width = items.Max(i => i.Name.Length);
        return string.Join("\n", items.Select(i => $"{i.Name.PadRight(width)} : {ListText(i.Value)}".TrimEnd()));
    }

    private static string ListText(object? value) => value switch
    {
        byte[] bytes => Enumeration(bytes.Select(b => b.ToString(CultureInfo.InvariantCulture)).ToList()),
        string[] strings => Enumeration(strings),
        _ => ScalarText(value),
    };

    private static string Enumeration(IReadOnlyList<string> items) =>
        "{" + string.Join(", ", items.Take(4)) + (items.Count > 4 ? "…" : "") + "}";

    private static string ScalarText(object? value) => value switch
    {
        null => "",
        int dword => unchecked((uint)dword).ToString(CultureInfo.InvariantCulture),
        long qword => unchecked((ulong)qword).ToString(CultureInfo.InvariantCulture),
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string ValueName(string name) => name.Equals("(default)", StringComparison.OrdinalIgnoreCase) ? "" : name;

    private static bool HasValue(RegistryKey key, string valueName) =>
        key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase);

    private static bool IsRegistryError(Exception e) =>
        e is RegistryCommandException or SecurityException or UnauthorizedAccessException or IOException;

    [GeneratedRegex("^([+-]?[0-9]+|0[xX][0-9a-fA-F]+)$")]
    private static partial Regex NumberPattern();
}
```

- [ ] **Step 5: Run the Core tests to verify they pass**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~RegistryServiceTests"`
Expected: `Passed!` with `Failed: 0`. Afterwards `reg query HKCU\Software\desktop-mcp-tests` prints `ERROR: The system was unable to find the specified registry key or value.` (the key was cleaned up).

- [ ] **Step 6: Add the tool and wire it**

`windows/src/DesktopMcp.Server/Tools/RegistryTool.cs`:

```csharp
using DesktopMcp.Core.WindowsRegistry;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/registry.py:20-51.</summary>
public sealed class RegistryTool(Tool protocolTool, RegistryService registry) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var mode = arguments.RequiredString("mode");
        var path = arguments.RequiredString("path");
        var name = arguments.OptionalString("name");
        var value = arguments.OptionalString("value");
        var type = arguments.String("type", "String");
        var text = await Task.Run(() => mode switch
        {
            "get" => name is null ? "Error: name parameter is required for get mode." : registry.GetValue(path, name),
            "set" => name is null ? "Error: name parameter is required for set mode."
                : value is null ? "Error: value parameter is required for set mode."
                : registry.SetValue(path, name, value, type),
            "delete" => registry.DeleteEntry(path, name),
            "list" => registry.ListKey(path),
            _ => "Error: mode must be \"get\", \"set\", \"delete\", or \"list\".",
        }, cancellationToken).ConfigureAwait(false);
        return ToolResults.Text(text);
    }
}
```

Replace `windows/src/DesktopMcp.Server/Tools/ToolServices.cs` with:

```csharp
using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Core.WindowsRegistry;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// The services the tools call. Tests replace the ones with side effects (clipboard, toasts) with fakes through
/// <c>with</c>. Creating the defaults touches nothing: no Windows App SDK, no desktop.
/// </summary>
public sealed record ToolServices
{
    public required PowerShellRunner PowerShell { get; init; }
    public required FileSystemService FileSystem { get; init; }
    public required RegistryService Registry { get; init; }

    public static ToolServices CreateDefault() => new()
    {
        PowerShell = new PowerShellRunner(),
        FileSystem = new FileSystemService(),
        Registry = new RegistryService(),
    };
}
```

In `windows/src/DesktopMcp.Server/Tools/ToolRegistry.cs` replace the switch body with:

```csharp
    public static McpServerTool Create(Tool protocolTool, ToolServices services) => protocolTool.Name switch
    {
        "Wait" => new WaitTool(protocolTool),
        "PowerShell" => new PowerShellTool(protocolTool, services.PowerShell),
        "FileSystem" => new FileSystemTool(protocolTool, services.FileSystem),
        "Registry" => new RegistryTool(protocolTool, services.Registry),
        _ => new PendingTool(protocolTool),
    };
```

- [ ] **Step 7: Run all tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each project, including `RegistryToolTests`.

- [ ] **Step 8: Commit**

```bash
git add windows/src/DesktopMcp.Core/WindowsRegistry windows/src/DesktopMcp.Server windows/tests/DesktopMcp.Core.Tests/WindowsRegistry windows/tests/DesktopMcp.Server.Tests/Tools/RegistryToolTests.cs
git commit -m "Add the Registry tool on Microsoft.Win32.Registry with PowerShell's output" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 11: Process

upstream `process/service.py` and `tools/process.py`. `list` builds rows of PID, name, CPU and memory in MB (rounded to 0.1). An optional fuzzy name filter keeps rows whose `partial_ratio` is above 60. Rows are sorted by memory or CPU descending, or by name ascending; the sort is stable, so ties keep PID order. The list is cut with Python slice semantics (a negative limit drops rows from the end) and printed with tabulate's `simple` table under `Processes (N shown):`. `kill` takes a PID, or else every process whose name equals `name` ignoring case. psutil's `terminate` and `kill` both call `TerminateProcess` on Windows, so `force` only changes the wording. Names and CPU follow Decisions 15.

**Files:**
- Modify: `windows/src/DesktopMcp.Core/Interop/CoreNative.cs` (Toolhelp)
- Create: `windows/src/DesktopMcp.Core/Processes/ProcessSnapshot.cs`, `ProcessService.cs`
- Create: `windows/src/DesktopMcp.Server/Tools/ProcessTool.cs`
- Modify: `windows/src/DesktopMcp.Server/Tools/ToolServices.cs`, `ToolRegistry.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Processes/ProcessServiceTests.cs`, `windows/tests/DesktopMcp.Server.Tests/Tools/ProcessToolTests.cs`

**Interfaces:**
- Consumes: `Fuzzy.PartialRatio`, `TableFormatter.Simple`, `PyText.Fixed`, `CoreNative.CloseHandle`, `TempDirectory`, `ToolArguments.LooseBool`.
- Produces (namespace `DesktopMcp.Core.Processes`):
  - internal `ProcessSnapshot.Names() : Dictionary<int, string>`.
  - `public sealed record ProcessRow(int Pid, string Name, double Cpu, double MemoryMb)`.
  - `public sealed class ProcessService`: `Task<string> ListAsync(string? name, string sortBy, long limit, CancellationToken)`, `string Kill(string? name, long? pid, bool force)`, internal `static string Format(IReadOnlyList<ProcessRow> rows, string? name, string sortBy, long limit)`, internal `CpuSampleInterval` (250 ms).
  - `ToolServices.Processes` (`ProcessService`).

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Core.Tests/Processes/ProcessServiceTests.cs`:

```csharp
using System.Diagnostics;
using DesktopMcp.Core.Processes;

namespace DesktopMcp.Core.Tests;

public class ProcessServiceTests
{
    private static readonly ProcessRow[] Rows =
    [
        new(88, "notepad.exe", 0, 20.0),
        new(4, "System", 0, 0.1),
        new(1234, "chrome.exe", 12.5, 300.5),
        new(40, "chrome.exe", 1.25, 20.0),
    ];

    private readonly ProcessService _processes = new();

    [Fact]
    public void ListSortsByMemoryAndKeepsPidOrderForTies() =>
        Assert.Equal(
            "Processes (4 shown):\n" +
            "  PID  Name         CPU%    Memory\n" +
            "-----  -----------  ------  --------\n" +
            " 1234  chrome.exe   12.5%   300.5 MB\n" +
            "   40  chrome.exe   1.2%    20.0 MB\n" +
            "   88  notepad.exe  0.0%    20.0 MB\n" +
            "    4  System       0.0%    0.1 MB",
            ProcessService.Format(Rows, null, "memory", 20));

    [Fact]
    public void ListByNameAndByCpu()
    {
        Assert.Equal(["chrome.exe", "chrome.exe", "notepad.exe", "System"], Names(ProcessService.Format(Rows, null, "name", 20)));
        Assert.Equal(["chrome.exe", "chrome.exe", "System", "notepad.exe"], Names(ProcessService.Format(Rows, null, "cpu", 20)));
        Assert.Equal(["chrome.exe", "chrome.exe", "notepad.exe", "System"], Names(ProcessService.Format(Rows, null, "bogus", 20)));
    }

    [Fact]
    public void FuzzyNameFilterAndLimitSlicing()
    {
        Assert.Equal(["chrome.exe", "chrome.exe"], Names(ProcessService.Format(Rows, "Chrome", "memory", 20)));
        Assert.Equal(["chrome.exe"], Names(ProcessService.Format(Rows, null, "memory", 1)));
        Assert.Equal(["chrome.exe", "chrome.exe", "notepad.exe"], Names(ProcessService.Format(Rows, null, "memory", -1)));
        Assert.Equal("No processes found.", ProcessService.Format(Rows, null, "memory", 0));
        Assert.Equal("No processes found matching zzz.", ProcessService.Format(Rows, "zzz", "memory", 20));
    }

    [Fact]
    public async Task ListFindsThisProcessByItsImageName()
    {
        var name = ProcessSnapshot.Names()[Environment.ProcessId];
        Assert.EndsWith(".exe", name);
        var text = await _processes.ListAsync(name, "name", 500, CancellationToken.None);
        Assert.StartsWith("Processes (", text);
        Assert.Contains(text.Split('\n'), line => line.TrimStart().StartsWith($"{Environment.ProcessId}  {name}", StringComparison.Ordinal));
    }

    [Fact]
    public void PidZeroIsTheSystemIdleProcess() => Assert.Equal("System Idle Process", ProcessSnapshot.Names()[0]);

    [Fact]
    public void KillByPid()
    {
        using var child = UniqueChild.Start();
        Assert.Equal($"Terminated: {child.Name} (PID {child.Process.Id})", _processes.Kill(null, child.Process.Id, force: false));
        Assert.True(child.Process.WaitForExit(5000));
    }

    [Fact]
    public void ForceKillByName()
    {
        using var child = UniqueChild.Start();
        Assert.Equal($"Force killed: {child.Name} (PID {child.Process.Id})", _processes.Kill(child.Name.ToUpperInvariant(), null, force: true));
        Assert.True(child.Process.WaitForExit(5000));
    }

    [Fact]
    public void KillErrors()
    {
        Assert.Equal("Error: Provide either pid or name parameter for kill mode.", _processes.Kill(null, null, false));
        Assert.Equal("No process with PID 2147483644 found.", _processes.Kill(null, 2147483644, false));
        Assert.Equal("No process matching \"no-such-process-dmcp.exe\" found or access denied.", _processes.Kill("no-such-process-dmcp.exe", null, false));
        Assert.Equal("pid must be a positive integer (got -1)", Assert.Throws<ArgumentException>(() => _processes.Kill(null, -1, false)).Message);
    }

    private static string[] Names(string table) =>
        table.Split('\n').Skip(3).Select(line => line.Split("  ", StringSplitOptions.RemoveEmptyEntries)[1].Trim()).ToArray();

    /// <summary>A copy of ping.exe under a unique name, so killing by name cannot touch anything else.</summary>
    private sealed class UniqueChild : IDisposable
    {
        private readonly string _directory;

        private UniqueChild(string directory, string name, Process process)
        {
            _directory = directory;
            Name = name;
            Process = process;
        }

        public string Name { get; }
        public Process Process { get; }

        public static UniqueChild Start()
        {
            var directory = Path.Combine(Path.GetTempPath(), "dmcp-proc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var name = $"dmcp-test-{Guid.NewGuid().ToString("N")[..8]}.exe";
            var exe = Path.Combine(directory, name);
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
            var process = Process.Start(new ProcessStartInfo(exe, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
            return new UniqueChild(directory, name, process);
        }

        public void Dispose()
        {
            if (!Process.HasExited) Process.Kill();
            Process.WaitForExit(5000);
            Process.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
```

`windows/tests/DesktopMcp.Server.Tests/Tools/ProcessToolTests.cs`:

```csharp
namespace DesktopMcp.Server.Tests;

public class ProcessToolTests
{
    private static Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(string json) => ToolCalls.CallAsync(ToolCalls.Create("Process"), json);

    [Fact]
    public async Task ListsWithALimitGivenAsAString() =>
        Assert.StartsWith("Processes (1 shown):\n  PID  Name", ToolCalls.OkText(await CallAsync("""{"mode": "list", "limit": "1"}""")));

    [Theory]
    [InlineData("""{"mode": "kill"}""", "Error: Provide either pid or name parameter for kill mode.")]
    [InlineData("""{"mode": "kill", "pid": 2147483644, "force": "yes"}""", "No process with PID 2147483644 found.")]
    [InlineData("""{"mode": "stop"}""", "Error: mode must be either \"list\" or \"kill\".")]
    public async Task UpstreamMessages(string json, string expected) => Assert.Equal(expected, ToolCalls.OkText(await CallAsync(json)));

    [Fact]
    public async Task ANegativePidIsAnError()
    {
        var result = await CallAsync("""{"mode": "kill", "pid": -1}""");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Process': pid must be a positive integer (got -1)", ToolCalls.Text(result));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~ProcessServiceTests"`
Expected: build FAILS with `error CS0234: The type or namespace name 'Processes' does not exist in the namespace 'DesktopMcp.Core'`.

- [ ] **Step 3: Add the Toolhelp entry points**

In `windows/src/DesktopMcp.Core/Interop/CoreNative.cs`, inside the class, after the final-path block:

```csharp
    // kernel32: Toolhelp process snapshot (process names as psutil reports them)
    internal const uint Th32csSnapProcess = 0x2;
    internal static readonly nint InvalidHandleValue = -1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessEntry32W
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Process32First(nint snapshot, ProcessEntry32W* entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Process32Next(nint snapshot, ProcessEntry32W* entry);
```

- [ ] **Step 4: Implement the snapshot and the service**

`windows/src/DesktopMcp.Core/Processes/ProcessSnapshot.cs`:

```csharp
using DesktopMcp.Core.Interop;

namespace DesktopMcp.Core.Processes;

/// <summary>Image names by PID, as psutil's name() reports them ("chrome.exe", "System", "System Idle Process").</summary>
internal static unsafe class ProcessSnapshot
{
    public static Dictionary<int, string> Names()
    {
        var names = new Dictionary<int, string>();
        var snapshot = CoreNative.CreateToolhelp32Snapshot(CoreNative.Th32csSnapProcess, 0);
        if (snapshot == CoreNative.InvalidHandleValue) return names;
        try
        {
            var entry = new CoreNative.ProcessEntry32W { Size = (uint)sizeof(CoreNative.ProcessEntry32W) };
            for (var ok = CoreNative.Process32First(snapshot, &entry); ok; ok = CoreNative.Process32Next(snapshot, &entry))
            {
                var name = new string(entry.ExeFile);
                // Toolhelp calls PID 0 "[System Process]"; psutil calls it "System Idle Process".
                names[(int)entry.ProcessId] = entry.ProcessId == 0 ? "System Idle Process" : name.Length > 0 ? name : "Unknown";
            }
        }
        finally
        {
            CoreNative.CloseHandle(snapshot);
        }
        return names;
    }
}
```

`windows/src/DesktopMcp.Core/Processes/ProcessService.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Processes;

public sealed record ProcessRow(int Pid, string Name, double Cpu, double MemoryMb);

/// <summary>upstream process/service.py (Decisions 15).</summary>
public sealed class ProcessService
{
    internal static readonly TimeSpan CpuSampleInterval = TimeSpan.FromMilliseconds(250);

    public async Task<string> ListAsync(string? name, string sortBy, long limit, CancellationToken cancellationToken)
    {
        var rows = await SampleAsync(cancellationToken).ConfigureAwait(false);
        return Format(rows, name, sortBy, limit);
    }

    internal static string Format(IReadOnlyList<ProcessRow> rows, string? name, string sortBy, long limit)
    {
        // psutil.process_iter yields processes in PID order; the sorts below are stable.
        IEnumerable<ProcessRow> processes = rows.OrderBy(r => r.Pid);
        if (!string.IsNullOrEmpty(name))
        {
            var needle = name.ToLowerInvariant();
            processes = processes.Where(p => Fuzzy.PartialRatio(needle, p.Name.ToLowerInvariant()) > 60);
        }
        var sorted = sortBy switch
        {
            "cpu" => processes.OrderByDescending(p => p.Cpu).ToList(),
            "name" => processes.OrderBy(p => p.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList(),
            _ => processes.OrderByDescending(p => p.MemoryMb).ToList(),
        };
        // Python's procs[:limit].
        var count = limit >= 0 ? Math.Min(limit, sorted.Count) : Math.Max(0, sorted.Count + limit);
        var shown = sorted.Take((int)count).ToList();
        if (shown.Count == 0) return $"No processes found{(string.IsNullOrEmpty(name) ? "" : $" matching {name}")}.";
        var table = TableFormatter.Simple(
            ["PID", "Name", "CPU%", "Memory"],
            shown.Select(p => (IReadOnlyList<string>)[p.Pid.ToString(CultureInfo.InvariantCulture), p.Name, $"{PyText.Fixed(p.Cpu, 1)}%", $"{PyText.Fixed(p.MemoryMb, 1)} MB"]).ToList());
        return $"Processes ({shown.Count} shown):\n{table}";
    }

    public string Kill(string? name, long? pid, bool force)
    {
        if (pid is null && name is null) return "Error: Provide either pid or name parameter for kill mode.";
        var killed = new List<string>();
        if (pid is { } id)
        {
            // psutil.Process(pid) raises this ValueError, which upstream does not catch.
            if (id < 0) throw new ArgumentException($"pid must be a positive integer (got {id})");
            if (id > int.MaxValue) return $"No process with PID {id} found.";
            Process process;
            try { process = Process.GetProcessById((int)id); }
            catch (ArgumentException) { return $"No process with PID {id} found."; }
            using (process)
            {
                var processName = ProcessSnapshot.Names().GetValueOrDefault((int)id) ?? process.ProcessName;
                try { process.Kill(); }
                catch (Win32Exception) { return $"Access denied to kill PID {id}. Try running as administrator."; }
                catch (InvalidOperationException) { return $"No process with PID {id} found."; }
                killed.Add($"{processName} (PID {id})");
            }
        }
        else
        {
            foreach (var (processId, processName) in ProcessSnapshot.Names().OrderBy(p => p.Key))
            {
                if (!processName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var process = Process.GetProcessById(processId);
                    process.Kill();
                    killed.Add($"{processName} (PID {processId})");
                }
                catch (Exception e) when (e is ArgumentException or Win32Exception or InvalidOperationException)
                {
                }
            }
        }
        if (killed.Count == 0) return $"No process matching \"{name}\" found or access denied.";
        return $"{(force ? "Force killed" : "Terminated")}: {string.Join(", ", killed)}";
    }

    // Two CPU-time samples CpuSampleInterval apart; like psutil, a share of one CPU (can exceed 100).
    private static async Task<IReadOnlyList<ProcessRow>> SampleAsync(CancellationToken cancellationToken)
    {
        var first = CpuTimes();
        var clock = Stopwatch.StartNew();
        await Task.Delay(CpuSampleInterval, cancellationToken).ConfigureAwait(false);
        var second = CpuTimes();
        var elapsed = clock.Elapsed.TotalSeconds;
        var names = ProcessSnapshot.Names();
        var rows = new List<ProcessRow>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!names.TryGetValue(process.Id, out var name)) continue;
                var cpu = first.TryGetValue(process.Id, out var before) && second.TryGetValue(process.Id, out var after)
                    ? Math.Max(0, (after - before).TotalSeconds / elapsed * 100)
                    : 0;
                long workingSet;
                try { workingSet = process.WorkingSet64; }
                catch (InvalidOperationException) { continue; }
                // Python's round(rss / 2**20, 1); sorting uses the rounded value, as upstream does.
                var memory = double.Parse(PyText.Fixed(workingSet / (1024.0 * 1024.0), 1), CultureInfo.InvariantCulture);
                rows.Add(new ProcessRow(process.Id, name, cpu, memory));
            }
        }
        return rows;
    }

    private static Dictionary<int, TimeSpan> CpuTimes()
    {
        var times = new Dictionary<int, TimeSpan>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { times[process.Id] = process.TotalProcessorTime; }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return times;
    }
}
```

- [ ] **Step 5: Run the Core tests to verify they pass**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~ProcessServiceTests"`
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 6: Add the tool and wire it**

`windows/src/DesktopMcp.Server/Tools/ProcessTool.cs`:

```csharp
using DesktopMcp.Core.Processes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/process.py:20-42; force uses the loose rule (Decisions 6).</summary>
public sealed class ProcessTool(Tool protocolTool, ProcessService processes) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var mode = arguments.RequiredString("mode");
        var name = arguments.OptionalString("name");
        var pid = arguments.OptionalInteger("pid");
        var sortBy = arguments.String("sort_by", "memory");
        var limit = arguments.Integer("limit", 20);
        var text = mode switch
        {
            "list" => await processes.ListAsync(name, sortBy, limit, cancellationToken).ConfigureAwait(false),
            "kill" => await Task.Run(() => processes.Kill(name, pid, arguments.LooseBool("force", false)), cancellationToken).ConfigureAwait(false),
            _ => "Error: mode must be either \"list\" or \"kill\".",
        };
        return ToolResults.Text(text);
    }
}
```

Replace `windows/src/DesktopMcp.Server/Tools/ToolServices.cs` with:

```csharp
using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Core.Processes;
using DesktopMcp.Core.WindowsRegistry;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// The services the tools call. Tests replace the ones with side effects (clipboard, toasts) with fakes through
/// <c>with</c>. Creating the defaults touches nothing: no Windows App SDK, no desktop.
/// </summary>
public sealed record ToolServices
{
    public required PowerShellRunner PowerShell { get; init; }
    public required FileSystemService FileSystem { get; init; }
    public required RegistryService Registry { get; init; }
    public required ProcessService Processes { get; init; }

    public static ToolServices CreateDefault() => new()
    {
        PowerShell = new PowerShellRunner(),
        FileSystem = new FileSystemService(),
        Registry = new RegistryService(),
        Processes = new ProcessService(),
    };
}
```

In `windows/src/DesktopMcp.Server/Tools/ToolRegistry.cs` replace the switch body with:

```csharp
    public static McpServerTool Create(Tool protocolTool, ToolServices services) => protocolTool.Name switch
    {
        "Wait" => new WaitTool(protocolTool),
        "PowerShell" => new PowerShellTool(protocolTool, services.PowerShell),
        "FileSystem" => new FileSystemTool(protocolTool, services.FileSystem),
        "Registry" => new RegistryTool(protocolTool, services.Registry),
        "Process" => new ProcessTool(protocolTool, services.Processes),
        _ => new PendingTool(protocolTool),
    };
```

- [ ] **Step 7: Run all tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each project, including `ProcessToolTests`.

- [ ] **Step 8: Commit**

```bash
git add windows/src/DesktopMcp.Core windows/src/DesktopMcp.Server windows/tests/DesktopMcp.Core.Tests/Processes windows/tests/DesktopMcp.Server.Tests/Tools/ProcessToolTests.cs
git commit -m "Add the Process tool with measured CPU, fuzzy filtering and tabulate output" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 12: Clipboard

upstream `tools/clipboard.py:20-54` reads and writes `CF_UNICODETEXT` through pywin32. Spec §2 ("WinRT first") and §8 put the WinRT `Windows.ApplicationModel.DataTransfer.Clipboard` first, with the Win32 clipboard on a thread-pool thread as the fallback. WinRT clipboard calls run on a short-lived STA thread; `Flush()` keeps the text after the server exits. A probe step checks both paths under NativeAOT (P1 follow-up).

Messages: `Clipboard content:\n{data}`, `Clipboard is empty or contains non-text data.`, `Clipboard set to: {first 100 code points}{"..." if longer}`, `Error: text parameter required for set mode.`, `Error: mode must be either "get" or "set".`

**Side effects:** the Desktop-category tests and the probe change the user's clipboard text and restore the previous text afterwards. Non-text clipboard content (an image, files) is not restored. They are excluded from the default run.

**Files:**
- Modify: `windows/src/DesktopMcp.Desktop/DesktopMcp.Desktop.csproj` (InternalsVisibleTo the probe)
- Modify: `windows/src/DesktopMcp.Desktop/Interop/Native.cs` (clipboard entry points)
- Create: `windows/src/DesktopMcp.Desktop/Threading/StaThread.cs`
- Create: `windows/src/DesktopMcp.Desktop/ClipboardAccess/IClipboard.cs`, `WinRtClipboard.cs`, `Win32Clipboard.cs`, `SystemClipboard.cs`
- Create: `windows/probes/DesktopMcp.Probe/ClipboardProbe.cs`; modify `windows/probes/DesktopMcp.Probe/Program.cs`
- Create: `windows/src/DesktopMcp.Server/Tools/ClipboardTool.cs`
- Modify: `windows/src/DesktopMcp.Server/Tools/ToolServices.cs`, `ToolRegistry.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/ClipboardTests.cs` (Category=Desktop), `windows/tests/DesktopMcp.Server.Tests/Tools/ClipboardToolTests.cs`

**Interfaces:**
- Consumes: `PyText.Prefix`, `PyText.CodePointLength`, `ToolArguments`.
- Produces:
  - `DesktopMcp.Desktop.ClipboardAccess.IClipboard`: `Task<string?> GetTextAsync(CancellationToken)` (null when there is no text), `Task SetTextAsync(string text, CancellationToken)`.
  - `public enum ClipboardPath { None, WinRt, Win32 }`; `public sealed class SystemClipboard : IClipboard` with `ClipboardPath LastPath` and `string? LastWinRtError`.
  - internal `WinRtClipboard.GetText()`, `WinRtClipboard.SetText(string)`, `Win32Clipboard.GetText()`, `Win32Clipboard.SetText(string)`.
  - internal `DesktopMcp.Desktop.Threading.StaThread.RunAsync<T>(Func<T>, CancellationToken)` and `RunAsync(Action, CancellationToken)`.
  - `ToolServices.Clipboard` (`IClipboard`).

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Server.Tests/Tools/ClipboardToolTests.cs`:

```csharp
using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Server.Tools;

namespace DesktopMcp.Server.Tests;

public class ClipboardToolTests
{
    private sealed class FakeClipboard(string? text) : IClipboard
    {
        public string? Text { get; private set; } = text;
        public Task<string?> GetTextAsync(CancellationToken cancellationToken) => Task.FromResult(Text);
        public Task SetTextAsync(string text, CancellationToken cancellationToken)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }

    private static Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(FakeClipboard clipboard, string json) =>
        ToolCalls.CallAsync(ToolCalls.Create("Clipboard", ToolServices.CreateDefault() with { Clipboard = clipboard }), json);

    [Fact]
    public async Task GetReturnsTheText() =>
        Assert.Equal("Clipboard content:\nhello", ToolCalls.OkText(await CallAsync(new FakeClipboard("hello"), """{"mode": "get"}""")));

    [Fact]
    public async Task GetWithoutText() =>
        Assert.Equal("Clipboard is empty or contains non-text data.", ToolCalls.OkText(await CallAsync(new FakeClipboard(null), """{"mode": "get"}""")));

    [Fact]
    public async Task SetEchoesUpToAHundredCodePoints()
    {
        var clipboard = new FakeClipboard(null);
        var text = new string('a', 99) + "\U0001F437" + "tail";
        Assert.Equal($"Clipboard set to: {new string('a', 99)}\U0001F437...", ToolCalls.OkText(await CallAsync(clipboard, $$"""{"mode": "set", "text": "{{text}}"}""")));
        Assert.Equal(text, clipboard.Text);
        Assert.Equal("Clipboard set to: short", ToolCalls.OkText(await CallAsync(clipboard, """{"mode": "set", "text": "short"}""")));
    }

    [Theory]
    [InlineData("""{"mode": "set"}""", "Error: text parameter required for set mode.")]
    [InlineData("""{"mode": "cut"}""", "Error: mode must be either \"get\" or \"set\".")]
    public async Task UpstreamMessages(string json, string expected) =>
        Assert.Equal(expected, ToolCalls.OkText(await CallAsync(new FakeClipboard("x"), json)));
}
```

`windows/tests/DesktopMcp.Desktop.Tests/ClipboardTests.cs`:

```csharp
using DesktopMcp.Desktop.ClipboardAccess;

namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// Side effect: changes the user's clipboard text and restores the previous text (non-text content is lost).
/// Category=Desktop, so the default run skips it.
/// </summary>
[Trait("Category", "Desktop")]
public sealed class ClipboardTests : IAsyncLifetime
{
    private readonly SystemClipboard _clipboard = new();
    private string? _saved;

    public async Task InitializeAsync() => _saved = await _clipboard.GetTextAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        if (_saved is not null) await _clipboard.SetTextAsync(_saved, CancellationToken.None);
    }

    [Fact]
    public async Task SystemClipboardRoundTripsThroughWinRt()
    {
        var text = "desktop-mcp " + Guid.NewGuid();
        await _clipboard.SetTextAsync(text, CancellationToken.None);
        Assert.Equal(text, await _clipboard.GetTextAsync(CancellationToken.None));
        Assert.True(_clipboard.LastPath == ClipboardPath.WinRt, $"fell back to {_clipboard.LastPath}: {_clipboard.LastWinRtError}");
    }

    [Fact]
    public async Task Win32FallbackRoundTripsUnicode()
    {
        var text = "Win32 ✓ \U0001F437 " + Guid.NewGuid();
        await Task.Run(() => Win32Clipboard.SetText(text));
        Assert.Equal(text, await Task.Run(Win32Clipboard.GetText));
    }

    [Fact]
    public async Task WinRtSeesWhatWin32Wrote()
    {
        var text = "cross " + Guid.NewGuid();
        await Task.Run(() => Win32Clipboard.SetText(text));
        Assert.Equal(text, await _clipboard.GetTextAsync(CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Server.Tests --filter "FullyQualifiedName~ClipboardToolTests"`
Expected: build FAILS with `error CS0234: The type or namespace name 'ClipboardAccess' does not exist in the namespace 'DesktopMcp.Desktop'`.

- [ ] **Step 3: Add the Win32 entry points and the STA helper**

In `windows/src/DesktopMcp.Desktop/Interop/Native.cs`, inside the class, after the monitors block:

```csharp
    // user32/kernel32: clipboard (the Win32 fallback for the Clipboard tool)
    internal const uint CfUnicodeText = 13;
    internal const uint GmemMoveable = 0x0002;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(nint newOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GlobalFree(nint memory);
```

In `windows/src/DesktopMcp.Desktop/DesktopMcp.Desktop.csproj`, next to the existing `InternalsVisibleTo`, add:

```xml
    <InternalsVisibleTo Include="desktop-mcp-probe" />
```

`windows/src/DesktopMcp.Desktop/Threading/StaThread.cs`:

```csharp
namespace DesktopMcp.Desktop.Threading;

/// <summary>Runs one piece of work on a new single-threaded-apartment thread (WinRT clipboard calls need STA).</summary>
internal static class StaThread
{
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception e) { completion.SetException(e); }
        })
        {
            IsBackground = true,
            Name = "desktop-mcp STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(cancellationToken);
    }

    public static Task RunAsync(Action work, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            work();
            return true;
        }, cancellationToken);
}
```

- [ ] **Step 4: Implement the clipboard**

`windows/src/DesktopMcp.Desktop/ClipboardAccess/IClipboard.cs`:

```csharp
namespace DesktopMcp.Desktop.ClipboardAccess;

/// <summary>The user's clipboard text.</summary>
public interface IClipboard
{
    /// <summary>The clipboard's Unicode text, or null when it holds no text.</summary>
    Task<string?> GetTextAsync(CancellationToken cancellationToken);

    Task SetTextAsync(string text, CancellationToken cancellationToken);
}

public enum ClipboardPath { None, WinRt, Win32 }
```

`windows/src/DesktopMcp.Desktop/ClipboardAccess/WinRtClipboard.cs`:

```csharp
using Windows.ApplicationModel.DataTransfer;
using WinRtSystemClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace DesktopMcp.Desktop.ClipboardAccess;

/// <summary>WinRT clipboard calls; run them on an STA thread.</summary>
internal static class WinRtClipboard
{
    public static string? GetText()
    {
        var view = WinRtSystemClipboard.GetContent();
        return view.Contains(StandardDataFormats.Text) ? view.GetTextAsync().AsTask().GetAwaiter().GetResult() : null;
    }

    public static void SetText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        WinRtSystemClipboard.SetContent(package);
        // Render the data now, so the text stays on the clipboard after this process exits.
        WinRtSystemClipboard.Flush();
    }
}
```

`windows/src/DesktopMcp.Desktop/ClipboardAccess/Win32Clipboard.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Interop;

namespace DesktopMcp.Desktop.ClipboardAccess;

/// <summary>The Win32 clipboard with CF_UNICODETEXT, as upstream's pywin32 code uses it (OpenClipboard with no owner).</summary>
internal static unsafe class Win32Clipboard
{
    public static string? GetText()
    {
        Open();
        try
        {
            if (!Native.IsClipboardFormatAvailable(Native.CfUnicodeText)) return null;
            var handle = Native.GetClipboardData(Native.CfUnicodeText);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var pointer = Native.GlobalLock(handle);
            if (pointer == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try { return new string((char*)pointer); }
            finally { Native.GlobalUnlock(handle); }
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    public static void SetText(string text)
    {
        Open();
        try
        {
            if (!Native.EmptyClipboard()) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var bytes = (nuint)((text.Length + 1) * sizeof(char));
            var memory = Native.GlobalAlloc(Native.GmemMoveable, bytes);
            if (memory == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var pointer = (char*)Native.GlobalLock(memory);
            if (pointer is null)
            {
                Native.GlobalFree(memory);
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            text.AsSpan().CopyTo(new Span<char>(pointer, text.Length));
            pointer[text.Length] = '\0';
            Native.GlobalUnlock(memory);
            if (Native.SetClipboardData(Native.CfUnicodeText, memory) == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                Native.GlobalFree(memory);
                throw new Win32Exception(error);
            }
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    // Another application may hold the clipboard for a moment: retry for about 200 ms.
    private static void Open()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (Native.OpenClipboard(0)) return;
            if (attempt == 9) throw new Win32Exception(Marshal.GetLastPInvokeError());
            Thread.Sleep(20);
        }
    }
}
```

`windows/src/DesktopMcp.Desktop/ClipboardAccess/SystemClipboard.cs`:

```csharp
using DesktopMcp.Desktop.Threading;

namespace DesktopMcp.Desktop.ClipboardAccess;

/// <summary>
/// spec §2 WinRT first: the WinRT Clipboard on a short-lived STA thread; when it throws, the Win32 clipboard on the
/// thread pool. Touches neither the Windows App SDK nor the dispatcher thread.
/// </summary>
public sealed class SystemClipboard : IClipboard
{
    /// <summary>The path the last call used (diagnostics, probe and tests).</summary>
    public ClipboardPath LastPath { get; private set; }

    /// <summary>Why WinRT failed on the last fallback, if it did.</summary>
    public string? LastWinRtError { get; private set; }

    public async Task<string?> GetTextAsync(CancellationToken cancellationToken)
    {
        try
        {
            var text = await StaThread.RunAsync(WinRtClipboard.GetText, cancellationToken).ConfigureAwait(false);
            LastPath = ClipboardPath.WinRt;
            return text;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastWinRtError = $"{e.GetType().Name} 0x{e.HResult:X8}: {e.Message}";
        }
        var fallback = await Task.Run(Win32Clipboard.GetText, cancellationToken).ConfigureAwait(false);
        LastPath = ClipboardPath.Win32;
        return fallback;
    }

    public async Task SetTextAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await StaThread.RunAsync(() => WinRtClipboard.SetText(text), cancellationToken).ConfigureAwait(false);
            LastPath = ClipboardPath.WinRt;
            return;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastWinRtError = $"{e.GetType().Name} 0x{e.HResult:X8}: {e.Message}";
        }
        await Task.Run(() => Win32Clipboard.SetText(text), cancellationToken).ConfigureAwait(false);
        LastPath = ClipboardPath.Win32;
    }
}
```

- [ ] **Step 5: Add the tool and wire it**

`windows/src/DesktopMcp.Server/Tools/ClipboardTool.cs`:

```csharp
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.ClipboardAccess;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/clipboard.py:20-54.</summary>
public sealed class ClipboardTool(Tool protocolTool, IClipboard clipboard) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var mode = arguments.RequiredString("mode");
        var text = arguments.OptionalString("text");
        switch (mode)
        {
            case "get":
                var data = await clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false);
                return ToolResults.Text(data is null ? "Clipboard is empty or contains non-text data." : $"Clipboard content:\n{data}");
            case "set":
                if (text is null) return ToolResults.Text("Error: text parameter required for set mode.");
                await clipboard.SetTextAsync(text, cancellationToken).ConfigureAwait(false);
                var cut = PyText.CodePointLength(text) > 100 ? "..." : "";
                return ToolResults.Text($"Clipboard set to: {PyText.Prefix(text, 100)}{cut}");
            default:
                return ToolResults.Text("Error: mode must be either \"get\" or \"set\".");
        }
    }
}
```

Replace `windows/src/DesktopMcp.Server/Tools/ToolServices.cs` with:

```csharp
using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Core.Processes;
using DesktopMcp.Core.WindowsRegistry;
using DesktopMcp.Desktop.ClipboardAccess;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// The services the tools call. Tests replace the ones with side effects (clipboard, toasts) with fakes through
/// <c>with</c>. Creating the defaults touches nothing: no Windows App SDK, no desktop.
/// </summary>
public sealed record ToolServices
{
    public required PowerShellRunner PowerShell { get; init; }
    public required FileSystemService FileSystem { get; init; }
    public required RegistryService Registry { get; init; }
    public required ProcessService Processes { get; init; }
    public required IClipboard Clipboard { get; init; }

    public static ToolServices CreateDefault() => new()
    {
        PowerShell = new PowerShellRunner(),
        FileSystem = new FileSystemService(),
        Registry = new RegistryService(),
        Processes = new ProcessService(),
        Clipboard = new SystemClipboard(),
    };
}
```

In `windows/src/DesktopMcp.Server/Tools/ToolRegistry.cs` replace the switch body with:

```csharp
    public static McpServerTool Create(Tool protocolTool, ToolServices services) => protocolTool.Name switch
    {
        "Wait" => new WaitTool(protocolTool),
        "PowerShell" => new PowerShellTool(protocolTool, services.PowerShell),
        "FileSystem" => new FileSystemTool(protocolTool, services.FileSystem),
        "Registry" => new RegistryTool(protocolTool, services.Registry),
        "Process" => new ProcessTool(protocolTool, services.Processes),
        "Clipboard" => new ClipboardTool(protocolTool, services.Clipboard),
        _ => new PendingTool(protocolTool),
    };
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each project, including `ClipboardToolTests` (the Desktop clipboard tests are skipped).

Run (interactive session; changes the clipboard, then restores the text): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~ClipboardTests"`
Expected: `Passed!` with `Failed: 0`. If `SystemClipboardRoundTripsThroughWinRt` fails with "fell back to Win32", record the `LastWinRtError` text and continue to Step 7; the decision rule there settles it.

- [ ] **Step 7: Probe both paths under NativeAOT**

`windows/probes/DesktopMcp.Probe/ClipboardProbe.cs`:

```csharp
using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Desktop.Threading;

namespace DesktopMcp.Probe;

/// <summary>
/// Clipboard paths under NativeAOT: WinRT on an STA thread, WinRT on the thread pool (MTA), Win32, and SystemClipboard.
/// Changes the user's clipboard text and restores it at the end.
/// </summary>
internal static class ClipboardProbe
{
    public static async Task<bool> RunAsync()
    {
        var clipboard = new SystemClipboard();
        var saved = await clipboard.GetTextAsync(CancellationToken.None);
        Console.WriteLine($"clipboard: saved {saved?.Length ?? 0} chars through {clipboard.LastPath}");
        Check("WinRT on STA", () => StaThread.RunAsync(() => { WinRtClipboard.SetText("probe-winrt-sta"); return WinRtClipboard.GetText(); }, CancellationToken.None).GetAwaiter().GetResult(), "probe-winrt-sta");
        Check("WinRT on MTA", () => Task.Run(() => { WinRtClipboard.SetText("probe-winrt-mta"); return WinRtClipboard.GetText(); }).GetAwaiter().GetResult(), "probe-winrt-mta");
        var win32 = Check("Win32", () => Task.Run(() => { Win32Clipboard.SetText("probe-win32"); return Win32Clipboard.GetText(); }).GetAwaiter().GetResult(), "probe-win32");
        await clipboard.SetTextAsync("probe-system", CancellationToken.None);
        var system = await clipboard.GetTextAsync(CancellationToken.None) == "probe-system";
        Console.WriteLine($"clipboard: SystemClipboard {(system ? "OK" : "FAIL")} through {clipboard.LastPath}{(clipboard.LastWinRtError is { } error ? $" (WinRT: {error})" : "")}");
        if (saved is not null) await clipboard.SetTextAsync(saved, CancellationToken.None);
        return system && win32;
    }

    private static bool Check(string name, Func<string?> roundTrip, string expected)
    {
        try
        {
            var text = roundTrip();
            Console.WriteLine($"clipboard: {name}: {(text == expected ? "OK" : $"FAIL (read {text ?? "null"})")}");
            return text == expected;
        }
        catch (Exception e)
        {
            Console.WriteLine($"clipboard: {name}: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            return false;
        }
    }
}
```

In `windows/probes/DesktopMcp.Probe/Program.cs`, change the usage comment to `// AOT probes. Usage: desktop-mcp-probe bootstrap | dispatcher | imaging [out.png] | capture [out.png] | clipboard` and add this arm to the `switch` before the `_` arm:

```csharp
    "clipboard" => await DesktopMcp.Probe.ClipboardProbe.RunAsync(),
```

Run:
```powershell
dotnet publish probes/DesktopMcp.Probe -c Release -r win-x64 -o probes/out
probes/out/desktop-mcp-probe.exe clipboard
```
Expected: the publish reports no warnings; the probe prints the three path lines and `clipboard: SystemClipboard OK through WinRt`, and exits 0. Record the four lines for the spec (Task 17).

Decision rule: if WinRT fails on STA but works on MTA under AOT, change `SystemClipboard` to call WinRT through `Task.Run` instead of `StaThread`, rerun Steps 6 and 7, and record why. If WinRT fails on both, keep the order (WinRT then Win32): the fallback carries the tool. Record the HRESULT and add a follow-up for P2. In every case `SystemClipboard OK` must print.

- [ ] **Step 8: Commit**

```bash
git add windows/src/DesktopMcp.Desktop windows/src/DesktopMcp.Server windows/probes/DesktopMcp.Probe windows/tests/DesktopMcp.Desktop.Tests/ClipboardTests.cs windows/tests/DesktopMcp.Server.Tests/Tools/ClipboardToolTests.cs
git commit -m "Add the Clipboard tool on the WinRT clipboard with a Win32 fallback" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 13: Notification

upstream `notifications/service.py:9-57` builds a ToastGeneric XML through Windows PowerShell. Spec §8 uses CsWinRT directly: `ToastNotificationManager.CreateToastNotifier(app_id).Show(...)` with the same XML. Title and message are XML-escaped (`&`, `<`, `>`, `"`, `'`), and `notifier.Setting` is never read (it throws for unpackaged callers). Success: `Notification sent: "{title}" - {message}`. Failure: Decisions 14.

**Side effects:** the Desktop-category test and the probe show a toast under Windows PowerShell's AUMID.

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Toasts/IToastSender.cs`, `ToastSender.cs`
- Modify: `windows/probes/DesktopMcp.Probe/Program.cs` (toast probe)
- Create: `windows/src/DesktopMcp.Server/Tools/NotificationTool.cs`
- Modify: `windows/src/DesktopMcp.Server/Tools/ToolServices.cs`, `ToolRegistry.cs`
- Test: `windows/tests/DesktopMcp.Desktop.Tests/ToastSenderTests.cs`, `windows/tests/DesktopMcp.Server.Tests/Tools/NotificationToolTests.cs`

**Interfaces:**
- Consumes: `PyText.Prefix`, `ToolArguments`.
- Produces: `DesktopMcp.Desktop.Toasts.IToastSender.Show(string title, string message, string appId)`; `public sealed class ToastSender : IToastSender` with `const string WindowsPowerShellAppId` and internal `static string BuildXml(string title, string message)`; `ToolServices.Toasts` (`IToastSender`).

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Desktop.Tests/ToastSenderTests.cs`:

```csharp
using DesktopMcp.Desktop.Toasts;

namespace DesktopMcp.Desktop.Tests;

public class ToastSenderTests
{
    [Fact]
    public void TitleAndMessageAreXmlEscaped() =>
        Assert.Equal(
            "<toast><visual><binding template=\"ToastGeneric\"><text>a &lt;b&gt; &amp; &quot;c&quot; &apos;d&apos;</text><text>m</text></binding></visual></toast>",
            ToastSender.BuildXml("a <b> & \"c\" 'd'", "m"));

    /// <summary>Side effect: shows a toast under Windows PowerShell's AUMID.</summary>
    [Fact]
    [Trait("Category", "Desktop")]
    public void ShowsAToastUnderAnotherAppsAumid() =>
        new ToastSender().Show("desktop-mcp test", "ToastSenderTests: you can dismiss this.", ToastSender.WindowsPowerShellAppId);
}
```

`windows/tests/DesktopMcp.Server.Tests/Tools/NotificationToolTests.cs`:

```csharp
using DesktopMcp.Desktop.Toasts;
using DesktopMcp.Server.Tools;

namespace DesktopMcp.Server.Tests;

public class NotificationToolTests
{
    private sealed class FakeToasts(Exception? failure = null) : IToastSender
    {
        public List<(string Title, string Message, string AppId)> Shown { get; } = [];

        public void Show(string title, string message, string appId)
        {
            if (failure is not null) throw failure;
            Shown.Add((title, message, appId));
        }
    }

    private static Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(FakeToasts toasts, string json) =>
        ToolCalls.CallAsync(ToolCalls.Create("Notification", ToolServices.CreateDefault() with { Toasts = toasts }), json);

    [Fact]
    public async Task SendsAndReportsTheToast()
    {
        var toasts = new FakeToasts();
        Assert.Equal("Notification sent: \"Hi\" - Body", ToolCalls.OkText(await CallAsync(toasts, """{"title": "Hi", "message": "Body", "app_id": "My.App"}""")));
        Assert.Equal(("Hi", "Body", "My.App"), Assert.Single(toasts.Shown));
    }

    [Fact]
    public async Task AFailureKeepsTheFirstTwoHundredCharacters() =>
        Assert.Equal("Notification may have been sent. Error: " + new string('x', 200),
            ToolCalls.OkText(await CallAsync(new FakeToasts(new InvalidOperationException(new string('x', 300))), """{"title": "t", "message": "m", "app_id": "a"}""")));

    [Fact]
    public async Task AppIdIsRequired()
    {
        var result = await CallAsync(new FakeToasts(), """{"title": "t", "message": "m"}""");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Notification': Missing required argument: app_id", ToolCalls.Text(result));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Server.Tests --filter "FullyQualifiedName~NotificationToolTests"`
Expected: build FAILS with `error CS0234: The type or namespace name 'Toasts' does not exist in the namespace 'DesktopMcp.Desktop'`.

- [ ] **Step 3: Implement the toast sender**

`windows/src/DesktopMcp.Desktop/Toasts/IToastSender.cs`:

```csharp
namespace DesktopMcp.Desktop.Toasts;

public interface IToastSender
{
    /// <summary>Shows a ToastGeneric toast with a title and a body under the given Application User Model ID.</summary>
    void Show(string title, string message, string appId);
}
```

`windows/src/DesktopMcp.Desktop/Toasts/ToastSender.cs`:

```csharp
using System.Security;
using Windows.UI.Notifications;
using XmlDocument = Windows.Data.Xml.Dom.XmlDocument;

namespace DesktopMcp.Desktop.Toasts;

/// <summary>
/// spec §8: CsWinRT ToastNotificationManager. app_id takes any AUMID, which AppNotificationManager cannot do. Never reads
/// notifier.Setting, which throws for unpackaged callers. Needs no Windows App SDK.
/// </summary>
public sealed class ToastSender : IToastSender
{
    /// <summary>Windows PowerShell's AUMID, registered on every Windows install (probe and tests).</summary>
    public const string WindowsPowerShellAppId = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

    public void Show(string title, string message, string appId)
    {
        var xml = new XmlDocument();
        xml.LoadXml(BuildXml(title, message));
        ToastNotificationManager.CreateToastNotifier(appId).Show(new ToastNotification(xml));
    }

    // upstream's template (notifications/service.py:30-44) with ps_quote_for_xml's escaping.
    internal static string BuildXml(string title, string message) =>
        "<toast><visual><binding template=\"ToastGeneric\">" +
        $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(message)}</text>" +
        "</binding></visual></toast>";
}
```

- [ ] **Step 4: Add the tool and wire it**

`windows/src/DesktopMcp.Server/Tools/NotificationTool.cs`:

```csharp
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Toasts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/notification.py and notifications/service.py (Decisions 14).</summary>
public sealed class NotificationTool(Tool protocolTool, IToastSender toasts) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var title = arguments.RequiredString("title");
        var message = arguments.RequiredString("message");
        var appId = arguments.RequiredString("app_id");
        try
        {
            await Task.Run(() => toasts.Show(title, message, appId), cancellationToken).ConfigureAwait(false);
            return ToolResults.Text($"Notification sent: \"{title}\" - {message}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ToolResults.Text($"Notification may have been sent. Error: {PyText.Prefix(e.Message, 200)}");
        }
    }
}
```

Replace `windows/src/DesktopMcp.Server/Tools/ToolServices.cs` with:

```csharp
using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Core.Processes;
using DesktopMcp.Core.WindowsRegistry;
using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Desktop.Toasts;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// The services the tools call. Tests replace the ones with side effects (clipboard, toasts) with fakes through
/// <c>with</c>. Creating the defaults touches nothing: no Windows App SDK, no desktop.
/// </summary>
public sealed record ToolServices
{
    public required PowerShellRunner PowerShell { get; init; }
    public required FileSystemService FileSystem { get; init; }
    public required RegistryService Registry { get; init; }
    public required ProcessService Processes { get; init; }
    public required IClipboard Clipboard { get; init; }
    public required IToastSender Toasts { get; init; }

    public static ToolServices CreateDefault() => new()
    {
        PowerShell = new PowerShellRunner(),
        FileSystem = new FileSystemService(),
        Registry = new RegistryService(),
        Processes = new ProcessService(),
        Clipboard = new SystemClipboard(),
        Toasts = new ToastSender(),
    };
}
```

In `windows/src/DesktopMcp.Server/Tools/ToolRegistry.cs` replace the switch body with:

```csharp
    public static McpServerTool Create(Tool protocolTool, ToolServices services) => protocolTool.Name switch
    {
        "Wait" => new WaitTool(protocolTool),
        "PowerShell" => new PowerShellTool(protocolTool, services.PowerShell),
        "FileSystem" => new FileSystemTool(protocolTool, services.FileSystem),
        "Registry" => new RegistryTool(protocolTool, services.Registry),
        "Process" => new ProcessTool(protocolTool, services.Processes),
        "Clipboard" => new ClipboardTool(protocolTool, services.Clipboard),
        "Notification" => new NotificationTool(protocolTool, services.Toasts),
        _ => new PendingTool(protocolTool),
    };
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each project; `ToastSenderTests.TitleAndMessageAreXmlEscaped` runs by default, the Desktop toast test does not.

Run (interactive session; shows a toast): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath= --filter "FullyQualifiedName~ToastSenderTests"`
Expected: `Passed!` with `Failed: 0`, and a "desktop-mcp test" toast appears under Windows PowerShell.

- [ ] **Step 6: Probe the toast under NativeAOT**

In `windows/probes/DesktopMcp.Probe/Program.cs`, change the usage comment to end in `| clipboard | toast`, add this arm before the `_` arm:

```csharp
    "toast" => ProbeToast(),
```

and add this local function next to the others:

```csharp
static bool ProbeToast()
{
    try
    {
        new DesktopMcp.Desktop.Toasts.ToastSender().Show("desktop-mcp probe", "Toast through CsWinRT under NativeAOT",
            DesktopMcp.Desktop.Toasts.ToastSender.WindowsPowerShellAppId);
        Console.WriteLine("toast: shown");
        return true;
    }
    catch (Exception e)
    {
        Console.WriteLine($"toast: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
        return false;
    }
}
```

Run:
```powershell
dotnet publish probes/DesktopMcp.Probe -c Release -r win-x64 -o probes/out
probes/out/desktop-mcp-probe.exe toast
```
Expected: no publish warnings; `toast: shown`, exit code 0, and the toast appears. Record the line for the spec (Task 17).

Decision rule: if the toast fails only under AOT, stop and report BLOCKED with the HRESULT; do not fall back to PowerShell (spec §10 says Notification does not use PowerShell).

- [ ] **Step 7: Commit**

```bash
git add windows/src/DesktopMcp.Desktop/Toasts windows/src/DesktopMcp.Server windows/probes/DesktopMcp.Probe/Program.cs windows/tests/DesktopMcp.Desktop.Tests/ToastSenderTests.cs windows/tests/DesktopMcp.Server.Tests/Tools/NotificationToolTests.cs
git commit -m "Add the Notification tool on CsWinRT ToastNotificationManager" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 14: Scrape: URL parsing and address rules

upstream `validate_url` (`infrastructure/security.py:41-103`) checks a URL with Python's `urlsplit` and `ipaddress`. This task ports the two pure parts: a `urlsplit`-compatible parser (scheme, hostname, credentials, port with Python's messages) and the address rules. An address is blocked when Python 3.14 says `is_private`, `is_loopback`, `is_link_local`, `is_multicast`, `is_reserved` or `is_unspecified`; IPv4-mapped IPv6 addresses are judged as their IPv4 address, as in Python 3.13+. The Fake-IP ranges get their own message. These networks come from CPython 3.13+/3.14 `Lib/ipaddress.py`:

- IPv4 private: `0.0.0.0/8, 10.0.0.0/8, 127.0.0.0/8, 169.254.0.0/16, 172.16.0.0/12, 192.0.0.0/24, 192.0.0.170/31, 192.0.2.0/24, 192.168.0.0/16, 198.18.0.0/15, 198.51.100.0/24, 203.0.113.0/24, 240.0.0.0/4, 255.255.255.255/32`, except `192.0.0.9/32, 192.0.0.10/32`. Also blocked: multicast `224.0.0.0/4`, reserved `240.0.0.0/4`, loopback, link-local, unspecified. `100.64.0.0/10` (shared address space) is not blocked, as in Python.
- IPv6 private: `::1/128, ::/128, 64:ff9b:1::/48, 100::/64, 2001::/23, 2001:db8::/32, 2002::/16, 3fff::/20, fc00::/7, fe80::/10`, except `2001:1::1/128, 2001:1::2/128, 2001:3::/32, 2001:4:112::/48, 2001:20::/28, 2001:30::/28`. Also blocked: multicast `ff00::/8` and reserved `::/8, 100::/8, 200::/7, 400::/6, 800::/5, 1000::/4, 4000::/3, 6000::/3, 8000::/3, a000::/3, c000::/3, e000::/4, f000::/5, f800::/6, fe00::/9`.
- Fake-IP DNS ranges: `198.18.0.0/15, 2001:2::/48`.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Scrape/ScrapeException.cs`, `UrlParts.cs`, `AddressPolicy.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Scrape/UrlPartsTests.cs`, `AddressPolicyTests.cs`

**Interfaces:**
- Consumes: `PyText.Repr`.
- Produces (namespace `DesktopMcp.Core.Scrape`):
  - `public sealed class ScrapeException(string message) : Exception` (the ValueError/ConnectionError/TimeoutError texts the tool reports).
  - `public sealed record UrlParts(string Scheme, string Netloc, string? Hostname, string? Username, string? Password, string? PortText)` with `static UrlParts Parse(string url)` (throws `FormatException` for a bad IPv6 netloc) and `int? ReadPort()` (throws `FormatException` with Python's port messages).
  - `public static class AddressPolicy`: `bool IsBlocked(IPAddress address)`, `bool IsFakeIpRange(IPAddress address)`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Core.Tests/Scrape/UrlPartsTests.cs`:

```csharp
using DesktopMcp.Core.Scrape;

namespace DesktopMcp.Core.Tests;

public class UrlPartsTests
{
    [Theory]
    [InlineData("https://Example.COM/x", "https", "example.com")]
    [InlineData("HTTP://h", "http", "h")]
    [InlineData("ftp://x/y", "ftp", "x")]
    [InlineData("example.com", "", null)]
    [InlineData("localhost:8000", "localhost", null)]
    [InlineData("http:///path", "http", null)]
    [InlineData("http://[::1]:80/", "http", "::1")]
    [InlineData("  http://h/ \t", "http", "h")]
    [InlineData("javascript:alert(1)", "javascript", null)]
    public void SchemeAndHostnameFollowUrlsplit(string url, string scheme, string? hostname)
    {
        var parts = UrlParts.Parse(url);
        Assert.Equal(scheme, parts.Scheme);
        Assert.Equal(hostname, parts.Hostname);
    }

    [Fact]
    public void Credentials()
    {
        var parts = UrlParts.Parse("http://user:pw@host:81/");
        Assert.Equal(("user", "pw", "host", 81), (parts.Username, parts.Password, parts.Hostname, parts.ReadPort()));
        Assert.Equal("", UrlParts.Parse("http://@host/").Username);
        Assert.Null(UrlParts.Parse("http://host/").Username);
    }

    [Theory]
    [InlineData("http://[::1/")]
    [InlineData("http://::1]/")]
    [InlineData("http://[1.2.3.4]/")]
    public void BadIpv6NetlocsThrow(string url) => Assert.Equal("Invalid IPv6 URL", Assert.Throws<FormatException>(() => UrlParts.Parse(url)).Message);

    [Theory]
    [InlineData("http://h:abc/", "Port could not be cast to integer value as 'abc'")]
    [InlineData("http://h:99999/", "Port out of range 0-65535")]
    public void BadPortsUsePythonsMessages(string url, string message) =>
        Assert.Equal(message, Assert.Throws<FormatException>(() => UrlParts.Parse(url).ReadPort()).Message);

    [Fact]
    public void AnEmptyPortIsNone() => Assert.Null(UrlParts.Parse("http://h:/").ReadPort());
}
```

`windows/tests/DesktopMcp.Core.Tests/Scrape/AddressPolicyTests.cs`:

```csharp
using System.Net;
using DesktopMcp.Core.Scrape;

namespace DesktopMcp.Core.Tests;

public class AddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.1.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.0.0.8")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002::1")]
    [InlineData("3fff::1")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    public void BlockedAddresses(string address) => Assert.True(AddressPolicy.IsBlocked(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("192.0.0.9")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2001:20::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("fec0::1")]
    public void AllowedAddresses(string address) => Assert.False(AddressPolicy.IsBlocked(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("198.18.0.5", true)]
    [InlineData("198.19.255.255", true)]
    [InlineData("2001:2::1", true)]
    [InlineData("198.20.0.1", false)]
    [InlineData("::ffff:198.18.0.5", false)]
    public void FakeIpRanges(string address, bool expected) => Assert.Equal(expected, AddressPolicy.IsFakeIpRange(IPAddress.Parse(address)));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~UrlPartsTests|FullyQualifiedName~AddressPolicyTests"`
Expected: build FAILS with `error CS0234: The type or namespace name 'Scrape' does not exist in the namespace 'DesktopMcp.Core'`.

- [ ] **Step 3: Implement**

`windows/src/DesktopMcp.Core/Scrape/ScrapeException.cs`:

```csharp
namespace DesktopMcp.Core.Scrape;

/// <summary>A Scrape failure whose message the tool reports as <c>Error calling tool 'Scrape': ...</c>.</summary>
public sealed class ScrapeException(string message) : Exception(message);
```

`windows/src/DesktopMcp.Core/Scrape/UrlParts.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Scrape;

/// <summary>The parts of a URL that validate_url reads, split the way Python 3.14's urllib.parse.urlsplit splits them.</summary>
public sealed record UrlParts(string Scheme, string Netloc, string? Hostname, string? Username, string? Password, string? PortText)
{
    public static UrlParts Parse(string url)
    {
        // urlsplit strips C0 controls and spaces at both ends and removes tabs and newlines anywhere.
        var text = url.Trim(TrimCharacters).Replace("\t", "").Replace("\r", "").Replace("\n", "");
        var scheme = "";
        var colon = text.IndexOf(':');
        if (colon > 0 && char.IsAsciiLetter(text[0]) && text[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            scheme = text[..colon].ToLowerInvariant();
            text = text[(colon + 1)..];
        }

        var netloc = "";
        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            var end = text.IndexOfAny(['/', '?', '#'], 2);
            netloc = end < 0 ? text[2..] : text[2..end];
            if (netloc.Contains('[') != netloc.Contains(']')) throw new FormatException("Invalid IPv6 URL");
        }

        var at = netloc.LastIndexOf('@');
        var userinfo = at < 0 ? null : netloc[..at];
        var hostAndPort = at < 0 ? netloc : netloc[(at + 1)..];
        string hostname;
        string? port;
        if (hostAndPort.Contains('['))
        {
            var open = hostAndPort.IndexOf('[');
            var close = hostAndPort.IndexOf(']');
            if (open != 0 || close < 0) throw new FormatException("Invalid IPv6 URL");
            hostname = hostAndPort[1..close];
            if (!IPAddress.TryParse(hostname, out var address) || address.AddressFamily != AddressFamily.InterNetworkV6)
                throw new FormatException("Invalid IPv6 URL");
            var after = hostAndPort[(close + 1)..];
            if (after.Length > 0 && !after.StartsWith(':')) throw new FormatException("Invalid IPv6 URL");
            port = after.Length > 0 ? after[1..] : null;
        }
        else
        {
            var portColon = hostAndPort.IndexOf(':');
            hostname = portColon < 0 ? hostAndPort : hostAndPort[..portColon];
            port = portColon < 0 ? null : hostAndPort[(portColon + 1)..];
        }

        string? username = null, password = null;
        if (userinfo is not null)
        {
            var separator = userinfo.IndexOf(':');
            username = separator < 0 ? userinfo : userinfo[..separator];
            password = separator < 0 ? null : userinfo[(separator + 1)..];
        }
        return new UrlParts(scheme, netloc, hostname.Length == 0 ? null : hostname.ToLowerInvariant(), username, password,
            string.IsNullOrEmpty(port) ? null : port);
    }

    /// <summary>Python's SplitResult.port.</summary>
    public int? ReadPort()
    {
        if (PortText is null) return null;
        if (!PortText.All(char.IsAsciiDigit)) throw new FormatException($"Port could not be cast to integer value as {PyText.Repr(PortText)}");
        if (!int.TryParse(PortText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > 65535)
            throw new FormatException("Port out of range 0-65535");
        return port;
    }

    private static readonly char[] TrimCharacters = Enumerable.Range(0, 0x21).Select(i => (char)i).ToArray();
}
```

`windows/src/DesktopMcp.Core/Scrape/AddressPolicy.cs`:

```csharp
using System.Globalization;
using System.Net;

namespace DesktopMcp.Core.Scrape;

/// <summary>
/// upstream _is_private_target and _is_fake_ip_dns_answer (infrastructure/security.py:19-38) with Python 3.14's
/// ipaddress network lists. IPv4-mapped IPv6 addresses are judged as their IPv4 address, as Python 3.13+ does.
/// </summary>
public static class AddressPolicy
{
    private static readonly Network[] V4Private = Networks(
        "0.0.0.0/8", "10.0.0.0/8", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.0.170/31",
        "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "240.0.0.0/4", "255.255.255.255/32");
    private static readonly Network[] V4PrivateExceptions = Networks("192.0.0.9/32", "192.0.0.10/32");
    // loopback, link-local, multicast, reserved, unspecified
    private static readonly Network[] V4Other = Networks("127.0.0.0/8", "169.254.0.0/16", "224.0.0.0/4", "240.0.0.0/4", "0.0.0.0/32");
    private static readonly Network[] V6Private = Networks(
        "::1/128", "::/128", "64:ff9b:1::/48", "100::/64", "2001::/23", "2001:db8::/32", "2002::/16", "3fff::/20", "fc00::/7", "fe80::/10");
    private static readonly Network[] V6PrivateExceptions = Networks(
        "2001:1::1/128", "2001:1::2/128", "2001:3::/32", "2001:4:112::/48", "2001:20::/28", "2001:30::/28");
    // loopback, link-local, multicast, the reserved list, unspecified
    private static readonly Network[] V6Other = Networks(
        "::1/128", "fe80::/10", "ff00::/8", "::/8", "100::/8", "200::/7", "400::/6", "800::/5", "1000::/4", "4000::/3", "6000::/3",
        "8000::/3", "a000::/3", "c000::/3", "e000::/4", "f000::/5", "f800::/6", "fe00::/9", "::/128");
    private static readonly Network[] FakeIpRanges = Networks("198.18.0.0/15", "2001:2::/48");

    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            ? (In(V4Private, bytes) && !In(V4PrivateExceptions, bytes)) || In(V4Other, bytes)
            : (In(V6Private, bytes) && !In(V6PrivateExceptions, bytes)) || In(V6Other, bytes);
    }

    /// <summary>Python's "ip in network": an IPv6 address is never in an IPv4 network, so no mapping here.</summary>
    public static bool IsFakeIpRange(IPAddress address) => In(FakeIpRanges, address.GetAddressBytes());

    private static bool In(Network[] networks, byte[] address) => networks.Any(n => n.Contains(address));

    private static Network[] Networks(params string[] cidrs) => cidrs.Select(Network.Parse).ToArray();

    private sealed record Network(byte[] Bytes, int Prefix)
    {
        public static Network Parse(string cidr)
        {
            var slash = cidr.IndexOf('/');
            return new Network(IPAddress.Parse(cidr[..slash]).GetAddressBytes(), int.Parse(cidr[(slash + 1)..], CultureInfo.InvariantCulture));
        }

        public bool Contains(byte[] address)
        {
            if (address.Length != Bytes.Length) return false;
            for (var bit = 0; bit < Prefix; bit += 8)
            {
                var index = bit / 8;
                var mask = (byte)(0xFF << (8 - Math.Min(8, Prefix - bit)));
                if ((address[index] & mask) != (Bytes[index] & mask)) return false;
            }
            return true;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~UrlPartsTests|FullyQualifiedName~AddressPolicyTests"`
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core/Scrape windows/tests/DesktopMcp.Core.Tests/Scrape
git commit -m "Port urlsplit and Python's address rules for the Scrape SSRF guard" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 15: Scrape: HTML to Markdown

Spec §8: "a C# port of the macOS port's HTMLToMarkdown.swift" (`macos/Sources/DesktopMCPCore/HTMLToMarkdown.swift`). The Swift renderer walks a tree built by libxml2's tidy-HTML parser, which .NET does not have. So this task adds a small tolerant parser that builds the same kind of tree, then ports the renderer line by line. The parser handles void elements, raw-text elements (script, style, ...), entities (`WebUtility.HtmlDecode`), implied end tags (p, li, dt/dd, tr, td/th, thead/tbody/tfoot, option, headings) and end tags matched against the open-element stack. No package is added, so there is no AOT or licence risk. Like the macOS port, script, style and other non-content elements are dropped, and the page title is put first unless the body starts with a level-1 heading.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Scrape/HtmlParser.cs`, `HtmlToMarkdown.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Scrape/HtmlToMarkdownTests.cs`

**Interfaces:**
- Produces: internal `HtmlNode` (`Name`, `Text`, `Attributes`, `Children`, `Attribute(string)`, `InnerText()`, `Find(string tag)`); internal `HtmlParser.Parse(string html) : HtmlNode`; `public static class HtmlToMarkdown` with `string Convert(string html)`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Core.Tests/Scrape/HtmlToMarkdownTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~HtmlToMarkdownTests"`
Expected: build FAILS with `error CS0103: The name 'HtmlToMarkdown' does not exist in the current context`.

- [ ] **Step 3: Implement the parser**

`windows/src/DesktopMcp.Core/Scrape/HtmlParser.cs`:

```csharp
using System.Net;

namespace DesktopMcp.Core.Scrape;

/// <summary>An element or text node of a parsed HTML document.</summary>
internal sealed class HtmlNode(string name)
{
    public const string TextName = "#text";

    public string Name { get; } = name;
    public string Text { get; init; } = "";
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
    public List<HtmlNode> Children { get; } = [];

    public string Attribute(string attributeName) => Attributes.GetValueOrDefault(attributeName, "");

    public string InnerText() => Name == TextName ? Text : string.Concat(Children.Select(child => child.InnerText()));

    public HtmlNode? Find(string tag)
    {
        foreach (var child in Children)
        {
            if (child.Name == tag) return child;
            if (child.Find(tag) is { } found) return found;
        }
        return null;
    }
}

/// <summary>
/// A tolerant HTML parser standing in for the libxml2 tidy parser the macOS port uses: just enough tree for
/// <see cref="HtmlToMarkdown"/>. Tag and attribute names are lower-cased; entities are decoded in text and attributes.
/// </summary>
internal static class HtmlParser
{
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
                builder.Text(WebUtility.HtmlDecode(html[position..]));
                break;
            }
            if (lt > position) builder.Text(WebUtility.HtmlDecode(html[position..lt]));
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
                builder.Start(tag.Name, tag.Attributes, tag.SelfClosing);
                if (RawTextElements.Contains(tag.Name) && !tag.SelfClosing)
                {
                    var close = IndexOfCloseTag(html, position, tag.Name);
                    var content = html[position..close];
                    builder.Text(EscapableRawText.Contains(tag.Name) ? WebUtility.HtmlDecode(content) : content);
                    builder.End(tag.Name);
                    position = close >= html.Length ? html.Length : SkipPast(html, close, '>');
                }
            }
            else
            {
                builder.Text("<");
                position++;
            }
        }
        return document;
    }

    private readonly record struct Tag(string Name, Dictionary<string, string> Attributes, bool SelfClosing, int Next);

    private static Tag ReadTag(string html, int start)
    {
        var i = start;
        while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('/' or '>')) i++;
        var name = html[start..i].ToLowerInvariant();
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
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
            var attributeName = html[nameStart..i].ToLowerInvariant();
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
                    value = html[(i + 1)..end];
                    i = Math.Min(end + 1, html.Length);
                }
                else
                {
                    var valueStart = i;
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>') i++;
                    value = html[valueStart..i];
                }
            }
            attributes.TryAdd(attributeName, WebUtility.HtmlDecode(value));
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

    private sealed class Builder(HtmlNode document)
    {
        private readonly List<HtmlNode> _stack = [document];

        private HtmlNode Current => _stack[^1];

        public void Text(string text)
        {
            if (text.Length > 0) Current.Children.Add(new HtmlNode(HtmlNode.TextName) { Text = text });
        }

        public void Start(string tag, Dictionary<string, string> attributes, bool selfClosing)
        {
            if (ClosesParagraph.Contains(tag)) CloseInScope("p", ScopeBoundaries);
            if (tag == "li") CloseInScope("li", [.. ScopeBoundaries, "ul", "ol"]);
            if (tag is "dt" or "dd")
            {
                CloseInScope("dt", [.. ScopeBoundaries, "dl"]);
                CloseInScope("dd", [.. ScopeBoundaries, "dl"]);
            }
            if (tag == "tr") CloseInScope("tr", ["table", "thead", "tbody", "tfoot", "html"]);
            if (tag is "td" or "th")
            {
                CloseInScope("td", ["tr", "table", "html"]);
                CloseInScope("th", ["tr", "table", "html"]);
            }
            if (tag is "thead" or "tbody" or "tfoot")
            {
                foreach (var section in new[] { "thead", "tbody", "tfoot" }) CloseInScope(section, ["table", "html"]);
            }
            if (tag == "option") CloseInScope("option", ["select", "datalist", "html"]);
            if (Headings.Contains(tag) && Headings.Contains(Current.Name)) _stack.RemoveAt(_stack.Count - 1);

            var element = new HtmlNode(tag);
            foreach (var (name, value) in attributes) element.Attributes[name] = value;
            Current.Children.Add(element);
            if (!selfClosing && !VoidElements.Contains(tag)) _stack.Add(element);
        }

        public void End(string tag)
        {
            if (tag == "br")
            {
                Start("br", [], selfClosing: true);
                return;
            }
            for (var i = _stack.Count - 1; i > 0; i--)
            {
                if (_stack[i].Name != tag) continue;
                _stack.RemoveRange(i, _stack.Count - i);
                return;
            }
        }

        private void CloseInScope(string tag, ICollection<string> boundaries)
        {
            for (var i = _stack.Count - 1; i > 0; i--)
            {
                if (_stack[i].Name == tag)
                {
                    _stack.RemoveRange(i, _stack.Count - i);
                    return;
                }
                if (boundaries.Contains(_stack[i].Name)) return;
            }
        }
    }
}
```

- [ ] **Step 4: Port the renderer**

`windows/src/DesktopMcp.Core/Scrape/HtmlToMarkdown.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopMcp.Core.Scrape;

/// <summary>
/// HTML to Markdown, ported from the macOS port's HTMLToMarkdown.swift: the same element rules, frames for inline
/// wrappers, list nesting, whitespace collapsing and final tidy. Unlike Python's markdownify, the contents of script,
/// style and other non-content elements are dropped.
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
                    var indent = new string(' ', 2 * (Math.Max(_lists.Count, 1) - 1));
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
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~HtmlToMarkdownTests"`
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add windows/src/DesktopMcp.Core/Scrape windows/tests/DesktopMcp.Core.Tests/Scrape/HtmlToMarkdownTests.cs
git commit -m "Port the macOS HTML to Markdown converter with a tolerant HTML parser" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 16: Scrape: pinned fetch and the Scrape tool

upstream `Desktop.scrape` (`desktop/service.py:1033-1052`), `safe_get` and `validate_url` (`infrastructure/security.py:41-207`), and the tool (`tools/scrape.py:20-73`). Each request is validated first: scheme, hostname, credentials, port, and every resolved address against `AddressPolicy`. Then `SocketsHttpHandler.ConnectCallback` connects only to the first validated address, so a second DNS answer can never redirect the connection. Redirects are not followed automatically: at most 5 requests are made, and each `Location` is validated again. 4xx/5xx responses raise requests' `HTTPError` text, and the body becomes Markdown (Task 15). Decisions 16 and 17 list the departures and the P1 behaviour of `use_dom`/`use_sampling`.

Tests use a raw `TcpListener` HTTP server on 127.0.0.1 rather than `HttpListener`, because http.sys rejects requests whose Host header (the pinned test hostname) does not match a registered prefix, and non-admin prefixes are limited. The test seam is an internal constructor that replaces the address rule and the resolver. Production code always uses `AddressPolicy.IsBlocked` and `Dns`, and the production tests prove that loopback is blocked before any connection is made.

**System proxy amendment (user decision, 2026-10-19; Decision 16).** It overrides the step code below wherever they differ:

- The internal constructor gains a third parameter, `Func<Uri, Uri?> proxyFor`. It returns the proxy for a target URI, or `null` for a direct connection. The public constructor passes `SystemProxy`:

  ```csharp
  public ScrapeClient() : this(AddressPolicy.IsBlocked, Dns.GetHostAddressesAsync, SystemProxy) { }

  // HttpClient.DefaultProxy reads the Windows proxy settings (and HTTP(S)_PROXY / NO_PROXY).
  internal static Uri? SystemProxy(Uri target)
  {
      var proxy = HttpClient.DefaultProxy;
      if (proxy.IsBypassed(target)) return null;
      var address = proxy.GetProxy(target);
      return address is null || address == target ? null : address;
  }
  ```

- `GetAsync` still calls `ValidateAsync` first. Then `var proxy = _proxyFor(new Uri(url));`. When `proxy` is `null`, the handler is the one shown (`UseProxy = false` and the pinning `ConnectCallback`). Otherwise the handler sets `UseProxy = true` and `Proxy = new WebProxy(proxy)`, and has no `ConnectCallback`. The proxy path does not pin, so DNS rebinding is possible there, as with upstream behind a proxy. Note this in a comment.
- `Permissive(...)` in the tests passes `_ => null` as `proxyFor`, so a proxy configured on the test machine never affects the other tests.
- Add these two tests to `ScrapeClientTests`. The loopback server plays the proxy and receives the absolute-form request line.

  ```csharp
  [Fact]
  public async Task AProxyReceivesTheAbsoluteUrl()
  {
      await using var proxy = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(200, "OK", "<p>via proxy</p>"));
      var client = new ScrapeClient(_ => false, Resolve, _ => new Uri($"http://127.0.0.1:{proxy.Port}/"));
      Assert.Equal("via proxy", await client.FetchMarkdownAsync("http://site.test:8080/page", CancellationToken.None));
      var request = Assert.Single(proxy.Requests);
      Assert.Equal("http://site.test:8080/page", request.Path);
  }

  [Fact]
  public async Task AProxyDoesNotSkipValidation()
  {
      await using var proxy = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(200, "OK", "<p>secret</p>"));
      var client = new ScrapeClient(AddressPolicy.IsBlocked, Resolve, _ => new Uri($"http://127.0.0.1:{proxy.Port}/"));
      var error = await Assert.ThrowsAsync<ScrapeException>(() => client.FetchMarkdownAsync("http://site.test/", CancellationToken.None));
      Assert.Equal("Private, loopback, link-local, multicast, and reserved addresses are blocked: 127.0.0.1", error.Message);
      Assert.Empty(proxy.Requests);
  }
  ```

  If `LoopbackHttpServer` records only the path part of an absolute-form request line, make it record the request target verbatim.
- Update the Interfaces block accordingly: the internal constructor has three parameters, and there is an internal `static Uri? SystemProxy(Uri)`.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Scrape/ScrapeClient.cs`
- Create: `windows/src/DesktopMcp.Server/Tools/ScrapeTool.cs`
- Modify: `windows/src/DesktopMcp.Server/Tools/ToolServices.cs`, `ToolRegistry.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Scrape/LoopbackHttpServer.cs`, `ScrapeClientTests.cs`; `windows/tests/DesktopMcp.Server.Tests/Tools/ScrapeToolTests.cs`

**Interfaces:**
- Consumes: `UrlParts`, `AddressPolicy`, `ScrapeException`, `HtmlToMarkdown.Convert`, `IpAddressText.TryParse`, `TextEncodings.Get`, `ServerText.Version`, `ToolArguments.LooseBool`.
- Produces:
  - `public sealed class ScrapeClient` with `ScrapeClient()`, internal `ScrapeClient(Func<IPAddress, bool> isBlocked, Func<string, CancellationToken, Task<IPAddress[]>> resolve)`, `Task<string> FetchMarkdownAsync(string url, CancellationToken)`, internal `Task<IPAddress> ValidateAsync(string url, CancellationToken)`, constants `MaxRequests` (5), `MaxDownloadBytes` (8 MB), `RequestTimeout` (10 s).
  - `ToolServices.Scrape` (`ScrapeClient`).
  - Test helper `LoopbackHttpServer(Func<string, string, string> respond)` with `Port`, `Requests` and `static string Response(int status, string reason, string body, string contentType = "text/html; charset=utf-8", string? location = null)`.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Core.Tests/Scrape/LoopbackHttpServer.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DesktopMcp.Core.Tests;

/// <summary>
/// A one-connection-at-a-time HTTP/1.1 server on 127.0.0.1 for Scrape tests. <c>respond(path, host)</c> returns the
/// raw response; every request's path and Host header are recorded.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, string, string> _respond;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public LoopbackHttpServer(Func<string, string, string> respond)
    {
        _respond = respond;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public ConcurrentQueue<(string Path, string Host)> Requests { get; } = new();

    public static string Response(int status, string reason, string body, string contentType = "text/html; charset=utf-8", string? location = null) =>
        $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n" +
        (location is null ? "" : $"Location: {location}\r\n") + "\r\n" + body;

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            using (client)
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream);
                var lines = head.Split("\r\n");
                var path = lines[0].Split(' ')[1];
                var host = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim() ?? "";
                Requests.Enqueue((path, host));
                await stream.WriteAsync(Encoding.UTF8.GetBytes(_respond(path, host)));
            }
        }
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n') break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        _stop.Dispose();
    }
}
```

`windows/tests/DesktopMcp.Core.Tests/Scrape/ScrapeClientTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using DesktopMcp.Core.Scrape;

namespace DesktopMcp.Core.Tests;

public class ScrapeClientTests
{
    // Test resolver: IP literals as themselves, site.test as 127.0.0.1, fake.test as a Fake-IP answer.
    private static Task<IPAddress[]> Resolve(string host, CancellationToken _)
    {
        if (IPAddress.TryParse(host, out var literal)) return Task.FromResult(new[] { literal });
        if (host == "site.test") return Task.FromResult(new[] { IPAddress.Loopback });
        if (host == "fake.test") return Task.FromResult(new[] { IPAddress.Parse("198.18.0.5") });
        throw new SocketException(11001);
    }

    // The test seam: only the listed addresses are blocked, so the loopback test server is reachable.
    private static ScrapeClient Permissive(params string[] blocked) => new(address => blocked.Contains(address.ToString()), Resolve);

    [Fact]
    public async Task ProductionBlocksLoopbackBeforeConnecting()
    {
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(200, "OK", "<p>secret</p>"));
        var error = await Assert.ThrowsAsync<ScrapeException>(() => new ScrapeClient().FetchMarkdownAsync($"http://127.0.0.1:{server.Port}/", CancellationToken.None));
        Assert.Equal("Private, loopback, link-local, multicast, and reserved addresses are blocked: 127.0.0.1", error.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ProductionBlocksLocalhost()
    {
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(200, "OK", "<p>secret</p>"));
        var error = await Assert.ThrowsAsync<ScrapeException>(() => new ScrapeClient().FetchMarkdownAsync($"http://localhost:{server.Port}/", CancellationToken.None));
        Assert.StartsWith("Private, loopback, link-local, multicast, and reserved addresses are blocked: ", error.Message);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("ftp://example.com/", "URL scheme 'ftp' is not allowed; use http or https.")]
    [InlineData("example.com", "URL scheme '' is not allowed; use http or https.")]
    [InlineData("http:///path", "URL has no hostname: http:///path")]
    [InlineData("http://user:pw@example.com/", "URLs with embedded credentials are not allowed.")]
    [InlineData("http://[::1/", "Invalid URL: http://[::1/")]
    [InlineData("http://example.com:abc/", "Port could not be cast to integer value as 'abc'")]
    [InlineData("http://198.18.0.5/", "Private, loopback, link-local, multicast, and reserved addresses are blocked: 198.18.0.5")]
    [InlineData("http://[::ffff:10.0.0.1]/", "Private, loopback, link-local, multicast, and reserved addresses are blocked: ::ffff:10.0.0.1")]
    public async Task UrlRulesUseUpstreamMessages(string url, string message) =>
        Assert.Equal(message, (await Assert.ThrowsAsync<ScrapeException>(() => new ScrapeClient().FetchMarkdownAsync(url, CancellationToken.None))).Message);

    [Fact]
    public async Task AFakeIpDnsAnswerGetsItsOwnMessage()
    {
        var client = new ScrapeClient(AddressPolicy.IsBlocked, Resolve);
        var error = await Assert.ThrowsAsync<ScrapeException>(() => client.FetchMarkdownAsync("http://fake.test/", CancellationToken.None));
        Assert.Equal(
            "Hostname 'fake.test' resolved to 198.18.0.5, which is in a benchmarking range commonly used by proxy or VPN Fake-IP DNS. " +
            "The address remains blocked by SSRF protection. Configure the proxy/VPN to return the hostname's real public address or " +
            "exclude this hostname from Fake-IP mode.",
            error.Message);
    }

    [Fact]
    public async Task AnUnresolvableHostIsReported() =>
        Assert.StartsWith("Could not resolve hostname 'nowhere.test': ",
            (await Assert.ThrowsAsync<ScrapeException>(() => Permissive().FetchMarkdownAsync("http://nowhere.test/", CancellationToken.None))).Message);

    [Fact]
    public async Task FetchesThroughThePinnedAddressAndConverts()
    {
        await using var server = new LoopbackHttpServer((_, _) =>
            LoopbackHttpServer.Response(200, "OK", "<html><head><title>T</title></head><body><h1>Hello</h1><p>World</p></body></html>"));
        Assert.Equal("# Hello\n\nWorld", await Permissive().FetchMarkdownAsync($"http://site.test:{server.Port}/page", CancellationToken.None));
        var request = Assert.Single(server.Requests);
        Assert.Equal(("/page", $"site.test:{server.Port}"), request);
    }

    [Fact]
    public async Task FollowsRedirects()
    {
        await using var server = new LoopbackHttpServer((path, _) => path == "/a"
            ? LoopbackHttpServer.Response(302, "Found", "", location: "/b")
            : LoopbackHttpServer.Response(200, "OK", "<p>B</p>"));
        Assert.Equal("B", await Permissive().FetchMarkdownAsync($"http://site.test:{server.Port}/a", CancellationToken.None));
        Assert.Equal(["/a", "/b"], server.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task RedirectTargetsAreValidatedAgain()
    {
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(302, "Found", "", location: "http://127.0.0.2/"));
        var error = await Assert.ThrowsAsync<ScrapeException>(() => Permissive("127.0.0.2").FetchMarkdownAsync($"http://site.test:{server.Port}/a", CancellationToken.None));
        Assert.Equal("Private, loopback, link-local, multicast, and reserved addresses are blocked: 127.0.0.2", error.Message);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task StopsAfterFiveRequests()
    {
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(301, "Moved Permanently", "", location: "/loop"));
        var error = await Assert.ThrowsAsync<ScrapeException>(() => Permissive().FetchMarkdownAsync($"http://site.test:{server.Port}/", CancellationToken.None));
        Assert.Equal("Too many redirects while fetching URL", error.Message);
        Assert.Equal(5, server.Requests.Count);
    }

    [Theory]
    [InlineData(404, "Not Found", "Client")]
    [InlineData(503, "Service Unavailable", "Server")]
    public async Task HttpErrorsUseRequestsText(int status, string reason, string kind)
    {
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(status, reason, "<p>no</p>"));
        var url = $"http://site.test:{server.Port}/missing";
        var error = await Assert.ThrowsAsync<ScrapeException>(() => Permissive().FetchMarkdownAsync(url, CancellationToken.None));
        Assert.Equal($"HTTP error for {url}: {status} {kind} Error: {reason} for url: {url}", error.Message);
    }

    [Fact]
    public async Task CharsetComesFromTheHeaderThenTheMetaTag()
    {
        await using var server = new LoopbackHttpServer((path, _) => path == "/meta"
            ? LoopbackHttpServer.Response(200, "OK", "<meta charset=\"utf-8\"><p>héllo</p>", contentType: "text/html")
            : LoopbackHttpServer.Response(200, "OK", "<p>é</p>", contentType: "text/html; charset=iso-8859-1"));
        Assert.Equal("héllo", await Permissive().FetchMarkdownAsync($"http://site.test:{server.Port}/meta", CancellationToken.None));
        Assert.Equal("Ã©", await Permissive().FetchMarkdownAsync($"http://site.test:{server.Port}/header", CancellationToken.None));
    }

    [Fact]
    public async Task AConnectionFailureIsReported()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var url = $"http://site.test:{port}/";
        var error = await Assert.ThrowsAsync<ScrapeException>(() => Permissive().FetchMarkdownAsync(url, CancellationToken.None));
        Assert.StartsWith($"Failed to connect to {url}: ", error.Message);
    }

    [Fact]
    public async Task CancellationIsNotAnError()
    {
        await using var server = new LoopbackHttpServer((_, _) =>
        {
            Thread.Sleep(3000);
            return LoopbackHttpServer.Response(200, "OK", "<p>late</p>");
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Permissive().FetchMarkdownAsync($"http://site.test:{server.Port}/", cancellation.Token));
    }
}
```

`windows/tests/DesktopMcp.Server.Tests/Tools/ScrapeToolTests.cs`:

```csharp
namespace DesktopMcp.Server.Tests;

public class ScrapeToolTests
{
    private static Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(string json) => ToolCalls.CallAsync(ToolCalls.Create("Scrape"), json);

    [Theory]
    [InlineData("""{"url": "https://example.com", "use_dom": true}""")]
    [InlineData("""{"url": "https://example.com", "use_dom": "TRUE"}""")]
    public async Task UseDomReturnsUpstreamsNoDomMessage(string json) =>
        Assert.Equal("No DOM information found. Please open https://example.com in browser first.", ToolCalls.OkText(await CallAsync(json)));

    [Theory]
    [InlineData("""{"url": "http://127.0.0.1:9/"}""")]
    [InlineData("""{"url": "http://127.0.0.1:9/", "use_dom": "yes", "use_sampling": false}""")]
    public async Task ABlockedAddressIsAnErrorResult(string json)
    {
        var result = await CallAsync(json);
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Scrape': Private, loopback, link-local, multicast, and reserved addresses are blocked: 127.0.0.1", ToolCalls.Text(result));
    }

    [Fact]
    public async Task ASchemeErrorIsAnErrorResult() =>
        Assert.Equal("Error calling tool 'Scrape': URL scheme 'file' is not allowed; use http or https.",
            ToolCalls.Text(await CallAsync("""{"url": "file:///C:/Windows/win.ini"}""")));

    [Fact]
    public async Task UrlIsRequired() =>
        Assert.Equal("Error calling tool 'Scrape': Missing required argument: url", ToolCalls.Text(await CallAsync("{}")));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~ScrapeClientTests"`
Expected: build FAILS with `error CS0246: The type or namespace name 'ScrapeClient' could not be found`.

- [ ] **Step 3: Implement the client**

`windows/src/DesktopMcp.Core/Scrape/ScrapeClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.Net;

namespace DesktopMcp.Core.Scrape;

/// <summary>upstream Desktop.scrape over safe_get and validate_url (Decisions 16).</summary>
public sealed partial class ScrapeClient
{
    internal const int MaxRequests = 5;
    internal const int MaxDownloadBytes = 8 * 1024 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<IPAddress, bool> _isBlocked;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public ScrapeClient() : this(AddressPolicy.IsBlocked, Dns.GetHostAddressesAsync) { }

    /// <summary>Test seam: tests replace the address rule and the resolver. Production always uses the defaults.</summary>
    internal ScrapeClient(Func<IPAddress, bool> isBlocked, Func<string, CancellationToken, Task<IPAddress[]>> resolve)
    {
        _isBlocked = isBlocked;
        _resolve = resolve;
    }

    /// <summary>The page as Markdown. Validation, HTTP and network failures throw <see cref="ScrapeException"/>.</summary>
    public async Task<string> FetchMarkdownAsync(string url, CancellationToken cancellationToken)
    {
        var current = url;
        for (var request = 1; ; request++)
        {
            var response = await GetAsync(current, cancellationToken).ConfigureAwait(false);
            if (response.Location is null)
            {
                if (response.Status is >= 400 and < 600)
                {
                    var kind = response.Status < 500 ? "Client" : "Server";
                    throw new ScrapeException($"HTTP error for {current}: {response.Status} {kind} Error: {response.Reason} for url: {response.RequestUri}");
                }
                return HtmlToMarkdown.Convert(Decode(response.Body, response.Charset));
            }
            if (request == MaxRequests) throw new ScrapeException("Too many redirects while fetching URL");
            current = new Uri(new Uri(current), response.Location).AbsoluteUri;   // urljoin
        }
    }

    /// <summary>upstream validate_url: the address to connect to, or a ScrapeException with upstream's message.</summary>
    internal async Task<IPAddress> ValidateAsync(string url, CancellationToken cancellationToken)
    {
        UrlParts parts;
        try { parts = UrlParts.Parse(url); }
        catch (FormatException) { throw new ScrapeException($"Invalid URL: {url}"); }
        if (parts.Scheme is not ("http" or "https")) throw new ScrapeException($"URL scheme '{parts.Scheme}' is not allowed; use http or https.");
        if (string.IsNullOrEmpty(parts.Hostname)) throw new ScrapeException($"URL has no hostname: {url}");
        if (!string.IsNullOrEmpty(parts.Username) || !string.IsNullOrEmpty(parts.Password))
            throw new ScrapeException("URLs with embedded credentials are not allowed.");
        try { parts.ReadPort(); }
        catch (FormatException e) { throw new ScrapeException(e.Message); }

        var hostnameIsLiteral = IpAddressText.TryParse(parts.Hostname, out _);
        IPAddress[] addresses;
        try { addresses = await _resolve(parts.Hostname, cancellationToken).ConfigureAwait(false); }
        catch (SocketException e) { throw new ScrapeException($"Could not resolve hostname '{parts.Hostname}': {e.Message}"); }
        if (addresses.Length == 0) throw new ScrapeException($"Could not resolve hostname '{parts.Hostname}': no addresses");
        foreach (var address in addresses)
        {
            if (!_isBlocked(address)) continue;
            if (!hostnameIsLiteral && AddressPolicy.IsFakeIpRange(address))
            {
                throw new ScrapeException(
                    $"Hostname '{parts.Hostname}' resolved to {address}, which is in a benchmarking range commonly used by proxy or VPN Fake-IP DNS. " +
                    "The address remains blocked by SSRF protection. Configure the proxy/VPN to return the hostname's real public address or " +
                    "exclude this hostname from Fake-IP mode.");
            }
            throw new ScrapeException($"Private, loopback, link-local, multicast, and reserved addresses are blocked: {address}");
        }
        return addresses[0];
    }

    private sealed record Fetched(int Status, string Reason, Uri? Location, byte[] Body, string? Charset, string RequestUri);

    // upstream safe_get: one validated request, never following redirects, connecting only to the validated address.
    private async Task<Fetched> GetAsync(string url, CancellationToken cancellationToken)
    {
        var pinned = await ValidateAsync(url, cancellationToken).ConfigureAwait(false);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = RequestTimeout,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(pinned.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(pinned, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("desktop-mcp", ServerText.Version));
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            // requests' is_redirect: a Location header and 301, 302, 303, 307 or 308.
            var location = status is 301 or 302 or 303 or 307 or 308 ? response.Headers.Location : null;
            var body = location is null ? await ReadBodyAsync(response.Content, linked.Token).ConfigureAwait(false) : [];
            return new Fetched(status, response.ReasonPhrase ?? "", location, body, response.Content.Headers.ContentType?.CharSet,
                request.RequestUri!.AbsoluteUri);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ScrapeException($"Request timed out for {url}: no complete response within {RequestTimeout.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException e)
        {
            throw new ScrapeException($"Failed to connect to {url}: {e.Message}");
        }
    }

    // The body up to MaxDownloadBytes; a page that keeps streaming cannot fill memory.
    private static async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < MaxDownloadBytes && (read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            buffer.Write(chunk, 0, (int)Math.Min(read, MaxDownloadBytes - buffer.Length));
        return buffer.ToArray();
    }

    // The header's charset, else a <meta charset> in the first 1024 bytes, else UTF-8.
    internal static string Decode(byte[] body, string? charset)
    {
        var name = charset ?? MetaCharset(body);
        if (name is not null)
        {
            try { return TextEncodings.Get(name).Encoding.GetString(body); }
            catch (ArgumentException) { }
        }
        var text = Encoding.UTF8.GetString(body);
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }

    private static string? MetaCharset(byte[] body)
    {
        var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 1024));
        var match = MetaCharsetPattern().Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""<meta[^>]+charset\s*=\s*["']?([A-Za-z0-9_\-:.]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharsetPattern();
}
```

The User-Agent header is `desktop-mcp/<version>` (`ProductInfoHeaderValue`); `ServerText` is `DesktopMcp.Core.ServerText`, so no extra `using` is needed.

- [ ] **Step 4: Run the Core tests to verify they pass**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~ScrapeClientTests"`
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 5: Add the tool and wire it**

`windows/src/DesktopMcp.Server/Tools/ScrapeTool.cs`:

```csharp
using DesktopMcp.Core.Scrape;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/scrape.py:20-73 without sampling (Decisions 17): use_dom and use_sampling are loose booleans.</summary>
public sealed class ScrapeTool(Tool protocolTool, ScrapeClient scrape) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var url = arguments.RequiredString("url");
        _ = arguments.OptionalString("query");          // used by sampling, which arrives in P5
        var useDom = arguments.LooseBool("use_dom", false);
        _ = arguments.LooseBool("use_sampling", true);  // P1 always returns the raw content (upstream's fallback)
        if (useDom)
        {
            // The browser DOM arrives with Snapshot (P2) and its DOM mode (P6); until then there is never a DOM node.
            return ToolResults.Text($"No DOM information found. Please open {url} in browser first.");
        }
        var content = await scrape.FetchMarkdownAsync(url, cancellationToken).ConfigureAwait(false);
        return ToolResults.Text($"URL: {url}\nContent:\n{content}");
    }
}
```

Replace `windows/src/DesktopMcp.Server/Tools/ToolServices.cs` with:

```csharp
using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Core.Processes;
using DesktopMcp.Core.Scrape;
using DesktopMcp.Core.WindowsRegistry;
using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Desktop.Toasts;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// The services the tools call. Tests replace the ones with side effects (clipboard, toasts) with fakes through
/// <c>with</c>. Creating the defaults touches nothing: no Windows App SDK, no desktop.
/// </summary>
public sealed record ToolServices
{
    public required PowerShellRunner PowerShell { get; init; }
    public required FileSystemService FileSystem { get; init; }
    public required RegistryService Registry { get; init; }
    public required ProcessService Processes { get; init; }
    public required IClipboard Clipboard { get; init; }
    public required IToastSender Toasts { get; init; }
    public required ScrapeClient Scrape { get; init; }

    public static ToolServices CreateDefault() => new()
    {
        PowerShell = new PowerShellRunner(),
        FileSystem = new FileSystemService(),
        Registry = new RegistryService(),
        Processes = new ProcessService(),
        Clipboard = new SystemClipboard(),
        Toasts = new ToastSender(),
        Scrape = new ScrapeClient(),
    };
}
```

In `windows/src/DesktopMcp.Server/Tools/ToolRegistry.cs` replace the switch body with:

```csharp
    public static McpServerTool Create(Tool protocolTool, ToolServices services) => protocolTool.Name switch
    {
        "Wait" => new WaitTool(protocolTool),
        "PowerShell" => new PowerShellTool(protocolTool, services.PowerShell),
        "FileSystem" => new FileSystemTool(protocolTool, services.FileSystem),
        "Registry" => new RegistryTool(protocolTool, services.Registry),
        "Process" => new ProcessTool(protocolTool, services.Processes),
        "Clipboard" => new ClipboardTool(protocolTool, services.Clipboard),
        "Notification" => new NotificationTool(protocolTool, services.Toasts),
        "Scrape" => new ScrapeTool(protocolTool, services.Scrape),
        _ => new PendingTool(protocolTool),
    };
```

- [ ] **Step 6: Run all tests**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each project, including `ScrapeToolTests` and `ContractTests` (the contract is unchanged).

- [ ] **Step 7: Commit**

```bash
git add windows/src/DesktopMcp.Core/Scrape windows/src/DesktopMcp.Server windows/tests/DesktopMcp.Core.Tests/Scrape windows/tests/DesktopMcp.Server.Tests/Tools/ScrapeToolTests.cs
git commit -m "Add the Scrape tool with a pinned-address fetch and validated redirects" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 17: Release verification and documentation

The P1 checkpoint. The AOT smoke test calls every headless tool that has no desktop side effect, because xUnit runs everything under the JIT. Both RIDs are published, the Desktop tests run by hand, and the results go into the spec and the follow-ups list. Closes the remaining P1 follow-ups: record the stdio smoke result in the spec, and run the smoke test once on a machine without the Windows App SDK runtime (needs the user).

**Files:**
- Modify: `windows/scripts/smoke-stdio.ps1`
- Modify: `docs/superpowers/specs/2026-10-19-windows-port-design.md` (§8, §9, §11, §12)
- Modify: `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`

**Interfaces:**
- Consumes: every tool from Tasks 4-16, `build-release.ps1`, the probe's `imaging`, `clipboard` and `toast` commands.
- Produces: the measured P1 results recorded in the spec; the follow-ups list without the closed P1 items.

- [ ] **Step 1: Call the headless tools in the smoke test**

In `windows/scripts/smoke-stdio.ps1`, add this function after `Assert-ToolsMatchSpec`:

```powershell
function Invoke-Tool([int]$Id, [string]$Name, [hashtable]$Arguments) {
    Send @{ jsonrpc = '2.0'; id = $Id; method = 'tools/call'; params = @{ name = $Name; arguments = $Arguments } }
    $response = Receive $Id
    if ($null -eq $response.result) { throw "$Name failed: $($response | ConvertTo-Json -Depth 5)" }
    return $response.result
}
```

and insert this block right after the Wait checks (before the graphics-stack check, so the check also covers these calls):

```powershell
    # Headless tools under NativeAOT. Clipboard and Notification are left out: they change the user's desktop.
    $cases = @(
        @{ Name = 'FileSystem'; Arguments = @{ mode = 'info'; path = $exePath }; Prefix = 'Path: '; IsError = $false },
        @{ Name = 'Process'; Arguments = @{ mode = 'list'; limit = 1 }; Prefix = 'Processes (1 shown):'; IsError = $false },
        @{ Name = 'Registry'; Arguments = @{ mode = 'list'; path = 'HKCU:\Environment' }; Prefix = 'Registry key [HKCU:\Environment]:'; IsError = $false },
        @{ Name = 'PowerShell'; Arguments = @{ command = 'Write-Output smoke'; timeout = 30 }; Prefix = 'Response: smoke'; IsError = $false },
        @{ Name = 'Scrape'; Arguments = @{ url = 'http://127.0.0.1:9/'; use_sampling = $false }; Prefix = "Error calling tool 'Scrape': Private, loopback"; IsError = $true }
    )
    $id = 10
    foreach ($case in $cases) {
        $result = Invoke-Tool ($id++) $case.Name $case.Arguments
        $text = $result.content[0].text
        if ([bool]$result.isError -ne $case.IsError -or -not $text.StartsWith($case.Prefix, [StringComparison]::Ordinal)) {
            throw "$($case.Name): unexpected result (isError=$($result.isError)): $text"
        }
    }
```

and change the success line to:

```powershell
    Write-Host "smoke OK: $($init.result.serverInfo.name) $($init.result.serverInfo.version), $($list.result.tools.Count) tools match spec/windows/tools.json, Wait and $($cases.Count) headless tools OK, graphics stack not loaded ($($process.Modules.Count) modules)"
```

- [ ] **Step 2: Run the full default suite**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each of the three test projects. Record the passed counts.

- [ ] **Step 3: Run the Desktop tests by hand**

Run (interactive session; changes the clipboard text and restores it, and shows a toast): `dotnet test tests/DesktopMcp.Desktop.Tests -p:RunSettingsFilePath=`
Expected: `Passed!` with `Failed: 0` (the P0b tests plus `ClipboardTests` and `ToastSenderTests`). `DispatcherQueueThreadTests` is timing-dependent; if only its timer test fails, rerun it once and record both runs.

- [ ] **Step 4: Publish both RIDs and run the checks**

Run: `pwsh scripts/build-release.ps1`
Expected: the publish reports no warnings; `win-x64: <size> MB, folder OK (desktop-mcp.exe, desktop-mcp.pdb)` and the same for `win-arm64`; `smoke OK: desktop-mcp 0.1.0, 21 tools match spec/windows/tools.json, Wait and 5 headless tools OK, graphics stack not loaded (<n> modules)`; `AOT activation check OK: ...`. Record both exe sizes and the module count.

Run:
```powershell
probes/out/desktop-mcp-probe.exe clipboard
probes/out/desktop-mcp-probe.exe toast
```
Expected: `clipboard: SystemClipboard OK through WinRt` (or the outcome recorded in Task 12) and `toast: shown`. Record the lines.

- [ ] **Step 5: Run the smoke test without the Windows App SDK runtime (needs the user)**

Ask the user to copy `windows/dist/win-x64/desktop-mcp.exe`, `windows/scripts/smoke-stdio.ps1` and `spec/windows/tools.json` into Windows Sandbox (which has no Windows App SDK runtime) and run `pwsh smoke-stdio.ps1 -Exe .\desktop-mcp.exe -Spec .\tools.json` there. PowerShell 7 must be installed in the sandbox first, because the script needs pwsh. Expected: the same `smoke OK` line, which backs the lazy-initialisation claim in spec §10. If the user declines or cannot run it, leave that follow-up in the list and say so in the report.

- [ ] **Step 6: Record the results in the spec**

In `docs/superpowers/specs/2026-10-19-windows-port-design.md`:

1. In §8 "CLI and configuration", append to the first bullet: `P1 uses System.CommandLine 2.0.12. The CLI lives in DesktopMcp.Server/Cli so it can be tested; the exe's Program.cs only calls it. In P1, install, uninstall, auth and the HTTP transports parse their options and report the phase that brings them.` Append to the config bullet: `Tomlyn 2.10.1 reads TOML 1.1, a superset of the TOML 1.0 that upstream's tomllib reads. Its source-generated context reads the file as an untyped table, so the strict-type messages are upstream's. Reflection is off in every build, tests included.`
2. In §8 "Core tools", append to the Clipboard bullet the probe outcome from Task 12 Step 7 (which path works under AOT, on which apartment). Append to the Scrape bullet: `P1 ships without sampling and without DOM: use_dom returns upstream's "No DOM information found" text, and sampling returns the raw content, as upstream does for a client that cannot sample.`
3. In §9, add a paragraph after the table: `P1 results (<date>, Windows build <build>): default suite <n> tests passed (Core <a>, Server <b>, Desktop non-desktop <c>); Desktop suite passed; build-release: win-x64 <size> bytes, win-arm64 <size> bytes, each dist folder holding only the exe and its pdb; stdio smoke OK under NativeAOT (initialize, tools/list equal to spec/windows/tools.json, Wait, FileSystem, Process, Registry, PowerShell, Scrape; graphics stack not loaded, <n> modules); AOT activation check OK; clipboard probe: <lines>; toast probe: shown.` Use the values recorded in Steps 2 to 5, and add the Windows Sandbox result, or say that it was not run.
4. In §11, change the P1 row's Outcome cell to `A useful server without desktop access (done; results in §9)`.
5. In §12, add a row: `| The Job Object kills every process the server started when the server exits, including apps started through the PowerShell tool (Start-Process) | Spec §5 behaviour, kept in P1; upstream has no job. Revisit if users report it; JOB_OBJECT_LIMIT_BREAKAWAY_OK plus CREATE_BREAKAWAY_FROM_JOB would let chosen launches escape. |`

- [ ] **Step 7: Update the follow-ups list**

In `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`, in the P1 section, delete every item this plan closed: ServerTextTests, the SpecTool cancellation test, moving the PendingTool test, the shared JsonTypeInfo, duplicate tool names, the smoke isError and contract comparison, keeping DispatcherQueueThreadTests out of the default run, recording the smoke result, Clipboard WinRT first, the AOT activation check, and AllowedReferenceRelatedFileExtensions. Keep the Windows Sandbox item if Step 5 did not run. If the section is then empty, replace it with `All P1 items are done (see docs/superpowers/plans/2026-10-19-windows-p1-headless.md).` Then add:

- under P2: `Scrape use_dom: read DOM text nodes from the last snapshot once Snapshot exists (spec §8); P1 always returns "No DOM information found".` and, if Task 12 Step 7 found that WinRT fails under AOT, the recorded clipboard HRESULT with "revisit the WinRT clipboard path".
- under P5: `Scrape sampling: call SampleAsync with upstream's system prompt and max_tokens 2048, falling back to the raw content (tools/scrape.py:52-71).`
- under P6: `Registry list wraps nothing: if a client depends on PowerShell's Format-List line wrapping, reproduce it; P1 prints each value on one line (P1 plan, Decisions 13).`

- [ ] **Step 8: Commit**

```bash
git add windows/scripts/smoke-stdio.ps1 docs/superpowers/specs/2026-10-19-windows-port-design.md docs/superpowers/plans/2026-10-19-windows-p0-followups.md
git commit -m "Smoke-test the headless tools under NativeAOT and record the P1 results" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Self-review

**Spec coverage (§11 P1 and the task brief):**

| Requirement | Task |
|---|---|
| CLI `serve` (default), `install`, `uninstall`, `auth`; every upstream flag; `DESKTOP_MCP_*` variables; legacy usage error; complete `--help`; P5/P6 "not available" errors | 7 (Decisions 1, 2) |
| `System.CommandLine` 2.0 stable, published by 2026-10-05, AOT-safe | 7 (2.0.12, AOT probe noted; release publish in 7 and 17) |
| `config.toml` with Tomlyn source generation, strict types, upstream sections, path resolution | 5 |
| Precedence CLI/env, then TOML, then defaults; ServePlan messages; port range | 6, 7 |
| `--tools` / `--exclude-tools`, ControlStatus always kept; duplicate names in the contract | 6, 2 |
| stdio serve through the CLI, existing host kept, logs on stderr | 7 |
| Coercion: strict and loose booleans, JSON-string lists (spec §4) | 4 |
| Wait | 4 |
| PowerShell (shell lookup, encoding, environment, timeout, Ctrl+Break then tree kill, elevation hint) and the startup Job Object (spec §5) | 8 |
| FileSystem | 9 |
| Registry on `Microsoft.Win32.Registry` | 10 |
| Process with measured CPU and fuzzy filter in Core; tabulate table in Core | 11, 3 |
| Clipboard: WinRT first, Win32 fallback, both probed under AOT | 12 |
| Notification on CsWinRT `ToastNotificationManager`, probed under AOT | 13 |
| Scrape without sampling: SSRF guard, pinned-IP `ConnectCallback`, manual redirects, HTML to Markdown, `use_dom` returns "No DOM information found" | 14, 15, 16 |
| Tool output per upstream with §10 cleanups; contract unchanged | every tool task; `ContractTests` and the smoke contract comparison (1, 17) |
| Single exe, folder check, lazy App SDK, no graphics DLLs in headless runs | 1, 4, 7, 17 (no P1 code calls `AppRuntime`) |

**P1 follow-ups** (`2026-10-19-windows-p0-followups.md`): ServerText version → 2; SpecTool cancellation test → 4; PendingTool test moved → 4; shared JsonTypeInfo → 2; duplicate tool names → 2; smoke `isError` → 4 and full contract comparison → 1; DispatcherQueueThreadTests kept out of the default run → 1 Step 1; record the smoke result → 17 Step 6; Windows Sandbox run → 17 Step 5 (needs the user); Clipboard WinRT first → 12; AOT activation check → 1; AllowedReferenceRelatedFileExtensions publish-only → 1.

**Placeholder scan:** no "TBD", "TODO" or "similar to Task N". The bracketed values in Task 17 Step 6 (`<date>`, `<size>`, ...) are measurements that the earlier steps of that task record. Every code step shows complete files or exact replacement snippets. Every file that changes in several tasks (`ToolServices.cs`, the `ToolRegistry.Create` switch, `CoreNative.cs`, `smoke-stdio.ps1`) is shown in full, or with the exact old and new text, each time it changes.

**Type consistency** (checked across tasks):
- `ToolArguments` methods (`RequiredString`, `OptionalString`, `String`, `RequiredInteger`, `OptionalInteger`, `Integer`, `StrictBool`, `LooseBool`) are used with those names in Tasks 4 and 8-16.
- `ToolServices` properties grow in this order: `PowerShell` (8), `FileSystem` (9), `Registry` (10), `Processes` (11), `Clipboard` (12), `Toasts` (13), `Scrape` (16). Tests use `ToolServices.CreateDefault() with { ... }` with these names.
- `ToolRegistry.Create(Tool, ToolServices)` and `CreateAll(ToolSpecCatalog, ToolServices?)` match `ToolCalls` and `StdioServer`.
- `ServeArguments` and `ServePlan` member names in Task 6 match `ServeOptions.Read` in Task 7.
- `ConfigFile.Policies` and `ConfigFile.Transports` are used by Task 7.
- `IdleTimeout.Validate(object?)` takes the boxed `double` in Task 7.
- `PyText.Fixed`, `Thousands`, `Prefix`, `CodePointLength`, `Repr` and `ShellJoin` are used with the Task 3 signatures.
- `TextEncodings.Get(string)` returns `(Encoding Encoding, bool Signature)` in Tasks 9 and 16.
- `PathResolver.Resolve` is internal; only the Core tests call it, through InternalsVisibleTo.
- `ScrapeException`, `UrlParts.ReadPort` and `AddressPolicy.IsBlocked`/`IsFakeIpRange` match between Tasks 14 and 16.
- `IClipboard`, `SystemClipboard.LastPath`/`LastWinRtError`, `IToastSender` and `ToastSender.WindowsPowerShellAppId` match their tests and probes.
- `CoreNative` grows in Tasks 8, 9 and 11, and every use (`GenerateConsoleCtrlEvent`, `CreateJobObject`, `IsProcessInJob`, `CreateFile`, `GetFinalPathNameByHandle`, `CreateToolhelp32Snapshot`, `Process32First`/`Next`, `CloseHandle`) refers to a declared entry point.
