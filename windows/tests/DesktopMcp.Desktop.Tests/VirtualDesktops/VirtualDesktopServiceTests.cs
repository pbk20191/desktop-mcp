using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop.Tests.VirtualDesktops;

public class VirtualDesktopServiceTests
{
    private static readonly Guid First = new("033294df-1cac-47b0-a62e-d3e53b23f4bc");
    private static readonly Guid Second = new("c4e16f48-2232-4ef9-8671-d901d748ad83");

    private sealed class FakeSource : IVirtualDesktopSource
    {
        public Func<long, bool> OnCurrent { get; init; } = _ => true;
        public Func<IReadOnlyList<Guid?>> Ids { get; init; } = () => [First, Second];
        public Func<Guid> Current { get; init; } = () => Second;
        public bool IsOnCurrentDesktop(long window) => OnCurrent(window);
        public IReadOnlyList<Guid?> DesktopIds() => Ids();
        public Guid CurrentDesktopId() => Current();
    }

    /// <summary>A shell that hangs in every call until <see cref="Release"/> is set, counting the calls that started.</summary>
    private sealed class BlockingSource : IVirtualDesktopSource, IDisposable
    {
        private int _calls;

        public ManualResetEventSlim Release { get; } = new();
        public int Calls => Volatile.Read(ref _calls);

        public bool IsOnCurrentDesktop(long window) => Enter(() => true);
        public IReadOnlyList<Guid?> DesktopIds() => Enter<IReadOnlyList<Guid?>>(() => [First]);
        public Guid CurrentDesktopId() => Enter(() => First);
        public void Dispose() => Release.Set();

        private T Enter<T>(Func<T> result)
        {
            Interlocked.Increment(ref _calls);
            Release.Wait();
            return result();
        }
    }

    private static VirtualDesktopService Service(IVirtualDesktopSource source, Func<Guid, string?>? names = null, int timeoutMs = 2000) =>
        new(source, names ?? (_ => null), TimeSpan.FromMilliseconds(timeoutMs));

    [Fact]
    public void NamesComeFromTheRegistryElseDesktopN()
    {
        var (active, all) = Service(new FakeSource(), id => id == Second ? "Work" : null).GetDesktops();
        Assert.Equal(
            [new VirtualDesktopInfo("{033294DF-1CAC-47B0-A62E-D3E53B23F4BC}", "Desktop 1"), new VirtualDesktopInfo("{C4E16F48-2232-4EF9-8671-D901D748AD83}", "Work")],
            all);
        Assert.Equal("Work", active.Name);
    }

    [Fact]
    public void AnUnreadableDesktopIsSkippedButKeepsItsNumber()
    {
        var (_, all) = Service(new FakeSource { Ids = () => [null, Second] }).GetDesktops();
        Assert.Equal("Desktop 2", Assert.Single(all).Name);
    }

    [Fact]
    public void ACurrentDesktopMissingFromTheListIsUnknown()
    {
        var (active, _) = Service(new FakeSource { Current = () => Guid.Empty }).GetDesktops();
        Assert.Equal(new VirtualDesktopInfo("{00000000-0000-0000-0000-000000000000}", "Unknown"), active);
    }

    [Fact]
    public void FailuresAndHangsFallBackToTheDefaultDesktop()
    {
        var failing = Service(new FakeSource { Ids = () => throw new InvalidOperationException("no shell") }).GetDesktops();
        Assert.Equal(VirtualDesktopInfo.Default, failing.Active);
        Assert.Equal([VirtualDesktopInfo.Default], failing.All);
        using var hung = new BlockingSource();
        var hanging = Service(hung, timeoutMs: 50).GetDesktops();
        Assert.Equal(VirtualDesktopInfo.Default, hanging.Active);
    }

    [Fact]
    public void WindowFilterKeepsOrderAndFailsOpen()
    {
        var source = new FakeSource { OnCurrent = window => window == 3 ? throw new InvalidOperationException("bad window") : window != 2 };
        Assert.Equal([1L, 3L], Service(source).OnCurrentDesktop([1, 2, 3]));
    }

    [Fact]
    public void AHungCallTimesOutToTheFallback()
    {
        using var source = new BlockingSource();
        var service = Service(source, timeoutMs: 50);

        var (active, all) = service.GetDesktops();

        Assert.Equal(VirtualDesktopInfo.Default, active);
        Assert.Equal([VirtualDesktopInfo.Default], all);
        Assert.Equal(1, source.Calls);
        Assert.True(service.CallAbandoned);
    }

    [Fact]
    public void WhileAnAbandonedCallIsStillBlockedNewCallsFailOpenAtOnceWithoutCallingTheShell()
    {
        using var source = new BlockingSource();
        var service = Service(source, timeoutMs: 50);
        Assert.Equal(VirtualDesktopInfo.Default, service.GetDesktops().Active);
        Assert.True(service.CallAbandoned);

        var desktops = service.GetDesktops();
        var windows = service.OnCurrentDesktop([4, 5]);

        Assert.Equal(VirtualDesktopInfo.Default, desktops.Active);
        Assert.Equal([4L, 5L], windows);
        Assert.Equal(1, source.Calls); // neither call reached the shell
    }

