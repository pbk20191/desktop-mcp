using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Control;

public enum ControlState { Disabled, Unavailable, User, Ready, Ai }

public static class ControlStates
{
    public static string Wire(ControlState state) => state switch
    {
        ControlState.Disabled => "disabled",
        ControlState.Unavailable => "unavailable",
        ControlState.User => "user",
        ControlState.Ready => "ready",
        ControlState.Ai => "ai",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}

public sealed record ControlStatus(ControlState State, long Generation, double WaitSeconds, bool Enabled, string Policy, double IdleTimeoutSeconds);

public static class ControlCodes
{
    public const string UserControl = "USER_CONTROL";
    public const string ControlUnavailable = "CONTROL_UNAVAILABLE";
    public const string ControlPreempted = "CONTROL_PREEMPTED";
    public const string PhysicalInputHeld = "PHYSICAL_INPUT_HELD";

    public static string ForState(ControlState state) => state == ControlState.User ? UserControl : ControlUnavailable;
}

public sealed class ControlRejectedException(string code, ControlStatus status, long? executedSteps = null)
    : Exception($"{code}: {ControlStates.Wire(status.State)}")
{
    public string Code { get; } = code;
    public ControlStatus Status { get; } = status;
    public long? ExecutedSteps { get; } = executedSteps;
}

public static class ControlJson
{
    /// <summary>Compact JSON in upstream's key order (the ControlStatus tool result).</summary>
    public static string Status(ControlStatus status)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("state", ControlStates.Wire(status.State));
            writer.WriteNumber("generation", status.Generation);
            writer.WritePropertyName("wait_seconds");
            writer.WriteRawValue(PyText.FloatRepr(status.WaitSeconds));
            writer.WriteBoolean("enabled", status.Enabled);
            writer.WriteString("policy", status.Policy);
            writer.WritePropertyName("idle_timeout_seconds");
            writer.WriteRawValue(PyText.FloatRepr(status.IdleTimeoutSeconds));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>Python's <c>json.dumps</c> form: <c>, </c> and <c>: </c> separators.</summary>
    public static string Rejection(ControlRejectedException rejection)
    {
        var s = rejection.Status;
        var text = new StringBuilder("{\"code\": ").Append(Quote(rejection.Code))
            .Append(", \"status\": {\"state\": ").Append(Quote(ControlStates.Wire(s.State)))
            .Append(", \"generation\": ").Append(s.Generation)
            .Append(", \"wait_seconds\": ").Append(PyText.FloatRepr(s.WaitSeconds))
            .Append(", \"enabled\": ").Append(s.Enabled ? "true" : "false")
            .Append(", \"policy\": ").Append(Quote(s.Policy))
            .Append(", \"idle_timeout_seconds\": ").Append(PyText.FloatRepr(s.IdleTimeoutSeconds));
        if (rejection.ExecutedSteps is { } steps) text.Append(", \"executed_steps\": ").Append(steps);
        return text.Append("}}").ToString();
    }

    private static string Quote(string value) => "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString() + "\"";
}
