using System.Text.Json;
using DesktopMcp.Core.Control;
using ModelContextProtocol.Protocol;

namespace DesktopMcp.Server.Tools;

public static class ToolResults
{
    /// <summary>A text result shaped like upstream's: the text, plus structuredContent {"result": text}.</summary>
    public static CallToolResult Text(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        StructuredContent = JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { ["result"] = text }, ToolJsonContext.Default.DictionaryStringString),
    };

    /// <summary>
    /// A result for a tool without an outputSchema (Snapshot, Screenshot; spec §4): the text, then the PNG when there is
    /// one, and no structuredContent.
    /// </summary>
    public static CallToolResult Content(string text, byte[]? png) => new()
    {
        Content = png is null
            ? [new TextContentBlock { Text = text }]
            : [new TextContentBlock { Text = text }, ImageContentBlock.FromBytes(png, "image/png")],
    };

    /// <summary>A JSON result (spec §4): the JSON as text, plus structuredContent {"result": json}.</summary>
    public static CallToolResult Json(string json)
    {
        using var document = JsonDocument.Parse($"{{\"result\":{json}}}");
        return new()
        {
            Content = [new TextContentBlock { Text = json }],
            StructuredContent = document.RootElement.Clone(),
        };
    }

    /// <summary>
    /// A JSON object result whose object is the structuredContent itself (ControlStatus, spec §4): the JSON as text, plus
    /// the parsed object, not wrapped in <c>result</c>.
    /// </summary>
    public static CallToolResult Object(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Not a JSON object.", nameof(json));
        return new()
        {
            Content = [new TextContentBlock { Text = json }],
            StructuredContent = document.RootElement.Clone(),
        };
    }

    /// <summary>
    /// A control rejection (spec §4): an <c>isError</c> result whose whole text is <see cref="ControlJson.Rejection"/>,
    /// with no <c>Error calling tool</c> prefix.
    /// </summary>
    public static CallToolResult Rejection(ControlRejectedException rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        return new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = ControlJson.Rejection(rejection) }],
        };
    }

    public static CallToolResult Error(string toolName, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"Error calling tool '{toolName}': {message}" }],
    };
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ToolJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
