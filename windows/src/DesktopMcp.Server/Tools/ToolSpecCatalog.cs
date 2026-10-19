using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DesktopMcp.Server.Tools;

/// <summary>The tool contract (spec/windows/tools.json) as protocol <see cref="Tool"/> objects.</summary>
public sealed class ToolSpecCatalog
{
    private const string ResourceName = "DesktopMcp.Server.tools.json";
    private readonly Dictionary<string, Tool> _tools;

    private ToolSpecCatalog(List<Tool> tools)
    {
        Names = tools.Select(t => t.Name).ToArray();
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Names { get; }

    public Tool Get(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool : throw new KeyNotFoundException($"Tool '{name}' is not in the contract.");

    public static ToolSpecCatalog LoadEmbedded()
    {
        using var stream = typeof(ToolSpecCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        return Parse(stream);
    }

    public static ToolSpecCatalog Parse(Stream utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var typeInfo = (JsonTypeInfo<Tool>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool));
        var tools = new List<Tool>();
        foreach (var element in document.RootElement.GetProperty("tools").EnumerateArray())
        {
            tools.Add(element.Deserialize(typeInfo) ?? throw new JsonException("null tool entry"));
        }
        return new ToolSpecCatalog(tools);
    }
}
