using DesktopMcp.Core.Config;
using DesktopMcp.Core.Control;

namespace DesktopMcp.Core.Tests;

public class ControlStateMachineTests
{
    private readonly StepClock _clock = new();
    private ControlStateMachine Machine(double idle = 10, bool enabled = true, long pixels = 500) =>
        new(new ControlSettings(enabled, "relaxed", idle, 500, pixels), _clock);
    private ControlStateMachine Ready() { var m = Machine(); m.Start(0); return m; }
    private PhysicalEvent Key() => new(PhysicalEventKind.Key, _clock.Stamp());
    private PhysicalEvent Raw(int dx, int dy, long device = 7) => new(PhysicalEventKind.RawMotion, _clock.Stamp(), device, dx, dy);
    private PhysicalEvent Foreign(int dx, int dy) => new(PhysicalEventKind.ForeignMotion, _clock.Stamp(), 0, dx, dy);
    private (ControlStateMachine M, long Token) InArmedAi()
    {
        var m = Ready();
        var begin = m.Begin();
        Assert.True(m.Arm(begin.Token));
        return (m, begin.Token);
    }

    [Fact]
    public void StartsReadyWithoutRecentInputAndUserWithIt()
    {
        Assert.Equal(ControlState.Ready, Ready().Status.State);
        _clock.Advance(100);
        var m = Machine();
        var recent = _clock.Stamp();
        _clock.Advance(2);
        Assert.Equal(new ControlStatus(ControlState.User, 1, 8.0, true, "relaxed", 10.0), m.Start(recent));
    }

    [Fact]
    public void PhysicalInputInReadyMovesToUserAndRejectsBegin()
    {
        var m = Ready();
        Assert.Equal(ControlState.User, m.Handle(Key())!.State);
        var error = Assert.Throws<ControlRejectedException>(() => m.Begin());
        Assert.Equal(ControlCodes.UserControl, error.Code);
        Assert.Equal(10.0, error.Status.WaitSeconds);
    }

    [Fact]
    public void UserReturnsToReadyAfterTheIdleTimeoutOnlyWithNothingHeld()
    {
        var m = Ready();
        m.Handle(Key());
        m.PhysicalHeld = true;
        _clock.Advance(10);
        Assert.Null(m.Tick());
        m.PhysicalHeld = false;
        Assert.Equal(ControlState.Ready, m.Tick()!.State);
    }

    [Fact]
    public void HeldPhysicalInputRejectsBegin()
    {
        var m = Ready();
        m.PhysicalHeld = true;
        Assert.Equal(ControlCodes.PhysicalInputHeld, Assert.Throws<ControlRejectedException>(() => m.Begin()).Code);
    }

    [Fact]
    public void BeginFromReadyEntersAiAndNeedsTheIndicatorOnce()
    {
        var m = Ready();
        var first = m.Begin();
        Assert.Equal((ControlState.Ai, 2L, true), (first.Changed!.State, first.Token, first.NeedsIndicator));
        m.Arm(first.Token);
        m.End(first.Token);
        var second = m.Begin();
        Assert.Equal((2L, (ControlStatus?)null, false), (second.Token, second.Changed, second.NeedsIndicator));
    }

    [Fact]
    public void RawPathLengthReachingTheThresholdTakesOver()
    {
        var (m, _) = InArmedAi();
        _clock.Advance(0.1);
        Assert.Null(m.Handle(Raw(300, 0)));
        _clock.Advance(0.1);
        Assert.Equal(ControlState.User, m.Handle(Raw(0, -200))!.State);   // path 500, displacement 360
    }

    [Fact]
    public void APauseOrAnotherDeviceRestartsThePath()
    {
        var (m, _) = InArmedAi();
        _clock.Advance(0.1); m.Handle(Raw(400, 0));
        _clock.Advance(0.6); Assert.Null(m.Handle(Raw(400, 0)));
        _clock.Advance(0.1); Assert.Null(m.Handle(Raw(400, 0, device: 8)));
    }

