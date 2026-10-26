using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Input;

namespace DesktopMcp.Desktop.Tests;

public class GuardedInjectorTests
{
    private const nint TestWindowHandle = 100;
    private const nint UserWindow = 200;

    private sealed class Recorder : IInputInjector
    {
        public List<IReadOnlyList<InjectedInput>> Batches { get; } = [];

        public int Calls => Batches.Count;

        public void Send(IReadOnlyList<InjectedInput> inputs) => Batches.Add([.. inputs]);
    }

    private sealed class Rig
    {
        public Rig() =>
            Guard = new GuardedInjector(Inner, TestWindowHandle, () => Foreground, w => w == TestWindowHandle ? 7u : w == UserWindow ? 9u : 0u,
                _ => true, _ => TestWindowHandle, () => new ScreenRect(0, 0, 1001, 1001), () => new ScreenPoint(5, 5));

        public nint Foreground { get; set; } = TestWindowHandle;

        public Recorder Inner { get; } = new();

        public GuardedInjector Guard { get; }
    }

    private static InjectedInput Key(ushort vk, bool up) => InjectedInput.Keyboard(new KeyInput(vk, '\0', up));

    [Fact]
    public void AMissingWindowIsRejected() =>
        Assert.Throws<ArgumentException>(() => new GuardedInjector(new Recorder(), 0));

    [Fact]
    public void AnUpMatchingAForwardedKeyDownIsForwardedAfterTheForegroundChanges()
    {
        var rig = new Rig();
        rig.Guard.Send([Key(0x41, false)]);
        rig.Foreground = UserWindow;

        rig.Guard.Send([Key(0x41, true)]);
        Assert.Equal([Key(0x41, true)], rig.Inner.Batches[^1]);

        Assert.Throws<InvalidOperationException>(() => rig.Guard.Send([Key(0x41, true)])); // already released
        Assert.Throws<InvalidOperationException>(() => rig.Guard.Send([Key(0x42, true)])); // never pressed
        Assert.Throws<InvalidOperationException>(() => rig.Guard.Send([Key(0x41, false)]));
        Assert.Equal(2, rig.Inner.Calls);
    }

    [Fact]
    public void AnUpMatchingAForwardedButtonDownIsForwardedWithoutItsMove()
    {
        var rig = new Rig();
        rig.Guard.Send([InjectedInput.MouseAt(MouseFlags.AbsoluteMove | MouseFlags.LeftDown, (6554, 6554))]);
        rig.Foreground = UserWindow;

        rig.Guard.Send([InjectedInput.MouseAt(MouseFlags.AbsoluteMove | MouseFlags.LeftUp, (65535, 65535))]);
        var up = Assert.Single(rig.Inner.Batches[^1]);
        Assert.Equal((MouseFlags.LeftUp, 0, 0), (up.Mouse, up.X, up.Y));

        Assert.Throws<InvalidOperationException>(() => rig.Guard.Send([InjectedInput.MouseAt(MouseFlags.AbsoluteMove | MouseFlags.RightUp, (0, 0))]));
        Assert.Throws<InvalidOperationException>(() => rig.Guard.Send([InjectedInput.MouseAt(MouseFlags.AbsoluteMove, (0, 0))]));
        Assert.Equal(2, rig.Inner.Calls);
    }

    [Fact]
    public void AMixedBatchIsRefusedWhenTheForegroundIsElsewhere()
    {
        var rig = new Rig();
        rig.Guard.Send([Key(0x11, false)]);
        rig.Foreground = UserWindow;
        var error = Assert.Throws<InvalidOperationException>(() => rig.Guard.Send([Key(0x11, true), Key(0x41, false)]));
        Assert.StartsWith("Test input guard: the foreground window 0xC8", error.Message);
        Assert.EndsWith("; nothing was sent.", error.Message);
        Assert.Equal(1, rig.Inner.Calls);
    }

    [Fact]
    public void AMouseEventOffTheTestWindowIsRefused()
    {
        var inner = new Recorder();
        var guard = new GuardedInjector(inner, TestWindowHandle, () => TestWindowHandle, _ => 7, _ => true,
            p => p.X < 500 ? TestWindowHandle : UserWindow, () => new ScreenRect(0, 0, 1001, 1001), () => new ScreenPoint(5, 5));
        guard.Send([InjectedInput.MouseAt(MouseFlags.AbsoluteMove, (6554, 0))]);
        Assert.Throws<InvalidOperationException>(() => guard.Send([InjectedInput.MouseAt(MouseFlags.AbsoluteMove, (6554, 0)),
            InjectedInput.MouseAt(MouseFlags.AbsoluteMove, (65535, 0))]));
        Assert.Throws<InvalidOperationException>(() => guard.Send([new InjectedInput(false, MouseFlags.Move, 1, 0, 0, default)]));
        Assert.Equal(1, inner.Calls);
    }

    /// <summary>
    /// Reads only: the test window is shown without activation, so the user's window stays in the foreground and the
    /// guard must refuse; the recorder (not SendInput) would receive anything forwarded.
    /// </summary>
    [Fact]
    [Trait("Category", "Desktop")]
    public void RefusesAndForwardsNothingWhenTheTestWindowIsNotForeground()
    {
        using var window = new TestWindow();
        var recorder = new Recorder();
        var guard = new GuardedInjector(recorder, window.Handle);
        var error = Assert.Throws<InvalidOperationException>(() => guard.Send([Key(0x41, false), Key(0x41, true)]));
        Assert.StartsWith("Test input guard: ", error.Message);
        Assert.EndsWith("; nothing was sent.", error.Message);
        Assert.Equal(0, recorder.Calls);
    }
}
