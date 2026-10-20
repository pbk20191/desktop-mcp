# P5 HTTP plan: pre-flight conflict scan

Plan: `docs/superpowers/plans/2026-10-21-windows-p5-http.md` (10 tasks). Spec: `docs/superpowers/specs/2026-10-19-windows-port-design.md`. Code: `windows/` on `windows-p5` (889435d). Read-only scan, 2026-10-21.
User decisions applied: no telemetry; open questions 1-4 take the plan's recommendations.

SDK evidence comes from the NuGet cache `E:\packages\NuGet\cache` (`modelcontextprotocol.aspnetcore/2.2.0`, `modelcontextprotocol.core/2.2.0`): the xml docs plus a metadata/IL scan done with System.Reflection.Metadata (nothing was loaded or executed).

## 1. Cross-task interfaces (produced vs consumed)

| # | Producer → consumer | Interface (exact) | Status |
|---|---|---|---|
| P1 | T1 → T2 | `HttpServer.RunAsync(plan, services, ct, listening?, announce?)`, `EndpointPath`, `SessionTtl` (T1 stub 60 s, T2 fills it); `HttpRig.StartAsync/ConnectAsync(mode, headers?, options?)/Base/Endpoint/Http/Announced`; T2 adds `ReadSseEndpointEventAsync`, `OnLogMessage`, `HostIsRunning`, `LegacySseSwitch` | match |
| P2 | T1 (McpServerSetup remember) → T2 (notifications) | `ControlGateFilter.Create(gate, s => sessions.Remember(s, new McpServerSession(s)))` keyed by `request.Server` | **mismatch with spec §7 "each SDK session"**: see F1 |
| P3 | T3 → T4 | `HttpGuards.IsTrustedHost(string?, IReadOnlyList<string>?)` ↔ `ServePlan.AllowedHosts` (`IReadOnlyList<string>?`); `IsAllowedOrigin(string?, IReadOnlyList<string>)` ↔ `plan.CorsOrigins`; `AllowlistRejection(IPAddress?, IReadOnlyList<IpNetwork>)` ↔ `plan.IpAllowlist`; `IsPublicPath`, `FixedTimeEquals`, `SecurityJson.Error/Health` | match (types checked against `ServePlan.cs`, `IpNetwork.cs`, `IpAddressText.cs`) |
| P4 | T3 → T5 | `HttpGuards.FixedTimeEquals`, `SecurityJson.OAuthError`; T5 "modifies SecurityJson (metadata, token response, register body)" | **partial**: T5 names no new `SecurityJson` members (file map says `OAuthMetadata`, `TokenResponse`); F10 |
| P5 | T4 → T6 | `HttpPipeline.Use(IApplicationBuilder, ServePlan, Func<string,bool>? validateOAuthToken)` ← `oauth.ValidateToken` | match |
| P6 | T5 → T6 | `OAuthServer(string, string, TimeProvider, Func<int,string>? = null)`; `Metadata(string)`, `Register()`, `Authorize(IReadOnlyDictionary<string,string>)`, `Token(IReadOnlyDictionary<string,string>)`, `ValidateToken(string)`; `OAuthResult(int Status, string? Json, string? Location)` ↔ `OAuthEndpoints.ReadFieldsAsync` → `IReadOnlyDictionary<string,string>?` | match |
| P7 | T1 → T2, T6 | `HttpRig.Plan(ServeArguments)`, `HttpRig.InitializeRequest(Uri)` used by T1/T2/T6 tests; not in T1 Produces. T6 `TheIssuerFollowsDecisions12` needs `Plan` **not** to force host/port (rows 0.0.0.0, ::, localhost) | **undefined**: F7 |
| P8 | T1 → T7 | Kestrel listeners `Http1` (T1) → `UseHttps` + `Http1AndHttp2` (T7); `HttpRig.ConnectAsync(..., HttpClient? http)` added in T7; `HttpRig.PinnedClient(thumbprint, Version?)`; `rig.Base` scheme from the announcement | match |
| P9 | T7 Core ↔ T7 Server tests | `TestCertificate.Create(string commonName = "localhost")` vs test call `TestCertificate.Create(encryptKey: true)` | **mismatch**: F8 |
| P10 | T2 → T8 | `services with { SamplingEnabled = SessionTtl(plan) != TimeSpan.Zero }`; `ToolServices.SamplingEnabled` (init, default true) | match |
| P11 | T1 → T8 | `StdioServer.RunAsync(plan, services, Action<IMcpServerBuilder>, ct, DesktopLogBridge?)` kept as internal overload; `RawMcpClient` serves over it | match (signature unchanged in T1) |
| P12 | T8 internal | `IWebScraper.FetchMarkdownAsync(string, CancellationToken)` = `ScrapeClient.FetchMarkdownAsync` (existing signature); `ToolServices.Scrape` `ScrapeClient` → `IWebScraper`; `ToolRegistry` `new ScrapeTool(protocolTool, services.Scrape, services.SamplingEnabled)` | match (only `ToolRegistry.cs:25` constructs ScrapeTool) |
| P13 | T1, T2, T4, T7 → T10 smoke | announcement `desktop-mcp: serving <t> on <url>`, `/health`, 401 body + `WWW-Authenticate`, Origin 403, Host 400, `DELETE /mcp` → later 404, SSE `endpoint` event, TLS + HTTP/2 | match; PowerShell feasibility of the pinned HTTP/2 client: F11 |
| P14 | T9 → T10 | `ShellEnvironment.SecretVariables`; follow-ups line struck | match |