    [Fact]
    public void MotionStampedBeforeEnteringAiDoesNotCount()
    {
        var m = Ready();
        var early = Raw(600, 0);
        _clock.Advance(0.01);
        var begin = m.Begin();
        m.Arm(begin.Token);
        Assert.Null(m.Handle(early));
    }

    [Fact]
    public void MotionStampedAtTheInstantOfEnteringAiCounts()
    {
        var (m, _) = InArmedAi();
        Assert.Equal(ControlState.User, m.Handle(Raw(600, 0))!.State);
    }

    [Fact]
    public void ForeignMotionSumsPixelsAgainstItsOwnThreshold()
    {
        var m = Machine(pixels: 100);
        m.Start(0);
        var begin = m.Begin();
        Assert.True(m.Arm(begin.Token));
        _clock.Advance(0.1);
        Assert.Null(m.Handle(Raw(60, 80)));                                  // 100 units < 500
        _clock.Advance(0.1);
        Assert.Null(m.Handle(Foreign(30, 40)));                              // 50 px
        _clock.Advance(0.1);
        Assert.Equal(ControlState.User, m.Handle(Foreign(-30, -40))!.State); // 100 px
    }

    [Fact]
    public void ZeroLengthForeignMotionIsNotInput()
    {
        var m = Ready();
        var before = m.LastInput;
        _clock.Advance(1);
        Assert.Null(m.Handle(Foreign(0, 0)));
        Assert.Equal((ControlState.Ready, before), (m.Status.State, m.LastInput));
    }

    [Fact]
    public void KeysAreSwallowedWithoutTakeoverWhileArmedButTakeOverWhenNotArmed()
    {
        var (armed, _) = InArmedAi();
        Assert.Null(armed.Handle(Key()));
        Assert.True(armed.SuppressionWanted);
        var m = Ready();
        m.Begin();                                  // the indicator never acknowledged
        Assert.False(m.SuppressionWanted);
        Assert.Equal(ControlState.User, m.Handle(Key())!.State);
    }

    [Fact]
    public void AnEventSinceEnteringAiPreventsArming()
    {
        var m = Ready();
        var begin = m.Begin();
        m.Handle(new PhysicalEvent(PhysicalEventKind.RawMotion, _clock.Stamp(), 7, 1, 0));
        Assert.False(m.Arm(begin.Token));
    }

    [Fact]
    public void HeldInputOrATakeoverSinceEnteringAiPreventsArming()
    {
        var m = Ready();
        var begin = m.Begin();
        m.PhysicalHeld = true;
        Assert.False(m.Arm(begin.Token));
        m.PhysicalHeld = false;
        m.TakeoverPending = true;
        Assert.False(m.Arm(begin.Token));
        m.TakeoverPending = false;
        Assert.False(m.Arm(begin.Token - 1));
        Assert.True(m.Arm(begin.Token));
    }

    [Fact]
    public void CheckpointPreemptsAfterTakeoverWithExecutedSteps()
    {
        var (m, token) = InArmedAi();
        m.TakeoverPending = true;
        var error = Assert.Throws<ControlRejectedException>(() => m.Checkpoint(token, 3, requestClosed: false));
        Assert.Equal((ControlCodes.ControlPreempted, ControlState.User, 3L), (error.Code, error.Status.State, error.ExecutedSteps));
        Assert.Equal(ControlState.User, m.Handle(new PhysicalEvent(PhysicalEventKind.Hotkey, _clock.Stamp()))!.State);
        Assert.False(m.TakeoverPending);
    }

    [Fact]
    public void AHandledTakeoverReturnsToReadyAfterTheIdleTimeout()
    {
        var (m, _) = InArmedAi();
        m.TakeoverPending = true;
        Assert.Equal(ControlState.User, m.Status.State);
        Assert.False(m.SuppressionWanted);
        m.Handle(new PhysicalEvent(PhysicalEventKind.Hotkey, _clock.Stamp()));
        _clock.Advance(10);
        Assert.Equal(ControlState.Ready, m.Tick()!.State);
        Assert.Equal(5L, m.Begin().Token);
    }

