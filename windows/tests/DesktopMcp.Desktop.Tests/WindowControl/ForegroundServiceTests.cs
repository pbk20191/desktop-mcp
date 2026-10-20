using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.WindowControl;

namespace DesktopMcp.Desktop.Tests.WindowControl;

/// <summary>
/// The target is window 42 on thread 7; the foreground starts at window 99 on thread 5; the service's threads are 3.
/// The fake calls log what they are asked and make the target the foreground when the named call happens; delays move
/// the fake clock.
/// </summary>
internal sealed class ForegroundRig
{
    public const long Target = 42;
    public const uint TargetThread = 7;
    public const uint OtherThread = 5;
    public const uint ServiceThread = 3;

    public ForegroundRig()
    {
        Windows = new RigWindows(this);
        Calls = new RigCalls(this);
        Service = new ForegroundService(Windows, Calls, (delay, _) => { Time.Now += delay; return Task.CompletedTask; }, Time);
    }

    public WindowResponse Response { get; init; } = WindowResponse.Responding;
    public bool Minimized { get; set; }
    public string? TakesForegroundOn { get; init; }
    public long ForegroundWindow { get; set; } = 99;
    public bool ForegroundThreadSameAsTarget { get; init; }

    public ManualTime Time { get; } = new();
    public RigWindows Windows { get; }
    public RigCalls Calls { get; }
    public ForegroundService Service { get; }

    internal sealed class RigWindows(ForegroundRig rig) : IWindowSystem
    {
        public IReadOnlyList<long> EnumerateTopLevel() => [Target, rig.ForegroundWindow];
        public bool IsWindow(long window) => true;
        public bool IsVisible(long window) => true;
        public bool IsMinimized(long window) => window == Target && rig.Minimized;
        public bool IsMaximized(long window) => false;
        public int GetProcessId(long window) => 100;
        public string GetClassName(long window) => "AppWindow";
        public long FindWindow(string className) => 0;
        public long GetForegroundWindow() => rig.ForegroundWindow;
        public long GetRoot(long window) => window;
        public string? GetProcessName(int processId) => "notepad.exe";

        public uint GetThreadId(long window) =>
            window == Target || (window == rig.ForegroundWindow && rig.ForegroundThreadSameAsTarget) ? TargetThread : OtherThread;

        public ScreenRect GetBounds(long window) => new(0, 0, 800, 600);
        public long WindowFromPoint(ScreenPoint point) => Target;
        public string GetTitle(long window) => window == Target ? "Notepad" : "Other";
        public WindowResponse CheckResponding(long window) => rig.Response;
    }

    internal sealed class RigCalls(ForegroundRig rig) : IForegroundCalls
    {
        public List<string> Log { get; } = [];
        public List<(long Window, int X, int Y, int Width, int Height, uint Flags)> Positions { get; } = [];
        public int SetWindowPosError { get; init; }

        private bool Record(string name, string call)
        {
            Log.Add(call);
            if (name == rig.TakesForegroundOn) rig.ForegroundWindow = Target;
            return true;
        }

        public bool ShowWindowAsync(long window, int command)
        {
            if (command == ForegroundService.SwRestore) rig.Minimized = false;
            return Record("ShowWindowAsync", $"ShowWindowAsync({window}, {(command == ForegroundService.SwRestore ? "SW_RESTORE" : command)})");
        }

        public bool SetForegroundWindow(long window) => Record("SetForegroundWindow", $"SetForegroundWindow({window})");
        public bool BringWindowToTop(long window) => Record("BringWindowToTop", $"BringWindowToTop({window})");

        public bool AttachThreadInput(uint attach, uint attachTo, bool doAttach) =>
            Record(doAttach ? "AttachThreadInput" : "DetachThreadInput", $"AttachThreadInput({attach}, {attachTo}, {doAttach})");

        public bool AllowSetForegroundWindow(uint processId) => Record("AllowSetForegroundWindow", $"AllowSetForegroundWindow({processId:X})");

        public bool SetWindowPos(long window, long insertAfter, int x, int y, int width, int height, uint flags, out int lastError)
        {
            Positions.Add((window, x, y, width, height, flags));
            lastError = SetWindowPosError;
            Record("SetWindowPos", $"SetWindowPos({window}, {insertAfter}, {x}, {y}, {width}, {height}, 0x{flags:X})");
            return SetWindowPosError == 0;
        }

        public uint GetCurrentThreadId() => ServiceThread;
        public bool IsIconic(long window) => window == Target && rig.Minimized;
    }
}

public class ForegroundServiceTests
{
    [Fact]
    public async Task AHungWindowGetsNoCall()
    {
        var rig = new ForegroundRig { Response = WindowResponse.NotResponding };
        var error = await Assert.ThrowsAsync<DesktopException>(() => rig.Service.EnsureKeyboardTargetAsync(42, "Notepad", default));
        Assert.Equal("Notepad is not responding; nothing was typed.", error.Message);
        Assert.Empty(rig.Calls.Log);
    }

