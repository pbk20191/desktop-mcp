using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Control;
using DesktopMcp.Desktop.Input;

namespace DesktopMcp.Desktop.Tests.Control;

public class PhysicalInputFilterTests
{
    private const uint KeyInjected = 0x10;
    private long _now = 1_000;
    private int _blocks;
    private PhysicalInputFilter Filter(ForeignInjectedPolicy foreign = ForeignInjectedPolicy.Physical) =>
        new(() => _now, TimeSpan.TicksPerSecond, foreign, () => _blocks++);
    private static List<PhysicalEvent> Drain(PhysicalInputFilter f) { var list = new List<PhysicalEvent>(); while (f.Events.TryRead(out var e)) list.Add(e); return list; }

    [Fact]
    public void OurSignatureIsIgnored()
    {
        var f = Filter();
        f.SetSuppression(true, _now + 10_000_000);
        Assert.Equal(HookVerdict.Pass, f.OnKey(0x41, true, KeyInjected, InputSignature.ExtraInfo));
        Assert.Empty(Drain(f));
        Assert.Equal(0, f.LastPhysical);
    }

    // The enums are internal, so a public theory takes their underlying values.
    [Theory]
    [InlineData((int)ForeignInjectedPolicy.Physical, true, 1)]
    [InlineData((int)ForeignInjectedPolicy.Ignore, false, 0)]
    public void ForeignInjectedInputFollowsThePolicy(int policy, bool swallowed, int events)
    {
        var f = Filter((ForeignInjectedPolicy)policy);
        f.SetSuppression(true, _now + 10_000_000);
        Assert.Equal(swallowed ? HookVerdict.Swallow : HookVerdict.Pass, f.OnKey(0x41, true, KeyInjected, 0));
        Assert.Equal(events, Drain(f).Count);
    }

    [Fact]
    public void KeysAreQueuedAndSwallowedWhileTheDeadlineIsAhead()
    {
        var f = Filter();
        f.SetSuppression(true, _now + 1);
        Assert.Equal(HookVerdict.Swallow, f.OnKey(0x41, true, 0, 0));
        Assert.Equal(PhysicalEventKind.Key, Assert.Single(Drain(f)).Kind);
        Assert.True(f.Held);
    }

    [Fact]
    public void PastTheDeadlineTheHookPassesAndRequestsFailOpen()
    {
        var f = Filter();
        f.SetSuppression(true, _now - 1);
        Assert.Equal(HookVerdict.Pass, f.OnMouse(0x200, 0, 5, 5, 0, 0));
        Assert.True(f.FailOpenRequested);
        Assert.False(f.Suppressing);
        Assert.Equal(1, _blocks);
    }

    [Fact]
    public void ChordWithRightHandModifiersTakesOver()
    {
        var f = Filter();
        f.SetSuppression(true, _now + 10_000_000);
        foreach (var vk in new uint[] { 0xA3, 0xA4, 0xA1 }) f.OnKey(vk, true, 0, 0);   // RCtrl, LAlt, RShift
        Assert.Equal(HookVerdict.Swallow, f.OnKey(0x08, true, 0, 0));
        Assert.True(f.TakeoverPending);
        Assert.False(f.Suppressing);
        Assert.Equal(PhysicalEventKind.Hotkey, Drain(f)[^1].Kind);
    }

    [Fact]
    public void ChordKeysStaySwallowedUntilReleased()
    {
        var f = TookOver();
        Assert.Equal(HookVerdict.Swallow, f.OnKey(0x08, false, 0, 0));
        Assert.Equal(HookVerdict.Swallow, f.OnKey(0xA3, false, 0, 0));
        Assert.Equal(HookVerdict.Pass, f.OnKey(0x41, true, 0, 0));   // suppression is off, A is not quarantined
    }

    [Fact]
    public void AutoRepeatedBackspaceDoesNotRetrigger()
    {
        var f = TookOver();
        f.ClearTakeover();
        f.SetSuppression(true, _now + 10_000_000);
        Drain(f);
        f.OnKey(0x08, true, 0, 0);                                    // repeat while still down and quarantined
        Assert.False(f.TakeoverPending);
    }

    [Fact]
    public void WithoutSuppressionTheChordIsOrdinaryInput()
    {
        var f = Filter();
        foreach (var vk in new uint[] { 0x11, 0x12, 0x10 }) f.OnKey(vk, true, 0, 0);
        Assert.Equal(HookVerdict.Pass, f.OnKey(0x08, true, 0, 0));
        Assert.False(f.TakeoverPending);
    }

