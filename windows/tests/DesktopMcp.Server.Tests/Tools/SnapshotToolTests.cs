using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Snapshots;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Protocol;

namespace DesktopMcp.Server.Tests;

public class SnapshotToolTests
{
    private sealed class FakeSnapshots(Func<SnapshotRequest, DesktopSnapshot> capture) : IDesktopSnapshots
    {
        public SnapshotRequest? Request { get; private set; }
        public int ThreadId { get; private set; }
        public DesktopStateStore State { get; } = new();

        public Task<DesktopSnapshot> CaptureAsync(SnapshotRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            ThreadId = Environment.CurrentManagedThreadId;
            return Task.FromResult(capture(request));
        }
    }

    private static DesktopSnapshot Sample(byte[]? png) => new()
    {
        ActiveDesktop = VirtualDesktopInfo.Default,
        AllDesktops = [VirtualDesktopInfo.Default],
        Windows = [],
        Cursor = new ScreenPoint(1, 2),
        Displays = [],
        Tree = TreeSnapshot.Empty,
        Png = png,
        OriginalSize = png is null ? null : new PixelSize(10, 10),
        Scale = png is null ? null : 1.0,
    };

    private static Task<CallToolResult> CallAsync(string tool, FakeSnapshots snapshots, string json) =>
        ToolCalls.CallAsync(ToolCalls.Create(tool, ToolServices.CreateDefault() with { Snapshots = snapshots }), json);

    [Fact]
    public async Task SnapshotWithoutVisionIsOnePlainTextBlock()
    {
        var snapshots = new FakeSnapshots(_ => Sample(null));
        var result = await CallAsync("Snapshot", snapshots, "{}");
        var text = ToolCalls.OkText(result);
        Assert.StartsWith("Cursor Position: (1, 2)\n\nActive Desktop:", text);
        Assert.EndsWith("UI Tree:\nNo elements", text);
        Assert.Null(result.StructuredContent);
        Assert.Equal(new SnapshotRequest(), snapshots.Request);
    }

