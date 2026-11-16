# desktop-mcp

Native, dependency-free MCP servers for computer use on desktop operating systems. Each port
implements the same tool contract as the corresponding CursorTouch server (tool names, parameters,
output format) and mirrors its CLI, config file format and HTTP behaviour, shipped as a single
`desktop-mcp` binary that needs no language runtime or package manager on the machine it runs on.
Only the command name, the config directory (`~/.desktop-mcp`) and the environment variable prefix
(`DESKTOP_MCP_*`) differ from upstream.

| Port | Port of | Status | Language |
|---|---|---|---|
| [`macos/`](macos/) + root `Package.swift` | [CursorTouch/MacOS-MCP](https://github.com/CursorTouch/MacOS-MCP) | working, 18 tools (plus opt-in `OCR`) | Swift 6 (SwiftPM) |
| [`crates/`](crates/) + root `Cargo.toml` | the same, sharing its core with the Windows port | all tools (plus opt-in `OCR`); stdio, and HTTP only with authentication; no `install`/`connect` | Rust |
| `windows/` | [CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP) | planned | Rust, on the core crate under `crates/` |
| `linux/` | none upstream | idea | to be decided (Rust) |

## Layout

```
desktop-mcp/
  Package.swift     # the macOS port's SwiftPM manifest; targets point into macos/, so
  Package.resolved  # `swift build`, `swift test` and Xcode work from the repository root
  Cargo.toml        # the Rust ports' workspace
  crates/           # core (no platform code), macos, cli, parity (black-box tests for any port's binary)
  scripts/parity.sh # runs the parity suite against the Swift and the Rust binary
  spec/
    tools.json      # the tool contract every port must match (names, parameters, descriptions)
  macos/            # the Swift port: Sources, Tests, README, scripts/build-release.sh (output: macos/dist/)
  windows/          # (planned)
  linux/            # (idea)
```

Each port has its own README, build script, tests and release cadence. Release tags are prefixed
with the port (`macos-v0.1.0`), since the upstream projects version independently.

## The tool contract

`spec/tools.json` is the single description of the tools: it is what `tools/list` must return,
byte for byte apart from ordering. The macOS port generates it today and has a test that fails
when its own `tools/list` drifts from it; every new port gets the same test. The Rust port is the
exception: its SDK's macros write the tool schemas, so its `tools/list` is not compared with this
file (it pins its own output with a snapshot test instead). Platform-specific
behaviour (what a key name means, which windows can be switched to) belongs in each port's
README, not in the contract.

Tools split into two groups, which matters for headless use:

- **Core tools** work without a display: `Shell`, `Scrape`, `Wait`, and the like. They run on a
  server or in CI as they are.
- **Desktop tools** need a session to drive: `Snapshot`, `Click`, `Type`, `Screenshot`, `App`.
  Without a display they return an error result instead of failing to start. On a headless
  machine, run the server inside a virtual display (Xvfb or a headless Wayland compositor on
  Linux) and they work as usual.

## Principles shared by all ports

- No runtime dependencies. Build-time packages compiled into the binary are fine.
- One root task per process; servers and signal handling are its children, and the
  process exits by returning, never by calling `exit()`.
- System frameworks over hand-rolled parsers where the platform has them, and the official MCP SDK of
  the port's language for the MCP protocol itself.
- No thread pool of the server's own: threads belong to the system (libdispatch on macOS).
- Telemetry is disabled: the upstream sends usage events to its own PostHog project, and this port sends nothing.

## License

MIT. See [LICENSE](LICENSE). The ports derive their tool contract from [CursorTouch](https://github.com/CursorTouch)'s MIT-licensed servers; their copyright notice is in [NOTICE](NOTICE).
