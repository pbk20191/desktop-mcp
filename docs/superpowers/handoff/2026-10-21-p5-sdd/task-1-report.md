# Task 1 report: HTTP host and Streamable HTTP

Status: DONE_WITH_CONCERNS (minor; see Concerns)
Commit: d7472c8 "Serve the tools over Streamable HTTP on Kestrel" (base 889435d, branch windows-p5)

## Implemented
- Package: `ModelContextProtocol.AspNetCore` 2.2.0 (`Directory.Packages.props` with the brief's comment; `<PackageReference>` in `DesktopMcp.Server.csproj`). `dotnet build DesktopMcp.slnx`: 0 warnings, 0 errors.
- `Hosting/McpServerSetup.cs` (internal static):
  - `Add(services, plan, tools, sessions)`: the control hosted service, the notifier hosted service, `AddMcpServer` (info, instructions, `DeclareLogging`), the filtered tools and the gate filter. **F1:** the filter remembers under `session.SessionId ?? StdioSessionKey` (a static sentinel object), with `new McpServerSession(server)` of the latest call as the value.
  - `AfterBuild(provider, plan, tools, desktopLog?)` (**F4**): attaches the `DesktopLogBridge`, logs `plan.Warnings`, sets `Dispatcher.Report`, logs the debug start line, and returns the logger. Both hosts use it.
  - `ReleaseHeldInputOnStop(provider, tools, log)`: registers the release on `ApplicationStopping`. Disposing it unregisters and releases once more (this replaces StdioServer's `finally`).
- `Hosting/ServeCommand.cs`: puts the process in the Job Object (the warning on failure goes into `plan.Warnings`), then runs `StdioServer.RunAsync` or `HttpServer.RunAsync`.
- `Hosting/StdioServer.cs`: the public `RunAsync` keeps `NO_COLOR` and no longer handles the job. The internal overload now has the same signature and uses `McpServerSetup.Add` + `addTransport` + `AfterBuild` + `ReleaseHeldInputOnStop`. Every existing `StdioServerTests` test passes unchanged.
- `Http/ListenTarget.cs`: `Localhost`, `Address(IPAddress Ip)`, `Resolve(host, dns)` as in Decisions 7. The empty host resolves to `::`, brackets are stripped, and a name that resolves to nothing throws `ConfigException("Could not resolve server.host '<host>'.")`.
- `Http/HttpServer.cs`:
  - Public `RunAsync(plan, ct)`: production `ToolServices.Create(plan, desktopLog.Write)` plus a `DesktopLogBridge`.
  - Internal `RunAsync(plan, services, ct, listening?, announce?, desktopLog?)`. `desktopLog` is an extra optional last parameter, added for F4.
  - Builder: `CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production })` (**F3**), then `Configuration.Sources.Clear()`, `ClearProviders` + `StderrLoggerProvider`, and the same log level as stdio.
  - Kestrel: one `Listen`/`ListenLocalhost` per target, `HttpProtocols.Http1`. **F2:** I chose the bind rather than the ConfigException. `Localhost` with port 0 binds `127.0.0.1:0`, and the announcement still names `localhost`.
  - Transport and mapping: `WithHttpTransport` sets `SessionMode` to Stateless only for streamable-http with `--stateless-http`, otherwise Stateful. Then `MapMcp("/mcp")`, or `MapMcp("")` for sse.
  - Startup errors: an `IOException` on start prints `Error: Could not listen on <host>:<port>: <inner message>`, stops the services that did start, and returns 1. A `ConfigException` from `Resolve` prints `Error: <message>` and returns 1.
  - Announcement, from `IServerAddressesFeature`: `desktop-mcp: serving <t> on <scheme>://<host>:<port><path>` (IPv6 host in brackets, `""` → `[::]`). Then `listening(uri)` and `WaitForShutdownAsync(ct)`, return 0.
  - `EndpointPath`: `/sse` or `/mcp`. `SessionTtl` returns 60 s (a stub until Task 2).
  - Outside `--debug`, the `Microsoft.Extensions.Hosting.Internal.Host` log category is raised to Critical. Otherwise its "Hosting failed to start" error and stack trace print next to our one `Error:` line (Review Focus 5).
- CLI: removed the "arrives in phase P5" branch; `CliEnvironment.CreateDefault().Serve = ServeCommand.RunAsync`.
- Plan (docs/superpowers/plans/2026-10-21-windows-p5-http.md):
  - Decisions 18 reworded for F1: the key is the session, plus the stdio sentinel; a session is "forgotten on a failed send or after the TTL".
  - Decisions 7 gets the F2 rule.
  - The Global Constraints `WebApplicationOptions` line gets `EnvironmentName = Environments.Production` (F3).

## Tests
- New: `Http/HttpHostCollection.cs`, `Http/HttpRig.cs`, `Http/ListenTargetTests.cs` (12 cases), `Http/HttpServerTests.cs`. It has the brief's 6 tests plus `LocalhostOnPortZeroBindsTheIPv4Loopback` (F2); `APortInUseExitsWithAnError` additionally asserts a single line on `announce`.
- `HttpRig` details:
  - `Plan(a, c = null) => ServePlan.Resolve(a, c ?? new())` (F7).
  - `StartAsync` forces `Port = 0` and host `127.0.0.1`, unless the arguments name `127.0.0.1` or `::1`.
  - Its `HttpClient` uses `SocketsHttpHandler { UseProxy = false }`; `CreateClient()` is exposed.
  - `ConnectAsync` uses `HttpClientTransport(options, Http, null, ownsHttpClient: false)`.
  - `DisposeAsync` asserts exit code 0.
- `CliAppTests.HttpTransportsAreServed` replaces `HttpTransportsArriveInP5`: `sse`, and `streamable-http --host 127.0.0.1`, both return 0 with the transport served and nothing on stderr.
- `StdioServerTests.TwoCallsAreOneSessionAndHearOneNotification` (F1): two Wait calls, then one `RaiseStateChanged`. It waits for the first notification, then 500 ms plus a round trip, and expects exactly 1.

### RED
- F1 and CLI, with the Http test folder moved out temporarily: `dotnet test tests/DesktopMcp.Server.Tests --filter "FullyQualifiedName~TwoCallsAreOneSession|FullyQualifiedName~HttpTransportsAreServed"`. Result: 3 failed. `TwoCallsAreOneSessionAndHearOneNotification` got Expected 1, Actual 2, which confirms the live P4 duplicate on stdio. `HttpTransportsAreServed` got Expected 0, Actual 1, twice.
- `dotnet build tests/DesktopMcp.Server.Tests`: error CS0234, the namespace `DesktopMcp.Server.Http` is missing.

### GREEN
- Focused run (`Http|ListenTarget|StdioServerTests|CliAppTests`): 50/50 passed. `HttpServerTests`: 7/7 passed. The console showed no host error output after the log filter.
- `dotnet test DesktopMcp.slnx`, clean build: Server 294/294, Desktop 736/736, Core 1033/1033 passed, 0 warnings.
- F16: grep for the obsolete SDK symbols in src/tests found none.
- Publish (F12): `dotnet publish src/DesktopMcp -c Release -r win-x64 -o $env:TEMP\desktop-mcp-p5-publish` gave 0 warnings. The folder held only `desktop-mcp.exe` (25.1 MB) and `desktop-mcp.pdb`, so no `Directory.Build.targets` change was needed. I deleted the folder.

## Files changed
windows/Directory.Packages.props; windows/src/DesktopMcp.Server/DesktopMcp.Server.csproj; .../Cli/CliApp.cs; .../Cli/CliEnvironment.cs; .../Hosting/StdioServer.cs; .../Hosting/McpServerSetup.cs (new); .../Hosting/ServeCommand.cs (new); .../Http/HttpServer.cs (new); .../Http/ListenTarget.cs (new); windows/tests/DesktopMcp.Server.Tests/Cli/CliAppTests.cs; .../Hosting/StdioServerTests.cs; .../Http/{HttpHostCollection,HttpRig,HttpServerTests,ListenTargetTests}.cs (new); docs/superpowers/plans/2026-10-21-windows-p5-http.md.

## Self-review and concerns
1. I did not set a fixed `ApplicationName`, which the preflight F3 text mentioned in parentheses. WebApplication's hosting-startup scan tries `Assembly.Load(ApplicationName)`. A fixed "desktop-mcp" would fail to load under the test host and log errors. The default, the entry assembly name, is already `desktop-mcp` in production.
2. The Host log category is raised to Critical outside `--debug` to keep the single `Error:` line. This also hides other Host-category errors outside `--debug`. Our hosted services log their own failures, and none of them is a BackgroundService.
3. Start order: WebApplication appends `GenericWebHostService` after user-added hosted services at `Build()`, so the control starts before Kestrel accepts requests and stops after it. This comes from the ASP.NET Core source design and has no dedicated test.
4. The F1 stdio test uses a 500 ms settle window to catch a duplicate. A delayed duplicate could in theory arrive later, which would make the test pass falsely; it cannot fail falsely. It reliably failed before the fix.
5. No test covers F3 (setting `ASPNETCORE_ENVIRONMENT` is process-wide) or `ASPNETCORE_URLS`.
6. When a DNS name resolves to several addresses and the port is 0, each listener gets its own ephemeral port and the announcement uses the first. This is an edge case and not reachable from tests.
7. `SessionTtl(plan)` ignores `plan` until Task 2.