## 2. Per-task self-consistency

| Task | Tests vs code | Files created vs later touched | Interfaces listed vs used | Status |
|---|---|---|---|---|
| T1 | `ListenTargetTests` match Decisions 7; `HttpTransportsAreServed` replaces `HttpTransportsArriveInP5` (exists, `CliAppTests.cs:125`); the stateless GET 405 comes from routing (POST-only endpoint) | creates `McpServerSetup`, `ServeCommand`, `HttpServer`, `ListenTarget`; HttpServer later touched by T2, T4, T6, T7, T8 (all list it under Modify) | `HttpRig.Plan`/`InitializeRequest` used, not listed (F7). `McpServerSetup.Add` covers only registrations; StdioServer's post-build wiring (warnings, `Dispatcher.Report`, `DesktopLogBridge.Attach`) has no HTTP counterpart (F4). `ListenLocalhost(0)` throws (F2). Step 6 publishes into the repo root (F12) | issues F2, F4, F7, F12 |
| T2 | the TTL theory fails before Step 3 as stated; notification tests can race the client's GET stream (F5); the "switch read once" stop condition does not arise (F6) | modifies only HttpServer | `HttpRig.OnLogMessage` must key `McpLogging.MessageMethod`, not the obsolete `NotificationMethods.LoggingMessageNotification` (F16) | issues F1, F5, F6 |
| T3 | every table row follows the stated algorithm (`127.0.0.2` is false because the list is compared, not `IsLoopbackHost`; `http://[::1]:3000` `Uri.Host` is `[::1]`, brackets trimmed) | creates `HttpGuards`, `SecurityJson`; T5 modifies `SecurityJson` | match | ok |
| T4 | step order (OPTIONS, host, Origin, CORS, allowlist, auth) matches the tests; `/health` is exempt from the allowlist and auth | creates `HttpPipeline`; T6 modifies it | `HttpRig.SendAsync`/`SendInitializeAsync` described under the tests | ok |
| T5 | token sequence `tok1`/`tok2`, burn rules and the 1000-code cap agree with the tests; `StepClock` (Core.Tests/Control) fakes only `GetTimestamp`, as required | modifies `SecurityJson` | new `SecurityJson` members unnamed (F10) | minor |
| T6 | `text/plain` → empty fields → `unsupported_grant_type`; `[1]` → `null` → `invalid_request` | creates `OAuthEndpoints`; modifies HttpServer, HttpPipeline | `HttpServer.Issuer(plan, boundPort, context)` listed and used | ok (depends on F7) |
| T7 | `AnEncryptedKeyIsAConfigError` uses `encryptKey:` that Produces does not declare (F8); `ABadCertificateStopsServeWithOneError` runs with `CancellationToken.None` (F14) | creates two `TestCertificate` copies (stated) | `ConnectAsync(http:)` declared in prose | issues F8, F14 |
| T8 | tests use `RawMcpClient.ServeAsync`, `CallToolTextAsync`, `OnSamplingAsync`, `StartToolCall(...).RequestId`, `PingAsync`, `AssertNoResponseOrCancelledWithinAsync`; Produces lists `InitializeAsync`, `CallToolAsync` (F9). `ToolCalls` builds a root `McpServer` whose `ClientCapabilities` is null, so `ScrapeToolTests` stay raw | creates `IWebScraper`, `McpSampling`, `RawMcpClient` | `SamplingRequests` holds the request **params** (tests index `systemPrompt`) | issue F9 |
| T9 | `Build` copies into an `OrdinalIgnoreCase` dictionary (`ShellEnvironment.cs:42`), so `desktop_mcp_auth_key` is removed; `Variables` helper exists (`ShellEnvironmentTests.cs:7`) | modifies only ShellEnvironment | match | ok |
| T10 | smoke steps match the produced behaviour; PS scriptblock certificate callback is not runnable off-runspace (F11) | creates `smoke-http.ps1` (optional `smoke-common.ps1` covered by `git add windows/scripts`) | consumes every task | issue F11 |

