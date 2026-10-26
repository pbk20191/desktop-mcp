using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopMcp.Server.Control;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tests;

public class McpLoggingTests
{
    private static JsonElement Json(ServerCapabilities capabilities) =>
        JsonSerializer.SerializeToElement(capabilities, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ServerCapabilities)));

    [Fact]
    public void DeclaringLoggingAddsAnEmptyObject()
    {
        Assert.Equal("""{"logging":{}}""", Json(McpLogging.DeclareLogging(null)).GetRawText());
    }

    [Fact]
    public void DeclaringLoggingKeepsTheOtherCapabilities()
    {
        var declared = McpLogging.DeclareLogging(new ServerCapabilities { Tools = new() { ListChanged = true } });
        Assert.Equal("""{"logging":{},"tools":{"listChanged":true}}""", Json(McpLogging.DeclareLogging(declared)).GetRawText());
    }

    [Fact]
    public async Task AMessageReachesTheClientAsNotificationsMessage()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = McpServer.Create(new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions { Capabilities = McpLogging.DeclareLogging(null) });
        var serving = server.RunAsync(stop.Token);
        var received = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()), cancellationToken: stop.Token))
        {
            await using var registration = client.RegisterNotificationHandler(McpLogging.MessageMethod, (notification, _) =>
            {
                received.TrySetResult(notification.Params?.DeepClone());
                return ValueTask.CompletedTask;
            });
            var data = new JsonObject { ["state"] = "ai" };
            await McpLogging.SendMessageAsync(server, "info", "desktop-mcp.control", data, stop.Token);
            var message = await received.Task.WaitAsync(stop.Token);
            Assert.Equal("""{"level":"info","logger":"desktop-mcp.control","data":{"state":"ai"}}""", message?.ToJsonString());
        }
        await stop.CancelAsync();
        await serving.ContinueWith(_ => { }, TaskScheduler.Default);
    }
}
