using DesktopMcp.Desktop.Control;
using DesktopMcp.Desktop.Snapshots;

namespace DesktopMcp.Desktop.Tests.Control;

/// <summary>The interactive-desktop rule over its two checks; the real check is in <see cref="InputMonitorDesktopTests"/>.</summary>
public class InteractiveDesktopTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void BothTheActiveSessionAndTheDefaultDesktopAreNeeded(bool active, bool defaultDesktop, bool available) =>
        Assert.Equal(available, InteractiveDesktop.IsAvailable(() => active, () => defaultDesktop));

    [Fact]
    public void AFailingCheckCountsAsUnavailable()
    {
        Assert.False(InteractiveDesktop.IsAvailable(() => throw new InvalidOperationException(), () => true));
        Assert.False(InteractiveDesktop.IsAvailable(() => true, () => throw new InvalidOperationException()));
    }

    // An unreadable input-desktop name is not interactive for the control (strict, as control_win32.py:239-252), while the
    // P2 session check keeps treating it as the user's desktop.
    [Theory]
    [InlineData("Default", true, true)]
    [InlineData("default", true, true)]
    [InlineData("Winlogon", true, false)]
    [InlineData("Winlogon", false, false)]
    [InlineData(null, true, false)]
    [InlineData(null, false, true)]
    public void TheInputDesktopNameDecides(string? name, bool strict, bool isDefault) =>
        Assert.Equal(isDefault, DesktopSession.IsDefaultName(name, strict));
}
