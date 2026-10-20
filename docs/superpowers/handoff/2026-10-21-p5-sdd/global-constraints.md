## Global Constraints

- TFM `net10.0-windows10.0.22621.0`, `SupportedOSPlatformVersion` **10.0.22621.0**; RIDs `win-x64` and `win-arm64`.
- `TreatWarningsAsErrors`, `IsAotCompatible`, no warning suppression (no `#pragma warning disable`, no `NoWarn`, no `[SuppressMessage]`), in product and test code. Obsolete SDK APIs (MCP9004 legacy SSE, MCP9005 sampling and logging, MCP9006 stateful-session options) are not called; Decisions 4, 5 and 13 say what replaces them.
- **Package references: exactly one addition**, `ModelContextProtocol.AspNetCore` **2.2.0** (Decisions 2). No `Microsoft.AspNetCore.TestHost`, no CORS or authentication packages, no HTTP client packages.
- **The publish folder for each RID contains only `desktop-mcp.exe`** (plus `.pdb`); no `web.config`, `appsettings*.json`, `*.staticwebassets.*` or `*.deps.json`. `pwsh scripts/build-release.ps1` enforces it and must keep passing.
- **ASP.NET Core under NativeAOT:** `DesktopMcp.Server` is a class library, so the Request Delegate Generator does not run there. Every route and middleware is a `RequestDelegate` (`Task (HttpContext)`) or `app.Use(Func<HttpContext, RequestDelegate, Task>)`; no minimal-API lambdas with bound parameters, no `Results.Json<T>` with reflection, no MVC. JSON bodies use `SecurityJson`'s source-generated `JsonTypeInfo`s or `Utf8JsonWriter`.
- The host never reads ambient configuration: `WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory }`, `builder.Configuration.Sources.Clear()` right after creation (no `appsettings.json`, no `ASPNETCORE_*`/`DOTNET_*` variables, so `ASPNETCORE_URLS` cannot move the bind), `builder.Logging.ClearProviders()` then `StderrLoggerProvider`, Kestrel endpoints only from `ListenTarget`s.
- **Safety in tests (controller decision 3):** every test server binds `127.0.0.1` (or `[::1]` where a test says so) on port 0; no test binds `0.0.0.0`, `::` or a fixed port, opens a firewall port or makes an outbound network call (Scrape content comes from a fake `IWebScraper`). TLS tests use a self-signed certificate generated with `CertificateRequest` into a temp folder; nothing touches a certificate store (Decisions 11). No test takes control: HTTP tests use `ToolServices.CreateDefault()` (`DisabledControl`) or P4's `FakeControl`.
- HTTP host tests that build a `WebApplication` live in one xUnit collection, `[Collection(HttpHostCollection.Name)]`, with `DisableParallelization = true` (Decisions 4: the legacy-SSE switch is process-wide).
- The tool contract `spec/windows/tools.json` does not change. Result shapes (spec §4) are unchanged; Scrape's text stays `URL: <url>\nContent:\n<text>`.
- Naming: `desktop-mcp`, `~/.desktop-mcp`, `DESKTOP_MCP_*`. The server name in `initialize` stays `desktop-mcp`.
- Commit messages end with a blank line and `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Work happens on branch `windows-p5`.

All `dotnet` and `pwsh` commands run from `windows/`; `git` commands run from the repository root.

