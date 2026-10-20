# Windows port P5: HTTP, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Serve the existing tools over HTTP: Kestrel with the Streamable HTTP transport (stateful by default, `--stateless-http`) and the legacy HTTP+SSE transport, upstream's middleware (OPTIONS, trusted host, CORS, IP allowlist, bearer key and OAuth tokens) plus an Origin check, `/health`, TLS with HTTP/2, OAuth 2.0 + PKCE, control notifications to HTTP sessions with the 60 s TTL, and Scrape's `use_sampling`. Telemetry is not part of the Windows port (user decision, 2026-10-21).

**Architecture:**
- `DesktopMcp.Core` gains `Security/`: pure, unit-tested request guards (`HttpGuards`: trusted host, Origin, IP allowlist, constant-time compare, public paths), the in-memory `OAuthServer` (metadata, authorize, token, token validation; `TimeProvider` injected), `TlsCertificate` (PEM to a Schannel-usable certificate) and `SecurityJson` (source-generated JSON bodies). `Scrape/` gains the `IWebScraper` seam.
- `DesktopMcp.Server` gains `Http/`: `HttpServer` (a `WebApplication.CreateSlimBuilder` host with Kestrel, `WithHttpTransport` and `MapMcp`), `ListenTarget` (bind address resolution), `HttpPipeline` (the middleware in upstream's order, as `RequestDelegate` middleware), `OAuthEndpoints`. `Hosting/` gains `McpServerSetup` (the MCP, control, notifier and gate wiring extracted from `StdioServer`, shared by both hosts) and `ServeCommand` (the Job Object, then stdio or HTTP). `Tools/` gains `McpSampling` (raw JSON `sampling/createMessage`, as P4's `McpLogging`).
- The CLI stops refusing `sse` and `streamable-http`. `install`, `uninstall` and `auth` stay P6.

**Tech Stack:** .NET 10 NativeAOT, ASP.NET Core Kestrel (shared framework `Microsoft.AspNetCore.App`, already referenced by `DesktopMcp.Server` and the Web SDK exe since P0), **`ModelContextProtocol.AspNetCore` 2.2.0 (new package reference, Decisions 2)**, ModelContextProtocol 2.2.0 (`McpSession.SendRequestAsync(JsonRpcRequest)`, `McpServer.ClientCapabilities`, `HttpClientTransport` in tests), Schannel TLS with ALPN, System.Text.Json source generation, xUnit 2.9.3.

**Spec:** `docs/superpowers/specs/2026-10-19-windows-port-design.md`: §1, §2 (MCP protocol and HTTP rows), §5 (process root, Job Object), §7 Sessions and the notifier, §8 (CLI and configuration, Transports, HTTP pipeline, TLS, OAuth, Scrape `use_sampling`, Telemetry), §9, §10, §11 (P5 row). Upstream is CursorTouch/Windows-MCP at `128f2960c79b74bd2630f4dbc9d4f664f4ce89b3`; paths such as `infrastructure/oauth.py:136` are relative to `src/windows_mcp/`. Every string, default, limit and timing comes from there unless the spec or the Decisions below say otherwise. P0-P4 Decisions bind, in particular P1 Decisions 2 (environment variables), 6 (loose booleans), 16-17 (Scrape), 19 (Job Object) and P4 Decisions 22 (raw-JSON logging; P5 passes the HTTP TTLs).

**Scope (spec §11, P5 row, as amended by the user):** Kestrel, Streamable HTTP and SSE, middleware, TLS, OAuth, `/health`, Scrape sampling; the follow-ups' P5 items (Scrape sampling, IPv4-mapped allowlist addresses, the PowerShell child's secrets) except the JSONL audit log (Open question 2). **Not in P5:** telemetry (Decisions 1), `install`/`uninstall`/`auth` (P6), the macOS port's `connect` bridge and `--session-timeout` (not in the spec).

## Open questions for the user

The plan proceeds with each recommendation; an answer the other way changes only the tasks named.

