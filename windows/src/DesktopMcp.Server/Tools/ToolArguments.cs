using System.Text.Json;
using DesktopMcp.Core.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// Typed access to a tools/call request's arguments with upstream's coercion rules. A missing argument and JSON null
/// both mean "not given", so optional parameters take their defaults.
/// </summary>
internal sealed class ToolArguments(IDictionary<string, JsonElement> values)
{
    public static ToolArguments From(RequestContext<CallToolRequestParams> request) =>
        new(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());

    public JsonElement? Get(string name) =>
        values.TryGetValue(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value : null;

    public string RequiredString(string name) => Coercion.String(Get(name) ?? throw Missing(name), name);

    public string? OptionalString(string name) => Get(name) is { } value ? Coercion.String(value, name) : null;

    public string String(string name, string defaultValue) => OptionalString(name) ?? defaultValue;

    public long RequiredInteger(string name) => Coercion.Integer(Get(name) ?? throw Missing(name), name);

    public long? OptionalInteger(string name) => Get(name) is { } value ? Coercion.Integer(value, name) : null;

    public long Integer(string name, long defaultValue) => OptionalInteger(name) ?? defaultValue;

    public bool StrictBool(string name, bool defaultValue) => Get(name) is { } value ? Coercion.StrictBool(value, name) : defaultValue;

    public bool LooseBool(string name, bool defaultValue) => Get(name) is { } value ? Coercion.LooseBool(value) : defaultValue;

    private static ArgumentException Missing(string name) => new($"Missing required argument: {name}");
}
