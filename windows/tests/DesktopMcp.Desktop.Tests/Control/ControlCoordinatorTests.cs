using DesktopMcp.Core.Config;
using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Control;
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
