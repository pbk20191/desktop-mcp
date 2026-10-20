using System.Text.Json;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Control;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Server.Control;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Protocol;

namespace DesktopMcp.Server.Tests;

public class ControlGateTests
{
    private static readonly ControlStatus Ready = new(ControlState.Ready, 1, 0, true, "relaxed", 10);
    private static readonly ControlStatus User = new(ControlState.User, 2, 9.5, true, "relaxed", 10);

    [Fact]
    public async Task AGatedCallInUserStateIsRejectedAtOnce()
    {
        var control = new FakeControl { Current = User };
        var ran = false;
        var result = await new ControlGate(control, TimeProvider.System).InvokeAsync("Click", null, _ => { ran = true; return Ok(); }, default);
        Assert.False(ran);
        Assert.True(result.IsError);
        Assert.Equal("""{"code": "USER_CONTROL", "status": {"state": "user", "generation": 2, "wait_seconds": 9.5, "enabled": true, "policy": "relaxed", "idle_timeout_seconds": 10.0}}""",
            ToolCalls.Text(result));
    }

    [Theory]
    [InlineData("ControlStatus")]
    [InlineData("Wait")]
    [InlineData("Snapshot")]
    public async Task UngatedToolsRunWhileTheUserHasControl(string tool)
    {
        var result = await new ControlGate(new FakeControl { Current = User }, TimeProvider.System).InvokeAsync(tool, null, _ => Ok(), default);
        Assert.False(result.IsError ?? false);
    }

