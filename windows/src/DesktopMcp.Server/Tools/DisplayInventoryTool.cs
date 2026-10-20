using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Displays;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/display.py:26-61: the display list as JSON text and as structuredContent {"result": [...]}.</summary>
public sealed class DisplayInventoryTool(Tool protocolTool, IDisplayProvider displays) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var list = await Task.Run(displays.GetDisplays, cancellationToken).ConfigureAwait(false);
        return ToolResults.Json(Write(list));
    }

    /// <summary>
    /// upstream tools/display.py:13-23 and 49-61, key order kept; compact JSON, scale written as Python prints a float
    /// (Decisions 15).
    /// </summary>
    internal static string Write(IReadOnlyList<DisplayInfo> displays)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            foreach (var display in displays)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", display.Index);
                writer.WriteString("device", display.DeviceName);
                writer.WriteBoolean("primary", display.Primary);
                WriteRect(writer, "bounds", display.Bounds);
                if (display.WorkArea is { } workArea) WriteRect(writer, "work_area", workArea);
                else writer.WriteNull("work_area");
                writer.WriteString("resolution", $"{display.Bounds.Width}x{display.Bounds.Height}");
                writer.WriteString("orientation", display.Orientation);
                if (display.EffectiveDpi is { } dpi) writer.WriteNumber("effective_dpi", dpi);
                else writer.WriteNull("effective_dpi");
                writer.WritePropertyName("scale");
                if (display.Scale is { } scale) writer.WriteRawValue(PyText.FloatRepr(scale));
                else writer.WriteNullValue();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteRect(Utf8JsonWriter writer, string name, ScreenRect rect)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("left", rect.Left);
        writer.WriteNumber("top", rect.Top);
        writer.WriteNumber("right", rect.Right);
        writer.WriteNumber("bottom", rect.Bottom);
        writer.WriteNumber("width", rect.Width);
        writer.WriteNumber("height", rect.Height);
        writer.WriteEndObject();
    }
}
