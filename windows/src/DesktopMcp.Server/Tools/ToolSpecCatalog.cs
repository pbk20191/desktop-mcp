using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DesktopMcp.Server.Tools;

/// <summary>The tool contract (spec/windows/tools.json) as protocol <see cref="Tool"/> objects.</summary>
public sealed class ToolSpecCatalog
{
    private const string ResourceName = "DesktopMcp.Server.tools.json";
    private readonly Dictionary<string, Tool> _tools = new(StringComparer.Ordinal);

    private ToolSpecCatalog(List<Tool> tools)
    {
        foreach (var tool in tools)
        {
            if (!_tools.TryAdd(tool.Name, tool))
                throw new InvalidDataException($"The tool contract lists '{tool.Name}' more than once.");
        }
        Names = tools.Select(t => t.Name).ToArray();
    }

    /// <summary>The SDK's serialisation contract for <see cref="Tool"/>; ContractTests serialises with the same instance.</summary>
    internal static JsonTypeInfo<Tool> ToolTypeInfo { get; } =
        (JsonTypeInfo<Tool>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool));

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
        var tools = new List<Tool>();
        foreach (var element in document.RootElement.GetProperty("tools").EnumerateArray())
        {
            tools.Add(element.Deserialize(ToolTypeInfo) ?? throw new JsonException("null tool entry"));
        }
        return new ToolSpecCatalog(tools);
    }
}