    [Fact]
    public async Task GatedCallsRunOneAtATime()
    {
        var control = new FakeControl { Current = Ready };
        var gate = new ControlGate(control, TimeProvider.System);
        var release = new TaskCompletionSource();
        var first = gate.InvokeAsync("Click", null, async _ => { await release.Task; return await Ok(); }, default).AsTask();
        var secondRan = false;
        var second = gate.InvokeAsync("Type", null, _ => { secondRan = true; return Ok(); }, default).AsTask();
        await Task.Delay(150);
        Assert.False(secondRan);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondRan);
    }

    [Fact]
    public async Task AWaitingCallIsRejectedWhenTheUserTakesOver()
    {
        var control = new FakeControl { Current = Ready };
        var gate = new ControlGate(control, TimeProvider.System);
        var release = new TaskCompletionSource();
        var first = gate.InvokeAsync("Click", null, async _ => { await release.Task; return await Ok(); }, default).AsTask();
        var second = gate.InvokeAsync("Click", null, _ => Ok(), default).AsTask();
        await Task.Delay(50);
        control.Current = User;
        var rejected = await second.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.StartsWith("{\"code\": \"USER_CONTROL\"", ToolCalls.Text(rejected));
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task APreemptionInsideTheToolBecomesControlPreempted()
    {
        var control = new FakeControl { Current = Ready };
        var result = await new ControlGate(control, TimeProvider.System).InvokeAsync("Type", null,
            _ => throw new ControlRejectedException(ControlCodes.ControlPreempted, User, 4), default);
        Assert.EndsWith("\"executed_steps\": 4}}", ToolCalls.Text(result));
    }

    [Fact]
    public async Task DisabledControlSerialisesEveryCallExceptControlStatus()
    {
        var control = new FakeControl { Current = new(ControlState.Disabled, 0, 0, false, "relaxed", 10) };
        var gate = new ControlGate(control, TimeProvider.System);
        var release = new TaskCompletionSource();
        var wait = gate.InvokeAsync("Wait", null, async _ => { await release.Task; return await Ok(); }, default).AsTask();
        var statusRan = false;
        await gate.InvokeAsync("ControlStatus", null, _ => { statusRan = true; return Ok(); }, default);
        var snapshot = gate.InvokeAsync("Snapshot", null, _ => Ok(), default).AsTask();
        await Task.Delay(100);
        Assert.True(statusRan);
        Assert.False(snapshot.IsCompleted);
        release.SetResult();
        await Task.WhenAll(wait, snapshot);
    }

    [Fact]
    public async Task ACancelledCallReleasesTheLockAndEndsTheCall()
    {
        var control = new FakeControl { Current = Ready };
        var gate = new ControlGate(control, TimeProvider.System);
        using var cancel = new CancellationTokenSource();
        var call = gate.InvokeAsync("Click", null, async ct => { await Task.Delay(Timeout.Infinite, ct); return await Ok(); }, cancel.Token).AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, control.EndedCalls);
        Assert.False((await gate.InvokeAsync("Click", null, _ => Ok(), default)).IsError ?? false);
    }

    [Fact]
    public async Task ACallWaitingForTheLockIsCancellable()
    {
        var control = new FakeControl { Current = Ready };
        var gate = new ControlGate(control, TimeProvider.System);
        var release = new TaskCompletionSource();
        var first = gate.InvokeAsync("Click", null, async _ => { await release.Task; return await Ok(); }, default).AsTask();
        using var cancel = new CancellationTokenSource();
        var ran = false;
        var waiting = gate.InvokeAsync("Click", null, _ => { ran = true; return Ok(); }, cancel.Token).AsTask();
        await Task.Delay(50);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(ran);
        Assert.Equal(1, control.BegunCalls);
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task TheToolRunsInsideItsLeaseAfterOneCheckpoint()
    {
        var control = new FakeControl { Current = Ready };
        ControlLease? inside = null;
        var result = await new ControlGate(control, TimeProvider.System).InvokeAsync("Click", null,
            _ => { inside = ControlCall.Current; return Ok(); }, default);
        Assert.False(result.IsError ?? false);
        Assert.NotNull(inside);
        Assert.Same(inside, Assert.Single(control.CheckpointLeases));
        Assert.True(inside.Closed);
        Assert.Null(ControlCall.Current);
        Assert.Equal(1, control.EndedCalls);
    }

    [Fact]
    public async Task ARejectedBeginIsReturnedAndReleasesTheLock()
    {
        var control = new FakeControl { Current = Ready, BeginRejection = new ControlRejectedException(ControlCodes.ControlUnavailable, Ready) };
        var gate = new ControlGate(control, TimeProvider.System);
        var ran = false;
        var result = await gate.InvokeAsync("Click", null, _ => { ran = true; return Ok(); }, default);
        Assert.False(ran);
        Assert.True(result.IsError);
        Assert.StartsWith("{\"code\": \"CONTROL_UNAVAILABLE\"", ToolCalls.Text(result));
        control.BeginRejection = null;
        Assert.False((await gate.InvokeAsync("Click", null, _ => Ok(), default).AsTask().WaitAsync(TimeSpan.FromSeconds(1))).IsError ?? false);
    }

    [Fact]
    public async Task APreemptionAtTheFirstCheckpointRunsNothing()
    {
        var control = new FakeControl { Current = Ready, CheckpointThrows = new ControlRejectedException(ControlCodes.ControlPreempted, User, 0) };
        var ran = false;
        var result = await new ControlGate(control, TimeProvider.System).InvokeAsync("Click", null, _ => { ran = true; return Ok(); }, default);
        Assert.False(ran);
        Assert.StartsWith("{\"code\": \"CONTROL_PREEMPTED\"", ToolCalls.Text(result));
        Assert.Equal(1, control.EndedCalls);
    }

    [Fact]
    public async Task UnavailableInputInsideTheToolBecomesARejection()
    {
        var control = new FakeControl { Current = Ready };
        var result = await new ControlGate(control, TimeProvider.System).InvokeAsync("Click", null,
            _ => throw new InputUnavailableException("blocked"), default);
        Assert.True(result.IsError);
        Assert.StartsWith("{\"code\": \"CONTROL_PREEMPTED\"", ToolCalls.Text(result));
    }

    [Fact]
    public async Task DisabledControlTurnsUnavailableInputIntoAnError()
    {
        var control = new FakeControl { Current = new(ControlState.Disabled, 0, 0, false, "relaxed", 10) };
        var result = await new ControlGate(control, TimeProvider.System).InvokeAsync("Click", null,
            _ => throw new InputUnavailableException("Input is shutting down."), default);
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Click': Input is shutting down.", ToolCalls.Text(result));
        Assert.Equal(0, control.BegunCalls);
    }

    [Fact]
    public void DisabledControlNeverReportsItselfEnabled()
    {
        var control = new DisabledControl(new ControlSettings(true, "balanced", 12, 500, 500));
        Assert.False(control.Settings.Enabled);
        Assert.Equal(("balanced", 12.0), (control.Settings.Policy, control.Settings.IdleTimeoutSeconds));
        Assert.Equal(ControlState.Disabled, control.Status().State);
    }

    [Fact]
    public async Task DisabledControlTakesTheDisabledPathWhateverItsSettingsSay()
    {
        // Built with Enabled = true, the disabled control must still serialise and run calls, not reject them.
        var gate = new ControlGate(new DisabledControl(new ControlSettings(true, "relaxed", 10, 500, 500)), TimeProvider.System);
        var release = new TaskCompletionSource();
        var click = gate.InvokeAsync("Click", null, async _ => { await release.Task; return await Ok(); }, default).AsTask();
        var snapshotRan = false;
        var snapshot = gate.InvokeAsync("Snapshot", null, _ => { snapshotRan = true; return Ok(); }, default).AsTask();
        await Task.Delay(100);
        Assert.False(snapshotRan);
        release.SetResult();
        Assert.False((await click).IsError ?? false);
        Assert.False((await snapshot).IsError ?? false);
        Assert.True(snapshotRan);
    }

    [Fact]
    public async Task AnUngatedCallTurnsUnavailableInputIntoAnError()
    {
        var control = new FakeControl { Current = Ready };
        var result = await new ControlGate(control, TimeProvider.System).InvokeAsync("Wait", null,
            _ => throw new InputUnavailableException("Input is shutting down."), default);
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Wait': Input is shutting down.", ToolCalls.Text(result));
        Assert.Equal(0, control.BegunCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARejectionOutsideALeaseIsReturnedAsARejection(bool enabled)
    {
        var control = new FakeControl { Current = enabled ? Ready : new(ControlState.Disabled, 0, 0, false, "relaxed", 10) };
        var result = await new ControlGate(control, TimeProvider.System).InvokeAsync("Wait", null,
            _ => throw new ControlRejectedException(ControlCodes.ControlPreempted, User, 1), default);
        Assert.True(result.IsError);
        Assert.EndsWith("\"executed_steps\": 1}}", ToolCalls.Text(result));
    }

    [Theory]
    [InlineData("relaxed", "Process", """{"mode": "kill"}""", false)]
    [InlineData("balanced", "Process", """{"mode": "kill"}""", true)]
    [InlineData("balanced", "Process", """{"mode": "list"}""", false)]
    [InlineData("relaxed", "App", """{}""", false)]
    [InlineData("relaxed", "App", """{"mode": "switch"}""", true)]
    [InlineData("strict", "Wait", """{}""", true)]
    public async Task ThePolicyAndModeDecideWhetherACallIsGated(string policy, string tool, string json, bool gated)
    {
        var gate = new ControlGate(new FakeControl { Current = User with { Policy = policy } }, TimeProvider.System);
        var result = await gate.InvokeAsync(tool, Arguments(json), _ => Ok(), default);
        Assert.Equal(gated, result.IsError ?? false);
    }

    private static Dictionary<string, JsonElement> Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static ValueTask<CallToolResult> Ok() => ValueTask.FromResult(ToolResults.Text("ok"));
}
