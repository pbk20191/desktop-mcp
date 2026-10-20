using System.Diagnostics;
using DesktopMcp.Core.Control;
using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Control;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Input;
using Microsoft.Win32;

namespace DesktopMcp.Desktop.Tests.Control;

/// <summary>
/// The real hooks and Raw Input window on a real dispatcher (Decisions 3). Each test installs <c>WH_KEYBOARD_LL</c> and
/// <c>WH_MOUSE_LL</c>, brings an <see cref="ActiveTestWindow"/> to the front and sends unsigned input to it only through
/// <see cref="ForeignInjector"/> (absolute moves, never relative: preflight F6). Suppression is armed for at most 2 s and
/// turned off in <c>finally</c>. Run in the user's interactive session with hands off the mouse and keyboard: input to other
/// windows may be swallowed for up to 2 s, and the stalled-dispatcher test makes input lag for 3 s. A guard failure (the
/// foreground was taken) sends nothing: rerun.
/// </summary>
[Trait("Category", "Desktop")]
[Collection(InputMonitorCollection.Name)]
public class InputMonitorDesktopTests
{
    private const ushort KeyA = 0x41;
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaxArmed = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task HooksAndRawInputStartAndStopOnTheDispatcher()
    {
        await using var rig = await Rig.StartAsync(startMonitor: false);
        var watch = Stopwatch.StartNew();
        await rig.Monitor.StartAsync(default);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"the start took {watch.Elapsed}");

        var beat = rig.Monitor.LastBeat;
        await Task.Delay(Settle);
        Assert.True(rig.Monitor.LastBeat > beat, "the 0.1 s tick did not beat");