## 3. Findings

### Defects a reviewer would raise

- **F1 (high): the session registry key changes on every call.** `ControlGateFilter` remembers `request.Server` (`ControlGate.cs:136`). In ModelContextProtocol.Core 2.2.0, `McpServerImpl.InvokeHandlerAsync` calls `CreateDestinationBoundServer`, which constructs a new `DestinationBoundMcpServer` for every request. That type has no `Equals`/`GetHashCode` override. So each tool call adds a new registry entry. An HTTP session with N calls inside the 60 s TTL gets N copies of every notification, and stdio's registry (TTL `null`) grows by one entry per call for the process's life. This bug was already in P4. P5 Decisions 18 builds on it ("the registry key is the call's McpServer"), and spec §7 says each *session* is recorded. Task 2 tests make one call per session, so they would not catch it. The sends themselves work: once the POST's final response has gone, a destination-bound send falls through to the parent transport (`StreamableHttpPostTransport.SendMessageAsync` → `StreamableHttpServerTransport.SendMessageAsync`).
  **Ruling:** in `McpServerSetup.Add`, remember under `server.SessionId ?? StdioSessionKey` (a static sentinel object), with the latest `McpServerSession` as the value. `Remember` already overwrites by key, and `Forget(key, session)` already checks the reference. Add to Task 2 a test that makes two calls, then one state change, and expects exactly one `notifications/message`. Reword Decisions 18 to match.
- **F2 (medium): `ListenLocalhost(0)` throws.** Kestrel refuses a dynamic port on localhost ("Dynamic port binding is not supported when binding to localhost. You must either bind to 127.0.0.1:0 or [::1]:0, or both"; `InvalidOperationException`, not `IOException`). The default host is `localhost` (`DesktopMcpConfig.cs:16`), so `serve --transport streamable-http --port 0` would crash instead of following Decisions 7 ("Port 0 binds an ephemeral port") or printing one `Error:` line.
  **Ruling:** Decisions 7: `Localhost` with port 0 binds `127.0.0.1:0` only, and the announcement still names `localhost`. Add one `HttpServerTests` row with `Host = "localhost", Port = 0`. Alternatively, reject it in `ListenTarget.Resolve` with a `ConfigException`. Pick one.
- **F3 (medium): the environment name is read before `Sources.Clear()`.** `CreateSlimBuilder` fixes `EnvironmentName` from `ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` while it is being built. In `Development`, `WebApplication` adds the developer exception page, which sends stack traces to HTTP clients. This contradicts the Global Constraint "the host never reads ambient configuration".
  **Ruling:** add `EnvironmentName = Environments.Production` (and a fixed `ApplicationName`) to the `WebApplicationOptions` in the Global Constraints and in Task 1 Step 4.
- **F4 (medium): HttpServer lacks StdioServer's post-build wiring.** `StdioServer.RunAsync` logs `plan.Warnings` (which carries the Job Object warning that ServeCommand now adds), sets `services.Dispatcher.Report`, and calls `desktopLog?.Attach(logger)`. `McpServerSetup.Add` covers only the registrations, and Task 1 Step 4 for `HttpServer` mentions none of the three. Over HTTP, the job warning, dispatcher faults and desktop log lines would be lost.
  **Ruling:** add `McpServerSetup.AfterBuild(IServiceProvider, ServePlan, ToolServices, DesktopLogBridge?)`, used by both hosts. Production `HttpServer.RunAsync` creates a `DesktopLogBridge` as stdio does.
