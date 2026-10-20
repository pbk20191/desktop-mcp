using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Snapshots;

namespace DesktopMcp.Server.Tests;

public class WaitForToolTests
{
    [Fact]
    public async Task SatisfiedOnTheThirdAttempt()
    {
        var desktop = new FakeDesktop { WaitResults = [(false, "matching element was absent"), (false, "matching element was absent"), (true, "element matched 'OK'")] };
        Assert.Equal("WaitFor condition 'element_exists' satisfied after 0.50s and 3 attempt(s): element matched 'OK'.",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", desktop.Services()), """{"condition": "element", "text": "ok"}""")));
        Assert.Equal(["element_exists ok -", "element_exists ok -", "element_exists ok -"], desktop.WaitCalls);
        Assert.Equal(1, desktop.SessionChecks);
    }

    [Fact]
    public async Task TimeoutIsAToolError()
    {
        var desktop = new FakeDesktop { WaitResults = [(false, "active window was 'A'")] };
        Assert.Equal("Error calling tool 'WaitFor': Timed out after 1.00s waiting for 'active_window': active window was 'A'.",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", desktop.Services()), """{"condition": "active_window", "window_name": "b", "timeout": 1, "interval": 0.5}""")));
        // Polls at 0, 0.5 and 1.0 s; the last wait is cut to what remains.
        Assert.Equal(3, desktop.WaitCalls.Count);
    }

    [Fact]
    public async Task TransientPollsKeepThePreviousDetail()
    {
        var desktop = new FakeDesktop { WaitResults = [(false, "text 'a' was absent"), (false, null)] };
        Assert.Equal("Error calling tool 'WaitFor': Timed out after 1.00s waiting for 'text_exists': text 'a' was absent.",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", desktop.Services()), """{"condition": "text", "text": "a", "timeout": 1, "interval": 0.5}""")));
    }

    [Fact]
    public async Task EveryPollFailingTransientlyLeavesTheConditionNotEvaluated()
    {
        var desktop = new FakeDesktop { WaitResults = [(false, null)] };
        Assert.Equal("Error calling tool 'WaitFor': Timed out after 1.00s waiting for 'element_exists': condition was not evaluated.",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", desktop.Services()), """{"condition": "element", "text": "ok", "timeout": 1, "interval": 0.5}""")));
        Assert.Equal(3, desktop.WaitCalls.Count);
    }

    [Fact]
    public async Task TheLastWaitIsCutToTheRemainingTime()
    {
        var desktop = new FakeDesktop { WaitResults = [(false, "text 'a' was absent")] };
        Assert.Equal("Error calling tool 'WaitFor': Timed out after 0.30s waiting for 'text_exists': text 'a' was absent.",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", desktop.Services()), """{"condition": "text", "text": "a", "timeout": "0.3", "interval": 0.25}""")));
        Assert.Equal(3, desktop.WaitCalls.Count); // at 0, 0.25 and 0.30 s
    }

    [Fact]
    public async Task UseDomIsRefused() =>
        Assert.Equal($"Error calling tool 'WaitFor': {SnapshotService.UseDomUnavailable}",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", new FakeDesktop().Services()), """{"condition": "text", "text": "a", "use_dom": "true"}""")));

    [Theory]
    [InlineData("""{"condition": "text", "text": "a", "use_dom": "maybe"}""", "use_dom must be true or false")]
    [InlineData("""{"condition": "nope", "use_dom": true}""", "condition must be one of: text_exists, active_window, element_exists, element_enabled, focused_element")]
    [InlineData("""{"condition": "text", "text": "a", "timeout": true}""", "timeout must be a finite number")]
    [InlineData("""{"condition": "text", "text": "a", "interval": "inf"}""", "interval must be a finite number")]
    [InlineData("""{"condition": "text", "text": "a", "timeout": 121}""", "timeout must be greater than 0 and at most 120 seconds")]
    [InlineData("""{"text": "a"}""", "Missing required argument: condition")]
    public async Task ArgumentErrorsComeBeforeAnyPoll(string arguments, string message)
    {
        var desktop = new FakeDesktop();
        Assert.Equal($"Error calling tool 'WaitFor': {message}",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", desktop.Services()), arguments)));
        Assert.Empty(desktop.WaitCalls);
        Assert.Equal(0, desktop.SessionChecks);
    }

    [Fact]
    public async Task AnUnavailableDesktopEndsTheCallBeforeThePolls()
    {
        var desktop = new FakeDesktop { SessionError = "The desktop is not available." };
        Assert.Equal("Error calling tool 'WaitFor': The desktop is not available.",
            ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", desktop.Services()), """{"condition": "focused"}""")));
        Assert.Empty(desktop.WaitCalls);
    }

    [Fact]
    public async Task ADefaultPollSucceedsAtOnce()
    {
        var desktop = new FakeDesktop { WaitResults = [(true, "focused element matched 'Search'")] };
        Assert.Equal("WaitFor condition 'focused_element' satisfied after 0.00s and 1 attempt(s): focused element matched 'Search'.",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", desktop.Services()), """{"condition": "focused", "window_name": "explorer", "use_dom": false}""")));
        Assert.Equal(["focused_element - explorer"], desktop.WaitCalls);
    }

    /// <summary>A snapshot service that records captures; WaitFor must never take one (P3 Decisions 6 and 24, preflight F15).</summary>
    private sealed class RecordingSnapshots : IDesktopSnapshots
    {
        public int Captures { get; private set; }
        public DesktopStateStore State { get; } = new();

        public Task<DesktopSnapshot> CaptureAsync(SnapshotRequest request, CancellationToken cancellationToken)
        {
            Captures++;
            throw new InvalidOperationException("WaitFor must not capture a snapshot.");
        }
    }

    [Fact]
    public async Task WaitForLeavesTheLabelStoreAlone()
    {
        var desktop = new FakeDesktop { WaitResults = [(false, "text 'a' was absent"), (true, "text 'a' appeared")] };
        var snapshots = new RecordingSnapshots();
        var services = desktop.Services() with { Snapshots = snapshots };
        ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("WaitFor", services), """{"condition": "text", "text": "a"}"""));
        Assert.Equal(0, snapshots.Captures);
        Assert.Null(services.Snapshots.State.Latest);
    }
}