    [Fact]
    public void ADownIsDeliveredOnlyWhenForwardedAndNotSwallowedDownstream()
    {
        var f = Filter();
        f.OnKey(0xA2, true, 0, 0); f.AfterKeyForwarded(0xA2, true, 0);
        f.OnKey(0x41, true, 0, 0); f.AfterKeyForwarded(0x41, true, 1);
        Assert.True(f.KeyDelivered(0xA2));
        Assert.False(f.KeyDelivered(0x41));
        f.OnMouse(0x20B, 0x0002_0000, 0, 0, 0, 0); f.AfterMouseForwarded(0x20B, 0x0002_0000, 0);   // XBUTTON2 down
        Assert.True(f.ButtonDelivered(5));
    }

    [Fact]
    public void AFullQueueFailsOpen()
    {
        var f = new PhysicalInputFilter(() => _now, TimeSpan.TicksPerSecond, ForeignInjectedPolicy.Physical, () => _blocks++, queueCapacity: 2);
        for (var i = 0; i < 3; i++) f.OnMouse(0x200, 0, i, 0, 0, 0);
        Assert.True(f.FailOpenRequested);
    }

    [Fact]
    public void RawMouseKeepsOnlyRelativeMotionFromARealDevice()
    {
        Assert.Equal((7L, 3, -4), RawMouse.Relative(0, 7, 0, 3, -4));
        Assert.Null(RawMouse.Relative(0, 0, 0, 3, -4));       // null device
        Assert.Null(RawMouse.Relative(0, 7, 1, 3, -4));       // MOUSE_MOVE_ABSOLUTE
        Assert.Null(RawMouse.Relative(1, 7, 0, 3, -4));       // keyboard
    }

    [Fact]
    public void FiveAllUpSamplesClearAMissedKeyUp()
    {
        var f = Filter();
        f.OnKey(0x41, true, 0, 0);
        f.PublishState(ControlState.User, _now - 20 * TimeSpan.TicksPerSecond, 10);
        for (var i = 0; i < 4; i++) { f.SampleMissedReleases(_ => 0); _now += TimeSpan.TicksPerSecond / 10; }
        Assert.True(f.Held);
        f.SampleMissedReleases(_ => 0);
        Assert.False(f.Held);
    }

    [Fact]
    public void ADownSampleRestartsTheCount()
    {
        var f = Filter();
        f.OnKey(0x41, true, 0, 0);
        f.PublishState(ControlState.Ready, _now - 20 * TimeSpan.TicksPerSecond, 10);
        for (var i = 0; i < 4; i++) { f.SampleMissedReleases(_ => 0); _now += TimeSpan.TicksPerSecond / 10; }
        f.SampleMissedReleases(_ => unchecked((short)0x8000)); _now += TimeSpan.TicksPerSecond / 10;
        for (var i = 0; i < 4; i++) { f.SampleMissedReleases(_ => 0); _now += TimeSpan.TicksPerSecond / 10; }
        Assert.True(f.Held);
    }

    [Fact]
    public void NoSamplingDuringAi()
    {
        var f = Filter();
        f.OnKey(0x41, true, 0, 0);
        f.PublishState(ControlState.Ai, _now - 20 * TimeSpan.TicksPerSecond, 10);
        for (var i = 0; i < 6; i++) { f.SampleMissedReleases(_ => 0); _now += TimeSpan.TicksPerSecond / 10; }
        Assert.True(f.Held);
    }

    [Theory]
    [InlineData(0x01u)]
    [InlineData(0x02u)]
    public void OurSignedMouseInputIsIgnored(uint flags)
    {
        var f = Filter();
        f.SetSuppression(true, _now + 10_000_000);
        Assert.Equal(HookVerdict.Pass, f.OnMouse(0x201, 0, 5, 5, flags, InputSignature.ExtraInfo));
        Assert.Empty(Drain(f));
        Assert.False(f.Held);
    }

    [Fact]
    public void ForeignMovesQueueTheirDeltaFromThePreviousForeignPoint()
    {
        var f = Filter();
        f.OnMouse(0x200, 0, 10, 10, 0x01, 0);
        f.OnMouse(0x200, 0, 0, 0, 0, 0);                              // physical moves do not move the foreign point
        f.OnMouse(0x200, 0, 13, 6, 0x01, 0);
        var events = Drain(f);
        Assert.Equal(new PhysicalEvent(PhysicalEventKind.ForeignMotion, _now), events[0]);
        Assert.Equal(PhysicalEventKind.Pointer, events[1].Kind);
        Assert.Equal(new PhysicalEvent(PhysicalEventKind.ForeignMotion, _now, 0, 3, -4), events[2]);
    }