    [Fact]
    public void AfterTheBlockedCallReturnsTheNextCallGoesThrough()
    {
        using var source = new BlockingSource();
        var service = Service(source, timeoutMs: 50);
        Assert.Equal(VirtualDesktopInfo.Default, service.GetDesktops().Active);
        Assert.Equal(VirtualDesktopInfo.Default, service.GetDesktops().Active);
        Assert.Equal(1, source.Calls);

        source.Release.Set();
        Assert.True(SpinWait.SpinUntil(() => !service.CallInFlight, TimeSpan.FromSeconds(10)));
        Assert.Equal(2, source.Calls); // the abandoned read went on to CurrentDesktopId

        var (active, all) = service.GetDesktops();
        Assert.Equal(new VirtualDesktopInfo("{033294DF-1CAC-47B0-A62E-D3E53B23F4BC}", "Desktop 1"), active);
        Assert.Equal([active], all);
        Assert.Equal(4, source.Calls); // and this read made its own DesktopIds and CurrentDesktopId calls
    }
}

public class VirtualDesktopServiceConcurrencyTests
{
    private static readonly Guid Only = new("033294df-1cac-47b0-a62e-d3e53b23f4bc");

    /// <summary>A healthy but slow shell: the first DesktopIds call holds until released; tracks overlapping calls.</summary>
    private sealed class SlowSource : IVirtualDesktopSource, IDisposable
    {
        private int _idCalls;
        private int _running;
        private int _maxRunning;

        public ManualResetEventSlim FirstEntered { get; } = new();
        public ManualResetEventSlim ReleaseFirst { get; } = new();
        public int IdCalls => Volatile.Read(ref _idCalls);
        public int MaxRunning => Volatile.Read(ref _maxRunning);

        public bool IsOnCurrentDesktop(long window) => true;
        public Guid CurrentDesktopId() => Only;
        public void Dispose() => ReleaseFirst.Set();

        public IReadOnlyList<Guid?> DesktopIds()
        {
            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref _maxRunning, running);
            try
            {
                if (Interlocked.Increment(ref _idCalls) == 1)
                {
                    FirstEntered.Set();
                    ReleaseFirst.Wait();
                }
                return [Only];
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }
    }

    [Fact]
    public async Task ConcurrentHealthyCallsBothGetRealResultsOneAtATime()
    {
        using var source = new SlowSource();
        var service = new VirtualDesktopService(source, _ => null, TimeSpan.FromSeconds(10));

        var first = Task.Run(service.GetDesktops);
        Assert.True(source.FirstEntered.Wait(TimeSpan.FromSeconds(10)));
        var second = Task.Run(service.GetDesktops); // waits for the gate while the first call runs
        await Task.Delay(100);
        Assert.Equal(1, source.IdCalls);
        source.ReleaseFirst.Set();

        var results = await Task.WhenAll(first, second);

        var expected = new VirtualDesktopInfo("{033294DF-1CAC-47B0-A62E-D3E53B23F4BC}", "Desktop 1");
        Assert.All(results, result => Assert.Equal(expected, result.Active));
        Assert.Equal(2, source.IdCalls);
        Assert.Equal(1, source.MaxRunning);
    }

    [Fact]
    public void AnUnreadableRegistryNameFallsBackToDesktopNForThatDesktopOnly()
    {
        var second = new Guid("c4e16f48-2232-4ef9-8671-d901d748ad83");
        var source = new ListSource([Only, second]);
        var service = new VirtualDesktopService(
            source, id => id == Only ? throw new System.Security.SecurityException("denied") : "Work", TimeSpan.FromSeconds(10));

        var (_, all) = service.GetDesktops();

        Assert.Equal(["Desktop 1", "Work"], all.Select(desktop => desktop.Name));
    }

    private sealed class ListSource(IReadOnlyList<Guid?> ids) : IVirtualDesktopSource
    {
        public bool IsOnCurrentDesktop(long window) => true;
        public IReadOnlyList<Guid?> DesktopIds() => ids;
        public Guid CurrentDesktopId() => Only;
    }
}

[Trait("Category", "Desktop")]
public class ShellVirtualDesktopsTests
{
    [Fact]
    public void TheCurrentDesktopIsOneOfTheListedDesktops()
    {
        var (active, all) = new VirtualDesktopService().GetDesktops();
        Assert.NotEqual(VirtualDesktopInfo.Default, active);
        Assert.Contains(active, all);
    }

    [Fact]
    public void AnotherBuildsInterfaceIdFailsOpenToTheDefaultDesktop()
    {
        // The other supported build's IVirtualDesktopManagerInternal IID stands in for a build whose IIDs changed.
        var otherBuild = Environment.OSVersion.Version.Build >= 26100 ? 22621 : 26100;
        var source = new ShellVirtualDesktops(otherBuild);
        Assert.ThrowsAny<Exception>(() => source.DesktopIds());
        Assert.ThrowsAny<Exception>(() => source.CurrentDesktopId());

        var service = new VirtualDesktopService(source, _ => null, VirtualDesktopService.CallTimeout);
        var (active, all) = service.GetDesktops();
        Assert.Equal(VirtualDesktopInfo.Default, active);
        Assert.Equal([VirtualDesktopInfo.Default], all);
    }
}
