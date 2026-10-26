using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// MCP sampling (<c>sampling/createMessage</c>, upstream's <c>ctx.sample</c>) as raw JSON (P5 plan, Decisions 5).
/// ModelContextProtocol 2.2.0 marks its sampling API, request types and capability obsolete (MCP9005), so the request,
/// its result and the client's capability are read and written without them, as <c>McpLogging</c> does for logging.
/// </summary>
public static class McpSampling
{
    /// <summary>The sampling request's method.</summary>
    public const string CreateMessageMethod = "sampling/createMessage";

    /// <summary>How long the best-effort <c>notifications/cancelled</c> for an abandoned request may take to send.</summary>
    private static readonly TimeSpan CancelNoticeTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Whether the client of <paramref name="server"/> declared the <c>sampling</c> capability.</summary>
    public static bool ClientCanSample(McpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.ClientCapabilities is not { } capabilities) return false;
        var typeInfo = (JsonTypeInfo<ClientCapabilities>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ClientCapabilities));
        return JsonSerializer.SerializeToNode(capabilities, typeInfo) is JsonObject json && json["sampling"] is not null;
    }

    /// <summary>
    /// Asks the client of <paramref name="server"/> for one completion of <paramref name="userText"/> under
    /// <paramref name="systemPrompt"/>, and returns its text. Any failure (an error response, a result without a single
    /// text block, a broken transport) is <c>null</c>; only the cancellation of <paramref name="ct"/> propagates.
    /// There is no timeout of its own, as upstream: the call's cancellation ends the wait.
    /// </summary>
    public static async Task<string?> TrySampleTextAsync(McpServer server, string systemPrompt, string userText, int maxTokens, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        // An id of its own (a string never clashes with the SDK's numeric ids), so a cancellation can name the request.
        var id = $"desktop-mcp-sampling-{Guid.NewGuid():N}";
        var request = new JsonRpcRequest
        {
            Id = new RequestId(id),
            Method = CreateMessageMethod,
            Params = new JsonObject
            {
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject { ["type"] = "text", ["text"] = userText },
                }),
                ["systemPrompt"] = systemPrompt,
                ["maxTokens"] = maxTokens,
            },
        };
        try
        {
            var response = await server.SendRequestAsync(request, ct).ConfigureAwait(false);
            return ResultText(response.Result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The SDK stops waiting without telling the client; MCP's cancellation notice lets it drop the request (a
            // pending approval, a running completion). Best effort: the call's own cancellation still propagates.
            await NotifyCancelledAsync(server, id).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            // upstream: except Exception: pass (the raw content is returned).
            return null;
        }
    }

    private static async Task NotifyCancelledAsync(McpServer server, string id)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CancelNoticeTimeout);
            await server.SendMessageAsync(new JsonRpcNotification
            {
                Method = NotificationMethods.CancelledNotification,
                Params = new JsonObject { ["requestId"] = id, ["reason"] = "The tool call was cancelled." },
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The transport may already be gone (the session ended); the client then has nothing to cancel.
        }
    }

    /// <summary>The <c>text</c> of a result's <c>content</c> text block, or of an array of exactly one; else <c>null</c>.</summary>
    private static string? ResultText(JsonNode? result) => (result as JsonObject)?["content"] switch
    {
        JsonObject block => BlockText(block),
        JsonArray { Count: 1 } blocks => blocks[0] is JsonObject block ? BlockText(block) : null,
        _ => null,
    };

    private static string? BlockText(JsonObject block) =>
        block["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind == "text"
        && block["text"] is JsonValue text && text.TryGetValue<string>(out var value)
            ? value
            : null;
}