        await rig.Monitor.StopAsync();
        rig.Drain();
        try
        {
            rig.Filter.SetSuppression(true, rig.Now + rig.Seconds(1)); // a hook still installed would swallow
            var before = rig.EditText;
            rig.Foreign.Tap(KeyA);
            Assert.True(await rig.EditChangesAsync(before), "a key sent after StopAsync did not reach the edit");
            Assert.Empty(rig.Drain());
        }
        finally
        {
            rig.Disarm();
        }
    }

    [Fact]
    public async Task RawInputReachesTheMonitorWindow()
    {
        await using var rig = await Rig.StartAsync();
        var client = TestForeground.ClientRect(rig.Target.Handle);
        var before = rig.Monitor.RawMessages;

        // Absolute moves inside the test window and back (preflight F6). Injected raw input has the null device and the
        // absolute flag, so it reaches WM_INPUT, where the test counter counts it, but never the filter.
        rig.Foreign.MoveTo(client.Center);
        rig.Foreign.MoveTo(new ScreenPoint(client.Center.X + 1, client.Center.Y));
        rig.Foreign.MoveTo(client.Center);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (rig.Monitor.RawMessages <= before && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(rig.Monitor.RawMessages > before, "no WM_INPUT reached the message-only window (Decisions 23: switch to the hidden top-level fallback)");
        Assert.DoesNotContain(rig.Drain(), e => e.Kind == PhysicalEventKind.RawMotion);
    }

    [Fact]
    public async Task ForeignInputIsSwallowedOnlyWhileSuppressing()
    {
        await using var rig = await Rig.StartAsync();
        var before = rig.EditText;
        await using (rig.ArmWithRenewals())
        {
            rig.Foreign.Tap(KeyA);
            await Task.Delay(Settle);
            Assert.Equal(before, rig.EditText);
        }
        rig.Disarm();

        rig.Foreign.Tap(KeyA);
        Assert.True(await rig.EditChangesAsync(before), "a key sent after suppression ended did not reach the edit");
    }

    [Fact]
    public async Task SuppressionLapsesWithinOneSecondWhenTheHeartbeatStops()
    {
        await using var rig = await Rig.StartAsync();
        try
        {
            rig.Filter.SetSuppression(true, rig.Now + rig.Seconds(1)); // armed once, never renewed
            await Task.Delay(TimeSpan.FromSeconds(1.2));
            var before = rig.EditText;
            rig.Foreign.Tap(KeyA);
            Assert.True(await rig.EditChangesAsync(before), "a key sent after the deadline lapsed did not reach the edit");
            Assert.True(rig.Filter.FailOpenRequested);
        }
        finally
        {
            rig.Disarm();
        }
    }

    [Fact]
    public async Task AStalledDispatcherCannotLockInput()
    {
        await using var rig = await Rig.StartAsync();
        var limit = TimeSpan.FromMilliseconds(LowLevelHooksTimeoutMs() + 500);
        var dispatcher = await rig.Host.GetAsync(default);
        var before = rig.EditText;
        Task stall;
        TimeSpan elapsed;
        await using (rig.ArmWithRenewals())
        {
            stall = dispatcher.InvokeAsync(() => Thread.Sleep(3000));
            await Task.Delay(100); // the dispatcher is now asleep, the hooks with it
            var watch = Stopwatch.StartNew();
            var send = Task.Run(() => rig.Foreign.Tap(KeyA)); // SendInput itself waits for the hooks
            var changed = await rig.EditChangesAsync(before, TimeSpan.FromSeconds(3));
            elapsed = watch.Elapsed;
            await send;
            Assert.True(changed, "a key sent while the dispatcher was stalled never reached the edit");
        }
        rig.Disarm();
        await stall;
        Assert.True(elapsed <= limit, $"the key took {elapsed.TotalMilliseconds:0} ms (limit {limit.TotalMilliseconds:0} ms)");
    }

    [Fact]
    public async Task ForeignInjectedAndPhysicalChordTakesOver()
    {
        await using var rig = await Rig.StartAsync();
        var before = rig.EditText;
        ushort[] chord = [0x11, 0x12, 0x10]; // Ctrl, Alt, Shift
        try
        {
            await using (rig.ArmWithRenewals())
            {
                foreach (var key in chord) rig.Foreign.Key(key, up: false);
                rig.Foreign.Key(0x08, up: false); // Backspace
                await Task.Delay(100);
                Assert.True(rig.Filter.TakeoverPending);
                Assert.False(rig.Filter.Suppressing); // the chord released suppression itself
            }
            rig.Foreign.Key(0x08, up: true);
            foreach (var key in chord) rig.Foreign.Key(key, up: true);
            await Task.Delay(Settle);

            Assert.Equal(before, rig.EditText); // no stray Backspace, no Ctrl+Alt+Shift combination
            Assert.False(rig.Filter.Held);
            foreach (var key in chord.Append((ushort)0x08)) Assert.False(rig.Filter.KeyDelivered(key));
        }
        finally
        {
            rig.Disarm();
            rig.Filter.ClearTakeover();
        }
    }

    [Fact]
    public async Task HooksAreReinstalledEveryFiveSeconds()
    {
        await using var rig = await Rig.StartAsync();
        var client = TestForeground.ClientRect(rig.Target.Handle);
        var installed = rig.Monitor.LastRehook;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        var x = 0;
        var observedAfter = false;
        while (DateTime.UtcNow < deadline && !observedAfter)
        {
            var reinstalled = rig.Monitor.LastRehook > installed;
            rig.Drain();
            rig.Foreign.MoveTo(new ScreenPoint(client.Center.X + (x++ % 2), client.Center.Y));
            await Task.Delay(100);
            var seen = rig.Drain().Any(e => e.Kind == PhysicalEventKind.ForeignMotion);
            if (!reinstalled) Assert.True(seen, "foreign input was not observed before the reinstall");
            else observedAfter = seen;
        }
        Assert.True(rig.Monitor.LastRehook > installed, "the hooks were not reinstalled within 6 s");
        Assert.True(observedAfter, "foreign input was not observed after the reinstall");
    }

    [Fact]
    public void InteractiveDesktopIsAvailableInThisSession() => Assert.True(InteractiveDesktop.IsAvailable());

    private static int LowLevelHooksTimeoutMs()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return key?.GetValue("LowLevelHooksTimeout") switch
        {
            int value when value > 0 => value,
            string text when int.TryParse(text, out var value) && value > 0 => value,
            _ => 1000,
        };
    }

    /// <summary>A real monitor on its own dispatcher host, the active test window with its edit focused, and the injector.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private Rig(ActiveTestWindow target, DispatcherHost host, PhysicalInputFilter filter)
        {
            Target = target;
            Host = host;
            Filter = filter;
            Monitor = new InputMonitor(host, filter, Stopwatch.GetTimestamp);
            Foreign = new ForeignInjector(target.Handle);
        }

        public ActiveTestWindow Target { get; }
        public DispatcherHost Host { get; }
        public PhysicalInputFilter Filter { get; }
        public InputMonitor Monitor { get; }
        public ForeignInjector Foreign { get; }

        public long Now => Stopwatch.GetTimestamp();

        public string EditText => Target.Window.ReadEditText();

        public static async Task<Rig> StartAsync(bool startMonitor = true)
        {
            var target = new ActiveTestWindow();
            var host = new DispatcherHost();
            var filter = new PhysicalInputFilter(Stopwatch.GetTimestamp, Stopwatch.Frequency, ForeignInjectedPolicy.Physical, static () => { });
            var rig = new Rig(target, host, filter);
            try
            {
                // Focus the edit with one signed click (our own input, which the hooks ignore).
                var edit = TestForeground.Center(target.Window.EditHandle);
                var normalized = MouseCoordinates.Normalize(edit, ForeignInjector.VirtualScreen());
                new GuardedInjector(new SendInputInjector(), target.Handle).Send([
                    InjectedInput.MouseAt(MouseFlags.AbsoluteMove | MouseFlags.LeftDown, normalized),
                    InjectedInput.MouseAt(MouseFlags.AbsoluteMove | MouseFlags.LeftUp, normalized),
                ]);
                await Task.Delay(100);
                if (startMonitor) await rig.Monitor.StartAsync(default);
                return rig;
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }
        }

        public long Seconds(double seconds) => (long)(seconds * Stopwatch.Frequency);

        public void Disarm() => Filter.SetSuppression(false, 0);

        /// <summary>Arms suppression and renews its 1 s deadline every 100 ms, as the coordinator's heartbeat would, until
        /// disposed, the chord takes over, or 2 s have passed. Disposing stops the renewals but leaves suppression as it is.</summary>
        public IAsyncDisposable ArmWithRenewals()
        {
            var stop = new CancellationTokenSource(MaxArmed);
            Filter.SetSuppression(true, Now + Seconds(1));
            var renewals = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested && !Filter.TakeoverPending)
                {
                    Filter.SetSuppression(true, Now + Seconds(1));
                    try { await Task.Delay(100, stop.Token); }
                    catch (OperationCanceledException) { }
                }
            });
            return new Renewals(stop, renewals);
        }

        /// <summary>Waits until the edit's text differs from <paramref name="before"/>.</summary>
        public async Task<bool> EditChangesAsync(string before, TimeSpan? limit = null)
        {
            var deadline = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(2));
            while (DateTime.UtcNow < deadline)
            {
                if (EditText != before) return true;
                await Task.Delay(20);
            }
            return EditText != before;
        }

        public List<PhysicalEvent> Drain()
        {
            var events = new List<PhysicalEvent>();
            while (Filter.Events.TryRead(out var e)) events.Add(e);
            return events;
        }

        public async ValueTask DisposeAsync()
        {
            Disarm();
            try
            {
                await Monitor.StopAsync();
                await Host.ShutdownAsync();
            }
            finally
            {
                Target.Dispose();
            }
        }

        private sealed class Renewals(CancellationTokenSource stop, Task loop) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await stop.CancelAsync();
                await loop;
                stop.Dispose();
            }
        }
    }
}