- **F5 (medium): the notification tests can be flaky, and the Decisions 18 wording overclaims.** When the client's standalone GET stream is not open yet, `StreamableHttpServerTransport.SendMessageAsync` drops an unsolicited notification and only logs it (`LogNotificationDroppedNoGetStream`); it does not throw. The SDK client opens the GET stream asynchronously after `initialize`, and `AnHttpClientReceivesUserControlNotifications` / `ADeletedSessionIsForgotten...` raise only once. The same silent drop also means a deleted session is not guaranteed to "fail its next send and be forgotten"; the 60 s TTL is what actually bounds it.
  **Ruling:** in those tests, alternate `User`/`Ready` with rising generations every 250 ms until a `USER_CONTROL` is heard (5 s bound). Reword Decisions 18 and Review Focus 3 to say "forgotten on a failed send or after the TTL".

### Minor plan-consistency rulings

- **F7 (low):** `HttpRig.Plan` and `HttpRig.InitializeRequest` are undeclared.
  **Ruling:** add to Task 1 Produces `static ServePlan Plan(ServeArguments a, DesktopMcpConfig? c = null) => ServePlan.Resolve(a, c ?? new())`, with no host or port forcing (only `StartAsync` forces). The default config is `new DesktopMcpConfig()`, never `~/.desktop-mcp/config.toml`.
- **F8 (low):** **Ruling:** `TestCertificate.Create(string commonName = "localhost", bool encryptKey = false)` in both copies.
- **F9 (low):** **Ruling:** list the `RawMcpClient` members the tests use (`ServeAsync`, `CallToolTextAsync`, `OnSamplingAsync`, `StartToolCall` → `{RequestId}`, `PingAsync`, `AssertNoResponseOrCancelledWithinAsync`, `DisposeAsync`). `SamplingRequests` holds the request params. Pin `protocolVersion` to `"2025-06-18"` in its `initialize`; 2026-07-28 has no `initialize`, and the SDK would route server requests via MRTR.
- **F10 (low):** **Ruling:** name Task 5's additions, `SecurityJson.Metadata(string issuer)` and `SecurityJson.Token(string accessToken)` (or state that `OAuthServer` writes them with `Utf8JsonWriter`). `Register` uses `OAuthError`.
- **F11 (low):** a PowerShell scriptblock used as `RemoteCertificateValidationCallback` runs on a thread-pool thread and fails with "There is no Runspace available". `Invoke-WebRequest -Headers @{Host=...}` is also unreliable for overriding Host.
  **Ruling:** the smoke compiles a tiny C# thumbprint validator with `Add-Type`, or uses `Invoke-WebRequest -HttpVersion 2.0 -SkipCertificateCheck` (loopback only, certificate just created). It sends the Host/Origin probes through `System.Net.Http.HttpClient` with `TryAddWithoutValidation`.
- **F12 (low):** Task 1 Step 6 publishes to `../scratch-p5-publish`, which is the repository root, inside the work tree.
  **Ruling:** publish to `$env:TEMP\desktop-mcp-p5-publish` (or the scratchpad).
- **F13 (low, safety note):** the default-flag `LoadPkcs12` writes the private key to the user's CNG key folder (`%APPDATA%\Microsoft\Crypto\Keys`) until the certificate is disposed. No certificate store is opened, so the Global Constraint holds, but a crashed test leaves a key file behind.
  **Ruling:** accept. Decisions 11 says so explicitly, and tests dispose with `using` (already written).
- **F14 (low):** **Ruling:** `ABadCertificateStopsServeWithOneError` uses a 10 s `CancellationTokenSource`, as `APortInUseExitsWithAnError` does.
- **F15 (low, spec text):** spec §8 CORS lists `Content-Type, Authorization, Mcp-Session-Id`; Decisions 16 sends `content-type, authorization, mcp-session-id`. Header values are case-insensitive, so behaviour is the same.
  **Ruling:** Task 10 Step 5.4 writes the lower-case form into the spec.

### Contradictions with the spec or Global Constraints

- None beyond F1 (spec §7 "each SDK session") and F3 (the "never reads ambient configuration" constraint).
- These spec deviations are intended, and Task 10 rewrites them: Origin step (Q1), SSE via the switch rather than the `EnableLegacySse` property (§8 Transports), `SessionMode` instead of `Stateless = false`, raw-JSON sampling instead of `SampleAsync` (§8 Scrape), real Kestrel rather than in-memory tests (§9), telemetry removed (user decision).

