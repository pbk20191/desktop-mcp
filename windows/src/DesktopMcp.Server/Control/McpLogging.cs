using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Control;

/// <summary>
/// MCP logging (spec §7, upstream's <c>notifications/message</c>) as raw JSON. ModelContextProtocol 2.2.0 marks its
/// logging types obsolete (MCP9005), so the capability and the notifications are built without them.
/// </summary>
public static class McpLogging
{
    /// <summary>The log message notification's method.</summary>
    public const string MessageMethod = "notifications/message";

    /// <summary><paramref name="capabilities"/> (or none) with the <c>logging</c> capability (<c>{}</c>) declared.</summary>
    public static ServerCapabilities DeclareLogging(ServerCapabilities? capabilities)
    {
        var typeInfo = (JsonTypeInfo<ServerCapabilities>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ServerCapabilities));
        var json = capabilities is null ? new JsonObject() : JsonSerializer.SerializeToNode(capabilities, typeInfo)!.AsObject();
        json["logging"] ??= new JsonObject();
        return json.Deserialize(typeInfo)!;
    }

    /// <summary>
    /// Sends <c>notifications/message</c> with <c>level</c> (an MCP level name such as <c>info</c>), <c>logger</c> and
    /// <c>data</c> to the client of <paramref name="server"/>.
    /// </summary>
    public static Task SendMessageAsync(McpServer server, string level, string logger, JsonNode? data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(logger);
        return server.SendMessageAsync(new JsonRpcNotification
        {
            Method = MessageMethod,
            Params = new JsonObject { ["level"] = level, ["logger"] = logger, ["data"] = data?.DeepClone() },
        }, ct);
    }
}
