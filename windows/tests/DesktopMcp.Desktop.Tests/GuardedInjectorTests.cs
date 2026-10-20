using DesktopMcp.Desktop.Input;

namespace DesktopMcp.Desktop.Tests;

public class GuardedInjectorTests
{
    private sealed class Recorder : IInputInjector
    {
        public int Calls { get; private set; }

        public void Send(IReadOnlyList<InjectedInput> inputs) => Calls++;
    }

    [Fact]
    public void AMissingWindowIsRejected() =>
        Assert.Throws<ArgumentException>(() => new GuardedInjector(new Recorder(), 0));

    /// <summary>
    /// Reads only: the test window is shown without activation, so the user's window stays in the foreground and the
    /// guard must refuse; the recorder (not SendInput) would receive anything forwarded.
    /// </summary>
    [Fact]
    [Trait("Category", "Desktop")]
    public void RefusesAndForwardsNothingWhenTheTestWindowIsNotForeground()
    {
        using var window = new TestWindow();
        var recorder = new Recorder();
        var guard = new GuardedInjector(recorder, window.Handle);
        var error = Assert.Throws<InvalidOperationException>(() =>
            guard.Send([InjectedInput.Keyboard(new(0x41, '\0', false)), InjectedInput.Keyboard(new(0x41, '\0', true))]));
        Assert.StartsWith("Test input guard: ", error.Message);
        Assert.EndsWith("; nothing was sent.", error.Message);
        Assert.Equal(0, recorder.Calls);
    }
}
