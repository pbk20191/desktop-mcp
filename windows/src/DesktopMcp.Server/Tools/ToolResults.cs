using System.Text.Json;
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

    public static CallToolResult Error(string toolName, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"Error calling tool '{toolName}': {message}" }],
    };
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ToolJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
