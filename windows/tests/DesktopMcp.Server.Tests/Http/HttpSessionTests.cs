using System.Text.Json;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.Control;
using DesktopMcp.Server.Http;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Client;

namespace DesktopMcp.Server.Tests.Http;

/// <summary>HTTP sessions and the control notifications (spec §7 Sessions; Decisions 18).</summary>
[Collection(HttpHostCollection.Name)]
public class HttpSessionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

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
        await using var client = await rig.ConnectAsync(mode, options: HttpRig.OnLogMessage(received, ControlCodes.UserControl));
        await client.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 });   // remembered by the gate filter
        await RaiseUntilHeardAsync(control, received.Task);
        var data = await received.Task;
        Assert.Equal("USER_CONTROL", data.GetProperty("data").GetProperty("event").GetString());
        Assert.Equal("desktop-mcp.control", data.GetProperty("logger").GetString());
    }

    /// <summary>Preflight F1 over HTTP: the registry key is the session, not each call's server.</summary>
    [Fact]
    public async Task TwoCallsInOneHttpSessionHearOneNotification()
    {
        var control = new FakeControl();
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" }, ToolServices.CreateDefault() with { Control = control });
        var userControl = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = await rig.ConnectAsync(HttpTransportMode.StreamableHttp, options: HttpRig.OnLogMessage(message =>
        {
            if (HttpRig.EventOf(message) != ControlCodes.UserControl) return;
            Interlocked.Increment(ref userControl);
            first.TrySetResult();
        }));
        for (var i = 0; i < 2; i++)
            Assert.False((await client.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 })).IsError ?? false);
        // Once one is heard, the client's GET stream is open and nothing more is dropped (preflight F5).
        var generation = await RaiseUntilHeardAsync(control, first.Task);
        await SettleAsync(client);
        var before = Volatile.Read(ref userControl);

        control.RaiseStateChanged(Status(user: true, generation));
        using var timeout = new CancellationTokenSource(Bound);
        while (Volatile.Read(ref userControl) == before) await Task.Delay(50, timeout.Token);
        await SettleAsync(client);
        Assert.Equal(before + 1, Volatile.Read(ref userControl));
    }

    /// <summary>
    /// Two legacy-SSE sessions are two registry entries (each has its own SessionId, not
    /// <see cref="DesktopMcp.Server.Hosting.McpServerSetup.NoSessionIdKey"/>): one change reaches each exactly once. The
    /// SSE stream is open before <c>initialize</c> completes, so one raise is enough here (no preflight F5 drop).
    /// </summary>
    [Fact]
    public async Task TwoSseClientsEachHearOneNotification()
    {
        var control = new FakeControl();
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "sse" }, ToolServices.CreateDefault() with { Control = control });
        var counts = new int[2];
        var clients = new McpClient[2];
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var index = i;
                clients[i] = await rig.ConnectAsync(HttpTransportMode.Sse, options: HttpRig.OnLogMessage(message =>
                {
                    if (HttpRig.EventOf(message) == ControlCodes.UserControl) Interlocked.Increment(ref counts[index]);
                }));
            }
            foreach (var client in clients)
                Assert.False((await client.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 })).IsError ?? false);

            control.RaiseStateChanged(Status(user: true, 2));
            using var timeout = new CancellationTokenSource(Bound);
            while (Volatile.Read(ref counts[0]) == 0 || Volatile.Read(ref counts[1]) == 0) await Task.Delay(50, timeout.Token);
            foreach (var client in clients) await SettleAsync(client);
            Assert.Equal([1, 1], [Volatile.Read(ref counts[0]), Volatile.Read(ref counts[1])]);
        }
        finally
        {
            foreach (var client in clients)
                if (client is not null) await client.DisposeAsync();
        }
    }

    /// <summary>
    /// Review Focus 3: after a remembered session is deleted, the others keep hearing notifications and the host keeps
    /// running. Whether the deleted entry goes on a failed send or after the TTL is not observable here (preflight F5).
    /// </summary>
    [Fact]
    public async Task OthersStillHearNotificationsAfterASessionIsDeleted()
    {
        var control = new FakeControl();
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" }, ToolServices.CreateDefault() with { Control = control });
        var heard = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var kept = await rig.ConnectAsync(HttpTransportMode.StreamableHttp, options: HttpRig.OnLogMessage(heard, ControlCodes.UserControl));
        await using (var gone = await rig.ConnectAsync(HttpTransportMode.StreamableHttp))
        {
            await kept.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 });
            await gone.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 });
        }                                                                // disposing it sends DELETE
        await RaiseUntilHeardAsync(control, heard.Task);
        Assert.True(rig.HostIsRunning);
        Assert.Equal(21, (await kept.ListToolsAsync()).Count);
    }

    /// <summary>
    /// Alternates <c>user</c> and <c>ready</c> with rising generations (from 2) every 250 ms until <paramref name="heard"/>
    /// completes, within 5 s: the SDK drops a notification while the client's GET stream is not open yet (preflight F5).
    /// Leaves the control at <c>ready</c> and returns the next generation.
    /// </summary>
    private static async Task<long> RaiseUntilHeardAsync(FakeControl control, Task heard)
    {
        using var timeout = new CancellationTokenSource(Bound);
        long generation = 2;
        var user = true;
        while (!heard.IsCompleted)
        {
            control.RaiseStateChanged(Status(user, generation++));
            user = !user;
            try
            {
                await heard.WaitAsync(TimeSpan.FromMilliseconds(250), timeout.Token);
            }
            catch (TimeoutException)
            {
            }
        }
        if (!user) control.RaiseStateChanged(Status(user: false, generation++));
        return generation;
    }

    private static ControlStatus Status(bool user, long generation) =>
        new(user ? ControlState.User : ControlState.Ready, generation, user ? 10 : 0, true, "relaxed", 10);

    /// <summary>Gives notifications in flight (a duplicate included) time to arrive, then makes one round trip.</summary>
    private static async Task SettleAsync(McpClient client)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await client.ListToolsAsync();
    }
}
