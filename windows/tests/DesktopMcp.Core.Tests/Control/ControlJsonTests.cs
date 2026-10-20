using DesktopMcp.Core.Control;

namespace DesktopMcp.Core.Tests;

public class ControlJsonTests
{
    private static readonly ControlStatus User = new(ControlState.User, 4, 9.5, true, "relaxed", 10.0);

    [Fact]
    public void StatusIsCompactWithUpstreamsKeyOrder() =>
        Assert.Equal("""{"state":"ready","generation":1,"wait_seconds":0.0,"enabled":true,"policy":"balanced","idle_timeout_seconds":12.5}""",
            ControlJson.Status(new(ControlState.Ready, 1, 0, true, "balanced", 12.5)));

    [Fact]
    public void RejectionUsesPythonsDefaultSeparators() =>
        Assert.Equal("""{"code": "USER_CONTROL", "status": {"state": "user", "generation": 4, "wait_seconds": 9.5, "enabled": true, "policy": "relaxed", "idle_timeout_seconds": 10.0}}""",
            ControlJson.Rejection(new ControlRejectedException(ControlCodes.UserControl, User)));

    [Fact]
    public void PreemptionAddsExecutedStepsLast() =>
        Assert.EndsWith("""
            "idle_timeout_seconds": 10.0, "executed_steps": 3}}
            """, ControlJson.Rejection(new ControlRejectedException(ControlCodes.ControlPreempted, User, 3)));

    [Fact]
    public void DisabledStatus() =>
        Assert.Equal("""{"state":"disabled","generation":0,"wait_seconds":0.0,"enabled":false,"policy":"relaxed","idle_timeout_seconds":10.0}""",
            ControlJson.Status(new(ControlState.Disabled, 0, 0, false, "relaxed", 10.0)));

    [Theory]
    [InlineData(ControlState.User, "USER_CONTROL")]
    [InlineData(ControlState.Ai, "CONTROL_UNAVAILABLE")]
    [InlineData(ControlState.Disabled, "CONTROL_UNAVAILABLE")]
    public void CodesFollowState(ControlState state, string code) => Assert.Equal(code, ControlCodes.ForState(state));

    [Fact]
    public void ExceptionMessageNamesCodeAndState() =>
        Assert.Equal("USER_CONTROL: user", new ControlRejectedException(ControlCodes.UserControl, User).Message);
}
