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