    [Fact]
    public void AZeroLengthForeignMoveIsNotStampedAsInput()
    {
        var f = Filter();
        f.OnMouse(0x200, 0, 10, 10, 0x01, 0);
        Assert.Equal(0, f.LastPhysical);
    }

    [Fact]
    public void RawMotionFromARealDeviceIsQueuedAndTheNullDeviceIsDropped()
    {
        var f = Filter();
        f.OnRawMouse(0, 0, 5, 5);
        f.OnRawMouse(9, 1, 5, 5);
        Assert.Empty(Drain(f));
        Assert.Equal(0, f.LastPhysical);
        f.OnRawMouse(9, 0, 2, -1);
        Assert.Equal(new PhysicalEvent(PhysicalEventKind.RawMotion, _now, 9, 2, -1), Assert.Single(Drain(f)));
        Assert.Equal(_now, f.LastPhysical);
    }

    [Fact]
    public void ClearTakeoverLetsTheChordFireAgainAfterRelease()
    {
        var f = TookOver();
        foreach (var vk in new uint[] { 0x08, 0xA3, 0xA4, 0xA1 }) f.OnKey(vk, false, 0, 0);
        f.ClearTakeover();
        Assert.False(f.TakeoverPending);
        f.SetSuppression(true, _now + 10_000_000);
        foreach (var vk in new uint[] { 0x11, 0x12, 0x10 }) f.OnKey(vk, true, 0, 0);
        Assert.Equal(HookVerdict.Swallow, f.OnKey(0x08, true, 0, 0));
        Assert.True(f.TakeoverPending);
        Assert.Equal(2, _blocks);
    }

    [Fact]
    public void AnUpClearsDeliveryAndASwallowedDownIsNotDelivered()
    {
        var f = Filter();
        f.OnMouse(0x201, 0, 0, 0, 0, 0); f.AfterMouseForwarded(0x201, 0, 0);
        Assert.True(f.ButtonDelivered(1));
        f.OnMouse(0x202, 0, 0, 0, 0, 0); f.AfterMouseForwarded(0x202, 0, 0);
        Assert.False(f.ButtonDelivered(1));
        Assert.False(f.Held);
        f.SetSuppression(true, _now + 10_000_000);
        Assert.Equal(HookVerdict.Swallow, f.OnKey(0x41, true, 0, 0));
        f.AfterKeyForwarded(0x41, true, 0);                           // not forwarded by the hook: ignored
        Assert.False(f.KeyDelivered(0x41));
    }

    [Fact]
    public void AThrowingClockFailsOpenAndPasses()
    {
        var f = new PhysicalInputFilter(() => throw new InvalidOperationException(), TimeSpan.TicksPerSecond, ForeignInjectedPolicy.Physical, () => _blocks++);
        f.SetSuppression(true, long.MaxValue);
        Assert.Equal(HookVerdict.Pass, f.OnKey(0x41, true, 0, 0));
        Assert.True(f.FailOpenRequested);
        Assert.False(f.Suppressing);
        f.ClearFailOpen();
        Assert.False(f.FailOpenRequested);
    }

    [Fact]
    public void MissedButtonUpIsClearedWithItsDelivery()
    {
        var f = Filter();
        f.OnMouse(0x207, 0, 0, 0, 0, 0); f.AfterMouseForwarded(0x207, 0, 0);
        f.PublishState(ControlState.Ready, _now - 20 * TimeSpan.TicksPerSecond, 10);
        var asked = new List<int>();
        for (var i = 0; i < 5; i++) { f.SampleMissedReleases(vk => { asked.Add(vk); return 0; }); _now += TimeSpan.TicksPerSecond / 10; }
        Assert.False(f.Held);
        Assert.False(f.ButtonDelivered(3));
        Assert.All(asked, vk => Assert.Equal(0x04, vk));
    }

    [Fact]
    public void NoSamplingBeforeTheIdleTimeout()
    {
        var f = Filter();
        f.OnKey(0x41, true, 0, 0);
        f.PublishState(ControlState.User, _now - 5 * TimeSpan.TicksPerSecond, 10);
        for (var i = 0; i < 6; i++) { f.SampleMissedReleases(_ => 0); _now += TimeSpan.TicksPerSecond / 10; }
        Assert.True(f.Held);
    }

    private PhysicalInputFilter TookOver()
    {
        var f = Filter();
        f.SetSuppression(true, _now + 10_000_000);
        foreach (var vk in new uint[] { 0xA3, 0xA4, 0xA1, 0x08 }) f.OnKey(vk, true, 0, 0);
        return f;
    }
}
