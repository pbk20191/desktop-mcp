# desktop-mcp in Rust

A port of the macOS server to Rust, built so that the Windows port can use the same core. It is in
progress: the Swift port under [`macos/`](../macos/) is the one to use.

## What works

`desktop-mcp serve --transport streamable-http [--host localhost] [--port 8000] [--session-timeout 1800]`
serves `/mcp` (and `/mcp/`) with one tool so far, `Wait` with a `duration`:

- Stateless by default: `POST` with one JSON-RPC message, answered with `application/json`.
- Sessions on request: an `initialize` whose `Accept` includes `text/event-stream` gets an
  `Mcp-Session-Id`. Requests carrying it are answered as server-sent events (an empty-data event,
  then the answer); `GET` opens the one stream for server-initiated messages (409 for a second);
  `DELETE` ends the session; an unknown or ended session is 404. A session ends after
  `--session-timeout` seconds without a `POST` (a running call counts, an open `GET` does not),
  after sending 64 MiB, or to make room when 64 exist (the least recently used idle one; 503 when
  all are busy). `notifications/cancelled` and a client leaving a call cancel that call only.
The status codes follow the Swift port: 406 without `application/json` in `Accept`, 415 for a
non-JSON `Content-Type`, 400 for a batch or invalid JSON, 405 with `Allow: POST` for `GET` and
`DELETE`, 202 for a notification. A client that disconnects cancels its tool call, and `Connection: close` and HTTP/1.0 clients get
their whole answer before the connection closes. SIGINT and
SIGTERM end the server with exit status 0.

Before any of that, as in the Swift port: browser requests must come from a loopback `Origin`
(403 otherwise; `OPTIONS` answers CORS), `--ip-allowlist` limits client addresses,
`--auth-key` requires `Authorization: Bearer <key>`, and `--oauth-client-id`/`--oauth-client-secret`
turn on the built-in OAuth server (authorization code with PKCE, loopback redirects only).
`/health` and the OAuth endpoints need no key. Settings come from the command line, then the
`DESKTOP_MCP_*` environment variables, then `~/.desktop-mcp/config.toml` (or `--config`); binding a
non-loopback host without authentication is refused unless `--allow-insecure-remote`.

Not there yet: HTTPS (`--ssl-certfile`/`--ssl-keyfile` are refused), stdio, the Unix socket,
`connect`, `install`, and every tool but `Wait` by duration.

## How it is built

- **No thread of its own.** Network.framework delivers on the main queue, the whole server is one
  future polled on the main run loop (`crates/macos/src/main_loop.rs`), and blocking work goes to a
  libdispatch queue (`offload`).
- **MCP by [rmcp](https://crates.io/crates/rmcp)**, with its tool macros, which also write the tool
  schemas: `tools/list` is not compared with `spec/tools.json`, only pinned by a snapshot. rmcp needs a tokio runtime;
  it runs as a guest of the run loop (`crates/core/src/guest.rs`): a current-thread runtime that is
  never parked, given short turns by run loop timers that are armed only while a connection or a
  request exists. An idle server is not woken at all.
- **HTTP by hyper** over `NwIo` (`crates/macos/src/nw.rs`). Received bytes stay in `dispatch_data`
  until they are copied once into hyper's buffer. libdispatch is used through the `dispatch2` crate;
  Network.framework has no binding crate, so its C functions are declared in `nw.rs`.
- **Stateless requests** each get a fresh rmcp service, as in rmcp's own HTTP server. A session keeps
  one service behind a channel transport (`crates/core/src/session.rs`); answers go to the `POST`
  that asked, anything else to the session's `GET` stream. While a session lives, the guest
  runtime turns once a second (its idle sweeper and rmcp's own timers need it); with no session
  and no connection, nothing runs.

| Crate | Contents |
|---|---|
| `core` | guest runtime, MCP server and tools, HTTP app. No platform code |
| `macos` | main run loop executor, Network.framework, signals, the tools' macOS behaviour |
| `cli` | the `desktop-mcp` binary |
| `parity` | black-box tests that drive a binary given in `DESKTOP_MCP_BIN` |

## Building and testing

```bash
cargo build -p desktop-mcp-cli                           # → target/debug/desktop-mcp
cargo test --workspace --exclude desktop-mcp-parity      # unit and integration tests
scripts/parity.sh                                        # the parity suite, against the Swift and the Rust binary
```

Tests under `crates/macos/tests` run on the main thread with their own small runner, because the
code under test owns the main run loop. After reviewing a change to `tools/list`, refresh its
snapshot with `UPDATE_SNAPSHOTS=1 cargo test -p desktop-mcp-core --test mcp snapshot`.
