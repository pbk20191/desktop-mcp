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
| `windows/` | [CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP) | planned | to be decided (C# NativeAOT or Rust) |
| `linux/` | none upstream | idea | to be decided (Rust) |

## Layout

```
desktop-mcp/
  Package.swift     # the macOS port's SwiftPM manifest; targets point into macos/, so
  Package.resolved  # `swift build`, `swift test` and Xcode work from the repository root
  spec/
    tools.json      # the tool contract every port must match (names, parameters, descriptions)
  macos/            # Sources, Tests, README, scripts/build-release.sh (output: macos/dist/)
  windows/          # (planned; its own solution or Cargo workspace)
  linux/            # (idea)
```

Each port has its own README, build script, tests and release cadence. Release tags are prefixed
with the port (`macos-v0.1.0`), since the upstream projects version independently.

## The tool contract

`spec/tools.json` is the single description of the tools: it is what `tools/list` must return,
byte for byte apart from ordering. The macOS port generates it today and has a test that fails
when its own `tools/list` drifts from it; every new port gets the same test. Platform-specific
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
- One root task per process; servers, telemetry and signal handling are its children, and the
  process exits by returning, never by calling `exit()`.
- System frameworks over hand-rolled parsers where the platform has them, and the official MCP Swift SDK
  for the MCP protocol itself.
- Telemetry is off by default, unlike upstream: it would go to the upstream project's PostHog, not
  ours. `ANONYMIZED_TELEMETRY=true` turns it on.

## License

MIT. See [LICENSE](LICENSE). The ports derive their tool contract from [CursorTouch](https://github.com/CursorTouch)'s MIT-licensed servers; their copyright notice is in [NOTICE](NOTICE).
