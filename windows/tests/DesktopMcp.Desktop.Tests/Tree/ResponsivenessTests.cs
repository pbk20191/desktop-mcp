using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;

namespace DesktopMcp.Desktop.Tests.Tree;

/// <summary>The responsiveness guard's edges that need no window (Decisions 38).</summary>
public class ResponsivenessTests
{
    [Fact]
    public void ASourceWithoutAWindowIsNeverChecked() =>
        Assert.Equal(WindowResponse.Responding, new UiaTreeSource(null!, 0).CheckWindow());

    [Fact]
    public void AnInvalidWindowCountsAsResponding() =>
        // Not a timeout: the next UIA call reports the window as gone.
        Assert.Equal(WindowResponse.Responding, Win32WindowSystem.CheckResponding(0x7FFF_FFFE));
}