### Existing-code references (every "Consumes" item checked)

| Item | Real code | Status |
|---|---|---|
| `StdioServer.RunAsync(ServePlan, CancellationToken)` (public, does NO_COLOR + job) and internal `(plan, services, Action<IMcpServerBuilder>, ct, DesktopLogBridge?)` | `Hosting/StdioServer.cs:18,37` | matches; the job moves to ServeCommand as planned (F4 for the remainder) |
| `ToolServices` record, `CreateDefault()`, `Create(ServePlan, DesktopLog?)`, `Scrape` is `ScrapeClient`, `Time`, `Control`, `Ledger`, `Dispatcher` | `Tools/ToolServices.cs` | matches; `Create(plan, desktopLog.Write)` is valid |
| `SessionRegistry(TimeProvider, TimeSpan?)`, `Remember/Live/Forget` | `Control/SessionRegistry.cs` | matches |
| `ControlGate(control, time)`, `ControlGateFilter.Create(gate, Action<McpServer>)` | `Control/ControlGate.cs:127-140` | matches (F1) |
| `ControlNotifier` forgets on a send exception | `Control/ControlNotifier.cs:146-153` | matches (F5: HTTP drops do not throw) |
| `ControlNotifierHostedService`, `ControlHostedService`, `StderrLoggerProvider`, `DesktopLogBridge` | `StdioServer.cs`, `Hosting/*` | exist (internal) |
| `McpLogging.DeclareLogging`, `MessageMethod`, `SendMessageAsync` | `Control/McpLogging.cs` | matches |
| `ServePlan` (`Transport`, `Host`, `Port:int`, `AuthKey`, `StatelessHttp`, `IpAllowlist:IReadOnlyList<IpNetwork>`, `CorsOrigins`, `SslCertfile/Keyfile`, `OAuthClientId/Secret`, `AllowedHosts`, `Warnings`, `Debug`, `Scheme`) | `Config/ServePlan.cs` | matches; non-loopback refusal and allowed hosts already there |
| `ServeArguments` (`Port:long?`, `StatelessHttp:bool?`, `AllowInsecureRemote:bool?`, …) | `Config/ServeArguments.cs` | matches the test initialisers |
| `ConfigFile.Transports` includes `sse`, `streamable-http` | `Config/ConfigFile.cs:14` | matches |
| CLI P5 branch | `Cli/CliApp.cs:108-111`; `CliEnvironment.Serve = StdioServer.RunAsync` | matches the plan's edit |
| `IpAddressText.TryParse` (rejects brackets), `IsLoopbackHost` (`localhost` or a loopback IP, mapped IPv4 handled) | `Net/IpAddressText.cs` | matches (callers strip brackets as planned) |
| `IpNetwork.ParseAllowlist`, `Contains` | `Config/IpNetwork.cs` | matches |
| `ScrapeTool(Tool, ScrapeClient)`, `use_sampling` parsed with `LooseBool` (default true) | `Tools/ScrapeTool.cs`; `Coercion.LooseBool` (true or `"true"` case-insensitive) | matches; the `"no"` row is false |
| `ProcessJob.TryAssignKillOnClose()` | `PowerShell/ProcessJob.cs:17` | matches |
| `ShellEnvironment.Build(inherited, machine, user, computer, user, profile)` internal; `RegistryEnvironment` | `PowerShell/ShellEnvironment.cs:8,39` | matches |
| `FakeControl.RaiseStateChanged`, `ControlStatus(State, Generation, Wait, Enabled, Policy, Idle)` | test `Control/FakeControl.cs`; `Core/Control/ControlStatus.cs:24` | matches |
| `StepClock` (`GetTimestamp` only, `Advance(double)`) | `Core.Tests/Control/StepClock.cs` | matches (needs a `using` in Security tests) |
| 21 tools | `spec/windows/tools.json` has 21 | matches |

### SDK feasibility (ModelContextProtocol.AspNetCore 2.2.0, verified in the cache)

