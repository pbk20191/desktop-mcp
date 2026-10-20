using DesktopMcp.Desktop.Input;

namespace DesktopMcp.Desktop.Tests.Input;

public class InputLedgerTests
{
    [Fact]
    public void ReleaseSendsUpAndForgetsTheHold()
    {
        var log = new List<string>();
        var ledger = new InputLedger();
        ledger.Press("key:ctrl", () => log.Add("down"), () => log.Add("up"));
        Assert.Equal(["key:ctrl"], ledger.Pending);
        ledger.Release("key:ctrl");
        ledger.Release("key:ctrl");
        Assert.Equal(["down", "up"], log);
        Assert.Empty(ledger.Pending);
    }

    [Fact]
    public void APhysicalDownSuppressesTheInjectedUp()
    {
        var log = new List<string>();
        var ledger = new InputLedger(name => name == "key:shift");
        ledger.Press("key:shift", () => log.Add("down"), () => log.Add("up"));
        ledger.Release("key:shift");
        Assert.Equal(["down"], log);
    }

    [Fact]
    public void ADownThatThrowsIsReleasedAndRethrown()
    {
        var log = new List<string>();
        var ledger = new InputLedger();
        Assert.Throws<InvalidOperationException>(() => ledger.Press("mouse:left", () => throw new InvalidOperationException("boom"), () => log.Add("up")));
        Assert.Equal(["up"], log);
        Assert.Empty(ledger.Pending);
    }

    [Fact]
    public void UpstreamsGuardTexts()
    {
        var ledger = new InputLedger();
        ledger.Press("key:ctrl", () => { }, () => { });
        Assert.Equal("AI input already held: key:ctrl", Assert.Throws<InvalidOperationException>(() => ledger.Press("key:ctrl", () => { }, () => { })).Message);
        Assert.Equal("AI input remains held", Assert.Throws<InvalidOperationException>(ledger.Enable).Message);
        ledger.ReleaseAll();
        ledger.BlockNew();
        Assert.Equal("AI input is unavailable", Assert.Throws<InputUnavailableException>(() => ledger.Press("key:alt", () => { }, () => { })).Message);
    }

    [Fact]
    public void ReleaseAllReleasesEveryHoldAndRethrowsTheFirstError()
    {
        var ledger = new InputLedger();
        var released = new List<string>();
        ledger.Press("a", () => { }, () => throw new InvalidOperationException("first"));
        ledger.Press("b", () => { }, () => released.Add("b"));
        Assert.Equal("first", Assert.Throws<InvalidOperationException>(ledger.ReleaseAll).Message);
        Assert.Equal(["b"], released);
        Assert.Equal(["a"], ledger.Pending); // upstream keeps a failed entry for shutdown's second attempt
    }

    [Fact]
    public void PendingAndReleaseAllKeepInsertionOrder()
    {
        var ledger = new InputLedger();
        var released = new List<string>();
        foreach (var name in new[] { "a", "b", "c" }) ledger.Press(name, () => { }, () => released.Add(name));
        ledger.Release("b");
        ledger.Press("d", () => { }, () => released.Add("d"));
        Assert.Equal(["a", "c", "d"], ledger.Pending);
        released.Clear();
        ledger.ReleaseAll();
        Assert.Equal(["a", "c", "d"], released);
    }

    [Fact]
    public void WhenTheDownAndTheCleanupUpBothThrowTheDownsErrorWinsAndTheEntryStays()
    {
        var ledger = new InputLedger();
        var error = Assert.Throws<InvalidOperationException>(() => ledger.Press("k",
            () => throw new InvalidOperationException("down"), () => throw new InvalidOperationException("up")));
        Assert.Equal("down", error.Message);
        Assert.Equal(["k"], ledger.Pending);
    }

    [Fact]
    public async Task BlockNewDoesNotWaitForAPressInProgress()
    {
        var ledger = new InputLedger();
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var press = Task.Run(() => ledger.Press("key:ctrl", () => { entered.Set(); gate.Wait(); }, () => { }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var block = Task.Run(ledger.BlockNew);
        var finished = await Task.WhenAny(block, Task.Delay(TimeSpan.FromMilliseconds(100)));
        gate.Set();
        await press;
        Assert.Same(block, finished);
        Assert.Throws<InputUnavailableException>(() => ledger.Press("key:shift", () => { }, () => { }));
    }
}