1. **Origin check (DNS rebinding and cross-site requests).** The spec's pipeline has a trusted-host check and CORS but no Origin check; upstream has none (`__main__.py:87-120`), and the MCP specification requires servers to validate `Origin`. The macOS port answers 403 to any `Origin` that is not loopback (macOS README "Differences", `HTTPApp.swift:152-179`). **Recommended: reject with 403 `{"error":"Forbidden: origin '<origin>' is not allowed"}` any request whose `Origin` header is present and is neither an http(s) origin with a loopback host nor listed in `--cors-origins`;** requests without `Origin` (every non-browser client) are unaffected, and listed CORS origins keep working (Decisions 9). The other answer deletes step 3 of `HttpPipeline` (Task 4) and its tests.
2. **The follow-ups' "JSONL audit log" P5 item.** It is in neither the spec nor upstream and needs a file location, a config key, a redaction rule and a rotation rule, all user-visible. **Recommended: not in P5; the item moves to the follow-ups' P6 section** (Task 10 Step 6). The other answer adds a task after Task 8 with an `[server] audit_log` key.
3. **Legacy SSE transport.** The spec keeps `--transport sse` (`GET /sse` plus the SDK's `/message`). The macOS port dropped SSE (deprecated by MCP 2025-03-26), and ModelContextProtocol.AspNetCore 2.2.0 marks `HttpServerTransportOptions.EnableLegacySse` obsolete (MCP9004: "should only be used with completely trusted clients in isolated processes"), which `TreatWarningsAsErrors` turns into a build error. **Recommended: keep SSE as the spec says,** enabled through the SDK's documented AppContext switch `ModelContextProtocol.AspNetCore.EnableLegacySse` only when `--transport sse` is chosen (Decisions 4). The other answer removes `sse` from `ConfigFile.Transports` with a migration warning as macOS does (`ServePlan.swift:64-69`) and deletes Task 2's SSE half.
4. **Clients on protocol 2026-07-28 against the stateful server.** In 2.2.0, `HttpServerSessionMode.Stateful` refuses 2026-07-28 requests with `-32022` so dual-path clients fall back to the `initialize` handshake; `StatefulForInitializeClients` would serve them statelessly on the same endpoint (no notifications, no sampling for them). **Recommended: `Stateful`** (spec §8 "stateful by default to match upstream"; every client gets sessions, notifications and sampling). The other answer changes one line in `HttpServer.ConfigureTransport` (Task 1) and one test.

## Global Constraints

- TFM `net10.0-windows10.0.22621.0`, `SupportedOSPlatformVersion` **10.0.22621.0**; RIDs `win-x64` and `win-arm64`.
- `TreatWarningsAsErrors`, `IsAotCompatible`, no warning suppression (no `#pragma warning disable`, no `NoWarn`, no `[SuppressMessage]`), in product and test code. Obsolete SDK APIs (MCP9004 legacy SSE, MCP9005 sampling and logging, MCP9006 stateful-session options) are not called; Decisions 4, 5 and 13 say what replaces them.
- **Package references: exactly one addition**, `ModelContextProtocol.AspNetCore` **2.2.0** (Decisions 2). No `Microsoft.AspNetCore.TestHost`, no CORS or authentication packages, no HTTP client packages.
- **The publish folder for each RID contains only `desktop-mcp.exe`** (plus `.pdb`); no `web.config`, `appsettings*.json`, `*.staticwebassets.*` or `*.deps.json`. `pwsh scripts/build-release.ps1` enforces it and must keep passing.
- **ASP.NET Core under NativeAOT:** `DesktopMcp.Server` is a class library, so the Request Delegate Generator does not run there. Every route and middleware is a `RequestDelegate` (`Task (HttpContext)`) or `app.Use(Func<HttpContext, RequestDelegate, Task>)`; no minimal-API lambdas with bound parameters, no `Results.Json<T>` with reflection, no MVC. JSON bodies use `SecurityJson`'s source-generated `JsonTypeInfo`s or `Utf8JsonWriter`.
- The host never reads ambient configuration: `WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production }` (a fixed environment, so `ASPNETCORE_ENVIRONMENT=Development` cannot add the developer exception page; preflight F3), `builder.Configuration.Sources.Clear()` right after creation (no `appsettings.json`, no `ASPNETCORE_*`/`DOTNET_*` variables), then one in-memory source with `urls = ";"` and `preferHostingUrls = "false"`, because the builder copies `ASPNETCORE_URLS`/`ASPNETCORE_PREFERHOSTINGURLS` into its host options before the clear and the web host falls back to that copy when `urls` is empty (`UseSetting` needs a source, and `""` falls back); `";"` is a non-empty list of no URLs, so neither variable can move the bind or make Kestrel warn that it overrides addresses, `builder.Logging.ClearProviders()` then `StderrLoggerProvider`, Kestrel endpoints only from `ListenTarget`s.
- **Safety in tests (controller decision 3):** every test server binds `127.0.0.1` (or `[::1]` where a test says so) on port 0; no test binds `0.0.0.0`, `::` or a fixed port, opens a firewall port or makes an outbound network call (Scrape content comes from a fake `IWebScraper`). TLS tests use a self-signed certificate generated with `CertificateRequest` into a temp folder; nothing touches a certificate store (Decisions 11). No test takes control: HTTP tests use `ToolServices.CreateDefault()` (`DisabledControl`) or P4's `FakeControl`.
- HTTP host tests that build a `WebApplication` live in one xUnit collection, `[Collection(HttpHostCollection.Name)]`, with `DisableParallelization = true` (Decisions 4: the legacy-SSE switch is process-wide).
- The tool contract `spec/windows/tools.json` does not change. Result shapes (spec §4) are unchanged; Scrape's text stays `URL: <url>\nContent:\n<text>`.
- Naming: `desktop-mcp`, `~/.desktop-mcp`, `DESKTOP_MCP_*`. The server name in `initialize` stays `desktop-mcp`.
- Commit messages end with a blank line and `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Work happens on branch `windows-p5`.

All `dotnet` and `pwsh` commands run from `windows/`; `git` commands run from the repository root.

## Review Focus

The five conditions the spec implies but no feature test would naturally hit, most likely first. Each has a test in the task named.

1. **A web page in the user's browser reaching the loopback server** (DNS rebinding with `Host: attacker.example`, or a cross-site `fetch` with `Origin: https://attacker.example`). It must get 400 or 403 before the auth or the MCP endpoint runs, also when no auth key is set. Tests: Task 3 `TrustedHostTable`, `OriginTable`; Task 4 `ARebindingHostIsRejectedBeforeTheEndpoint`, `ACrossSiteOriginIsRejectedEvenWithoutAuth`.
2. **IPv6 loopback and IPv4-mapped clients.** `Host: [::1]:8000` must pass the trusted-host check (upstream's port strip rejects it), and a client seen as `::ffff:10.1.2.3` must match an `10.0.0.0/8` allowlist entry. Tests: Task 3 `TrustedHostTable` (`[::1]`), `AMappedClientMatchesAnIPv4Entry`; Task 4 `AnIPv6LoopbackClientPassesTheChecks` (binds `[::1]`).
3. **A session that ends while a notification is being sent** (client DELETE, dropped connection, server-side expiry). The notifier must forget it on a failed send or after the TTL (the SDK drops a send to a stream that is not open without failing it) and keep serving others; no exception may reach the host. Tests: Task 2 `ADeletedSessionIsForgottenAndOthersStillHearNotifications`.
4. **A sampling client that never answers, or a call cancelled while sampling waits.** The Scrape call must end with the cancellation, and stateless HTTP (which cannot send requests) must never wait. Tests: Task 8 `ACancelledCallStopsWaitingForSampling`, `SamplingIsSkippedWhenDisabled`.
5. **Bad TLS or bind inputs at startup** (a key that does not match the certificate, a missing file, an encrypted key, a port already in use). `serve` must print one `Error: ...` line and exit 1 within a few seconds, never hang or crash. Tests: Task 7 `AMismatchedKeyIsAConfigError`, `AMissingFileIsAConfigError`; Task 1 `APortInUseExitsWithAnError`.

---

## Decisions on upstream ambiguities

Each decision is binding for the tasks. Decisions 1-6 are the controller's, delegated by the user (2026-10-20, "continue"), and the user's (Decisions 1).

1. **No telemetry in the Windows port** (user decision, 2026-10-21; replaces spec §8 Telemetry and the follow-ups' P5 opt-in line). No PostHog or analytics code, no user-id file, no `tool_executed`/`exception` events. `ANONYMIZED_TELEMETRY`, `POSTHOG_API_KEY` and `POSTHOG_HOST` are ignored: nothing reads them. Task 10 rewrites the spec's Telemetry section, §1, §3, §10, §11 and the follow-ups' line to say so.
2. **Package and framework references** (spec §2 MCP protocol row names `ModelContextProtocol` and `.AspNetCore`). Added: `ModelContextProtocol.AspNetCore` 2.2.0 (`<PackageVersion>` in `Directory.Packages.props`, `<PackageReference>` in `DesktopMcp.Server.csproj`). Why: it provides `WithHttpTransport`/`MapMcp`, the Streamable HTTP and legacy SSE endpoints and session handling on Kestrel; its only dependencies are `ModelContextProtocol` [2.2.0] (already referenced, exact match) and the `Microsoft.AspNetCore.App` framework reference; its net10.0 assembly is marked `IsTrimmable` and `IsAotCompatible`. Already present and unchanged: `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in `DesktopMcp.Server.csproj` and `Microsoft.NET.Sdk.Web` for the exe. Not added: `Microsoft.AspNetCore.TestHost` (tests use real Kestrel on loopback port 0, Decisions 14). This is the spec's own choice, so it is not an open question; P2-P4's "no new package references" rule is lifted for this one package only.
3. **Upstream behaviour unless the spec, an earlier Decision or security says otherwise.** The pipeline ports `__main__.py:87-176` (`_http_middleware`, `OptionsMiddleware`), `infrastructure/security.py:210-266` (allowlist), `infrastructure/auth.py:14-89` (bearer and OAuth-only middleware, public paths, `is_loopback_host`), `infrastructure/oauth.py:24-288` (OAuth), `__main__.py:807-851` (bind refusal, allowed hosts, issuer), `__main__.py:512-541` (transport run, notifier TTL) and `tools/scrape.py:56-73` (sampling). Each deviation below names its reason.
4. **Transports** (spec §8 Transports). `streamable-http`: `MapMcp("/mcp")` with `options.SessionMode = HttpServerSessionMode.Stateful` (Open question 4), or `HttpServerSessionMode.Stateless` with `--stateless-http`. `sse`: `MapMcp("")` with `SessionMode = Stateful` (legacy SSE needs it; `--stateless-http` has no effect on SSE, as upstream), which maps `GET /sse` and `POST /message?sessionId=` (spec §10 Protocol: `/message`, not upstream's `/messages/`). Legacy SSE is enabled by the SDK's documented AppContext switch `ModelContextProtocol.AspNetCore.EnableLegacySse`, set to `plan.Transport == "sse"` immediately before `MapMcp` under a static lock, instead of the obsolete `EnableLegacySse` property (Open question 3; no warning suppression). For `sse`, the Streamable endpoints that `MapMcp("")` also maps at `/` answer 404 (spec: "Only the selected HTTP transport is mapped"); for `streamable-http`, `/sse` and `/message` are never mapped. `/mcp/` reaches the same endpoint as `/mcp` (upstream's `auth` prints `/mcp/`, `__main__.py:1135`). The MCP9006-obsolete options (`IdleTimeout`, `MaxIdleSessionCount`, `EventStreamStore`, `PerSessionExecutionContext`) keep their defaults and are not touched.
5. **Sampling** (spec §8 Scrape; follow-ups P5; `tools/scrape.py:56-73`). ModelContextProtocol 2.2.0 marks `SampleAsync`, `CreateMessageRequestParams` and `ClientCapabilities.Sampling` obsolete (MCP9005), so `McpSampling` builds `sampling/createMessage` as raw JSON and sends it with `McpSession.SendRequestAsync(JsonRpcRequest, ct)` on the calling request's `McpServer` (so Streamable HTTP routes it through the originating POST's response stream), and reads the client's capabilities as JSON (as `McpLogging.DeclareLogging` does). Params: `{"messages":[{"role":"user","content":{"type":"text","text":"Raw scraped content from <url>:\n\n<content>"}}],"systemPrompt":"<upstream's prompt><focus>","maxTokens":2048}` (no temperature, model preferences or context, as upstream). The result's text is the `text` of `content` when `content` is an object with `"type":"text"`, or the single text block when it is an array of exactly one text block; anything else counts as a failure. No sampling request is sent when `use_sampling` is false, the client did not declare `sampling`, or sampling is disabled for the transport (stateless Streamable HTTP, whose SDK mode cannot send requests). Any failure returns the raw content (upstream's `except Exception: pass`), except that cancellation of the call itself propagates. There is no timeout beyond the call's own cancellation, as upstream.
6. **Release verification** (controller decision 5): Task 10 as P4 Task 14. A new `scripts/smoke-http.ps1` runs the NativeAOT exe over HTTP on `127.0.0.1` port 0 with control disabled (it never takes control, shows nothing and injects nothing) and a TLS session with a temp self-signed certificate; `build-release.ps1` runs it after `smoke-stdio.ps1`.
7. **Bind targets** (upstream lets uvicorn bind `host`). `ListenTarget.Resolve`: `localhost` (any case) is Kestrel's `ListenLocalhost` (127.0.0.1 and ::1); an IP literal (`IpAddressText.TryParse`, brackets stripped) is that address; `0.0.0.0` is `IPAddress.Any`; `::` and the empty string are `IPAddress.IPv6Any` (Kestrel binds it dual-mode); any other name is resolved once with DNS at startup and every returned address is bound, else `ConfigException("Could not resolve server.host '<host>'.")`. Port 0 binds an ephemeral port; `localhost` with port 0 binds `127.0.0.1:0` only, because Kestrel cannot bind localhost on a dynamic port, and the announcement still names `localhost` (preflight F2). A name that resolves to several addresses with port 0 gives each listener its own ephemeral port; the announcement names the first one's. A failed bind (an `IOException`, or a bare `SocketException` such as `WSAEACCES` in an excluded port range) prints `Error: Could not listen on <host>:<port>: <message>` and exits 1.
8. **Announcement.** After Kestrel has bound, the server writes one line to stderr whatever the log level: `desktop-mcp: serving <transport> on <scheme>://<host>:<actual port><path>` (`<path>` is `/mcp` or `/sse`; an IPv6 host in brackets), like uvicorn's `Uvicorn running on ...`. The smoke test reads the port from it.
9. **Origin check** (Open question 1; MCP specification; macOS port). Step 3 of the pipeline: a request whose `Origin` header is present and is neither listed exactly in `CorsOrigins` nor an `http`/`https` origin whose host (brackets stripped) passes `IpAddressText.IsLoopbackHost` gets 403 `{"error":"Forbidden: origin '<origin>' is not allowed"}`. `Origin: null` is rejected. OPTIONS is answered before it (step 1, spec), so a preflight from any origin still gets 200, without CORS headers unless listed.
10. **Trusted host** (spec §8 step 2; `__main__.py:98-101, 826-834`; Starlette's `TrustedHostMiddleware`). `ServePlan.AllowedHosts` (P1) is the list. The `Host` header's host part is taken with `HostString.Host` (so `[::1]:8000` gives `[::1]`; upstream's `split(":")[0]` gives `[` and rejects IPv6 loopback, a bug), compared case-insensitively with brackets stripped on both sides. A missing or mismatched host gets Starlette's 400 `Invalid host header` (`text/plain; charset=utf-8`).
11. **TLS** (spec §8 TLS). `TlsCertificate.Load(cert, key)`: `X509Certificate2.CreateFromPemFile(cert, key)`, then `X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pkcs12), null)` with default storage flags (Schannel cannot use an ephemeral key; the key container this creates is temporary and removed when the certificate is disposed at shutdown; no certificate store is opened). Any failure is `ConfigException("Could not load the TLS certificate '<cert>' with key '<key>': <message>")`, printed as `Error: ...`, exit 1. A key that does not match the certificate fails here. TLS listeners use `HttpProtocols.Http1AndHttp2` (ALPN `h2`, `http/1.1`); plain listeners use `HttpProtocols.Http1` only, so cleartext HTTP/2 (h2c) is not offered (macOS port; uvicorn speaks HTTP/1.1 only). Relative CLI paths resolve against the current directory; config paths were already resolved against the config folder (P1).
12. **OAuth** (spec §8; `infrastructure/oauth.py`). Routes exist only when both id and secret are configured (`__main__.py:837-851`). Upstream's texts and order are kept; deviations, each for security or robustness (all as the macOS port, `OAuth.swift`):
    - secret, PKCE and bearer comparisons are constant time (`CryptographicOperations.FixedTimeEquals` over UTF-8; a length mismatch returns false);
    - a failed PKCE check also deletes the code (RFC 7636 §4.6; upstream keeps it, `oauth.py:237-240`);
    - an unparsable JSON body, or JSON that is not an object, is 400 `{"error":"invalid_request"}` (upstream answers 500); JSON values that are not strings are used as their raw JSON text;
    - at most 1000 pending codes (the oldest is dropped); expired codes and tokens are removed whenever a code or token is issued (upstream only on lookup);
    - the issuer is `{scheme}://{host}:{port}` with the actual bound port; an IPv6 host is bracketed; for a wildcard bind (`0.0.0.0`, `::`, empty) the request's `Host` header replaces `{host}:{port}` (upstream advertises `http://0.0.0.0:8000`, which no client can use).
    Kept as upstream: the code is consumed before the client secret is checked (`oauth.py:244`); `client_id` and `redirect_uri` mismatches keep the code; authorize errors are JSON, not redirects; no consent step; no refresh token; no `/.well-known/oauth-protected-resource`.
13. **Auth** (spec §8 step 5; `auth.py:23-79`). The bearer prefix is the case-sensitive `Bearer ` and the token is the rest, untrimmed, as upstream. With an auth key: no prefix → 401 `{"error":"Missing or invalid Authorization header. Expected: Bearer <token>"}`; key mismatch and no valid OAuth token → 401 `{"error":"Invalid authentication token"}`. OAuth only: any failure → 401 `{"error":"Unauthorized"}`. **Every 401 carries `WWW-Authenticate: Bearer`** (spec §8; upstream omits it for OAuth-only, `auth.py:62-79`, which RFC 6750 requires). Public paths, exact and case-sensitive: `/health`, `/.well-known/oauth-authorization-server`, `/oauth/register`, `/oauth/authorize`, `/oauth/token`. No auth key and no OAuth: no auth step.
14. **Tests on real Kestrel** (spec §9 says "in-memory Kestrel tests"; an in-memory server needs `Microsoft.AspNetCore.TestHost`, a new package). `HttpRig` runs the production `HttpServer.RunAsync` on `127.0.0.1:0` and talks to it with `HttpClient` and the SDK's `HttpClientTransport`. Task 10 rewords spec §9.
15. **IP allowlist** (spec §8 step 4; `security.py:239-266`; follow-ups P5). The client address is `HttpContext.Connection.RemoteIpAddress`; `X-Forwarded-For` and `Forwarded` are never read (uvicorn trusts them from 127.0.0.1 by default; the port does not). An IPv4-mapped IPv6 address is mapped to IPv4 before matching and in the message. `/health` is exempt, the OAuth paths are not (as upstream). 403 bodies: `{"error":"Forbidden: missing client address"}`, `{"error":"Forbidden: <ip> is not in the IP allowlist"}`; upstream's `invalid client address` cannot occur (Kestrel gives a parsed address).
16. **CORS and OPTIONS** (spec §8 steps 1 and 3; `__main__.py:143-176, 102-111`). Step 1: every OPTIONS request gets 200 with `Content-Length: 0` and an empty body; when the `Origin` is listed exactly in `CorsOrigins` it adds `Access-Control-Allow-Origin: <origin>`, `Access-Control-Allow-Methods: GET, POST, OPTIONS`, `Access-Control-Allow-Headers: content-type, authorization, mcp-session-id`, `Vary: Origin`. Step 4 (Starlette's simple-response path): a non-OPTIONS request with a listed `Origin` gets `Access-Control-Allow-Origin: <origin>` and `Vary: Origin` on its response; no credentials header, no `Access-Control-Expose-Headers` (as upstream). Written as our own middleware (no ASP.NET CORS services).
17. **`/health`** (spec §8 step 6, §10 "`/health` exists"; upstream lists it as public but has no route, `auth.py:15`, and macOS returns 404). `GET /health` → 200 `application/json` `{"status":"ok"}`, exempt from the allowlist and auth, subject to the trusted-host and Origin checks; mapped for both transports.
18. **Sessions and notifications** (spec §7 Sessions; `__main__.py:512-516`; P4 Decisions 22). `HttpServer.SessionTtl(plan)`: `TimeSpan.Zero` for `streamable-http` with `--stateless-http`, else 60 s; stdio keeps `null`. The registry key is the session: `McpServer.SessionId`, or one static `McpServerSetup.NoSessionIdKey` when `SessionId` is null (stdio; also every stateless HTTP request, which is why stateless HTTP must get a TTL of zero), with the latest call's `McpServer` as the value (`Remember` overwrites by key). Not the call's `McpServer` (P4): the SDK builds a new destination-bound `McpServer` for every request, so that key gave one entry, and one copy of every notification, per call (preflight F1; fixed in Task 1 with a stdio test: two calls and one state change give exactly one notification). A session the client deletes or the SDK disposes is forgotten on a failed send or after the TTL.
19. **Process root for HTTP** (spec §5). `ServeCommand.RunAsync` puts the process in the kill-on-close Job Object for every transport (as stdio since P1) and sets `NO_COLOR=1` only for stdio (`__main__.py:725-726`). The HTTP host stops on Ctrl+C or the console closing (the slim builder's console lifetime) and on the CLI's cancellation token; held input is released on stop as in `StdioServer`.
20. **The PowerShell child's environment** (follow-ups P1 leftover "For P5"). `ShellEnvironment.Build` removes `DESKTOP_MCP_AUTH_KEY` and `DESKTOP_MCP_OAUTH_CLIENT_SECRET` after the registry merge (so a value set in HKCU\Environment is removed too), case-insensitively. Other `DESKTOP_MCP_*` variables stay (they hold no secrets and scripts may read them).
21. **Not in P5:** telemetry (Decisions 1); the JSONL audit log (Open question 2); request body limits other than Kestrel's default 30 MB; the macOS port's `connect` stdio bridge, `--session-timeout`, 64-session cap and per-session byte budget (not in the spec; the SDK keeps its own idle defaults); `auth` client snippets (P6).

## Follow-ups this plan picks up

From `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`:

| Follow-up | Where |
|---|---|
| P5: telemetry opt-in | Dropped by the user (Decisions 1); Task 10 rewrites the line |
| P5: Scrape sampling with upstream's prompt and max_tokens 2048, raw fallback | Task 8; Decisions 5 |
| P5: IP allowlist matches IPv4-mapped IPv6 clients | Tasks 3, 4; Decisions 15 |
| P5: JSONL audit log | Not in P5 (Open question 2); Task 10 moves it to P6 |
| P1 leftovers: strip `DESKTOP_MCP_*` secrets from the PowerShell child's environment | Task 9; Decisions 20 |
| P4 Decisions 22: P5 passes 60 s / zero TTLs to `SessionRegistry` | Task 2; Decisions 18 |

## File map

```
windows/
  Directory.Packages.props                                    (ModelContextProtocol.AspNetCore 2.2.0; Task 1)
  scripts/smoke-http.ps1, scripts/build-release.ps1           (Task 10)
  src/DesktopMcp.Core/Security/HttpGuards.cs                  (Task 3)
  src/DesktopMcp.Core/Security/SecurityJson.cs                (ErrorBody, OAuthErrorBody, HealthBody, OAuthMetadata, TokenResponse; Tasks 3, 5)
  src/DesktopMcp.Core/Security/OAuthServer.cs                 (OAuthResult; Task 5)
  src/DesktopMcp.Core/Security/TlsCertificate.cs              (Task 7)
  src/DesktopMcp.Core/Scrape/{IWebScraper,ScrapeClient}.cs    (Task 8)
  src/DesktopMcp.Core/PowerShell/ShellEnvironment.cs          (Task 9)
  src/DesktopMcp.Server/DesktopMcp.Server.csproj              (package reference; Task 1)
  src/DesktopMcp.Server/Cli/{CliApp,CliEnvironment}.cs        (HTTP transports served; Task 1)
  src/DesktopMcp.Server/Hosting/McpServerSetup.cs             (Task 1)
  src/DesktopMcp.Server/Hosting/ServeCommand.cs               (Task 1)
  src/DesktopMcp.Server/Hosting/StdioServer.cs                (uses McpServerSetup; Task 1)
  src/DesktopMcp.Server/Http/HttpServer.cs                    (Tasks 1, 2, 4, 6, 7, 8)
  src/DesktopMcp.Server/Http/ListenTarget.cs                  (Task 1)
  src/DesktopMcp.Server/Http/HttpPipeline.cs                  (Tasks 4, 6)
  src/DesktopMcp.Server/Http/OAuthEndpoints.cs                (Task 6)
  src/DesktopMcp.Server/Tools/McpSampling.cs                  (Task 8)
  src/DesktopMcp.Server/Tools/{ScrapeTool,ToolServices,ToolRegistry}.cs   (Task 8)
  tests/DesktopMcp.Core.Tests/Security/{HttpGuardsTests,OAuthServerTests,TlsCertificateTests,TestCertificate}.cs
  tests/DesktopMcp.Core.Tests/PowerShell/ShellEnvironmentTests.cs (additions)
  tests/DesktopMcp.Server.Tests/Http/{HttpHostCollection,HttpRig,TestCertificate}.cs
  tests/DesktopMcp.Server.Tests/Http/{HttpServerTests,LegacySseTests,HttpSessionTests,HttpPipelineTests,OAuthEndpointTests,TlsTests,ListenTargetTests}.cs
  tests/DesktopMcp.Server.Tests/Tools/{RawMcpClient,ScrapeSamplingTests,ScrapeToolTests}.cs
  tests/DesktopMcp.Server.Tests/Cli/CliAppTests.cs            (Task 1)
docs/superpowers/specs/2026-10-19-windows-port-design.md     (Task 10)
docs/superpowers/plans/2026-10-19-windows-p0-followups.md    (Task 10)
```

## Tasks

1. HTTP host and Streamable HTTP
2. Legacy SSE and HTTP sessions
3. Core request guards
4. HTTP pipeline and `/health`
5. Core OAuth server
6. OAuth endpoints and OAuth tokens in the auth step
7. TLS and HTTP/2
8. Scrape sampling
9. Secrets out of the PowerShell child's environment
10. Release verification and documentation

---

### Task 1: HTTP host and Streamable HTTP

Spec §2 HTTP row, §5, §8 Transports; Decisions 2, 4, 7, 8, 19.

**Files:**
- Create: `windows/src/DesktopMcp.Server/Hosting/McpServerSetup.cs`, `ServeCommand.cs`, `windows/src/DesktopMcp.Server/Http/HttpServer.cs`, `ListenTarget.cs`
- Modify: `windows/Directory.Packages.props`, `windows/src/DesktopMcp.Server/DesktopMcp.Server.csproj`, `windows/src/DesktopMcp.Server/Hosting/StdioServer.cs` (uses `McpServerSetup`; the job moves to `ServeCommand`), `windows/src/DesktopMcp.Server/Cli/CliApp.cs` (drop the "arrives in phase P5" branch), `CliEnvironment.cs` (`Serve = ServeCommand.RunAsync`)
- Test: `windows/tests/DesktopMcp.Server.Tests/Http/HttpHostCollection.cs`, `HttpRig.cs`, `HttpServerTests.cs`, `ListenTargetTests.cs`, `windows/tests/DesktopMcp.Server.Tests/Cli/CliAppTests.cs`

**Interfaces:**
- Consumes: `ServePlan` (P1), `ToolServices` (P4), `SessionRegistry`, `ControlNotifier`, `ControlGate`/`ControlGateFilter`, `McpLogging`, `ControlHostedService`, `StderrLoggerProvider` (P4), `ProcessJob.TryAssignKillOnClose` (P1).
- Produces:
  - `internal static class McpServerSetup`:
    - `static IMcpServerBuilder Add(IServiceCollection services, ServePlan plan, ToolServices tools, SessionRegistry sessions)`: registers, in this order, the `ControlHostedService`, the `ControlNotifierHostedService`, `AddMcpServer` (server info, instructions, `DeclareLogging`), the filtered tools and the gate filter that remembers sessions. Exactly what `StdioServer.RunAsync` does today; the caller adds the transport.
    - `static IDisposable ReleaseHeldInputOnStop(IServiceProvider services, ToolServices tools, ILogger log)`: the `ReleaseHeldInput` registration on `ApplicationStopping`; disposing it runs the release once more (the `finally` in `StdioServer`).
  - `public static class ServeCommand { public static Task<int> RunAsync(ServePlan plan, CancellationToken ct); }`: the Job Object (warning on failure, as now), then `StdioServer.RunAsync` or `HttpServer.RunAsync`.
  - `public static class HttpServer`:
    - `public static Task<int> RunAsync(ServePlan plan, CancellationToken ct)` (production services: `ToolServices.Create(plan, desktopLog.Write)`);
    - `internal static Task<int> RunAsync(ServePlan plan, ToolServices services, CancellationToken ct, Action<Uri>? listening = null, TextWriter? announce = null)` (`announce` defaults to `Console.Error`; `listening` gets the announced base URI, e.g. `http://127.0.0.1:53110/mcp`);
    - `internal static string EndpointPath(ServePlan plan)` → `"/sse"` for `sse`, else `"/mcp"`;
    - `internal static TimeSpan? SessionTtl(ServePlan plan)` (Task 2 fills it; here it returns 60 s).
  - `internal abstract record ListenTarget` with `Localhost`, `Address(IPAddress Ip)` cases and `static IReadOnlyList<ListenTarget> Resolve(string host, Func<string, IPAddress[]> dns)`.
  - Tests: `HttpHostCollection` (`[CollectionDefinition(Name, DisableParallelization = true)]`, `Name = "HTTP host"`); `HttpRig` (`static Task<HttpRig> StartAsync(ServeArguments arguments, ToolServices? services = null, DesktopMcpConfig? config = null)`; resolves the plan with `ServePlan.Resolve` and forces `Host = "127.0.0.1"`, `Port = 0` unless the arguments name `127.0.0.1`/`::1`; exposes `Uri Endpoint`, `Uri Base`, `HttpClient Http` (no default auth), `string Announced`, `Task<McpClient> ConnectAsync(HttpTransportMode mode, IDictionary<string,string>? headers = null, McpClientOptions? options = null)`, `ValueTask DisposeAsync()` that cancels and asserts exit code 0).

- [ ] **Step 1: Add the package**

`Directory.Packages.props`: `<PackageVersion Include="ModelContextProtocol.AspNetCore" Version="2.2.0" />` next to `ModelContextProtocol`, with the comment `<!-- Kestrel endpoints for the MCP SDK (spec §2); its only dependency is ModelContextProtocol [2.2.0]. -->`. `DesktopMcp.Server.csproj`: `<PackageReference Include="ModelContextProtocol.AspNetCore" />`.
Run: `dotnet build DesktopMcp.slnx` → 0 warnings.

- [ ] **Step 2: Write the failing tests**

```csharp
public class ListenTargetTests
{
    private static IPAddress[] NoDns(string _) => throw new InvalidOperationException("no DNS expected");

    [Theory]
    [InlineData("localhost")] [InlineData("LOCALHOST")]
    public void LocalhostIsKestrelsLocalhost(string host) =>
        Assert.IsType<ListenTarget.Localhost>(Assert.Single(ListenTarget.Resolve(host, NoDns)));

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")] [InlineData("::1", "::1")] [InlineData("[::1]", "::1")]
    [InlineData("0.0.0.0", "0.0.0.0")] [InlineData("::", "::")] [InlineData("", "::")]
    [InlineData("192.168.1.5", "192.168.1.5")]
    public void AddressesBindThemselves(string host, string expected) =>
        Assert.Equal(IPAddress.Parse(expected), Assert.IsType<ListenTarget.Address>(Assert.Single(ListenTarget.Resolve(host, NoDns))).Ip);

    [Fact]
    public void ANameBindsEveryResolvedAddress() =>
        Assert.Equal(2, ListenTarget.Resolve("myhost", _ => [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("fe80::2")]).Count);

    [Fact]
    public void AnUnresolvableNameIsAConfigError() =>
        Assert.Equal("Could not resolve server.host 'nowhere'.",
            Assert.Throws<ConfigException>(() => ListenTarget.Resolve("nowhere", _ => [])).Message);
}

[Collection(HttpHostCollection.Name)]
public class HttpServerTests
{
    [Fact]
    public async Task StreamableHttpServesInitializeAndTheContract()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" });
        Assert.Matches(@"^desktop-mcp: serving streamable-http on http://127\.0\.0\.1:\d+/mcp$", rig.Announced);
        await using var client = await rig.ConnectAsync(HttpTransportMode.StreamableHttp);
        Assert.Equal("desktop-mcp", client.ServerInfo.Name);
        var tools = await client.ListToolsAsync();
        Assert.Equal(21, tools.Count);          // ContractTests compares the JSON; here the transport carries it
        var wait = await client.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 });
        Assert.False(wait.IsError ?? false);
    }

    [Fact]
    public async Task StatefulResponsesCarryASessionId()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" });
        using var response = await rig.Http.SendAsync(HttpRig.InitializeRequest(rig.Endpoint));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Mcp-Session-Id"));
    }

    [Fact]
    public async Task StatelessResponsesCarryNoSessionIdAndGetIs405()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http", StatelessHttp = true });
        using var response = await rig.Http.SendAsync(HttpRig.InitializeRequest(rig.Endpoint));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        using var get = await rig.Http.GetAsync(rig.Endpoint);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
    }

    [Fact]
    public async Task TheTrailingSlashPathIsTheSameEndpoint()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" });
        using var response = await rig.Http.SendAsync(HttpRig.InitializeRequest(new Uri(rig.Base, "/mcp/")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StreamableHttpMapsNoLegacySse()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" });
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.GetAsync(new Uri(rig.Base, "/sse"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.PostAsync(new Uri(rig.Base, "/message"), new StringContent("{}"))).StatusCode);
    }

    [Fact]
    public async Task APortInUseExitsWithAnError()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;
        var error = new StringWriter();
        var plan = HttpRig.Plan(new ServeArguments { Transport = "streamable-http", Host = "127.0.0.1", Port = port });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(1, await HttpServer.RunAsync(plan, ToolServices.CreateDefault(), timeout.Token, announce: error));
        Assert.StartsWith($"Error: Could not listen on 127.0.0.1:{port}: ", error.ToString());
    }
}
```

`HttpRig.InitializeRequest(Uri)` is a POST with `Accept: application/json, text/event-stream`, `Content-Type: application/json` and the body `{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"rig","version":"1"}}}`.
`CliAppTests`: `HttpTransportsArriveInP5` becomes `HttpTransportsAreServed` (`serve --transport sse` and `--transport streamable-http --host 127.0.0.1` return 0 from the fake `Serve` with `_served.Transport` equal to the transport; nothing on stderr).

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Server.Tests`
Expected: FAIL to compile (`HttpServer`, `ListenTarget`, `HttpRig` missing).

- [ ] **Step 4: Implement**

- `McpServerSetup` and `ServeCommand` as above; `StdioServer` keeps its public `RunAsync(ServePlan, CancellationToken)` signature minus the job (now in `ServeCommand`), and its internal overload calls `McpServerSetup.Add` then `WithStdioServerTransport`/the test's stream transport. Every existing `StdioServerTests` must pass unchanged.
- `HttpServer.RunAsync`: `WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory })`; clear configuration sources and logging providers, add `StderrLoggerProvider`, level as `StdioServer`; `builder.WebHost.ConfigureKestrel` with one `Listen`/`ListenLocalhost` per `ListenTarget` on `plan.Port`, `Protocols = HttpProtocols.Http1` (Task 7 adds TLS); `McpServerSetup.Add(builder.Services, plan, services, new SessionRegistry(services.Time, SessionTtl(plan)))` then `.WithHttpTransport(ConfigureTransport(plan))` with `ConfigureTransport` setting `SessionMode` per Decisions 4; `app.MapMcp(EndpointPath(plan) == "/sse" ? "" : "/mcp")` (Task 2 completes SSE); `await app.StartAsync(ct)` inside a `try` that maps `IOException` (the inner `SocketException` or `AddressInUseException`) to `Error: Could not listen on <host>:<port>: <message>` and 1; read the bound port from `IServerAddressesFeature`, write the announcement (Decisions 8), call `listening`; then `await app.WaitForShutdownAsync(ct)`, release held input (`McpServerSetup.ReleaseHeldInputOnStop`), return 0. A `ConfigException` from `ListenTarget.Resolve` (or Task 7's certificate) prints `Error: <message>` and returns 1.
- `CliApp.ServeAsync`: remove the `plan.Transport != "stdio"` branch. `CliEnvironment.CreateDefault`: `Serve = ServeCommand.RunAsync`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/DesktopMcp.Server.Tests`
Expected: PASS (the earlier tests included).

- [ ] **Step 6: Check the publish folder**

Run: `dotnet publish src/DesktopMcp -c Release -r win-x64 -o ../scratch-p5-publish` (outside the repo's `dist`), then list the folder.
Expected: 0 warnings; only `desktop-mcp.exe` and `desktop-mcp.pdb`. Any other file (a `web.config`, `*.deps.json`, a static-web-assets manifest) is removed by the existing `Directory.Build.targets` trim or by a property set there, never by deleting it in the script. Delete the scratch folder.

- [ ] **Step 7: Commit**

```bash
git add windows/Directory.Packages.props windows/src/DesktopMcp.Server windows/tests/DesktopMcp.Server.Tests
git commit -m "Serve the tools over Streamable HTTP on Kestrel" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Legacy SSE and HTTP sessions

Spec §8 Transports (sse row), §7 Sessions; Decisions 4, 18; P4 Decisions 22.

**Files:**
- Modify: `windows/src/DesktopMcp.Server/Http/HttpServer.cs`
- Test: `windows/tests/DesktopMcp.Server.Tests/Http/LegacySseTests.cs`, `HttpSessionTests.cs`

**Interfaces:**
- Consumes: Task 1 (`HttpServer`, `HttpRig`), `SessionRegistry`, `ControlNotifier` (P4), `FakeControl` (P4 tests).
- Produces: `HttpServer.SessionTtl(plan)` per Decisions 18; the `sse` mapping; `internal const string HttpServer.LegacySseSwitch = "ModelContextProtocol.AspNetCore.EnableLegacySse"`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Collection(HttpHostCollection.Name)]
public class LegacySseTests
{
    [Fact]
    public async Task SseAnnouncesTheMessageEndpointAndServesTools()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "sse" });
        Assert.Matches(@"^desktop-mcp: serving sse on http://127\.0\.0\.1:\d+/sse$", rig.Announced);
        var endpoint = await rig.ReadSseEndpointEventAsync();          // first event of GET /sse
        Assert.StartsWith("/message?sessionId=", endpoint);
        await using var client = await rig.ConnectAsync(HttpTransportMode.Sse);
        Assert.Equal(21, (await client.ListToolsAsync()).Count);
    }

    [Fact]
    public async Task SseMapsNoStreamableEndpoint()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "sse" });
        using var post = await rig.Http.SendAsync(HttpRig.InitializeRequest(rig.Base));
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.SendAsync(HttpRig.InitializeRequest(new Uri(rig.Base, "/mcp")))).StatusCode);
    }

    [Fact]
    public async Task StatelessHttpIsIgnoredForSse()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "sse", StatelessHttp = true });
        Assert.StartsWith("/message?sessionId=", await rig.ReadSseEndpointEventAsync());
    }

    [Fact]
    public async Task HostsAfterAnSseHostMapNoSse()
    {
        await (await HttpRig.StartAsync(new ServeArguments { Transport = "sse" })).DisposeAsync();
        await using var stateless = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http", StatelessHttp = true });
        Assert.Equal(HttpStatusCode.NotFound, (await stateless.Http.GetAsync(new Uri(stateless.Base, "/sse"))).StatusCode);
    }
}

[Collection(HttpHostCollection.Name)]
public class HttpSessionTests
{
    [Theory]
    [InlineData("streamable-http", false, 60.0)]
    [InlineData("streamable-http", true, 0.0)]
    [InlineData("sse", false, 60.0)]
    [InlineData("sse", true, 60.0)]
    public void SessionTtlFollowsUpstream(string transport, bool stateless, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), HttpServer.SessionTtl(HttpRig.Plan(new ServeArguments { Transport = transport, StatelessHttp = stateless })));

    [Theory]
    [InlineData(HttpTransportMode.StreamableHttp, "streamable-http")]
    [InlineData(HttpTransportMode.Sse, "sse")]
    public async Task AnHttpClientReceivesUserControlNotifications(HttpTransportMode mode, string transport)
    {
        var control = new FakeControl();
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = transport }, ToolServices.CreateDefault() with { Control = control });
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = await rig.ConnectAsync(mode, options: HttpRig.OnLogMessage(received));
        await client.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 });   // remembered by the gate filter
        control.RaiseStateChanged(new(ControlState.Ready, 1, 0, true, "relaxed", 10));
        control.RaiseStateChanged(new(ControlState.User, 2, 10, true, "relaxed", 10));
        var data = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("USER_CONTROL", data.GetProperty("data").GetProperty("event").GetString());
        Assert.Equal("desktop-mcp.control", data.GetProperty("logger").GetString());
    }

    [Fact]
    public async Task ADeletedSessionIsForgottenAndOthersStillHearNotifications()
    {
        var control = new FakeControl();
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" }, ToolServices.CreateDefault() with { Control = control });
        var heard = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var kept = await rig.ConnectAsync(HttpTransportMode.StreamableHttp, options: HttpRig.OnLogMessage(heard));
        var gone = await rig.ConnectAsync(HttpTransportMode.StreamableHttp);
        await kept.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 });
        await gone.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 });
        await gone.DisposeAsync();                                       // the SDK client sends DELETE
        control.RaiseStateChanged(new(ControlState.User, 2, 10, true, "relaxed", 10));
        await heard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(rig.HostIsRunning);
    }
}
```

`HttpRig.ReadSseEndpointEventAsync()` opens `GET /sse` with `Accept: text/event-stream` and returns the `data` of the first `endpoint` event (5 s bound). `HttpRig.OnLogMessage(tcs)` builds `McpClientOptions` whose `Handlers.NotificationHandlers` maps `notifications/message` to `tcs.TrySetResult(params)`. `HttpRig.HostIsRunning` is true while `RunAsync` has not completed.

As built (preflight F1, F5): `OnLogMessage(tcs, eventName)` completes only on the first message whose `data.event` is `eventName` (an `OnLogMessage(Action<JsonElement>)` overload sees every message). The SDK drops a notification while the client's GET stream is not open yet, so the notification tests raise `user`/`ready` alternately with rising generations every 250 ms until `USER_CONTROL` is heard (5 s bound) instead of once. `TwoCallsInOneHttpSessionHearOneNotification` makes two calls in one session, warms the stream that way, then raises one change and expects exactly one more `USER_CONTROL`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/DesktopMcp.Server.Tests --filter "FullyQualifiedName~LegacySseTests|FullyQualifiedName~HttpSessionTests"`
Expected: FAIL (no `/sse`; the TTL theory fails for stateless).

- [ ] **Step 3: Implement**

`SessionTtl` per Decisions 18. In `HttpServer`: under `static readonly Lock LegacySseLock`, `AppContext.SetSwitch(LegacySseSwitch, plan.Transport == "sse")`, then `MapMcp`, then resolve `IOptions<HttpServerTransportOptions>.Value` so the switch is read while held. For `sse`, add before `MapMcp` a middleware that answers 404 to any request whose path is `/` or `/mcp` (with or without trailing slash) and is not `/sse` or `/message` (Decisions 4). Each `HttpServerTransportOptions` reads the switch when it is constructed, once per host (preflight F6), so no fallback is needed; `HostsAfterAnSseHostMapNoSse` keeps it so.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Server.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Server windows/tests/DesktopMcp.Server.Tests
git commit -m "Serve the legacy SSE transport and notify HTTP sessions with upstream's TTLs" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Core request guards

Spec §8 HTTP pipeline steps 2-5; Decisions 9, 10, 13, 15; `infrastructure/security.py:239-266`, `auth.py:14-20`.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Security/HttpGuards.cs`, `SecurityJson.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Security/HttpGuardsTests.cs`

**Interfaces:**
- Consumes: `IpNetwork`, `IpAddressText` (P1).
- Produces (`namespace DesktopMcp.Core.Security`):
  - `public static class HttpGuards`:
    - `static IReadOnlyList<string> PublicPaths` (the five, in upstream's order) and `static bool IsPublicPath(string path)` (ordinal, exact);
    - `static bool IsTrustedHost(string? hostPart, IReadOnlyList<string>? allowedHosts)`: `hostPart` is the Host header's host without port (`HostString.Host`); `null` list → true; null or empty `hostPart` with a list → false;
    - `static bool IsAllowedOrigin(string? origin, IReadOnlyList<string> corsOrigins)` (Decisions 9);
    - `static string? AllowlistRejection(IPAddress? client, IReadOnlyList<IpNetwork> allowlist)`: `null` when allowed or the list is empty, else the 403 `error` text (Decisions 15);
    - `static bool FixedTimeEquals(string a, string b)`.
  - `public sealed record ErrorBody(string Error)`, `OAuthErrorBody(string Error, string ErrorDescription)`, `HealthBody(string Status)`, and `[JsonSerializable]` `internal partial class SecurityJsonContext` exposed as `public static class SecurityJson { static string Error(string error); static string OAuthError(string error, string description); static string Health { get; } }` (snake_case property names, compact, `UnsafeRelaxedJsonEscaping` so `'` and `<`/`>` stay literal as Python's `json.dumps`).

- [ ] **Step 1: Write the failing tests**

```csharp
public class HttpGuardsTests
{
    private static readonly string[] Loopback = ["localhost", "127.0.0.1", "[::1]"];

    [Theory]
    [InlineData("localhost", true)] [InlineData("LOCALHOST", true)] [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)] [InlineData("attacker.example", false)] [InlineData("127.0.0.2", false)]
    [InlineData("", false)] [InlineData(null, false)]
    public void TrustedHostTable(string? host, bool expected) => Assert.Equal(expected, HttpGuards.IsTrustedHost(host, Loopback));

    [Fact]
    public void ASpecificIPv6BindTrustsItsBracketedHost() => Assert.True(HttpGuards.IsTrustedHost("[fe80::5]", ["fe80::5"]));

    [Fact]
    public void NoListTrustsEverything() => Assert.True(HttpGuards.IsTrustedHost("anything", null));

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://localhost:6274", true)] [InlineData("https://127.0.0.1", true)] [InlineData("http://[::1]:3000", true)]
    [InlineData("https://client.example.com", true)]                 // listed
    [InlineData("https://attacker.example", false)] [InlineData("null", false)]
    [InlineData("file://localhost", false)] [InlineData("http://localhost.attacker.example", false)]
    public void OriginTable(string? origin, bool expected) =>
        Assert.Equal(expected, HttpGuards.IsAllowedOrigin(origin, ["https://client.example.com"]));

    [Fact]
    public void AnEmptyAllowlistAllowsEveryone() => Assert.Null(HttpGuards.AllowlistRejection(IPAddress.Parse("8.8.8.8"), []));

    [Fact]
    public void AMappedClientMatchesAnIPv4Entry() =>
        Assert.Null(HttpGuards.AllowlistRejection(IPAddress.Parse("::ffff:10.1.2.3"), IpNetwork.ParseAllowlist(["10.0.0.0/8"])));

    [Fact]
    public void AnUnlistedClientIsNamedInIPv4Form() =>
        Assert.Equal("Forbidden: 192.168.0.9 is not in the IP allowlist",
            HttpGuards.AllowlistRejection(IPAddress.Parse("::ffff:192.168.0.9"), IpNetwork.ParseAllowlist(["10.0.0.0/8"])));

    [Fact]
    public void AMissingClientIsRejected() =>
        Assert.Equal("Forbidden: missing client address", HttpGuards.AllowlistRejection(null, IpNetwork.ParseAllowlist(["::1"])));

    [Theory]
    [InlineData("/health", true)] [InlineData("/oauth/token", true)] [InlineData("/.well-known/oauth-authorization-server", true)]
    [InlineData("/Health", false)] [InlineData("/health/", false)] [InlineData("/mcp", false)]
    public void PublicPathsAreExact(string path, bool expected) => Assert.Equal(expected, HttpGuards.IsPublicPath(path));

    [Theory]
    [InlineData("abc", "abc", true)] [InlineData("abc", "abd", false)] [InlineData("abc", "abcd", false)] [InlineData("", "", true)]
    public void FixedTimeEqualsCompares(string a, string b, bool expected) => Assert.Equal(expected, HttpGuards.FixedTimeEquals(a, b));

    [Fact]
    public void BodiesAreCompactPythonJson()
    {
        Assert.Equal("""{"error":"Forbidden: origin 'https://a.example' is not allowed"}""", SecurityJson.Error("Forbidden: origin 'https://a.example' is not allowed"));
        Assert.Equal("""{"error":"Missing or invalid Authorization header. Expected: Bearer <token>"}""", SecurityJson.Error("Missing or invalid Authorization header. Expected: Bearer <token>"));
        Assert.Equal("""{"error":"invalid_grant","error_description":"unknown code"}""", SecurityJson.OAuthError("invalid_grant", "unknown code"));
        Assert.Equal("""{"status":"ok"}""", SecurityJson.Health);
    }
}
```

(The pipeline passes `HostString.Host`, so `Host: [::1]:8000` arrives as `[::1]`, a row of `TrustedHostTable`; Task 4's `AnIPv6LoopbackClientPassesTheChecks` pins it end to end.)

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Core.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

As specified. `IsAllowedOrigin`: listed exactly → true; else `Uri.TryCreate(origin, UriKind.Absolute, out var uri)`, scheme `http` or `https`, `uri.Host` with brackets trimmed passes `IpAddressText.IsLoopbackHost`. `AllowlistRejection` maps with `IsIPv4MappedToIPv6` → `MapToIPv4()` and matches with `IpNetwork.Contains`. `FixedTimeEquals`: `CryptographicOperations.FixedTimeEquals` over the UTF-8 bytes when the lengths match, else false.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~HttpGuardsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core/Security windows/tests/DesktopMcp.Core.Tests/Security
git commit -m "Add the HTTP request guards: trusted host, origin, IP allowlist and constant-time keys" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: HTTP pipeline and `/health`

Spec §8 HTTP pipeline; Decisions 9, 10, 13, 15, 16, 17.

**Files:**
- Create: `windows/src/DesktopMcp.Server/Http/HttpPipeline.cs`
- Modify: `windows/src/DesktopMcp.Server/Http/HttpServer.cs` (call `HttpPipeline.Use` before `MapMcp`; map `/health`)
- Test: `windows/tests/DesktopMcp.Server.Tests/Http/HttpPipelineTests.cs`

**Interfaces:**
- Consumes: `HttpGuards`, `SecurityJson` (Task 3), `HttpRig` (Task 1).
- Produces: `internal static class HttpPipeline { static void Use(IApplicationBuilder app, ServePlan plan, Func<string, bool>? validateOAuthToken); static Task Health(HttpContext context); }`. The middleware, in order: (1) OPTIONS, (2) trusted host, (3) Origin, (4) CORS response headers, (5) IP allowlist (skips `/health`), (6) auth (skips public paths; present only with an auth key or `validateOAuthToken`). Task 6 passes `validateOAuthToken`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Collection(HttpHostCollection.Name)]
public class HttpPipelineTests
{
    private static ServeArguments Streamable(string? authKey = null, string? allowlist = null, string? cors = null) =>
        new() { Transport = "streamable-http", AuthKey = authKey, IpAllowlist = allowlist, CorsOrigins = cors };

    [Fact]
    public async Task HealthIsOkWithoutAuth()
    {
        await using var rig = await HttpRig.StartAsync(Streamable(authKey: "k", allowlist: "10.0.0.0/8"));
        using var response = await rig.Http.GetAsync(new Uri(rig.Base, "/health"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EveryOptionsRequestIs200AndOnlyListedOriginsGetCorsHeaders()
    {
        await using var rig = await HttpRig.StartAsync(Streamable(authKey: "k", cors: "https://client.example.com"));
        var listed = await rig.SendAsync(HttpMethod.Options, "/mcp", ("Origin", "https://client.example.com"));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal("https://client.example.com", listed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("GET, POST, OPTIONS", listed.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Equal("content-type, authorization, mcp-session-id", listed.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.Equal("Origin", listed.Headers.Vary.Single());
        var other = await rig.SendAsync(HttpMethod.Options, "/mcp", ("Origin", "https://attacker.example"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(0, other.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task ARebindingHostIsRejectedBeforeTheEndpoint()
    {
        await using var rig = await HttpRig.StartAsync(Streamable());
        var response = await rig.SendInitializeAsync(("Host", "attacker.example"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid host header", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ACrossSiteOriginIsRejectedEvenWithoutAuth()
    {
        await using var rig = await HttpRig.StartAsync(Streamable());
        var response = await rig.SendInitializeAsync(("Origin", "https://attacker.example"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("""{"error":"Forbidden: origin 'https://attacker.example' is not allowed"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ALoopbackOriginPassesAndAListedOriginGetsCorsHeaders()
    {
        await using var rig = await HttpRig.StartAsync(Streamable(cors: "https://client.example.com"));
        Assert.Equal(HttpStatusCode.OK, (await rig.SendInitializeAsync(("Origin", "http://localhost:6274"))).StatusCode);
        var listed = await rig.SendInitializeAsync(("Origin", "https://client.example.com"));
        Assert.Equal("https://client.example.com", listed.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task AnUnlistedClientIs403()
    {
        await using var rig = await HttpRig.StartAsync(Streamable(allowlist: "10.0.0.0/8"));
        var response = await rig.SendInitializeAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("""{"error":"Forbidden: 127.0.0.1 is not in the IP allowlist"}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null, """{"error":"Missing or invalid Authorization header. Expected: Bearer <token>"}""")]
    [InlineData("bearer secret", """{"error":"Missing or invalid Authorization header. Expected: Bearer <token>"}""")]
    [InlineData("Bearer wrong", """{"error":"Invalid authentication token"}""")]
    [InlineData("Bearer  secret", """{"error":"Invalid authentication token"}""")]
    public async Task TheAuthKeyIsRequired(string? authorization, string body)
    {
        await using var rig = await HttpRig.StartAsync(Streamable(authKey: "secret"));
        var response = authorization is null ? await rig.SendInitializeAsync() : await rig.SendInitializeAsync(("Authorization", authorization));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheRightKeyReachesTheEndpoint()
    {
        await using var rig = await HttpRig.StartAsync(Streamable(authKey: "secret"));
        Assert.Equal(HttpStatusCode.OK, (await rig.SendInitializeAsync(("Authorization", "Bearer secret"))).StatusCode);
        await using var client = await rig.ConnectAsync(HttpTransportMode.StreamableHttp, new Dictionary<string, string> { ["Authorization"] = "Bearer secret" });
        Assert.Equal(21, (await client.ListToolsAsync()).Count);
    }

    [Fact]
    public async Task TheHostCheckRunsBeforeTheAllowlistAndAuth()
    {
        await using var rig = await HttpRig.StartAsync(Streamable(authKey: "secret", allowlist: "10.0.0.0/8"));
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.SendInitializeAsync(("Host", "attacker.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.SendInitializeAsync()).StatusCode);   // allowlist before auth
    }

    [Fact]
    public async Task AnIPv6LoopbackClientPassesTheChecks()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http", Host = "::1", IpAllowlist = "::1" });
        var response = await rig.SendInitializeAsync(("Host", $"[::1]:{rig.Base.Port}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

`HttpRig.SendAsync(method, path, params (string, string)[] headers)` and `SendInitializeAsync(params (string, string)[] headers)` set headers with `TryAddWithoutValidation` (a `Host` header replaces the default). `AnIPv6LoopbackClientPassesTheChecks` binds `[::1]` port 0; if the machine has no IPv6 loopback, it returns early with a skip note in the test output (it must not fail).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/DesktopMcp.Server.Tests --filter "FullyQualifiedName~HttpPipelineTests"`
Expected: FAIL (no `/health`, no checks).

- [ ] **Step 3: Implement `HttpPipeline.Use` and `Health`**

Each step is one `app.Use` in the order above, writing its rejection with `SecurityJson` and `Content-Type: application/json` (the host rejection is `text/plain; charset=utf-8`). `HttpServer` calls `HttpPipeline.Use(app, plan, validateOAuthToken: null)` before mapping `/health` (`app.MapGet("/health", HttpPipeline.Health)`) and `MapMcp`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Server.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Server windows/tests/DesktopMcp.Server.Tests
git commit -m "Check host, origin, allowlist and auth key in upstream's order and answer /health" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Core OAuth server

Spec §8 OAuth; Decisions 12; `infrastructure/oauth.py:24-288`.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Security/OAuthServer.cs`
- Modify: `windows/src/DesktopMcp.Core/Security/SecurityJson.cs` (metadata, token response, register body)
- Test: `windows/tests/DesktopMcp.Core.Tests/Security/OAuthServerTests.cs`

**Interfaces:**
- Consumes: `HttpGuards.FixedTimeEquals`, `IpAddressText.IsLoopbackHost`, `SecurityJson` (Task 3).
- Produces:
  - `public sealed record OAuthResult(int Status, string? Json, string? Location)` (a 302 has `Location` and no body).
  - `public sealed class OAuthServer(string clientId, string clientSecret, TimeProvider time, Func<int, string>? token = null)`; `token(n)` defaults to `Base64Url(RandomNumberGenerator.GetBytes(n))` (Python's `token_urlsafe(n)`); constants `CodeLifetime = 300 s`, `TokenLifetime = 3600 s`, `MaxPendingCodes = 1000`:
    - `string Metadata(string issuer)`;
    - `OAuthResult Register()`;
    - `OAuthResult Authorize(IReadOnlyDictionary<string, string> query)`;
    - `OAuthResult Token(IReadOnlyDictionary<string, string> fields)`;
    - `bool ValidateToken(string token)`;
    - `static string Quote(string value)` (Python's `urllib.parse.quote` with `safe="/"`: UTF-8, keep `A-Za-z0-9_.-~/`, others `%XX` upper case).

- [ ] **Step 1: Write the failing tests**

```csharp
public class OAuthServerTests
{
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";   // RFC 7636 appendix B
    private readonly StepClock _time = new();                                        // Core.Tests' fake TimeProvider (P4): timestamps only
    private int _next;
    private OAuthServer Server() => new("client", "s3cret", _time, n => $"tok{++_next}");

    private static Dictionary<string, string> Query(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);
    private static Dictionary<string, string> GoodAuthorize(string redirect = "http://127.0.0.1:33418/callback", string state = "") =>
        Query(("response_type", "code"), ("client_id", "client"), ("redirect_uri", redirect), ("code_challenge", Challenge), ("code_challenge_method", "S256"), ("state", state));

    [Fact]
    public void MetadataIsUpstreams() =>
        Assert.Equal("""{"issuer":"http://localhost:8000","authorization_endpoint":"http://localhost:8000/oauth/authorize","token_endpoint":"http://localhost:8000/oauth/token","response_types_supported":["code"],"grant_types_supported":["authorization_code"],"code_challenge_methods_supported":["S256"],"token_endpoint_auth_methods_supported":["client_secret_post"]}""",
            Server().Metadata("http://localhost:8000"));

    [Fact]
    public void RegistrationIsDisabled()
    {
        var result = Server().Register();
        Assert.Equal(403, result.Status);
        Assert.Equal("""{"error":"registration_disabled","error_description":"Dynamic OAuth client registration is disabled. Configure --oauth-client-id and --oauth-client-secret on the server."}""", result.Json);
    }

    [Theory]
    [InlineData("response_type", "token", 400, """{"error":"unsupported_response_type"}""")]
    [InlineData("code_challenge", "", 400, """{"error":"invalid_request","error_description":"code_challenge required (PKCE)"}""")]
    [InlineData("code_challenge_method", "plain", 400, """{"error":"invalid_request","error_description":"code_challenge_method must be S256"}""")]
    [InlineData("redirect_uri", "https://attacker.example/cb", 400, """{"error":"invalid_request","error_description":"redirect_uri must be a loopback http(s) URI"}""")]
    [InlineData("redirect_uri", "http://localhost/cb#frag", 400, """{"error":"invalid_request","error_description":"redirect_uri must be a loopback http(s) URI"}""")]
    [InlineData("redirect_uri", "myapp://localhost/cb", 400, """{"error":"invalid_request","error_description":"redirect_uri must be a loopback http(s) URI"}""")]
    [InlineData("client_id", "other", 400, """{"error":"invalid_client"}""")]
    public void AuthorizeRejectsInUpstreamsOrder(string key, string value, int status, string json)
    {
        var query = GoodAuthorize();
        query[key] = value;
        Assert.Equal(new OAuthResult(status, json, null), Server().Authorize(query));
    }

    [Fact]
    public void AMissingMethodMeansS256() 
    {
        var query = GoodAuthorize();
        query.Remove("code_challenge_method");
        Assert.Equal(302, Server().Authorize(query).Status);
    }

    [Theory]
    [InlineData("http://127.0.0.1:33418/callback", "", "http://127.0.0.1:33418/callback?code=tok1")]
    [InlineData("http://localhost/cb?x=1", "a b/c", "http://localhost/cb?x=1&code=tok1&state=a%20b/c")]
    [InlineData("https://[::1]:5000/cb", "s", "https://[::1]:5000/cb?code=tok1&state=s")]
    public void AuthorizeRedirectsWithTheCode(string redirect, string state, string location) =>
        Assert.Equal(new OAuthResult(302, null, location), Server().Authorize(GoodAuthorize(redirect, state)));

    private (OAuthServer Server, Dictionary<string, string> Fields) Issued()
    {
        var server = Server();
        server.Authorize(GoodAuthorize());
        return (server, Query(("grant_type", "authorization_code"), ("code", "tok1"), ("client_id", "client"),
            ("redirect_uri", "http://127.0.0.1:33418/callback"), ("code_verifier", Verifier), ("client_secret", "s3cret")));
    }

    [Fact]
    public void TheCodeBuysOneToken()
    {
        var (server, fields) = Issued();
        var result = server.Token(fields);
        Assert.Equal(new OAuthResult(200, """{"access_token":"tok2","token_type":"Bearer","expires_in":3600}""", null), result);
        Assert.True(server.ValidateToken("tok2"));
        Assert.Equal("""{"error":"invalid_grant","error_description":"unknown code"}""", server.Token(fields).Json);   // single use
    }

    [Theory]
    [InlineData("grant_type", "password", 400, """{"error":"unsupported_grant_type"}""")]
    [InlineData("code", "nope", 400, """{"error":"invalid_grant","error_description":"unknown code"}""")]
    [InlineData("client_id", "other", 400, """{"error":"invalid_grant","error_description":"client_id mismatch"}""")]
    [InlineData("redirect_uri", "http://127.0.0.1:1/x", 400, """{"error":"invalid_grant","error_description":"redirect_uri mismatch"}""")]
    [InlineData("code_verifier", "wrong", 400, """{"error":"invalid_grant","error_description":"PKCE verification failed"}""")]
    [InlineData("client_secret", "bad", 401, """{"error":"invalid_client"}""")]
    [InlineData("client_secret", "", 401, """{"error":"invalid_client"}""")]
    public void TokenRejectsInUpstreamsOrder(string key, string value, int status, string json)
    {
        var (server, fields) = Issued();
        fields[key] = value;
        Assert.Equal(new OAuthResult(status, json, null), server.Token(fields));
    }

    [Theory]
    [InlineData("code_verifier", "wrong", true)]      // burned (Decisions 12)
    [InlineData("client_secret", "bad", true)]        // consumed before the secret check (upstream)
    [InlineData("client_id", "other", false)]
    [InlineData("redirect_uri", "http://127.0.0.1:1/x", false)]
    public void WhichFailuresBurnTheCode(string key, string value, bool burned)
    {
        var (server, fields) = Issued();
        var good = new Dictionary<string, string>(fields);
        fields[key] = value;
        server.Token(fields);
        Assert.Equal(burned ? 400 : 200, server.Token(good).Status);
    }

    [Fact]
    public void ACodeExpiresAfter300Seconds()
    {
        var (server, fields) = Issued();
        _time.Advance(301);
        Assert.Equal("""{"error":"invalid_grant","error_description":"code expired"}""", server.Token(fields).Json);
    }

    [Fact]
    public void ATokenExpiresAfter3600Seconds()
    {
        var (server, fields) = Issued();
        server.Token(fields);
        _time.Advance(3601);
        Assert.False(server.ValidateToken("tok2"));
        Assert.False(server.ValidateToken(""));
    }

    [Fact]
    public void AtMostAThousandCodesWait()
    {
        var server = Server();
        for (var i = 0; i < 1001; i++) server.Authorize(GoodAuthorize());
        var fields = Query(("grant_type", "authorization_code"), ("code", "tok1"), ("client_id", "client"),
            ("redirect_uri", "http://127.0.0.1:33418/callback"), ("code_verifier", Verifier), ("client_secret", "s3cret"));
        Assert.Equal("""{"error":"invalid_grant","error_description":"unknown code"}""", server.Token(fields).Json);
    }
}
```

(`StepClock` overrides only `GetTimestamp`, so `OAuthServer` measures lifetimes with `time.GetTimestamp()` and `time.GetElapsedTime(...)`, never `GetUtcNow()`.)

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Core.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

Port `oauth.py:113-288` with Decisions 12. Authorize: missing query keys are `""` except `code_challenge_method` (`"S256"`); loopback redirect per `oauth.py:81-91` (`Uri.TryCreate`, scheme http/https, non-empty host, no fragment, host with brackets trimmed passes `IpAddressText.IsLoopbackHost`); code record `(ClientId, RedirectUri, Challenge, ExpiresAt)`; redirect `redirect + (redirect.Contains('?') ? "&" : "?") + "code=" + Quote(code)` then `"&state=" + Quote(state)` when non-empty. Token: checks in the test's order; PKCE `Base64Url(SHA256(UTF-8 verifier))` without padding, compared with `FixedTimeEquals`. Missing fields are `""`. Lifetimes use `time.GetTimestamp()`/`GetElapsedTime` (monotonic; the tests' `StepClock` fakes only timestamps). One `Lock` guards the dictionaries.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~OAuthServerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core/Security windows/tests/DesktopMcp.Core.Tests/Security
git commit -m "Add the in-memory OAuth 2.0 + PKCE server" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: OAuth endpoints and OAuth tokens in the auth step

Spec §8 OAuth and HTTP pipeline step 5; Decisions 12, 13; `__main__.py:837-851`, `auth.py:23-79`, `oauth.py:195-215` (body parsing).

**Files:**
- Create: `windows/src/DesktopMcp.Server/Http/OAuthEndpoints.cs`
- Modify: `windows/src/DesktopMcp.Server/Http/HttpServer.cs`, `HttpPipeline.cs`
- Test: `windows/tests/DesktopMcp.Server.Tests/Http/OAuthEndpointTests.cs`

**Interfaces:**
- Consumes: `OAuthServer`, `OAuthResult` (Task 5), `HttpPipeline.Use(..., validateOAuthToken)` (Task 4).
- Produces: `internal static class OAuthEndpoints { static void Map(IEndpointRouteBuilder app, OAuthServer oauth, Func<HttpContext, string> issuer); static Task<IReadOnlyDictionary<string, string>?> ReadFieldsAsync(HttpRequest request); }` (`null` = unparsable JSON → 400 `invalid_request`); `internal static string HttpServer.Issuer(ServePlan plan, int boundPort, HttpContext context)` (Decisions 12).

- [ ] **Step 1: Write the failing tests**

```csharp
[Collection(HttpHostCollection.Name)]
public class OAuthEndpointTests
{
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private static ServeArguments WithOAuth(string? authKey = null) =>
        new() { Transport = "streamable-http", OAuthClientId = "client", OAuthClientSecret = "s3cret", AuthKey = authKey };

    [Fact]
    public async Task MetadataNamesTheBoundPort()
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth());
        var json = await rig.Http.GetStringAsync(new Uri(rig.Base, "/.well-known/oauth-authorization-server"));
        Assert.Contains($"\"issuer\":\"http://127.0.0.1:{rig.Base.Port}\"", json);
    }

    [Fact]
    public async Task TheCodeFlowYieldsATokenThatOpensTheEndpoint()
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth());
        var token = await rig.OAuthTokenAsync(Challenge, Verifier, form: true);      // authorize (302) then POST /oauth/token
        Assert.Equal(HttpStatusCode.OK, (await rig.SendInitializeAsync(("Authorization", $"Bearer {token}"))).StatusCode);
        var denied = await rig.SendInitializeAsync(("Authorization", "Bearer forged"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("""{"error":"Unauthorized"}""", await denied.Content.ReadAsStringAsync());
        Assert.Equal("Bearer", denied.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task TheTokenEndpointAcceptsJson()
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth());
        Assert.NotEmpty(await rig.OAuthTokenAsync(Challenge, Verifier, form: false));
    }

    [Theory]
    [InlineData("application/json", "{not json", """{"error":"invalid_request"}""")]
    [InlineData("application/json", "[1]", """{"error":"invalid_request"}""")]
    [InlineData("text/plain", "grant_type=authorization_code", """{"error":"unsupported_grant_type"}""")]
    public async Task BadTokenBodiesAre400(string contentType, string body, string json)
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth());
        using var response = await rig.Http.PostAsync(new Uri(rig.Base, "/oauth/token"), new StringContent(body, Encoding.UTF8, contentType));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(json, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnOAuthTokenAlsoPassesTheAuthKeyStep()
    {
        await using var rig = await HttpRig.StartAsync(WithOAuth(authKey: "key"));
        var token = await rig.OAuthTokenAsync(Challenge, Verifier, form: true);
        Assert.Equal(HttpStatusCode.OK, (await rig.SendInitializeAsync(("Authorization", $"Bearer {token}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.SendInitializeAsync(("Authorization", "Bearer key"))).StatusCode);
    }

    [Fact]
    public async Task WithoutOAuthTheRoutesDoNotExist()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" });
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.GetAsync(new Uri(rig.Base, "/.well-known/oauth-authorization-server"))).StatusCode);
    }

    [Theory]
    [InlineData("0.0.0.0", 8000, "127.0.0.1:5555", "http://127.0.0.1:5555")]
    [InlineData("::", 8000, "[::1]:5555", "http://[::1]:5555")]
    [InlineData("localhost", 0, "ignored", "http://localhost:41000")]
    [InlineData("::1", 41000, "ignored", "http://[::1]:41000")]
    public void TheIssuerFollowsDecisions12(string host, int port, string hostHeader, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = HostString.FromUriComponent(hostHeader);
        var plan = HttpRig.Plan(new ServeArguments { Transport = "streamable-http", Host = host, Port = port, AllowInsecureRemote = true });
        Assert.Equal(expected, HttpServer.Issuer(plan, boundPort: 41000, context));
    }
}
```

`HttpRig.OAuthTokenAsync(challenge, verifier, form)`: `GET /oauth/authorize?response_type=code&client_id=client&redirect_uri=http://127.0.0.1:9/cb&code_challenge=<c>&code_challenge_method=S256` with redirects off, reads `code` from `Location`, posts the token request as form or JSON, returns `access_token`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/DesktopMcp.Server.Tests --filter "FullyQualifiedName~OAuthEndpointTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

`HttpServer`: when `plan.OAuthClientId` and secret are set, create one `OAuthServer(id, secret, services.Time)`, pass `oauth.ValidateToken` to `HttpPipeline.Use` and call `OAuthEndpoints.Map` (`GET /.well-known/oauth-authorization-server`, `POST /oauth/register`, `GET /oauth/authorize`, `POST /oauth/token`; each writes `OAuthResult`: status, `application/json` body or `Location`). `ReadFieldsAsync` per `oauth.py:201-215` and Decisions 12: a content type containing `application/x-www-form-urlencoded` → form; containing `application/json` → a JSON object (strings as-is, other values as raw text) or `null`; anything else → empty fields. `HttpPipeline` auth step per Decisions 13: with an auth key, a key mismatch falls back to `validateOAuthToken`; with OAuth only, the `Unauthorized` body.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Server.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Server windows/tests/DesktopMcp.Server.Tests
git commit -m "Serve the OAuth endpoints and accept their tokens in the auth step" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: TLS and HTTP/2

Spec §2 HTTP row, §8 TLS; Decisions 11.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Security/TlsCertificate.cs`, `windows/tests/DesktopMcp.Core.Tests/Security/TestCertificate.cs`, `windows/tests/DesktopMcp.Server.Tests/Http/TestCertificate.cs` (the same helper; test projects do not share code)
- Modify: `windows/src/DesktopMcp.Server/Http/HttpServer.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/Security/TlsCertificateTests.cs`, `windows/tests/DesktopMcp.Server.Tests/Http/TlsTests.cs`

**Interfaces:**
- Consumes: `ConfigException` (P1), `HttpRig` (Task 1).
- Produces: `public static class TlsCertificate { public static X509Certificate2 Load(string certPath, string keyPath); }`; test helper `sealed class TestCertificate : IDisposable` (`static TestCertificate Create(string commonName = "localhost")`: ECDSA P-256 via `CertificateRequest`, SAN `localhost`, `127.0.0.1`, `::1`, valid now-1 day to now+1 day, written as `cert.pem` and PKCS#8 `key.pem` into a new temp folder; `CertPath`, `KeyPath`, `Thumbprint`; `Dispose` deletes the folder).

- [ ] **Step 1: Write the failing tests**

```csharp
public class TlsCertificateTests
{
    [Fact]
    public void APemPairLoadsWithAUsablePrivateKey()
    {
        using var pem = TestCertificate.Create();
        using var certificate = TlsCertificate.Load(pem.CertPath, pem.KeyPath);
        Assert.True(certificate.HasPrivateKey);
        Assert.Equal(pem.Thumbprint, certificate.Thumbprint);
    }

    [Fact]
    public void AMismatchedKeyIsAConfigError()
    {
        using var a = TestCertificate.Create();
        using var b = TestCertificate.Create();
        var error = Assert.Throws<ConfigException>(() => TlsCertificate.Load(a.CertPath, b.KeyPath));
        Assert.StartsWith($"Could not load the TLS certificate '{a.CertPath}' with key '{b.KeyPath}': ", error.Message);
    }

    [Fact]
    public void AMissingFileIsAConfigError()
    {
        using var pem = TestCertificate.Create();
        var missing = Path.Combine(Path.GetDirectoryName(pem.KeyPath)!, "nope.pem");
        Assert.StartsWith($"Could not load the TLS certificate '{pem.CertPath}' with key '{missing}': ",
            Assert.Throws<ConfigException>(() => TlsCertificate.Load(pem.CertPath, missing)).Message);
    }

    [Fact]
    public void AnEncryptedKeyIsAConfigError()
    {
        using var pem = TestCertificate.Create(encryptKey: true);
        Assert.Throws<ConfigException>(() => TlsCertificate.Load(pem.CertPath, pem.KeyPath));
    }
}

[Collection(HttpHostCollection.Name)]
public class TlsTests
{
    [Fact]
    public async Task HttpsOffersHttp2AndHttp11()
    {
        using var pem = TestCertificate.Create();
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http", SslCertfile = pem.CertPath, SslKeyfile = pem.KeyPath });
        Assert.Equal("https", rig.Base.Scheme);
        using var http = HttpRig.PinnedClient(pem.Thumbprint);
        foreach (var version in new[] { HttpVersion.Version20, HttpVersion.Version11 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(rig.Base, "/health")) { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var response = await http.SendAsync(request);
            Assert.Equal(version, response.Version);
        }
    }

    [Fact]
    public async Task McpWorksOverHttp2()
    {
        using var pem = TestCertificate.Create();
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http", SslCertfile = pem.CertPath, SslKeyfile = pem.KeyPath });
        await using var client = await rig.ConnectAsync(HttpTransportMode.StreamableHttp, http: HttpRig.PinnedClient(pem.Thumbprint, HttpVersion.Version20));
        Assert.Equal(21, (await client.ListToolsAsync()).Count);
    }

    [Fact]
    public async Task PlainHttpOffersNoH2c()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(rig.Base, "/health")) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Http.SendAsync(request));
    }

    [Fact]
    public async Task ABadCertificateStopsServeWithOneError()
    {
        using var a = TestCertificate.Create();
        using var b = TestCertificate.Create();
        var error = new StringWriter();
        var plan = HttpRig.Plan(new ServeArguments { Transport = "streamable-http", Host = "127.0.0.1", Port = 0, SslCertfile = a.CertPath, SslKeyfile = b.KeyPath });
        Assert.Equal(1, await HttpServer.RunAsync(plan, ToolServices.CreateDefault(), CancellationToken.None, announce: error));
        Assert.StartsWith("Error: Could not load the TLS certificate", error.ToString());
    }
}
```

`TestCertificate.Create(encryptKey: true)` writes the key with `ExportEncryptedPkcs8PrivateKeyPem("pw", ...)`. `HttpRig.PinnedClient(thumbprint, version?)` is an `HttpClient` over a `SocketsHttpHandler` whose `SslOptions.RemoteCertificateValidationCallback` accepts only that thumbprint (and, with `version`, sets `DefaultRequestVersion` and `RequestVersionExact`); `ConnectAsync` gains an optional `HttpClient http` parameter for `HttpClientTransport`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Core.Tests tests/DesktopMcp.Server.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`TlsCertificate.Load` per Decisions 11 (the intermediate PEM certificate is disposed). `HttpServer`: with `plan.SslCertfile`, load the certificate before building the host (a `ConfigException` prints `Error: <message>`, returns 1), configure each listener with `UseHttps(certificate)` and `HttpProtocols.Http1AndHttp2`, keep plain listeners `Http1`, dispose the certificate after the host stops. The announcement and issuer use `https`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~TlsCertificateTests"` then `dotnet test tests/DesktopMcp.Server.Tests --filter "FullyQualifiedName~TlsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core/Security windows/src/DesktopMcp.Server windows/tests
git commit -m "Serve HTTPS with HTTP/2 from PEM certificate files" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Scrape sampling

Spec §8 Scrape; follow-ups P5; Decisions 5; `tools/scrape.py:56-73`.

**Files:**
- Create: `windows/src/DesktopMcp.Core/Scrape/IWebScraper.cs`, `windows/src/DesktopMcp.Server/Tools/McpSampling.cs`, `windows/tests/DesktopMcp.Server.Tests/Tools/RawMcpClient.cs`
- Modify: `windows/src/DesktopMcp.Core/Scrape/ScrapeClient.cs` (implements `IWebScraper`), `windows/src/DesktopMcp.Server/Tools/ScrapeTool.cs`, `ToolServices.cs` (`IWebScraper Scrape`; `bool SamplingEnabled { get; init; } = true`), `ToolRegistry.cs`, `windows/src/DesktopMcp.Server/Http/HttpServer.cs` (`services with { SamplingEnabled = SessionTtl(plan) != TimeSpan.Zero }`)
- Test: `windows/tests/DesktopMcp.Server.Tests/Tools/ScrapeSamplingTests.cs`, `ScrapeToolTests.cs` (unchanged expectations)

**Interfaces:**
- Consumes: `StdioServer.RunAsync` internal overload with a stream transport (P4 tests' pattern), `ToolServices`.
- Produces:
  - `public interface IWebScraper { Task<string> FetchMarkdownAsync(string url, CancellationToken cancellationToken); }`.
  - `public static class McpSampling`: `const string CreateMessageMethod = "sampling/createMessage"`; `static bool ClientCanSample(McpServer server)` (`"sampling"` present in the client's capabilities serialised with `McpJsonUtilities.DefaultOptions`); `static Task<string?> TrySampleTextAsync(McpServer server, string systemPrompt, string userText, int maxTokens, CancellationToken ct)` (`null` on any failure except cancellation of `ct`).
  - `ScrapeTool(Tool protocolTool, IWebScraper scraper, bool samplingEnabled)`; `public const string ScrapeTool.SystemPrompt = "You are a web content extractor. Given raw webpage content, extract and present only the meaningful information in clean, concise prose or structured format. Strip out navigation menus, cookie banners, ads, footer links, and all other boilerplate. Preserve important data, facts, and structure."` (upstream's text, `scrape.py:62-65`, before `{focus}`).
  - Test helper `RawMcpClient` (JSON-RPC lines over the in-memory pipes: `InitializeAsync(bool sampling)`, `CallToolAsync(name, JsonObject args, CancellationToken)`, `Func<JsonObject, JsonNode?>? OnSampling` (return a `result` object, or `null` to answer with a JSON-RPC error `-32603`), `IReadOnlyList<JsonObject> SamplingRequests`, `SendCancelledAsync(requestId)`).

- [ ] **Step 1: Write the failing tests**

```csharp
public class ScrapeSamplingTests
{
    private const string Url = "https://example.com/page";
    private sealed class FakeScraper : IWebScraper
    {
        public Task<string> FetchMarkdownAsync(string url, CancellationToken ct) => Task.FromResult("# Title\nbody");
    }

    private static JsonObject TextResult(string text) =>
        new() { ["role"] = "assistant", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text }, ["model"] = "m" };

    [Fact]
    public async Task TheSummaryReplacesTheRawContent()
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper() }, sampling: true);
        rig.OnSampling = _ => TextResult("Summary");
        var text = await rig.CallToolTextAsync("Scrape", new JsonObject { ["url"] = Url, ["query"] = "prices" });
        Assert.Equal($"URL: {Url}\nContent:\nSummary", text);
        var request = Assert.Single(rig.SamplingRequests);
        Assert.Equal(ScrapeTool.SystemPrompt + " Focus specifically on: prices.", request["systemPrompt"]!.GetValue<string>());
        Assert.Equal(2048, request["maxTokens"]!.GetValue<int>());
        var message = Assert.Single(request["messages"]!.AsArray())!;
        Assert.Equal("user", message["role"]!.GetValue<string>());
        Assert.Equal($"Raw scraped content from {Url}:\n\n# Title\nbody", message["content"]!["text"]!.GetValue<string>());
        Assert.False(request.ContainsKey("temperature") || request.ContainsKey("modelPreferences") || request.ContainsKey("includeContext"));
    }

    [Fact]
    public async Task WithoutAQueryThePromptHasNoFocus()
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper() }, sampling: true);
        rig.OnSampling = _ => TextResult("S");
        await rig.CallToolTextAsync("Scrape", new JsonObject { ["url"] = Url });
        Assert.Equal(ScrapeTool.SystemPrompt, rig.SamplingRequests.Single()["systemPrompt"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, "true")]     // client cannot sample: no request at all
    [InlineData(true, "false")]     // use_sampling false
    [InlineData(true, "no")]        // loose boolean: anything but true/"true" is false
    public async Task RawContentWithoutASamplingRequest(bool clientSamples, string useSampling)
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper() }, sampling: clientSamples);
        rig.OnSampling = _ => TextResult("never");
        var text = await rig.CallToolTextAsync("Scrape", new JsonObject { ["url"] = Url, ["use_sampling"] = useSampling });
        Assert.Equal($"URL: {Url}\nContent:\n# Title\nbody", text);
        Assert.Empty(rig.SamplingRequests);
    }

    public static TheoryData<JsonObject?> FailedResults => new()
    {
        null,                                                                                   // JSON-RPC error
        new JsonObject { ["role"] = "assistant", ["content"] = new JsonObject { ["type"] = "image", ["data"] = "AA==", ["mimeType"] = "image/png" }, ["model"] = "m" },
        new JsonObject { ["role"] = "assistant", ["model"] = "m" },
    };

    [Theory]
    [MemberData(nameof(FailedResults))]
    public async Task AFailedSamplingFallsBackToTheRawContent(JsonObject? result)
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper() }, sampling: true);
        rig.OnSampling = _ => result?.DeepClone();
        Assert.Equal($"URL: {Url}\nContent:\n# Title\nbody", await rig.CallToolTextAsync("Scrape", new JsonObject { ["url"] = Url }));
    }

    [Fact]
    public async Task AnArrayWithOneTextBlockIsAccepted()
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper() }, sampling: true);
        rig.OnSampling = _ => new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Arr" }), ["model"] = "m" };
        Assert.Equal($"URL: {Url}\nContent:\nArr", await rig.CallToolTextAsync("Scrape", new JsonObject { ["url"] = Url }));
    }

    [Fact]
    public async Task SamplingIsSkippedWhenDisabled()
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper(), SamplingEnabled = false }, sampling: true);
        rig.OnSampling = _ => TextResult("never");
        await rig.CallToolTextAsync("Scrape", new JsonObject { ["url"] = Url });
        Assert.Empty(rig.SamplingRequests);
    }

    [Fact]
    public async Task ACancelledCallStopsWaitingForSampling()
    {
        await using var rig = await RawMcpClient.ServeAsync(ToolServices.CreateDefault() with { Scrape = new FakeScraper() }, sampling: true);
        var asked = new TaskCompletionSource();
        rig.OnSamplingAsync = async (_, ct) => { asked.SetResult(); await Task.Delay(Timeout.Infinite, ct); return null; };   // never answers
        var call = rig.StartToolCall("Scrape", new JsonObject { ["url"] = Url });
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.SendCancelledAsync(call.RequestId);
        await rig.AssertNoResponseOrCancelledWithinAsync(call, TimeSpan.FromSeconds(5));   // the call ends; the server stays responsive
        Assert.Equal("ok", await rig.PingAsync());
    }
}
```

`RawMcpClient.ServeAsync(services, sampling)` starts `StdioServer.RunAsync` over pipes (as `StdioServerTests.ServeAsync`), sends `initialize` with `capabilities` `{"sampling":{}}` when `sampling` is true (else `{}`) and `notifications/initialized`, and answers `sampling/createMessage` requests from `OnSampling`/`OnSamplingAsync`. `AssertNoResponseOrCancelledWithinAsync` accepts either no response for the cancelled id (the MCP rule) or an error response, and fails only if the server stops answering pings. Existing `ScrapeToolTests` keep passing: `ToolCalls.Create("Scrape")` builds the tool with `samplingEnabled: true` and no client, so `use_sampling` falls back to the raw text.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/DesktopMcp.Server.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`ScrapeTool.InvokeCoreAsync` (port of `scrape.py:28-73`): fetch through `IWebScraper`; when `useSampling && samplingEnabled && McpSampling.ClientCanSample(request.Server)`, call `TrySampleTextAsync(request.Server, SystemPrompt + focus, $"Raw scraped content from {url}:\n\n{content}", 2048, cancellationToken)` where `focus = query is { Length: > 0 } ? $" Focus specifically on: {query}." : ""`; a non-null result gives `URL: <url>\nContent:\n<result>`, else the raw text. `McpSampling.TrySampleTextAsync` builds the `JsonRpcRequest` (`Method = CreateMessageMethod`, `Params` per Decisions 5), `await server.SendRequestAsync(request, ct)`, parses `response.Result`; catches every exception except an `OperationCanceledException` while `ct.IsCancellationRequested`. `ScrapeClient : IWebScraper`. `ToolRegistry`: `new ScrapeTool(protocolTool, services.Scrape, services.SamplingEnabled)`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Server.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core/Scrape windows/src/DesktopMcp.Server windows/tests/DesktopMcp.Server.Tests
git commit -m "Summarise Scrape results through client sampling with upstream's prompt" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Secrets out of the PowerShell child's environment

Follow-ups P1 leftover "For P5"; Decisions 20.

**Files:**
- Modify: `windows/src/DesktopMcp.Core/PowerShell/ShellEnvironment.cs`
- Test: `windows/tests/DesktopMcp.Core.Tests/PowerShell/ShellEnvironmentTests.cs` (additions)

**Interfaces:**
- Consumes: `ShellEnvironment.Build(inherited, machine, user, ...)` (P1).
- Produces: `internal static IReadOnlyList<string> ShellEnvironment.SecretVariables` = `["DESKTOP_MCP_AUTH_KEY", "DESKTOP_MCP_OAUTH_CLIENT_SECRET"]`, removed by both `Build` overloads.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void TheServersSecretsNeverReachTheChild()
{
    var inherited = new Dictionary<string, string> { ["desktop_mcp_auth_key"] = "k", ["DESKTOP_MCP_OAUTH_CLIENT_SECRET"] = "s", ["DESKTOP_MCP_DEBUG"] = "1", ["PATH"] = @"C:\x" };
    var machine = new RegistryEnvironment(Variables(), "", "");
    var user = new RegistryEnvironment(Variables(("DESKTOP_MCP_AUTH_KEY", "from-hkcu")), "", "");   // the file's existing Variables helper
    var environment = ShellEnvironment.Build(inherited, machine, user, "PC", "me", @"C:\Users\me");
    Assert.False(environment.ContainsKey("DESKTOP_MCP_AUTH_KEY"));
    Assert.False(environment.ContainsKey("DESKTOP_MCP_OAUTH_CLIENT_SECRET"));
    Assert.Equal("1", environment["DESKTOP_MCP_DEBUG"]);
}

[Fact]
public void SecretsAreRemovedWithoutTheRegistryToo()
{
    var environment = ShellEnvironment.Build(new Dictionary<string, string> { ["DESKTOP_MCP_AUTH_KEY"] = "k" }, null, null, "PC", "me", @"C:\Users\me");
    Assert.False(environment.ContainsKey("DESKTOP_MCP_AUTH_KEY"));
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~ShellEnvironmentTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

At the end of the internal `Build`, remove each `SecretVariables` name (the dictionary is case-insensitive).

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DesktopMcp.Core.Tests --filter "FullyQualifiedName~ShellEnvironmentTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/DesktopMcp.Core/PowerShell windows/tests/DesktopMcp.Core.Tests/PowerShell
git commit -m "Keep the server's auth key and OAuth secret out of PowerShell children" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Release verification and documentation

The P5 checkpoint, as P4 Task 14 (Decisions 6, 1).

**Files:**
- Create: `windows/scripts/smoke-http.ps1`
- Modify: `windows/scripts/build-release.ps1`, `docs/superpowers/specs/2026-10-19-windows-port-design.md`, `docs/superpowers/plans/2026-10-19-windows-p0-followups.md`

**Interfaces:**
- Consumes: every task.
- Produces: the NativeAOT HTTP smoke, the measured P5 results in the spec, the spec without telemetry, and the follow-ups without the closed P5 items.

- [ ] **Step 1: Write `smoke-http.ps1`**

`param([Parameter(Mandatory)][string]$Exe, [string]$Spec = <as smoke-stdio>)`. The header comment says: both sessions bind `127.0.0.1` on port 0 (no firewall rule, nothing reachable from the network), run with `[control] enabled = false` and `DESKTOP_MCP_DISABLE_FLASH=1` from a temp config file, drop every inherited `DESKTOP_MCP_*` variable, never take control, show nothing and inject nothing; the server is stopped with `Stop-Process` (its job closes with it). Helpers: start the exe with redirected stderr, read lines until `desktop-mcp: serving ...` (15 s bound) and take the URL; `Invoke-Mcp` POSTs JSON-RPC with `Accept: application/json, text/event-stream` and reads either a JSON body or the `data:` lines of an SSE body.
Session 1: `serve --transport streamable-http --host 127.0.0.1 --port 0 --config <cfg>` with `DESKTOP_MCP_AUTH_KEY=<32 random bytes, base64url>`:
1. `GET /health` without auth → 200 `{"status":"ok"}`.
2. `POST /mcp` initialize without `Authorization` → 401, the missing-header body, `WWW-Authenticate: Bearer`.
3. initialize with the bearer → 200, an `Mcp-Session-Id`, `serverInfo.name` `desktop-mcp`; then `notifications/initialized` → 202.
4. `tools/list` → equals the spec (the same JSON comparison as `smoke-stdio.ps1`'s `Assert-ToolsMatchSpec`, moved to a shared `smoke-common.ps1` dot-sourced by both, or copied).
5. `ControlStatus` → the disabled text of P4's smoke; `Wait {duration: 0}` → not isError.
6. initialize with `Origin: https://attacker.example` → 403; with `Host: attacker.example` → 400 `Invalid host header`.
7. `DELETE /mcp` with the session id → 2xx; a `tools/list` with that id → 404.
8. The graphics stack (d2d1, d3d11, dwrite, d3dcompiler_47) is not loaded in the server process.
Session 2: a `TestCertificate`-equivalent created in the script with `[System.Security.Cryptography.X509Certificates.CertificateRequest]` (ECDSA P-256, SAN localhost/127.0.0.1/::1) and written as PEM to a temp folder (no certificate store), then `serve --transport sse --host 127.0.0.1 --port 0 --ssl-certfile ... --ssl-keyfile ...`:
1. The announcement starts with `https://` and ends with `/sse`.
2. `GET /health` over HTTP/2 (`HttpClient` with `RequestVersionExact` 2.0, the callback pinned to the certificate's thumbprint) → 200 and `response.Version` 2.0.
3. `GET /sse` → the first event is `endpoint` with data starting `/message?sessionId=`; `POST` initialize to that path → 202, and the `initialize` result arrives on the open stream within 10 s.
Final line: `HTTP smoke OK: streamable-http (auth, session, 21 tools, origin and host checks, delete), sse over TLS (HTTP/2, endpoint event, initialize)`. The temp folders are removed in `finally`.

- [ ] **Step 2: Run it from `build-release.ps1`**

After `smoke-stdio.ps1`, `& (Join-Path $PSScriptRoot 'smoke-http.ps1') -Exe <x64 exe>`; a throw fails the build.

- [ ] **Step 3: Run the full default suite**

Run: `dotnet test DesktopMcp.slnx`
Expected: `Passed!` with `Failed: 0` for each project, three runs in a row (the HTTP tests bind real sockets). Record the counts.

- [ ] **Step 4: Publish both RIDs and run the checks**

Run (interactive session): `pwsh scripts/build-release.ps1`
Expected: no publish warnings; each dist folder holds only the exe and its pdb; `smoke OK` (stdio, with the control session); `HTTP smoke OK: ...`; the AOT activation, UIA and overlay checks OK. Record both exe sizes (expect growth from Kestrel; record it) and whether the HTTP sessions loaded any graphics DLL.

- [ ] **Step 5: Update the spec**

In `docs/superpowers/specs/2026-10-19-windows-port-design.md`:
1. §1: the parity sentence drops "and telemetry" and gains `Telemetry is not implemented (user decision, 2026-10-21).`; the naming sentence becomes `ANONYMIZED_TELEMETRY, POSTHOG_API_KEY and POSTHOG_HOST are ignored: the Windows port has no telemetry.`
2. §3: `DesktopMcp.Core/` comment `Security, OAuth, Analytics` → `Security (request guards, OAuth, TLS)`; `DesktopMcp.Server/` comment adds `Http/ (Kestrel host, pipeline, OAuth endpoints)`.
3. §8 Transports: streamable-http `SessionMode = Stateful` (Open question 4's answer) or `Stateless`; sse through the AppContext switch (P5 plan, Decisions 4; Open question 3's answer), `/sse` and `/message`, the Streamable endpoints not mapped; the announcement line (Decisions 8); bind targets (Decisions 7).
4. §8 HTTP pipeline: insert the Origin step (Open question 1's answer, Decisions 9) after the trusted host; the trusted-host IPv6 fix (Decisions 10); every 401 carries `WWW-Authenticate: Bearer` (Decisions 13); `X-Forwarded-For` ignored and mapped IPv4 (Decisions 15); TLS: no h2c, key container note (Decisions 11); OAuth deviations (Decisions 12).
5. §8 Scrape: replace the P1 sentence with `use_sampling sends sampling/createMessage as raw JSON (MCP9005 marks the SDK's sampling API obsolete) with upstream's system prompt and maxTokens 2048, and returns the raw content when the client did not declare sampling, the transport is stateless Streamable HTTP, or sampling fails (P5 plan, Decisions 5).`
6. §8 Telemetry: replace the section's text with `Not implemented in the Windows port (user decision, 2026-10-21; P5 plan, Decisions 1). There is no analytics code; ANONYMIZED_TELEMETRY, POSTHOG_API_KEY and POSTHOG_HOST are ignored. Upstream sends PostHog events by default with its own key.`
7. §9: Server row `in-memory Kestrel tests` → `Kestrel on 127.0.0.1 port 0 (P5 plan, Decisions 14): middleware order, 401/403 bodies, CORS, OPTIONS, trusted host, Origin, /health, OAuth, TLS and HTTP/2, both transports, notifications to HTTP sessions, sampling over in-memory stdio`; AOT smoke row adds `smoke-http.ps1`; add `P5 results (<date>, Windows build <build>): default suite <n> (Core <a>, Server <b>, Desktop non-desktop <c>); build-release: win-x64 <size> bytes, win-arm64 <size> bytes, each dist folder holding only the exe and its pdb; stdio smoke OK; HTTP smoke OK under NativeAOT (<details>).`
8. §10: Bug fixes: replace `telemetry events are sent when enabled (opt-in, see Telemetry)` with `/health` answers `{"status":"ok"}` (keep the existing `/health exists`), `the trusted-host check accepts [::1]`, `OAuth-only 401s carry WWW-Authenticate`, `invalid OAuth JSON is 400, not 500`, `a failed PKCE check burns the code`; add a `- **Telemetry**: none (user decision, 2026-10-21); the upstream variables are ignored.` bullet; Protocol: the Origin check, no h2c, `X-Forwarded-For` ignored, the issuer uses the bound port and, on a wildcard bind, the request's host; add `- **P5 decisions**: P5's departures from upstream for HTTP (Origin check, trusted-host parsing, OAuth hardening, sampling as raw JSON, the PowerShell child's secrets, no telemetry) are listed in the P5 plan's "Decisions on upstream ambiguities" (docs/superpowers/plans/2026-10-21-windows-p5-http.md).`
9. §11 P5 row: Scope `Kestrel, Streamable HTTP and SSE, middleware, TLS, OAuth, /health, Scrape sampling (telemetry dropped: user decision, 2026-10-21)`; Outcome `Remote use (done; results in §9)`.
10. §12: the "MCP SDK's serialisation or legacy SSE support changes" row notes MCP9004/9005/9006 and the AppContext switch.

- [ ] **Step 6: Update the follow-ups**

Replace the P5 section with `All P5 items are done (see docs/superpowers/plans/2026-10-21-windows-p5-http.md), except:` and: `Telemetry: not implemented in the Windows port (user decision, 2026-10-21); the variables are ignored.`; move the JSONL audit log line to the P6 section (Open question 2's answer); add `## P5 leftovers` for minor review findings; strike the P1 leftovers' `For P5: strip DESKTOP_MCP_* secrets ...` clause (done, Task 9). Add to the P6 section: `auth` prints `/mcp/` URLs (still served, Decisions 4) and should not default to `0.0.0.0` in the printed client URL (upstream bug); the macOS port's `connect` bridge as a possible addition.

- [ ] **Step 7: List the by-hand checks for the user**

Add to the follow-ups' P5 section (none are required for P5 to pass; they need another machine or a client the default suite does not have):
- From another machine on the LAN: `desktop-mcp serve --transport streamable-http --host 0.0.0.0 --auth-key <key>` [Windows Firewall asks once; with no auth key the server refuses to start with upstream's message]; an MCP client with the bearer header lists 21 tools; without it, 401.
- MCP Inspector over Streamable HTTP and over SSE (`npx @modelcontextprotocol/inspector`, URL `http://localhost:8000/mcp` or `/sse`) [connects; `ControlStatus` works; the `desktop-mcp.control` notifications appear under Server Notifications].
- A client that supports sampling (for example Claude Desktop through a bridge, or the Inspector's sampling tab): `Scrape {url: 'https://example.com'}` [the client asks to approve a sampling request; the result is the summary].
- OAuth with a real client configured with `--oauth-client-id/--oauth-client-secret` [the code flow completes on a loopback redirect].

- [ ] **Step 8: Commit**

```bash
git add windows/scripts docs/superpowers/specs/2026-10-19-windows-port-design.md docs/superpowers/plans/2026-10-19-windows-p0-followups.md
git commit -m "Smoke-test HTTP and HTTPS under NativeAOT and record the P5 results" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Self-review

**Spec coverage (§11 P5 row, §8, §7 Sessions, §10, the follow-ups and the controller's decisions):**
- Kestrel via `CreateSlimBuilder`, no ambient configuration: Task 1. Streamable HTTP stateful/stateless at `/mcp`: Task 1. Legacy SSE at `/sse` + `/message`, only the selected transport mapped: Task 2.
- Middleware order, OPTIONS, trusted host, CORS, IP allowlist (mapped IPv4), auth bodies and `WWW-Authenticate`, public paths: Tasks 3, 4, 6. Origin check: Tasks 3, 4 (Open question 1).
- `/health`: Task 4. TLS from PEM via PKCS#12, ALPN h2/http1.1: Task 7. OAuth metadata, register, authorize, token, tokens in auth: Tasks 5, 6.
- Non-loopback bind refusal and allowed hosts: already in `ServePlan` (P1); exercised end to end by Task 4's tests and Task 10's by-hand LAN check.
- Notifier TTL 60 s / zero for stateless: Task 2. Scrape `use_sampling`: Task 8. Telemetry: removed (Decisions 1), documented in Task 10.
- Follow-ups P5: sampling (Task 8), mapped allowlist (Tasks 3, 4), audit log (Open question 2, moved), telemetry (dropped); P1 leftover secrets (Task 9).
- Single exe and AOT: Task 1 Step 6, Task 10 Step 4; smoke never takes control: Task 10 Step 1.

**Step scan:** every test step names its tests with exact strings; code steps give signatures, files and the upstream lines or Decisions their behaviour follows. Task 2's conditional switch check was dropped: the preflight (F6) found the switch is read per options instance.

**Type consistency:** `HttpRig` (Task 1: `StartAsync`, `Plan`, `InitializeRequest`, `Endpoint`, `Base`, `Http`, `Announced`, `ConnectAsync`; Task 2 adds `ReadSseEndpointEventAsync`, `OnLogMessage`, `HostIsRunning`; Task 4 `SendAsync`, `SendInitializeAsync`; Task 6 `OAuthTokenAsync`; Task 7 `PinnedClient` and `ConnectAsync(http:)`); `HttpServer.RunAsync/EndpointPath/SessionTtl/Issuer` (Tasks 1, 2, 6); `HttpGuards`, `SecurityJson` (Task 3) used by Tasks 4, 5, 6; `OAuthServer`, `OAuthResult` (Task 5) used by Task 6; `HttpPipeline.Use(app, plan, validateOAuthToken)` (Task 4) extended in Task 6; `TlsCertificate.Load` (Task 7); `IWebScraper`, `ToolServices.SamplingEnabled`, `McpSampling`, `ScrapeTool.SystemPrompt` (Task 8) and `HttpServer` setting `SamplingEnabled` from `SessionTtl`.

**Review Focus:** each of the five lines has its named tests in Tasks 1, 2, 3, 4, 7 and 8.

**Proportion:** the plan holds signatures, exact strings and tests; Kestrel wiring, middleware bodies, the raw JSON-RPC test client and the smoke script are left to the implementer, guided by the cited upstream lines and the Decisions.
