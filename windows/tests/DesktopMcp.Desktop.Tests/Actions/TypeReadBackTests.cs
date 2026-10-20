using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Actions;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tests.Tree;
using DesktopMcp.Desktop.Uia;

namespace DesktopMcp.Desktop.Tests.Actions;

public class TypeReadBackTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    [Theory]
    [InlineData("hello", "say hello", null)]
    [InlineData("a\r\nb", "a\nb", null)]
    [InlineData("a\nb", "a\rb", null)]
    [InlineData("hello", null, null)]
    [InlineData("", "anything", null)]
    [InlineData("hello", "helo", "Warning: the focused element's value does not contain the typed text; it reads 'helo'.")]
    public void WarningOnlyOnMismatch(string typed, string? value, string? warning) =>
        Assert.Equal(warning, TypeReadBack.Warning(typed, value));

    [Fact]
    public void LongValuesAreCut() =>
        Assert.EndsWith("...'.", TypeReadBack.Warning("x", new string('y', 300)));

    [Fact]
    public void ValuesAreCutAtTwoHundredCodePoints()
    {
        var value = string.Concat(Enumerable.Repeat("\U0001F600", 250));
        var expected = "Warning: the focused element's value does not contain the typed text; it reads '"
            + string.Concat(Enumerable.Repeat("\U0001F600", 200)) + "...'.";
        Assert.Equal(expected, TypeReadBack.Warning("x", value));
        Assert.Equal("Warning: the focused element's value does not contain the typed text; it reads '"
            + new string('y', 200) + "'.", TypeReadBack.Warning("x", new string('y', 200)));
    }

    [Fact]
    public void TheValueIsShownAsPythonsRepr() =>
        Assert.Equal("Warning: the focused element's value does not contain the typed text; it reads 'a\\r\\nb'.",
            TypeReadBack.Warning("c", "a\r\nb"));

    [Fact]
    public void AnItemWarningNamesItsPoint() =>
        Assert.Equal("Warning: the focused element's value at (1,2) does not contain the typed text; it reads 'zz'.",
            TypeReadBack.Warning("a", "zz", new ScreenPoint(1, 2)));

    [Fact]
    public async Task ChecksUpToThreeTimes()
    {
        var reads = new Queue<string?>(["", "hel", "hello"]);
        var reader = new FakeReader(() => reads.Dequeue());
        Assert.Null(await TypeReadBack.CheckAsync(reader, "hello", (_, _) => Task.CompletedTask, default));
        Assert.Equal(3, reader.Reads);
    }

    [Fact]
    public async Task AMismatchAfterThreeReadsIsTheWarningAndTheReadsAre150MsApart()
    {
        var delays = new List<TimeSpan>();
        var reader = new FakeReader(() => "nope");
        var warning = await TypeReadBack.CheckAsync(reader, "hello", (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        }, default);
        Assert.Equal("Warning: the focused element's value does not contain the typed text; it reads 'nope'.", warning);
        Assert.Equal(3, reader.Reads);
        Assert.Equal([TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150)], delays);
    }

    [Fact]
    public async Task AMatchStopsReading()
    {
        var reader = new FakeReader(() => "hello");
        Assert.Null(await TypeReadBack.CheckAsync(reader, "hello", NoDelay, default));
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task ASkippedOrUnreadableValueStopsReading()
    {
        var reader = new FakeReader(() => null);
        Assert.Null(await TypeReadBack.CheckAsync(reader, "hello", NoDelay, default));
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task TheItemVariantNamesThePoint()
    {
        var reader = new FakeReader(() => "zz");
        Assert.Equal("Warning: the focused element's value at (3,4) does not contain the typed text; it reads 'zz'.",
            await TypeReadBack.CheckAsync(reader, "a", new ScreenPoint(3, 4), NoDelay, default));
    }

    [Fact]
    public async Task CancellationStopsTheReads()
    {
        using var cancel = new CancellationTokenSource();
        var reader = new FakeReader(() =>
        {
            cancel.Cancel();
            return "nope";
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TypeReadBack.CheckAsync(reader, "hello", (delay, ct) => Task.Delay(delay, ct), cancel.Token));
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task NoForegroundWindowReadsNothing()
    {
        var reads = 0;
        var readBack = new UiaTypeReadBack(new FakeWindowSystem(), Blocks(), (_, _) =>
        {
            reads++;
            return Task.FromResult<string?>("x");
        });
        Assert.Null(await readBack.ReadFocusedValueAsync(default));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task ABlockedForegroundWindowReadsNothing()
    {
        var windows = new FakeWindowSystem();
        windows.Foreground.Enqueue(9);
        var blocks = Blocks();
        blocks.Block(9, windows.GetProcessId(9));
        var reads = 0;
        var readBack = new UiaTypeReadBack(windows, blocks, (_, _) =>
        {
            reads++;
            return Task.FromResult<string?>("x");
        });
        Assert.Null(await readBack.ReadFocusedValueAsync(default));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task TheReadGoesToTheForegroundWindow()
    {
        var windows = new FakeWindowSystem();
        windows.Foreground.Enqueue(9);
        long read = 0;
        var readBack = new UiaTypeReadBack(windows, Blocks(), (window, _) =>
        {
            read = window;
            return Task.FromResult<string?>("value");
        });
        Assert.Equal("value", await readBack.ReadFocusedValueAsync(default));
        Assert.Equal(9, read);
    }

    [Fact]
    public async Task ATimeoutBlocksTheWindowAndReadsAsNull()
    {
        var windows = new FakeWindowSystem();
        windows.Foreground.Enqueue(9);
        var blocks = Blocks();
        var readBack = new UiaTypeReadBack(windows, blocks, (_, _) => throw new UiaTimeoutException("late"));
        Assert.Null(await readBack.ReadFocusedValueAsync(default));
        Assert.True(blocks.IsBlocked(9, windows.GetProcessId(9)));
    }

    [Fact]
    public async Task AFailedReadIsNull()
    {
        var windows = new FakeWindowSystem();
        windows.Foreground.Enqueue(9);
        var readBack = new UiaTypeReadBack(windows, Blocks(), (_, _) => throw new UiaUnavailableException("stuck"));
        Assert.Null(await readBack.ReadFocusedValueAsync(default));
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        var windows = new FakeWindowSystem();
        windows.Foreground.Enqueue(9);
        var readBack = new UiaTypeReadBack(windows, Blocks(), (_, _) => throw new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readBack.ReadFocusedValueAsync(default));
    }

    private static HangBlockList Blocks() => new(TimeProvider.System, HangBlockList.DefaultDuration);

    private sealed class FakeReader(Func<string?> read) : ITypeReadBack
    {
        public int Reads { get; private set; }

        public Task<string?> ReadFocusedValueAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(read());
        }
    }
}
