# desktop-mcp

Native, dependency-free MCP servers for computer use on desktop operating systems. Each port
implements the same tool contract as the corresponding CursorTouch server (tool names, parameters,
output format) and mirrors its CLI, config file format and HTTP behaviour, shipped as a single
`desktop-mcp` binary that needs no language runtime or package manager on the machine it runs on.
Only the command name, the config directory (`~/.desktop-mcp`) and the environment variable prefix
(`DESKTOP_MCP_*`) differ from upstream.

| Port | Port of | Status | Language |
|---|---|---|---|
| [`macos/`](macos/) + root `Package.swift` | [CursorTouch/MacOS-MCP](https://github.com/CursorTouch/MacOS-MCP) | working, 12 tools | Swift 6 (SwiftPM) |
| [`windows/`](windows/) | [CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP) | in progress, Windows 11 22H2+ | C# (.NET 10 NativeAOT) |
| `linux/` | none upstream | idea | to be decided (Rust) |

## Layout

```
desktop-mcp/
  Package.swift     # the macOS port's SwiftPM manifest; targets point into macos/, so
  Package.resolved  # `swift build`, `swift test` and Xcode work from the repository root
  spec/
    macos/tools.json    # the macOS port's tool contract (names, parameters, descriptions)
    windows/tools.json  # the Windows port's tool contract
  macos/            # Sources, Tests, README, scripts/build-release.sh (output: macos/dist/)
  windows/          # .NET 10 NativeAOT solution, scripts/build-release.ps1 (output: windows/dist/<rid>/)
  linux/            # (idea)
```

Each port has its own README, build script, tests and release cadence. Release tags are prefixed
with the port (`macos-v0.1.0`), since the upstream projects version independently.

## The tool contract

Each port's contract is the tool list of its upstream server, kept in `spec/<port>/tools.json`
(`spec/macos/tools.json`, `spec/windows/tools.json`): what `tools/list` must return, compared as
JSON values. Each port has a test that fails when its own `tools/list` drifts from its file, so a
change to a tool's name, parameters or description is a deliberate change to the contract. The
upstream servers expose different tool sets, so the files differ; platform-specific behaviour
belongs in each port's README.

Tools split into two groups, which matters for headless use:

- **Core tools** work without a display: `Shell`/`PowerShell`, `Scrape`, `Wait`, and the like. They run on a
  server or in CI as they are.
- **Desktop tools** need a session to drive: `Snapshot`, `Click`, `Type`, `Screenshot`, `App`.
  Without a display they return an error result instead of failing to start. On a headless
  machine, run the server inside a virtual display (Xvfb or a headless Wayland compositor on
  Linux) and they work as usual.

## Principles shared by all ports

- No runtime dependencies. Build-time packages compiled into the binary are fine.
  The Windows port is an exception: it requires the system Windows App SDK 2.5 runtime (user
  decision), and ships as one exe that bootstraps it itself.
- One root task per process; servers, telemetry and signal handling are its children, and the
  process exits by returning, never by calling `exit()`.
- System frameworks over hand-rolled parsers where the platform has them, and the official MCP Swift SDK
  for the MCP protocol itself.
- Telemetry stays on by default with the upstream project key, and `ANONYMIZED_TELEMETRY=false`
  turns it off, exactly as upstream.

## License

MIT. See [LICENSE](LICENSE). The ports derive their tool contract from [CursorTouch](https://github.com/CursorTouch)'s MIT-licensed servers; their copyright notice is in [NOTICE](NOTICE).
