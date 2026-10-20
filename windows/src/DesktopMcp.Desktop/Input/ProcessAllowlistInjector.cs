using DesktopMcp.Core.Control;
using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Tree;

namespace DesktopMcp.Desktop.Input;

/// <summary>Refuses a batch whose target windows belong to a process outside control.process_allowlist (Decisions 5). Batches of only ups always pass.</summary>
internal sealed class ProcessAllowlistInjector(IInputInjector inner, ProcessAllowlist allowlist, IWindowSystem windows,
    Func<ScreenRect> virtualScreen, Func<ScreenPoint> cursor) : IInputInjector
{
    private const MouseFlags Ups = MouseFlags.LeftUp | MouseFlags.RightUp | MouseFlags.MiddleUp;
    private const MouseFlags NotMoves = ~(MouseFlags.Move | MouseFlags.Absolute | MouseFlags.VirtualDesk);

    public void Send(IReadOnlyList<InjectedInput> inputs)
    {
        if (!allowlist.AllowsAll && !inputs.All(IsUp))
        {
            var seen = new HashSet<long>();
            foreach (var input in inputs)
            {
                if (IsUp(input)) continue;
                var window = Target(input);
                if (!seen.Add(window)) continue;
                var name = window == 0 ? null : windows.GetProcessName(windows.GetProcessId(window));
                if (!allowlist.Allows(name)) throw new InputBlockedException(ProcessAllowlist.BlockedText(name));
            }
        }
        inner.Send(inputs);
    }

    private static bool IsUp(InjectedInput input) =>
        input.IsKeyboard ? input.Key.Up : (input.Mouse & NotMoves & Ups) != 0 && (input.Mouse & NotMoves & ~Ups) == 0;

    private long Target(InjectedInput input)
    {
        if (input.IsKeyboard) return windows.GetForegroundWindow();
        var point = (input.Mouse & MouseFlags.Absolute) != 0
            ? MouseCoordinates.Denormalize((input.X, input.Y), virtualScreen())
            : cursor();
        var window = windows.WindowFromPoint(point);
        return window == 0 ? 0 : windows.GetRoot(window);
    }
}
