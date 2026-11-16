# desktop-mcp

Native, dependency-free MCP servers for computer use on desktop operating systems. Each port
implements the same tool contract as the corresponding CursorTouch server (tool names, parameters,
output format) and mirrors its CLI, config file format and HTTP behaviour, shipped as a single
`desktop-mcp` binary that needs no language runtime or package manager on the machine it runs on.
Only the command name, the config directory (`~/.desktop-mcp`) and the environment variable prefix
(`DESKTOP_MCP_*`) differ from upstream.

| Platform | Port of | Status |
|---|---|---|
| macOS (`crates/macos`) | [CursorTouch/MacOS-MCP](https://github.com/CursorTouch/MacOS-MCP) | all tools (plus opt-in `OCR`); stdio, and HTTP only with authentication; a Claude Desktop extension (`scripts/build-mcpb.sh`) |
| Windows (`crates/windows`) | [CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP), with the core's tool arguments | 16 tools (plus opt-in `OCR`); stdio, HTTP and HTTPS; a Claude Desktop extension (`scripts/build-mcpb.ps1`) |
| Linux | none upstream | idea |

Both are written in Rust on one shared core; see [`crates/README.md`](crates/README.md). The earlier ports are
archived, no longer developed: Swift for macOS on branch `archive/swift`, C# for Windows on branch
`archive/csharp`.

## Layout

```
desktop-mcp/
  Cargo.toml        # the workspace
  crates/           # core (no platform code), macos, windows, cli, parity (black-box tests for the binary)
  scripts/          # parity.sh / parity.ps1 run the parity suite; build-mcpb.sh / build-mcpb.ps1 build the
                    # Claude Desktop extension (output: dist/)
  spec/
    tools.json      # the upstream tool contract the ports started from (names, parameters, descriptions)
  docs/             # designs and plans
```

Release tags are prefixed with the platform (`macos-v0.1.0`), since the upstream projects version independently.

## The tool contract

`spec/tools.json` describes the tools as the archived Swift port served them, from the upstream contract. The Rust
ports' SDK macros write their own schemas, so `tools/list` is pinned by snapshot tests instead
(`crates/core/tests/snapshots/tools.json`, `crates/cli/tests/snapshots/windows-tools.json`), and a platform may
reword a shared tool for its system. Platform-specific behaviour (what a key name means, which windows can be
switched to) is described in `crates/README.md`, not in the contract.

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
- System frameworks over hand-rolled parsers where the platform has them, and the official Rust MCP SDK (rmcp) for
  the MCP protocol itself.
- No thread pool of the server's own: threads belong to the system (libdispatch on macOS, the thread pool on
  Windows).
- Telemetry is disabled: the upstream sends usage events to its own PostHog project, and this port sends nothing.

## License

MIT. See [LICENSE](LICENSE). The ports derive their tool contract from [CursorTouch](https://github.com/CursorTouch)'s MIT-licensed servers; their copyright notice is in [NOTICE](NOTICE).