    [Fact]
    public void BeginWithTheMonitorFlagsCopiesThemInFirst()
    {
        var m = Ready();
        Assert.Equal(ControlCodes.PhysicalInputHeld, Assert.Throws<ControlRejectedException>(() => m.Begin(physicalHeld: true, takeoverPending: false)).Code);
        Assert.True(m.PhysicalHeld);
        Assert.Equal(ControlCodes.UserControl, Assert.Throws<ControlRejectedException>(() => m.Begin(physicalHeld: false, takeoverPending: true)).Code);
        Assert.Equal(ControlState.Ai, m.Begin(physicalHeld: false, takeoverPending: false).Changed!.State);
    }

    [Fact]
    public void CheckpointPreemptsAClosedRequestAndAStaleToken()
    {
        var (m, token) = InArmedAi();
        Assert.Throws<ControlRejectedException>(() => m.Checkpoint(token, 0, requestClosed: true));
        Assert.Throws<ControlRejectedException>(() => m.Checkpoint(token - 1, 0, requestClosed: false));
        m.Checkpoint(token, 0, requestClosed: false);
    }

    [Fact]
    public void CheckpointPreemptsAnUnarmedCall()
    {
        var m = Ready();
        var begin = m.Begin();
        Assert.Equal(ControlCodes.ControlPreempted, Assert.Throws<ControlRejectedException>(() => m.Checkpoint(begin.Token, 0, false)).Code);
    }

    [Fact]
    public void TheLeaseKeepsAiFifteenSecondsAfterTheLastCall()
    {
        var (m, token) = InArmedAi();
        m.End(token);
        _clock.Advance(14.9);
        Assert.Null(m.Tick());
        _clock.Advance(0.1);
        Assert.Equal(ControlState.Ready, m.Tick()!.State);
    }

    [Fact]
    public void AnExpiredLeaseWithRecentInputGoesToUser()
    {
        var (m, token) = InArmedAi();
        m.End(token);
        _clock.Advance(10);
        m.Handle(Key());                            // swallowed while armed
        _clock.Advance(5);
        var status = m.Tick()!;
        Assert.Equal((ControlState.User, 5.0), (status.State, status.WaitSeconds));
    }

    [Fact]
    public void FaultIsStickyAndRecoverGoesToUser()
    {
        var (m, token) = InArmedAi();
        Assert.Equal(ControlState.Unavailable, m.Fault()!.State);
        Assert.False(m.SuppressionWanted);
        Assert.Equal(ControlCodes.ControlUnavailable, Assert.Throws<ControlRejectedException>(() => m.Begin()).Code);
        Assert.Throws<ControlRejectedException>(() => m.Checkpoint(token, 0, false));
        Assert.Equal(new ControlStatus(ControlState.User, 4, 10.0, true, "relaxed", 10.0), m.Recover());
    }

    [Fact]
    public void StopIsFinal()
    {
        var m = Ready();
        Assert.Equal(ControlState.Unavailable, m.Stop()!.State);
        Assert.Null(m.Start(0));
        Assert.Null(m.Fault());
        Assert.Null(m.Recover());
        Assert.Null(m.Handle(new PhysicalEvent(PhysicalEventKind.Hotkey, _clock.Stamp())));
        Assert.Equal(ControlCodes.ControlUnavailable, Assert.Throws<ControlRejectedException>(() => m.Begin()).Code);
    }

    [Fact]
    public void NeverStartedIsUnavailable()
    {
        var m = Machine();
        Assert.Equal(new ControlStatus(ControlState.Unavailable, 0, 0, true, "relaxed", 10.0), m.Status);
        Assert.Null(m.Handle(Key()));
        Assert.Equal(ControlCodes.ControlUnavailable, Assert.Throws<ControlRejectedException>(() => m.Begin()).Code);
    }

    [Fact]
    public void DisabledNeverTicksOrGates()
    {
        var m = Machine(enabled: false);
        Assert.Null(m.Handle(Key()));
        Assert.Equal(new BeginResult(0, null, false), m.Begin());
        Assert.Equal(new ControlStatus(ControlState.Disabled, 0, 0, false, "relaxed", 10.0), m.Status);
    }
}