    [Fact]
    public async Task SnapshotArgumentsAreLooseBooleansIntegersAndSelections()
    {
        var snapshots = new FakeSnapshots(_ => Sample([1, 2, 3]));
        var result = await CallAsync("Snapshot", snapshots,
            """{"use_vision": "TRUE", "use_annotation": "no", "use_ui_tree": true, "width_reference_line": 4, "height_reference_line": "3", "display": [0], "region": "[0, 0, 10, 10]"}""");
        var request = snapshots.Request!;
        Assert.True(request.UseVision);
        Assert.False(request.UseAnnotation);
        Assert.Equal((4L, 3L), (request.WidthReferenceLine, request.HeightReferenceLine));
        Assert.Equal([0L], request.Displays);
        Assert.Equal("[0, 0, 10, 10]", request.Region!.Json);
        Assert.Equal(2, result.Content.Count);
        Assert.Null(result.StructuredContent);
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.DecodedData.ToArray());
    }

    [Fact]
    public async Task CaptureFailuresBecomeUpstreamsTextResult()
    {
        var snapshots = new FakeSnapshots(_ => throw new ArgumentException("region must satisfy right > left and bottom > top"));
        var result = await CallAsync("Snapshot", snapshots, """{"region": [5, 5, 1, 1]}""");
        Assert.Equal("Error capturing desktop state: region must satisfy right > left and bottom > top. Please try again.", ToolCalls.OkText(result));
    }

    [Fact]
    public async Task UseDomIsReportedAsNotSupportedYet()
    {
        var service = new SnapshotService();
        var result = await ToolCalls.CallAsync(
            ToolCalls.Create("Snapshot", ToolServices.CreateDefault() with { Snapshots = service }), """{"use_dom": true}""");
        Assert.Equal($"Error capturing desktop state: {SnapshotService.UseDomUnavailable}. Please try again.", ToolCalls.OkText(result));
    }

    [Fact]
    public async Task ArgumentTypeErrorsAreToolErrors()
    {
        var result = await CallAsync("Snapshot", new FakeSnapshots(_ => Sample(null)), """{"display": "0"}""");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Snapshot': display must be a list of integers", ToolCalls.Text(result));
    }

    [Fact]
    public async Task ScreenshotAlwaysCapturesWithoutTheTree()
    {
        var snapshots = new FakeSnapshots(_ => Sample([9]));
        var result = await CallAsync("Screenshot", snapshots, "{}");
        Assert.Equal(new SnapshotRequest { UseVision = true, UseAnnotation = false, UseUiTree = false }, snapshots.Request);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("\n" + SnapshotText.ScreenshotNote + "\n\nActive Desktop:", text);
        Assert.DoesNotContain("UI Tree:\n", text);
        Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Null(result.StructuredContent);
    }

    [Fact]
    public async Task ScreenshotFailuresUseItsOwnPrefix()
    {
        // The shape of AppRuntime.InstallHint(call, hresult): it ends with a period, which the wrapper must not double.
        const string missing = "The Windows App SDK runtime (Microsoft.WindowsAppRuntime.2_5bwekyb3d8bbwe, x64) is not installed: " +
            "MddBootstrapInitialize2 failed with HRESULT 0x80070490. Install the Windows App SDK 2.5 runtime (2.5.1.0) or later " +
            "from https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads.";
        var snapshots = new FakeSnapshots(_ => throw new InvalidOperationException(missing));
        var result = await CallAsync("Screenshot", snapshots, "{}");
        Assert.Equal(
            "Error capturing screenshot: The Windows App SDK runtime (Microsoft.WindowsAppRuntime.2_5bwekyb3d8bbwe, x64) is not installed: " +
            "MddBootstrapInitialize2 failed with HRESULT 0x80070490. Install the Windows App SDK 2.5 runtime (2.5.1.0) or later " +
            "from https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads. Please try again.", ToolCalls.OkText(result));
    }

    [Fact]
    public async Task AMessageEndingInAPeriodIsNotDoubled()
    {
        var snapshots = new FakeSnapshots(_ => throw new DesktopException("Screen capture failed: it did not finish within 2 s."));
        var result = await CallAsync("Snapshot", snapshots, "{}");
        Assert.Equal("Error capturing desktop state: Screen capture failed: it did not finish within 2 s. Please try again.", ToolCalls.OkText(result));
    }

    private sealed class PoolPostingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => ThreadPool.QueueUserWorkItem(_ => d(state));
    }

    [Fact]
    public void CaptureDoesNotRunItsSynchronousWorkOnTheCallersThread()
    {
        var snapshots = new FakeSnapshots(_ =>
        {
            Thread.Sleep(50); // synchronous work before the first await, like the scan and the tree walk
            return Sample(null);
        });
        var tool = ToolCalls.Create("Snapshot", ToolServices.CreateDefault() with { Snapshots = snapshots });
        var callerId = 0;
        var caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new PoolPostingContext());
            callerId = Environment.CurrentManagedThreadId;
            ToolCalls.CallAsync(tool, "{}").GetAwaiter().GetResult();
        });
        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(30)));
        Assert.NotEqual(0, snapshots.ThreadId);
        Assert.NotEqual(callerId, snapshots.ThreadId);
    }

    [Fact]
    public async Task CancellationByTheCallerIsRethrown()
    {
        using var cts = new CancellationTokenSource();
        var snapshots = new FakeSnapshots(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var tool = ToolCalls.Create("Snapshot", ToolServices.CreateDefault() with { Snapshots = snapshots });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolCalls.CallAsync(tool, "{}", cts.Token));
    }

    [Fact]
    public async Task CancellationNotFromTheCallerBecomesTheErrorText()
    {
        var snapshots = new FakeSnapshots(_ => throw new OperationCanceledException("capture was abandoned"));
        var result = await CallAsync("Snapshot", snapshots, "{}");
        Assert.Equal("Error capturing desktop state: capture was abandoned. Please try again.", ToolCalls.OkText(result));
    }
}
