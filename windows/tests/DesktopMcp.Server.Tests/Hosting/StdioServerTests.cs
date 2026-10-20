using System.IO.Pipelines;
using System.Text.Json;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Server.Hosting;
using DesktopMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DesktopMcp.Server.Tests;

/// <summary>The serve host's MCP setup over in-memory pipes in place of stdin and stdout (spec §9).</summary>
public sealed class StdioServerTests
{
    private static readonly ServePlan Plan = new() { Transport = "stdio", Host = "localhost", Port = 8000, Control = new ControlSettings(true, "relaxed", 10.0, 500, 500) };

    /// <summary>Serves <paramref name="services"/> over in-memory pipes, runs <paramref name="client"/>, then stops the server.</summary>
    private static async Task ServeAsync(ToolServices services, Func<McpClient, CancellationToken, Task> client)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = StdioServer.RunAsync(Plan, services,
            builder => builder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()), stop.Token);
        await using (var connected = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()), cancellationToken: stop.Token))
            await client(connected, stop.Token);
        await stop.CancelAsync();
        Assert.Equal(0, await serving);
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [Fact]
    public async Task GatedCallsGoThroughTheFilter()
    {
        var status = new ControlStatus(ControlState.User, 2, 9.5, true, "relaxed", 10);
        await ServeAsync(ToolServices.CreateDefault() with { Control = new FakeControl { Current = status } }, async (client, ct) =>
        {
            var click = await client.CallToolAsync("Click", new Dictionary<string, object?> { ["loc"] = JsonDocument.Parse("[1, 2]").RootElement }, cancellationToken: ct);
            Assert.True(click.IsError);
            Assert.Equal(ControlJson.Rejection(new ControlRejectedException(ControlCodes.UserControl, status)), Text(click));

            // The gate runs before the tool's own argument checks.
            var invalid = await client.CallToolAsync("Click", new Dictionary<string, object?>(), cancellationToken: ct);
            Assert.StartsWith("{\"code\": \"USER_CONTROL\"", Text(invalid));

            var wait = await client.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 }, cancellationToken: ct);
            Assert.False(wait.IsError ?? false);

            var controlStatus = await client.CallToolAsync("ControlStatus", new Dictionary<string, object?>(), cancellationToken: ct);
            Assert.False(controlStatus.IsError ?? false);
            Assert.Equal(ControlJson.Status(status), Text(controlStatus));
            Assert.Equal("user", controlStatus.StructuredContent?.GetProperty("state").GetString());
        });
    }

    [Fact]
    public async Task APreemptionInsideAToolReachesTheClientAsARejection()
    {
        var user = new ControlStatus(ControlState.User, 3, 1, true, "relaxed", 10);
        var desktop = new FakeDesktop { PointThrows = new ControlRejectedException(ControlCodes.ControlPreempted, user, 2) };
        var control = new FakeControl();
        await ServeAsync(desktop.Services() with { Control = control }, async (client, ct) =>
        {
            var click = await client.CallToolAsync("Click", new Dictionary<string, object?> { ["loc"] = JsonDocument.Parse("[1, 2]").RootElement }, cancellationToken: ct);
            Assert.True(click.IsError);
            Assert.Equal("""{"code": "CONTROL_PREEMPTED", "status": {"state": "user", "generation": 3, "wait_seconds": 1.0, "enabled": true, "policy": "relaxed", "idle_timeout_seconds": 10.0, "executed_steps": 2}}""",
                Text(click));
        });
        Assert.Equal(1, control.EndedCalls);
    }

    [Fact]
    public async Task TheControlStartsBeforeServingAndStopsAfter()
    {
        var control = new FakeControl();
        var dispatcher = new DispatcherHost(() => throw new InvalidOperationException("never started"));
        await ServeAsync(ToolServices.CreateDefault() with { Control = control, Dispatcher = dispatcher }, async (client, ct) =>
        {
            Assert.Equal(["start"], control.Events);
            await client.ListToolsAsync(cancellationToken: ct);
            Assert.Equal(["start"], control.Events);
        });
        Assert.Equal(["start", "stop"], control.Events);
        // spec §5: the shared dispatcher is shut down after the control stops (F17).
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.GetAsync(default));
    }

    [Fact]
    public async Task InitializeListsTheContractToolsAndCallsWait()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var plan = new ServePlan { Transport = "stdio", Host = "localhost", Port = 8000, Control = new ControlSettings(true, "relaxed", 10.0, 500, 500) };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = StdioServer.RunAsync(plan, ToolServices.CreateDefault(),
            builder => builder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()), stop.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()), cancellationToken: stop.Token))
        {
            Assert.Equal("desktop-mcp", client.ServerInfo.Name);

            var tools = await client.ListToolsAsync(cancellationToken: stop.Token);
            // The SDK does not keep registration order, so compare the names as sets.
            Assert.Equal(21, tools.Count);
            Assert.Equal(ToolSpecCatalog.LoadEmbedded().Names.Order(StringComparer.Ordinal), tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));

            var result = await client.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 }, cancellationToken: stop.Token);
            Assert.False(result.IsError ?? false);
            Assert.Equal("Waited for 0 seconds.", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        }

        await stop.CancelAsync();
        Assert.Equal(0, await serving);
    }

    [Fact]
    public async Task TheServerLogsTheDispatchersUnhandledExceptions()
    {
        var host = new DispatcherHost(() => throw new InvalidOperationException("never started"));
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var plan = new ServePlan { Transport = "stdio", Host = "localhost", Port = 8000, Control = new ControlSettings(true, "relaxed", 10.0, 500, 500) };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = StdioServer.RunAsync(plan, new FakeDesktop().Services() with { Dispatcher = host },
            builder => builder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()), stop.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()), cancellationToken: stop.Token))
        {
            Assert.NotNull(host.Report);
            host.Report(new InvalidOperationException("tick"));  // logged at warning level; must not throw
        }

        await stop.CancelAsync();
        Assert.Equal(0, await serving);
        Assert.False(host.IsStarted);
    }

    [Fact]
    public async Task StoppingReleasesHeldInput()
    {
        var ups = 0;
        var ledger = new InputLedger();
        ledger.Press("key:ctrl", () => { }, () => ups++);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var plan = new ServePlan { Transport = "stdio", Host = "localhost", Port = 8000, Control = new ControlSettings(true, "relaxed", 10.0, 500, 500) };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = StdioServer.RunAsync(plan, new FakeDesktop().Services() with { Ledger = ledger },
            builder => builder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()), stop.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()), cancellationToken: stop.Token))
            Assert.Equal(["key:ctrl"], ledger.Pending);

        await stop.CancelAsync();
        Assert.Equal(0, await serving);
        Assert.Empty(ledger.Pending);
        Assert.Equal(1, ups);
        // Decisions 19: nothing new can be pressed once shutdown has released the held input.
        Assert.Throws<InputUnavailableException>(() => ledger.Press("key:shift", () => { }, () => { }));
    }
}