    [Fact]
    public async Task AHungWindowIsNotSwitchedTo()
    {
        var rig = new ForegroundRig { Response = WindowResponse.Hung };
        var error = await Assert.ThrowsAsync<DesktopException>(() => rig.Service.SwitchToAsync(42, default));
        Assert.Equal("Notepad is not responding", error.Message);
        Assert.Empty(rig.Calls.Log);
    }

    [Fact]
    public async Task AMinimisedWindowIsRestoredAsynchronously()
    {
        var rig = new ForegroundRig { Minimized = true, TakesForegroundOn = "SetForegroundWindow" };
        var result = await rig.Service.SwitchToAsync(42, default);
        Assert.Equal(new ForegroundResult(true, true), result);
        Assert.Equal(["ShowWindowAsync(42, SW_RESTORE)", "SetForegroundWindow(42)"], rig.Calls.Log.Take(2));
    }

    [Fact]
    public async Task AttachThreadInputIsTheFallbackAndIsUndone()
    {
        var rig = new ForegroundRig { TakesForegroundOn = "AttachThreadInput" };
        await rig.Service.EnsureKeyboardTargetAsync(42, "Notepad", default);
        var attached = rig.Calls.Log.Count(c => c.StartsWith("AttachThreadInput(") && c.EndsWith(", True)"));
        Assert.True(attached > 0);
        Assert.Equal(attached, rig.Calls.Log.Count(c => c.StartsWith("AttachThreadInput(") && c.EndsWith(", False)")));
    }

    [Fact]
    public async Task TheAttachSequenceFollowsUpstreamAndDetachesInReverse()
    {
        var rig = new ForegroundRig { TakesForegroundOn = "SetWindowPos" };
        await rig.Service.EnsureKeyboardTargetAsync(42, "Notepad", default);
        Assert.Equal(
        [
            "SetForegroundWindow(42)",
            "AllowSetForegroundWindow(FFFFFFFF)",
            "AttachThreadInput(3, 5, True)",
            "AttachThreadInput(3, 7, True)",
            "SetForegroundWindow(42)",
            "BringWindowToTop(42)",
            "SetWindowPos(42, 0, 0, 0, 0, 0, 0x4043)",
            "AttachThreadInput(3, 7, False)",
            "AttachThreadInput(3, 5, False)",
        ], rig.Calls.Log);
    }

    [Fact]
    public async Task APopupOfTheSameThreadCountsForKeys()
    {
        var rig = new ForegroundRig { ForegroundWindow = 77, ForegroundThreadSameAsTarget = true };
        await rig.Service.EnsureKeyboardTargetAsync(42, "Notepad", default);
        Assert.DoesNotContain("SetForegroundWindow(42)", rig.Calls.Log);
    }

    [Fact]
    public async Task FailureAfterOneSecondIsAClearError()
    {
        var rig = new ForegroundRig { TakesForegroundOn = null };
        var start = rig.Time.Now;
        var error = await Assert.ThrowsAsync<DesktopException>(() => rig.Service.EnsureKeyboardTargetAsync(42, "Notepad", default));
        Assert.Equal("Could not bring Notepad to the foreground; nothing was typed.", error.Message);
        Assert.InRange(rig.Time.Now - start, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.2));
        Assert.False((await rig.Service.SwitchToAsync(42, default)).Succeeded);
    }

    [Fact]
    public async Task ResizeChecksTheWindowAndPlacesItAsynchronously()
    {
        var rig = new ForegroundRig();
        await new WindowPlacement(rig.Windows, rig.Calls).ResizeAsync(42, "Notepad", new ScreenRect(10, 20, 410, 320), default);
        Assert.Equal([(42L, 10, 20, 400, 300, 0x4014u)], rig.Calls.Positions);
    }

    [Fact]
    public async Task ResizeOfAHungWindowIsRefused()
    {
        var rig = new ForegroundRig { Response = WindowResponse.NotResponding };
        var error = await Assert.ThrowsAsync<DesktopException>(() =>
            new WindowPlacement(rig.Windows, rig.Calls).ResizeAsync(42, "Notepad", new ScreenRect(0, 0, 100, 100), default));
        Assert.Equal("Notepad is not responding", error.Message);
        Assert.Empty(rig.Calls.Positions);
    }

    [Fact]
    public async Task AFailedResizeSaysWhy()
    {
        var rig = new ForegroundRig();
        var calls = new ForegroundRig.RigCalls(rig) { SetWindowPosError = 5 };
        var error = await Assert.ThrowsAsync<DesktopException>(() =>
            new WindowPlacement(rig.Windows, calls).ResizeAsync(42, "Notepad", new ScreenRect(0, 0, 100, 100), default));
        Assert.Equal($"Failed to resize Notepad: {new System.ComponentModel.Win32Exception(5).Message}", error.Message);
    }

    /// <summary>Real foreground change between two of the test's own windows (no input is injected).</summary>
    [Fact]
    [Trait("Category", "Desktop")]
    public async Task SwitchesToTheTestWindowFromAnotherTestWindow()
    {
        using var first = new ActiveTestWindow();
        using var second = new TestWindow(left: 600, top: 160);
        var result = await new ForegroundService().SwitchToAsync(second.Handle, default);
        Assert.True(result.Succeeded);
        Assert.Equal(second.Handle, TestForeground.GetForegroundWindow());
    }
}
