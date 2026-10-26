using System.Runtime.InteropServices;
using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Input;

namespace DesktopMcp.Desktop.Tests.Control;

/// <summary>
/// Unsigned input (<c>dwExtraInfo</c> 0), as Chrome Remote Desktop or another program would inject it, which
/// <see cref="ForeignInjectedPolicy.Physical"/> treats as the user's (Decisions 3). It goes through
/// <see cref="GuardedInjector"/>, so the same checks apply: the test window must be the foreground window and every mouse
/// event must land on it; relative moves are refused (preflight F6), so mouse input is absolute only. Keys carry their
/// scan code, and nothing else is set.
/// </summary>
internal sealed partial class ForeignInjector
{
    private const uint KeyEventUp = 0x0002;
    private const uint MapVkToVsc = 0;

    private readonly GuardedInjector _guard;

    public ForeignInjector(nint window) => _guard = new GuardedInjector(new UnsignedSendInput(), window);

    public void Send(IReadOnlyList<InjectedInput> inputs) => _guard.Send(inputs);

    public void Key(ushort virtualKey, bool up) => Send([InjectedInput.Keyboard(new KeyInput(virtualKey, '\0', up))]);

    /// <summary>A down and an up, sent as two batches (the hook sees them one by one either way).</summary>
    public void Tap(ushort virtualKey)
    {
        Key(virtualKey, up: false);
        Key(virtualKey, up: true);
    }

    /// <summary>An absolute move to <paramref name="point"/>, which must be on the test window.</summary>
    public void MoveTo(ScreenPoint point) =>
        Send([InjectedInput.MouseAt(MouseFlags.AbsoluteMove, MouseCoordinates.Normalize(point, VirtualScreen()))]);

    /// <summary>The virtual screen <see cref="GuardedInjector"/> denormalises over.</summary>
    public static ScreenRect VirtualScreen()
    {
        var left = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        var top = GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        return new ScreenRect(left, top, left + GetSystemMetrics(78), top + GetSystemMetrics(79));
    }

    /// <summary>SendInput with <c>dwExtraInfo</c> 0, declared here (test code) so product code keeps only the signed path.</summary>
    private sealed class UnsignedSendInput : IInputInjector
    {
        public unsafe void Send(IReadOnlyList<InjectedInput> inputs)
        {
            if (inputs.Count == 0) return;
            var native = new Input[inputs.Count];
            for (var i = 0; i < native.Length; i++)
            {
                var input = inputs[i];
                if (input.IsKeyboard)
                {
                    native[i] = new Input
                    {
                        Type = 1, // INPUT_KEYBOARD
                        Keyboard = new KeyboardInput
                        {
                            VirtualKey = input.Key.VirtualKey,
                            Scan = (ushort)MapVirtualKeyW(input.Key.VirtualKey, MapVkToVsc),
                            Flags = input.Key.Up ? KeyEventUp : 0,
                        },
                    };
                }
                else
                {
                    native[i] = new Input
                    {
                        Type = 0, // INPUT_MOUSE
                        Mouse = new MouseInput { Dx = input.X, Dy = input.Y, MouseData = unchecked((uint)input.WheelDelta), Flags = (uint)input.Mouse },
                    };
                }
            }
            fixed (Input* first = native)
            {
                var sent = SendInput((uint)native.Length, first, sizeof(Input));
                if (sent != native.Length)
                    throw new InvalidOperationException($"SendInput inserted {sent} of {native.Length} events (Win32 error {Marshal.GetLastPInvokeError()}).");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    // INPUT on 64-bit Windows (x64 and arm64): the union starts at 8 and the whole is 40 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial uint SendInput(uint count, Input* inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint code, uint mapType);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
