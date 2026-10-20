using DesktopMcp.Core.Input;
using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Input;

[Flags]
public enum MouseFlags : uint
{
    None = 0,
    Move = 0x1,
    LeftDown = 0x2,
    LeftUp = 0x4,
    RightDown = 0x8,
    RightUp = 0x10,
    MiddleDown = 0x20,
    MiddleUp = 0x40,
    Wheel = 0x800,
    VirtualDesk = 0x4000,
    Absolute = 0x8000,
    AbsoluteMove = Move | Absolute | VirtualDesk,
}

/// <summary>One keyboard or mouse event for <see cref="IInputInjector"/>; mouse coordinates are normalised 0..65535.</summary>
public readonly record struct InjectedInput(bool IsKeyboard, MouseFlags Mouse, int X, int Y, int WheelDelta, KeyInput Key)
{
    public static InjectedInput MouseAt(MouseFlags flags, (int X, int Y) normalized) =>
        new(false, flags, normalized.X, normalized.Y, 0, default);

    public static InjectedInput Wheel(int delta) => new(false, MouseFlags.Wheel, 0, 0, delta, default);

    public static InjectedInput Keyboard(KeyInput key) => new(true, MouseFlags.None, 0, 0, 0, key);
}

/// <summary>The only path to SendInput. Events of one call are sent as one batch.</summary>
public interface IInputInjector
{
    void Send(IReadOnlyList<InjectedInput> inputs);
}

public static class InputSignature
{
    /// <summary>"DMCP": carried in dwExtraInfo of every injected event (the value the P0 hook probe used).</summary>
    public const uint ExtraInfo = 0x444D4350;
}

/// <summary>SendInput was refused because the target runs at a higher integrity level (UIPI).</summary>
public sealed class InputBlockedException(string message) : DesktopException(message)
{
    public static string ElevatedText(string? processName) =>
        $"Input blocked: the target window ({processName ?? "unknown"}) runs elevated (as administrator) and this server does not, so Windows blocks input to it (UIPI). Run desktop-mcp elevated to control it.";
}
