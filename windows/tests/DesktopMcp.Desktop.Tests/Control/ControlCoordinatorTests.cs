using DesktopMcp.Core.Config;
using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Control;
using DesktopMcp.Desktop;
using DesktopMcp.Desktop.Input;

namespace DesktopMcp.Desktop.Tests.Control;

public class ControlCoordinatorTests
{
    [Fact]
    public async Task BeginShowsTheIndicatorBeforeArmingSuppression()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Indicator.HoldShow();
        var begin = rig.Control.BeginCallAsync(default);
        await rig.Indicator.ShowStarted;
        Assert.False(rig.Monitor.Filter.Suppressing);
        rig.Indicator.CompleteShow();
        using var lease = await begin;
        Assert.True(rig.Monitor.Filter.Suppressing);
        Assert.Equal(ControlState.Ai, rig.Control.Status().State);
    }

    [Fact]
    public async Task AFailedIndicatorRejectsWithControlUnavailable()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Indicator.FailShow();
        var error = await Assert.ThrowsAsync<ControlRejectedException>(() => rig.Control.BeginCallAsync(default));
        Assert.Equal((ControlCodes.ControlUnavailable, ControlState.Unavailable), (error.Code, error.Status.State));
    }

    [Fact]
    public async Task ARenderFailureRejectsWithoutFaulting()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Indicator.FailRender();
        var error = await Assert.ThrowsAsync<ControlRejectedException>(() => rig.Control.BeginCallAsync(default));
        Assert.Equal(ControlCodes.ControlUnavailable, error.Code);
        Assert.NotEqual(ControlState.Unavailable, error.Status.State);
        Assert.False(rig.Monitor.Filter.Suppressing);
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.NotEqual(ControlState.Unavailable, rig.Control.Status().State);
        Assert.Equal(0, rig.Monitor.Rehooks);
    }

    [Fact]
    public async Task CancellingDuringTheShowEndsTheCall()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Indicator.NeverCompleteShow();
        using var cts = new CancellationTokenSource();
        var begin = rig.Control.BeginCallAsync(cts.Token);
        await rig.Indicator.ShowStarted;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => begin);
        Assert.False(rig.Monitor.Filter.Suppressing);
        rig.Advance(ControlStateMachine.LeaseSeconds + 0.1); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Ready, rig.Control.Status().State);  // the call ended, so the lease ran out
    }

    [Fact]
    public async Task TakeoverReleasesHeldInputExceptKeysTheUserHolds()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        rig.Control.Ledger.Press("key:ctrl", () => { }, () => rig.Ups.Add("ctrl"));
        rig.Control.Ledger.Press("key:vk:10", () => { }, () => rig.Ups.Add("shift"));
        rig.PhysicalDownDelivered(0xA0);                      // a physical left Shift down that reached the system (OnKey + AfterKeyForwarded with result 0 while suppression was briefly off)
        rig.RawMove(600, 0);
        await rig.Control.RunOnceAsync();
        Assert.Equal(["ctrl"], rig.Ups);
        Assert.Empty(rig.Control.Ledger.Pending);
        using (lease.Enter())
            Assert.Equal(ControlCodes.ControlPreempted, Assert.Throws<ControlRejectedException>(rig.Control.Checkpoint.Checkpoint).Code);
    }

    [Fact]
    public async Task TheTakeoverChordReturnsToReadyAfterTheIdleTimeout()
    {
        var rig = await ControlRig.StartedAsync();
        using (await rig.Control.BeginCallAsync(default)) { }
        foreach (var vk in new[] { 0xA2, 0xA0, 0xA4 }) Assert.Equal(HookVerdict.Swallow, rig.Key(vk, true));
        Assert.Equal(HookVerdict.Swallow, rig.Key(0x08, true));
        Assert.True(rig.Monitor.Filter.TakeoverPending);
        await rig.Control.RunOnceAsync();
        Assert.False(rig.Monitor.Filter.TakeoverPending);  // preflight F3: the filter's flag is cleared with the machine's
        Assert.Equal(ControlState.User, rig.Control.Status().State);
        foreach (var vk in new[] { 0x08, 0xA4, 0xA0, 0xA2 }) rig.Key(vk, false);
        await rig.Control.RunOnceAsync();
        rig.Advance(10); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Ready, rig.Control.Status().State);
        using var lease = await rig.Control.BeginCallAsync(default);
        Assert.Equal(ControlState.Ai, rig.Control.Status().State);
    }

    [Fact]
    public async Task TheHeartbeatRenewsTheDeadlineOnlyWhileArmed()
    {
        var rig = await ControlRig.StartedAsync();
        await rig.Control.RunOnceAsync();
        Assert.False(rig.Monitor.Filter.Suppressing);
        using var lease = await rig.Control.BeginCallAsync(default);
        rig.Advance(0.5); await rig.Control.RunOnceAsync();
        Assert.Equal(rig.Stamp() + rig.Seconds(1), rig.Monitor.Filter.Deadline);
    }

    [Fact]
    public async Task OnlyTheHeartbeatRenewsTheDeadline()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        var armed = rig.Monitor.Filter.Deadline;
        rig.Advance(0.5);
        Assert.Equal(ControlState.Ai, rig.Control.Status().State);
        using (lease.Enter()) rig.Control.Checkpoint.Checkpoint();
        Assert.Equal(armed, rig.Monitor.Filter.Deadline);
    }

    [Fact]
    public async Task AStoppedHeartbeatLetsTheDeadlineLapse()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        rig.Control.HeartbeatPaused = true;
        rig.Advance(1.1);
        Assert.Equal(HookVerdict.Pass, rig.Monitor.Filter.OnKey(0x41, true, 0, 0));
        rig.Control.HeartbeatPaused = false;
        await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);   // the lapse was a fault
        Assert.Equal(1, rig.Monitor.Rehooks);
    }

    [Fact]
    public async Task LeavingTheInputDesktopFailsOpenAndPreempts()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        rig.Control.Ledger.Press("mouse:left", () => { }, () => rig.Ups.Add("left"));
        rig.Interactive = false;
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        Assert.Equal(["left"], rig.Ups);
        Assert.False(rig.Monitor.Filter.Suppressing);
        Assert.Contains(rig.Control.Status().Generation, rig.Indicator.Hides);
        using (lease.Enter()) Assert.Throws<ControlRejectedException>(rig.Control.Checkpoint.Checkpoint);
    }

    [Fact]
    public async Task RecoveryNeedsARehookAndGoesToUser()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Interactive = false;
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        rig.Interactive = true;
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        rig.Monitor.LastRehook = rig.Stamp();
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.User, rig.Control.Status().State);
        rig.Advance(10); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Ready, rig.Control.Status().State);
    }

    [Fact]
    public async Task RecoveryClearsFailOpenAndTheTakeoverBeforeSuppressionResumes()
    {
        var rig = await ControlRig.StartedAsync();
        using (await rig.Control.BeginCallAsync(default)) { }
        rig.Monitor.Filter.RequestFailOpen();
        await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        rig.Monitor.LastRehook = rig.Stamp() + 1;
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.User, rig.Control.Status().State);
        Assert.False(rig.Monitor.Filter.FailOpenRequested);
        rig.Advance(10); await rig.Control.RunOnceAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        Assert.True(rig.Monitor.Filter.Suppressing);
    }

    [Fact]
    public async Task AnUnhealthyIndicatorFaultsAndRecoveryRecreatesIt()
    {
        var rig = await ControlRig.StartedAsync();
        var broken = rig.Indicator;
        broken.Healthy = false;
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        rig.Monitor.LastRehook = rig.Stamp() + 1;
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.User, rig.Control.Status().State);
        Assert.NotSame(broken, rig.Indicator);
        Assert.True(broken.Disposed);
        rig.Advance(10); await rig.Control.RunOnceAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        Assert.Single(rig.Indicator.Shows);
    }

    [Fact]
    public async Task AStaleDispatcherBeatFailsOpen()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Monitor.LastBeat = rig.Stamp() - rig.Seconds(1.5);
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
    }

    [Fact]
    public async Task CheckpointsOutsideACallDoNothingAndAClosedLeasePreempts()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Control.Checkpoint.Checkpoint();
        var lease = await rig.Control.BeginCallAsync(default);
        var scope = lease.Enter();
        rig.Control.Checkpoint.RecordStep();
        lease.Dispose();
        var error = Assert.Throws<ControlRejectedException>(rig.Control.Checkpoint.Checkpoint);
        Assert.Equal(1L, error.ExecutedSteps);
        scope.Dispose();
    }

    [Fact]
    public async Task ACallScopeFlowsIntoAwaitsAndEndsWithItsScope()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        Assert.Null(ControlCall.Current);
        using (lease.Enter())
        {
            await Task.Yield();
            Assert.Same(lease, ControlCall.Current);
            rig.Control.Checkpoint.Checkpoint();
        }
        Assert.Null(ControlCall.Current);
    }

    [Fact]
    public async Task InputUnavailableMapsToPreemptedOrUnavailable()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        Assert.Equal(ControlCodes.ControlPreempted, rig.Control.Interrupted(new InputUnavailableException("AI input is unavailable"), lease).Code);
        rig.Interactive = false; rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlCodes.ControlUnavailable, rig.Control.Interrupted(new InputUnavailableException("AI input is unavailable"), lease).Code);
    }

    [Fact]
    public async Task InterruptedPassesARejectionThroughAndCarriesTheSteps()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        using (lease.Enter()) { rig.Control.Checkpoint.RecordStep(); rig.Control.Checkpoint.RecordStep(); }
        var rejection = new ControlRejectedException(ControlCodes.UserControl, rig.Control.Status());
        Assert.Same(rejection, rig.Control.Interrupted(rejection, lease));
        Assert.Equal(2L, rig.Control.Interrupted(new InputUnavailableException("AI input is unavailable"), lease).ExecutedSteps);
        Assert.True(ControlErrors.IsInterruption(rejection));
        Assert.True(ControlErrors.IsInterruption(new InputUnavailableException("x")));
        Assert.False(ControlErrors.IsInterruption(new InvalidOperationException("x")));
    }

    [Fact]
    public async Task AFailedStartStaysUnavailableAndRetries()
    {
        var rig = ControlRig.Create(monitorStartFailures: 1);
        await rig.Control.StartAsync(default);
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        rig.Advance(5); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Ready, rig.Control.Status().State);
    }

    [Fact]
    public async Task ANonInteractiveStartWaitsForTheDesktop()
    {
        var rig = ControlRig.Create();
        rig.Interactive = false;
        await rig.Control.StartAsync(default);
        Assert.Equal(0, rig.Monitor.Starts);
        rig.Advance(5); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        rig.Interactive = true;
        rig.Advance(4); await rig.Control.RunOnceAsync();
        Assert.Equal(0, rig.Monitor.Starts);   // retried every 5 s, not every beat
        rig.Advance(1); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Ready, rig.Control.Status().State);
    }

    [Fact]
    public async Task StateChangesAreRaisedInGenerationOrder()
    {
        var rig = await ControlRig.StartedAsync();
        var seen = new List<long>();
        rig.Control.StateChanged += s => seen.Add(s.Generation);
        using (await rig.Control.BeginCallAsync(default)) { }
        rig.RawMove(600, 0); await rig.Control.RunOnceAsync();
        Assert.Equal(seen.Order(), seen);
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task AChangeMadeByARejectedBeginIsStillApplied()
    {
        var rig = await ControlRig.StartedAsync();
        var seen = new List<ControlStatus>();
        rig.Control.StateChanged += seen.Add;
        using (await rig.Control.BeginCallAsync(default))
            rig.Control.Ledger.Press("key:ctrl", () => { }, () => rig.Ups.Add("ctrl"));
        rig.Advance(ControlStateMachine.LeaseSeconds + 0.1);
        rig.PhysicalDownDelivered(0x41);   // held, not yet drained: Begin's own tick ends the lease, then it rejects
        var error = await Assert.ThrowsAsync<ControlRejectedException>(() => rig.Control.BeginCallAsync(default));
        Assert.Equal(ControlCodes.PhysicalInputHeld, error.Code);
        Assert.Equal([ControlState.Ai, ControlState.Ready], seen.Select(s => s.State));
        Assert.Equal(["ctrl"], rig.Ups);
        Assert.Contains(seen[^1].Generation, rig.Indicator.Hides);
    }

    [Fact]
    public async Task StopReleasesEverythingAndStaysUnavailable()
    {
        var rig = await ControlRig.StartedAsync();
        var seen = new List<ControlStatus>();
        rig.Control.StateChanged += seen.Add;
        var lease = await rig.Control.BeginCallAsync(default);
        rig.Control.Ledger.Press("mouse:right", () => { }, () => rig.Ups.Add("right"));
        await rig.Control.StopAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        Assert.Equal(ControlState.Unavailable, seen[^1].State);
        Assert.False(rig.Monitor.Filter.Suppressing);
        Assert.Equal(["right"], rig.Ups);
        Assert.Equal(1, rig.Monitor.Stops);
        Assert.True(rig.Indicator.Disposed);
        Assert.Throws<InputUnavailableException>(() => rig.Control.Ledger.Press("key:ctrl", () => { }, () => { }));
        lease.Dispose();
        rig.Advance(6); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        Assert.Equal(1, rig.Monitor.Starts);
    }

    [Fact]
    public async Task AChordBetweenBeatsPreemptsTheNextCheckpoint()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        rig.Chord();
        using (lease.Enter())
            Assert.Equal(ControlCodes.ControlPreempted, Assert.Throws<ControlRejectedException>(rig.Control.Checkpoint.Checkpoint).Code);
    }

    [Fact]
    public async Task AFailOpenBetweenBeatsPreemptsTheNextCheckpoint()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        rig.Monitor.Filter.RequestFailOpen();
        using (lease.Enter())
            Assert.Equal(ControlCodes.ControlPreempted, Assert.Throws<ControlRejectedException>(rig.Control.Checkpoint.Checkpoint).Code);
    }

    [Fact]
    public async Task ALapsedDeadlinePreemptsTheNextCheckpoint()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);
        rig.Advance(1.1);
        using (lease.Enter())
            Assert.Equal(ControlCodes.ControlPreempted, Assert.Throws<ControlRejectedException>(rig.Control.Checkpoint.Checkpoint).Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATakeoverOrFailOpenDuringBeginsEnableLeavesTheLedgerBlocked(bool chord)
    {
        var rig = await ControlRig.StartedAsync();
        rig.Control.StateChanged += s =>
        {
            if (s.State != ControlState.Ai) return;
            // After Begin entered ai and before it enables the ledger: the hook blocks the ledger without our lock.
            if (chord) rig.Chord();
            else rig.Monitor.Filter.RequestFailOpen();
        };
        using var lease = await rig.Control.BeginCallAsync(default);
        Assert.Throws<InputUnavailableException>(() => rig.Control.Ledger.Press("key:ctrl", () => { }, () => { }));
        Assert.False(rig.Monitor.Filter.Suppressing);
        using (lease.Enter()) Assert.Throws<ControlRejectedException>(rig.Control.Checkpoint.Checkpoint);
    }

    [Fact]
    public async Task InputDuringTheShowRefusesToArm()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Indicator.HoldShow();
        var begin = rig.Control.BeginCallAsync(default);
        await rig.Indicator.ShowStarted;
        Assert.Equal(HookVerdict.Pass, rig.Key(0x41, true));   // not drained: Arm reads the monitor's stamp
        rig.Indicator.CompleteShow();
        using var lease = await begin;
        Assert.False(rig.Monitor.Filter.Suppressing);
        using (lease.Enter())
            Assert.Equal(ControlCodes.ControlPreempted, Assert.Throws<ControlRejectedException>(rig.Control.Checkpoint.Checkpoint).Code);
    }

    [Fact]
    public async Task BeginCopiesThePendingTakeoverIn()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Chord();
        foreach (var vk in new[] { 0x08, 0xA4, 0xA0, 0xA2 }) rig.Key(vk, false);
        Assert.False(rig.Monitor.Filter.Held);
        var error = await Assert.ThrowsAsync<ControlRejectedException>(() => rig.Control.BeginCallAsync(default));
        Assert.Equal((ControlCodes.UserControl, ControlState.User), (error.Code, error.Status.State));
    }

    [Fact]
    public async Task AStaleShowFailureDoesNotFaultRecoveredControl()
    {
        var rig = await ControlRig.StartedAsync();
        var first = rig.Indicator;
        first.HoldShow();
        var begin = rig.Control.BeginCallAsync(default);
        await first.ShowStarted;
        first.Healthy = false;
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        rig.Monitor.LastRehook = rig.Stamp() + 1;
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.User, rig.Control.Status().State);
        Assert.NotSame(first, rig.Indicator);
        first.FailShow();
        first.CompleteShow();
        Assert.Equal(ControlCodes.ControlUnavailable, (await Assert.ThrowsAsync<ControlRejectedException>(() => begin)).Code);
        Assert.Equal(ControlState.User, rig.Control.Status().State);
        Assert.Equal(1, rig.Monitor.Rehooks);
    }

    [Fact]
    public async Task AJoinerRetriesAFailedShow()
    {
        var rig = await ControlRig.StartedAsync();
        rig.Indicator.FailRender();
        await Assert.ThrowsAsync<ControlRejectedException>(() => rig.Control.BeginCallAsync(default));
        Assert.Equal(ControlState.Ai, rig.Control.Status().State);   // unarmed until the lease runs out
        using var lease = await rig.Control.BeginCallAsync(default);
        Assert.Equal(2, rig.Indicator.Shows.Count);
        Assert.True(rig.Monitor.Filter.Suppressing);
        using (lease.Enter()) rig.Control.Checkpoint.Checkpoint();
    }

    [Fact]
    public async Task ConcurrentCallsKeepTheirOwnLeases()
    {
        var rig = await ControlRig.StartedAsync();
        using var a = await rig.Control.BeginCallAsync(default);
        using var b = await rig.Control.BeginCallAsync(default);
        using var barrier = new Barrier(2);
        Task Flow(ControlLease lease) => Task.Run(async () =>
        {
            using (lease.Enter())
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
                await Task.Yield();
                Assert.Same(lease, ControlCall.Current);
                rig.Control.Checkpoint.RecordStep();
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
                Assert.Same(lease, ControlCall.Current);
                rig.Control.Checkpoint.Checkpoint();
            }
        });
        await Task.WhenAll(Flow(a), Flow(b));
        Assert.Equal((1L, 1L), (a.ExecutedSteps, b.ExecutedSteps));
        Assert.Null(ControlCall.Current);
    }

    [Fact]
    public async Task ACancelledStartStillRetries()
    {
        var rig = ControlRig.Create();
        rig.Monitor.HoldStart();
        using var cts = new CancellationTokenSource();
        var start = rig.Control.StartAsync(cts.Token);
        await rig.Monitor.StartEntered;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.True(rig.Control.LoopStarted);
        rig.Monitor.ReleaseStart();
        rig.Advance(5); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Ready, rig.Control.Status().State);
    }

    [Fact]
    public async Task AStopDuringTheStartIsLoggedNotThrown()
    {
        var rig = ControlRig.Create();
        rig.Monitor.HoldStart();
        var start = rig.Control.StartAsync(default);
        await rig.Monitor.StartEntered;
        rig.Monitor.StopThrows = true;
        await rig.Control.StopAsync();
        rig.Monitor.ReleaseStart();
        await start;
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        Assert.Equal(2, rig.Monitor.Stops);
        Assert.False(rig.Control.LoopStarted);
        Assert.Equal(2, rig.Logs.Count(l => l.Level == DesktopLogLevel.Error && l.Message.Contains("failed to stop")));
    }

    [Fact]
    public async Task RepeatedStartFailuresWarnOnce()
    {
        var rig = ControlRig.Create(monitorStartFailures: 3);
        await rig.Control.StartAsync(default);
        for (var i = 0; i < 2; i++) { rig.Advance(5); await rig.Control.RunOnceAsync(); }
        Assert.Equal(3, rig.Monitor.Starts);
        Assert.Single(rig.Logs, l => l.Level == DesktopLogLevel.Warning && l.Message.Contains("failed to start"));
        rig.Advance(5); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Ready, rig.Control.Status().State);
    }

    [Fact]
    public async Task AnUnsupportedRuntimeArchitectureIsNotRetried()
    {
        var rig = ControlRig.Create();
        var calls = 0;
        rig.EnsureRuntime = () =>
        {
            calls++;
            throw new PlatformNotSupportedException("The Windows App SDK runtime has no Arm build.");
        };
        await rig.Control.StartAsync(default);
        for (var i = 0; i < 3; i++) { rig.Advance(5); await rig.Control.RunOnceAsync(); }
        Assert.Equal(1, calls);
        Assert.Equal(0, rig.Monitor.Starts);
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        Assert.Single(rig.Logs, l => l.Level >= DesktopLogLevel.Warning);
    }

    [Fact]
    public async Task RepeatedRuntimeStartFailuresWarnOnceAndRetry()
    {
        var rig = ControlRig.Create();
        var failures = 3;
        rig.EnsureRuntime = () =>
        {
            if (failures-- > 0) throw new System.Runtime.InteropServices.COMException("The runtime failed to start.", unchecked((int)0x80004005));
        };
        await rig.Control.StartAsync(default);
        for (var i = 0; i < 2; i++) { rig.Advance(5); await rig.Control.RunOnceAsync(); }
        Assert.Equal(0, rig.Monitor.Starts);
        Assert.Single(rig.Logs, l => l.Level == DesktopLogLevel.Warning && l.Message.Contains("Windows App SDK"));
        Assert.Equal(2, rig.Logs.Count(l => l.Level == DesktopLogLevel.Debug && l.Message.Contains("Windows App SDK")));
        rig.Advance(5); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Ready, rig.Control.Status().State);
    }

    [Fact]
    public async Task TheLedgerStaysBlockedAfterStartUntilACallEntersAi()
    {
        var rig = await ControlRig.StartedAsync();
        Assert.Throws<InputUnavailableException>(() => rig.Control.Ledger.Press("key:ctrl", () => { }, () => { }));
        using var lease = await rig.Control.BeginCallAsync(default);
        rig.Control.Ledger.Press("key:ctrl", () => { }, () => rig.Ups.Add("ctrl"));
        Assert.Single(rig.Control.Ledger.Pending);
    }

    [Fact]
    public async Task AChordSeenBeforeItsHotkeyIsQueuedReportsUserAndResolvesNextBeat()
    {
        var rig = await ControlRig.StartedAsync();
        using var lease = await rig.Control.BeginCallAsync(default);   // armed ai: the chord's other keys change nothing
        var before = rig.Control.Status().Generation;
        var inside = false;
        rig.OnBlockLedger = () =>
        {
            // The hook has set the takeover flag and not yet queued its Hotkey: one beat runs here.
            rig.OnBlockLedger = null;
            inside = true;
            Assert.True(rig.Control.RunOnceAsync().IsCompletedSuccessfully);
            Assert.Equal(ControlState.User, rig.Control.Status().State);
            var begin = rig.Control.BeginCallAsync(default);
            Assert.Equal(ControlCodes.UserControl, Assert.IsType<ControlRejectedException>(begin.Exception?.InnerException).Code);
            Assert.Equal(before, rig.Control.Status().Generation);    // reported as user, not yet a change
            Assert.True(rig.Monitor.Filter.TakeoverPending);
            Assert.False(rig.Monitor.Filter.Suppressing);
        };
        rig.Chord();
        Assert.True(inside);
        await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.User, rig.Control.Status().State);
        Assert.True(rig.Control.Status().Generation > before);
        Assert.False(rig.Monitor.Filter.TakeoverPending);
    }

    [Fact]
    public async Task ALostHotkeyRecoversThroughFailOpen()
    {
        var rig = await ControlRig.StartedAsync(queueCapacity: 1);
        using var lease = await rig.Control.BeginCallAsync(default);
        Assert.Equal(HookVerdict.Swallow, rig.Key(0xA2, true)); await rig.Control.RunOnceAsync();
        Assert.Equal(HookVerdict.Swallow, rig.Key(0xA0, true)); await rig.Control.RunOnceAsync();
        Assert.Equal(HookVerdict.Swallow, rig.Key(0xA4, true));   // not drained: the queue is full
        Assert.Equal(HookVerdict.Swallow, rig.Key(0x08, true));   // the chord; its Hotkey cannot be queued
        Assert.True(rig.Monitor.Filter.TakeoverPending);
        Assert.True(rig.Monitor.Filter.FailOpenRequested);
        Assert.False(rig.Monitor.Filter.Suppressing);
        await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.Unavailable, rig.Control.Status().State);
        Assert.Equal(1, rig.Monitor.Rehooks);
        Assert.True(rig.Monitor.Filter.TakeoverPending);           // no Hotkey was drained to clear it
        rig.Monitor.LastRehook = rig.Stamp() + 1;
        rig.Advance(0.3); await rig.Control.RunOnceAsync();
        Assert.Equal(ControlState.User, rig.Control.Status().State);
        Assert.False(rig.Monitor.Filter.TakeoverPending);
        Assert.False(rig.Monitor.Filter.FailOpenRequested);
    }

    [Fact]
    public void TheLogThrottleLetsANewErrorOrAnIntervalThrough()
    {
        var throttle = new LogThrottle(50);
        Assert.True(throttle.Allow("a", 0));
        Assert.False(throttle.Allow("a", 49));
        Assert.True(throttle.Allow("b", 49));
        Assert.True(throttle.Allow("a", 50));
        Assert.False(throttle.Allow("a", 99));
        Assert.True(throttle.Allow("a", 100));
        var once = new LogThrottle(long.MaxValue);
        Assert.True(once.Allow("a", 0));
        Assert.False(once.Allow("a", long.MaxValue / 2));
        once.Reset();
        Assert.True(once.Allow("a", 1));
    }

    [Fact]
    public async Task DisabledControlNeverTouchesTheDesktop()
    {
        var control = new DisabledControl(new ControlSettings(false, "relaxed", 10, 500, 500));
        await control.StartAsync(default);
        using var lease = await control.BeginCallAsync(default);
        using (lease.Enter()) control.Checkpoint.Checkpoint();
        Assert.Equal(ControlState.Disabled, control.Status().State);
        Assert.Equal(0L, lease.Token);
    }

    [Fact]
    public void CreateReturnsDisabledControlWhenNotEnabled() =>
        Assert.IsType<DisabledControl>(ControlCoordinator.Create(new ControlSettings(false, "relaxed", 10, 500, 500)));

    [Fact]
    public void ALeaseEndsOnce()
    {
        var ended = 0;
        var lease = new ControlLease(7, _ => ended++);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal((1, true, 7L), (ended, lease.Closed, lease.Token));
    }
}