| Claim | Evidence | Status |
|---|---|---|
| Only dependency `ModelContextProtocol [2.2.0]` + `Microsoft.AspNetCore.App` framework | nuspec | true |
| `IsTrimmable`/`IsAotCompatible` | `AssemblyMetadata` strings in net10.0 dll; no `RequiresUnreferencedCode`/`RequiresDynamicCode` strings | true |
| `WithHttpTransport(Action<HttpServerTransportOptions>)`, `MapMcp(IEndpointRouteBuilder, string)`, `HttpServerTransportOptions.SessionMode`, `HttpServerSessionMode.{Stateless,Stateful,StatefulForInitializeClients}` | xml docs | exist; `SessionMode` and the enum fields are **not** obsolete |
| Stateful refuses 2026-07-28 with -32022 (Q4) | xml `HttpServerSessionMode.Stateful` | true |
| Switch name `ModelContextProtocol.AspNetCore.EnableLegacySse` | xml remarks on `EnableLegacySse`; string in the dll | true |
| Switch read once per process? | IL: `HttpServerTransportOptions..ctor` calls `AppContext.TryGetSwitch` and stores the result in the `EnableLegacySse` backing field, **once per options instance** (one per host container). `MapMcp` resolves the options via `StreamableHttpHandler` and reads `EnableLegacySse` | **not process-wide.** Set the switch and call `MapMcp` under the lock; that is enough (F6: drop T2's stop-and-report branch, keep `HostsAfterAnSseHostMapNoSse`) |
| SSE + Stateless throws at `MapMcp` | dll message "Legacy SSE endpoints cannot be enabled in stateless mode…" | the plan forces Stateful for `sse`: ok |
| Legacy endpoints `/sse`, `/message` under the `MapMcp` pattern | xml `MapMcp` remarks | `MapMcp("")` gives `/sse`, `/message`: ok |
| Obsolete APIs to avoid | AspNetCore: `EnableLegacySse` (MCP9004), `IdleTimeout`, `MaxIdleSessionCount`, `EventStreamStore`, `SessionMigrationHandler`, `PerSessionExecutionContext`, `WithDistributedCacheEventStreamStore` (MCP9006). Core: `SampleAsync`, `AsSamplingChatClient`, `CreateMessageRequestParams`, `CreateMessageResult`, `ClientCapabilities.Sampling`, `McpServer.LoggingLevel`, `AsClientLoggerProvider`, and the fields `RequestMethods.SamplingCreateMessage` and `NotificationMethods.LoggingMessageNotification` (MCP9005) | **F16 (low):** the plan avoids all of these. Test helpers must also use `McpLogging.MessageMethod` / `McpSampling.CreateMessageMethod`, not the two obsolete constants |
| `McpSession.SendRequestAsync(JsonRpcRequest, CancellationToken)`, `McpServer.ClientCapabilities` | xml | exist, not obsolete. `ClientCapabilities` is null in stateless mode, so `ClientCanSample` is already false there |
| Sampling over Streamable HTTP goes to the originating POST | `DestinationBoundMcpServer.SendMessageAsync` sets `RelatedTransport` | true |
| `HttpClientTransport(options, HttpClient, ILoggerFactory?, bool)`, `HttpTransportMode.{StreamableHttp,Sse}`, `McpClientHandlers.NotificationHandlers`, `EnableStandaloneGetStream` (default true), `OwnsSession` (DELETE on dispose) | xml | exist |
| NativeAOT pieces | `CreateSlimBuilder` + explicit `UseHttps(X509Certificate2)` works without `UseKestrelHttpsConfiguration` (only config-bound HTTPS is excluded); `MapMcp` maps `RequestDelegate`s; `MapGet("/health", HttpPipeline.Health)` binds the `RequestDelegate` overload for a `Task(HttpContext)` method group | ok |

### Safety

| Check | Result |
|---|---|
| Test binds | every rig on `127.0.0.1:0`; `AnIPv6LoopbackClientPassesTheChecks` on `[::1]:0` (allowed); `APortInUse` holds an ephemeral loopback port from `TcpListener(Loopback, 0)`. No `0.0.0.0`/`::` binds and no firewall rule; the LAN check is by hand only |
| Certificate store | none opened. CNG key file is temporary (F13) |
| Outbound calls | none: `ListenTargetTests` inject DNS; Scrape uses `FakeScraper`; the OAuth `redirect_uri` `http://127.0.0.1:9/cb` is never contacted (redirects off) |
| Control | HTTP tests use `ToolServices.CreateDefault()` (`DisabledControl`) or `FakeControl`; the smoke runs `[control] enabled=false` |
