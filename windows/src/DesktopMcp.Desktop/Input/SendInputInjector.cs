using System.Runtime.InteropServices;
using DesktopMcp.Core.Input;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Snapshots;
using DesktopMcp.Desktop.Tree;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DesktopMcp.Desktop.Input;

internal delegate uint SendInputCall(ReadOnlySpan<INPUT> inputs, out int lastError);

/// <summary>
/// Injects through SendInput only. Every event carries <see cref="InputSignature.ExtraInfo"/>, the batch the caller passes
/// goes out in one call, and a short count is an error (UIPI is named when it is the likely cause).
/// </summary>
public sealed class SendInputInjector : IInputInjector
{
    private readonly SendInputCall _send;
    private readonly Func<ushort, ushort> _mapVirtualKey;
    private readonly Func<bool> _selfElevated;
    private readonly Func<string?> _foregroundProcessName;
    private readonly Func<bool?> _foregroundHigherIntegrity;
    private readonly Action _checkSession;

    public SendInputInjector() : this(NativeSend, MapVirtualKey, () => ProcessIntegrity.CurrentIsElevated, ForegroundProcessName,
        ForegroundHigherIntegrity, DesktopSession.ThrowIfUnavailable)
    {
    }

    internal SendInputInjector(SendInputCall send, Func<ushort, ushort> mapVirtualKey, Func<bool> selfElevated,
        Func<string?> foregroundProcessName) : this(send, mapVirtualKey, selfElevated, foregroundProcessName, () => null)
    {
    }

    internal SendInputInjector(SendInputCall send, Func<ushort, ushort> mapVirtualKey, Func<bool> selfElevated,
        Func<string?> foregroundProcessName, Func<bool?> foregroundHigherIntegrity)
        : this(send, mapVirtualKey, selfElevated, foregroundProcessName, foregroundHigherIntegrity, static () => { })
    {
    }

    /// <param name="foregroundHigherIntegrity">Whether the foreground process outranks this one; null when unknown.</param>
    /// <param name="checkSession">
    /// The desktop-session check (P2 Decisions 18), made only after a short count: off the user's default desktop (the lock
    /// screen, a UAC prompt) it throws the session text, so a blocked batch there is not reported as UIPI.
    /// </param>
    internal SendInputInjector(SendInputCall send, Func<ushort, ushort> mapVirtualKey, Func<bool> selfElevated,
        Func<string?> foregroundProcessName, Func<bool?> foregroundHigherIntegrity, Action checkSession)
    {
        _foregroundHigherIntegrity = foregroundHigherIntegrity;
        _checkSession = checkSession;
        _send = send;
        _mapVirtualKey = mapVirtualKey;
        _selfElevated = selfElevated;
        _foregroundProcessName = foregroundProcessName;
    }

    public void Send(IReadOnlyList<InjectedInput> inputs)
    {
        if (inputs.Count == 0) return;
        var native = new INPUT[inputs.Count];
        for (var i = 0; i < native.Length; i++) native[i] = inputs[i].IsKeyboard ? Keyboard(inputs[i].Key) : Mouse(inputs[i]);

        var inserted = _send(native, out var error);
        if (inserted == native.Length) return;
        _checkSession();
        // Access denied without elevation is UIPI unless the foreground process is known not to outrank us (secure desktop).
        if (error == 5 && !_selfElevated() && _foregroundHigherIntegrity() != false)
            throw new InputBlockedException(InputBlockedException.ElevatedText(_foregroundProcessName()));
        throw new DesktopException(
            $"SendInput inserted {inserted} of {native.Length} input events (Win32 error {error}): the input was blocked, for example by the secure desktop or another application.");
    }

    private static INPUT Mouse(InjectedInput input)
    {
        var native = new INPUT { type = INPUT_TYPE.INPUT_MOUSE };
        native.Anonymous.mi = new MOUSEINPUT
        {
            dx = input.X,
            dy = input.Y,
            mouseData = unchecked((uint)input.WheelDelta),
            dwFlags = (MOUSE_EVENT_FLAGS)(uint)input.Mouse,
            dwExtraInfo = InputSignature.ExtraInfo,
        };
        return native;
    }

    private INPUT Keyboard(KeyInput key)
    {
        var native = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        var flags = key.Up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0;
        if (key.VirtualKey == 0)
        {
            native.Anonymous.ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = key.Unicode,
                dwFlags = flags | KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE,
                dwExtraInfo = InputSignature.ExtraInfo,
            };
            return native;
        }

        // Decisions 14: the extended flag only for extended keys (an extended VK_MENU would be Right Alt / AltGr / Han-Eng).
        int scan = KeyTables.FixedScanCodes.TryGetValue(key.VirtualKey, out var fixedScan)
            ? fixedScan
            : _mapVirtualKey(key.VirtualKey) | (KeyTables.ScanExtendedKeys.Contains(key.VirtualKey) ? 0x100 : 0);
        if (KeyTables.ExtendedKeys.Contains(key.VirtualKey) || (scan & 0x100) != 0)
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
        native.Anonymous.ki = new KEYBDINPUT
        {
            wVk = (VIRTUAL_KEY)key.VirtualKey,
            wScan = (ushort)(scan & 0xFF),
            dwFlags = flags,
            dwExtraInfo = InputSignature.ExtraInfo,
        };
        return native;
    }

    private static unsafe uint NativeSend(ReadOnlySpan<INPUT> inputs, out int lastError)
    {
        var inserted = PInvoke.SendInput(inputs, sizeof(INPUT));
        lastError = inserted == inputs.Length ? 0 : Marshal.GetLastPInvokeError();
        return inserted;
    }

    private static ushort MapVirtualKey(ushort virtualKey) =>
        (ushort)PInvoke.MapVirtualKey(virtualKey, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);

    private static bool? ForegroundHigherIntegrity()
    {
        var windows = new Win32WindowSystem();
        var window = windows.GetForegroundWindow();
        var pid = window == 0 ? 0 : windows.GetProcessId(window);
        if (pid <= 0) return null;
        var target = ProcessIntegrity.IntegrityRid(pid);
        var current = ProcessIntegrity.IntegrityRid(Environment.ProcessId);
        return target is { } t && current is { } c ? t > c : null;
    }

    private static string? ForegroundProcessName()
    {
        var windows = new Win32WindowSystem();
        var window = windows.GetForegroundWindow();
        return window == 0 ? null : windows.GetProcessName(windows.GetProcessId(window));
    }
}
