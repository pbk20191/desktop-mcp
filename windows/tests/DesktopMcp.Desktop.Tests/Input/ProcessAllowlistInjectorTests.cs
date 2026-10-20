using DesktopMcp.Core.Control;
using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Desktop.Tests.Tree;

namespace DesktopMcp.Desktop.Tests.Input;

internal sealed class AllowlistRig
{
    private sealed class Recorder(List<InjectedInput> sent) : IInputInjector
    {
        public void Send(IReadOnlyList<InjectedInput> inputs) => sent.AddRange(inputs);
    }

    public List<InjectedInput> Sent { get; } = [];
    public ProcessAllowlistInjector Injector { get; }

    public AllowlistRig(string[] allowed, Func<ScreenPoint, string> windowAt, string foreground, ScreenPoint cursor = default)
    {
        var system = new FakeWindowSystem();
        var handles = new Dictionary<string, long>();
        long Handle(string name)
        {
            if (handles.TryGetValue(name, out var existing)) return existing;
            var handle = 1000 + handles.Count;
            handles[name] = handle;
            system.Processes[handle] = (int)handle;
            system.ProcessNames[(int)handle] = name;
            return handle;
        }
        system.WindowAt = point => Handle(windowAt(point));
        system.Foreground.Enqueue(Handle(foreground));
        Injector = new ProcessAllowlistInjector(new Recorder(Sent), new ProcessAllowlist(allowed), system,
            () => new ScreenRect(0, 0, 1001, 1001), () => cursor);
    }
}

public class ProcessAllowlistInjectorTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1001, 1001);

    [Fact]
    public void AMouseEventOverAnAllowedProcessPasses()
    {
        var rig = new AllowlistRig(["notepad.exe"], _ => "notepad.exe", "code.exe");
        rig.Injector.Send([InjectedInput.MouseAt(MouseFlags.AbsoluteMove | MouseFlags.LeftDown, MouseCoordinates.Normalize(new(10, 10), Screen))]);
        Assert.Single(rig.Sent);
    }

    [Fact]
    public void AKeyForAForegroundOutsideTheListSendsNothing()
    {
        var rig = new AllowlistRig(["notepad.exe"], _ => "notepad.exe", "code.exe");
        var error = Assert.Throws<InputBlockedException>(() => rig.Injector.Send([InjectedInput.Keyboard(new(0x41, '\0', false))]));
        Assert.Equal(ProcessAllowlist.BlockedText("code.exe"), error.Message);
        Assert.Empty(rig.Sent);
    }

    [Fact]
    public void ABatchOfOnlyUpsIsNeverBlocked()
    {
        var rig = new AllowlistRig(["notepad.exe"], _ => "code.exe", "code.exe");
        rig.Injector.Send([InjectedInput.Keyboard(new(0x11, '\0', true)), InjectedInput.MouseAt(MouseFlags.AbsoluteMove | MouseFlags.LeftUp, (0, 0))]);
        Assert.Equal(2, rig.Sent.Count);
    }

    [Fact]
    public void AWheelWithoutCoordinatesUsesTheWindowUnderTheCursor()
    {
        var rig = new AllowlistRig(["notepad.exe"], p => p == new ScreenPoint(5, 5) ? "notepad.exe" : "code.exe", "code.exe", new(5, 5));
        rig.Injector.Send([InjectedInput.Wheel(120)]);
        Assert.Single(rig.Sent);
    }
}
