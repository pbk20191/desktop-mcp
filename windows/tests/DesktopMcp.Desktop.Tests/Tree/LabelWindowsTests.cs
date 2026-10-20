using DesktopMcp.Desktop.Tree;

namespace DesktopMcp.Desktop.Tests.Tree;

// Decisions 42: the shared rule of the covered check and the label locator's point step.
public class LabelWindowsTests
{
    private const long Dialog = 100;

    private static FakeWindowSystem Windows(long root, uint rootThread, uint dialogThread = 11)
    {
        var windows = new FakeWindowSystem();
        if (rootThread != 0) windows.Threads[root] = rootThread;
        if (dialogThread != 0) windows.Threads[Dialog] = dialogThread;
        return windows;
    }

    [Fact]
    public void TheLabelWindowItselfCounts() => Assert.True(LabelWindows.IsLabelWindow(Windows(Dialog, 11), Dialog, Dialog));

    [Fact]
    public void ARootOnTheSameThreadCounts() =>
        // A classic combo box's ComboLBox or an unowned #32768 menu (GA_ROOTOWNER is the window itself), or an owned menu.
        Assert.True(LabelWindows.IsLabelWindow(Windows(77, 11), 77, Dialog));

    [Fact]
    public void ARootOnAnotherThreadDoesNotCount() =>
        // Owned by the dialog or not: another thread's window covers the label.
        Assert.False(LabelWindows.IsLabelWindow(Windows(77, 12), 77, Dialog));

    [Fact]
    public void AnUnreadableThreadDoesNotCount()
    {
        Assert.False(LabelWindows.IsLabelWindow(Windows(77, 0), 77, Dialog));
        Assert.False(LabelWindows.IsLabelWindow(Windows(77, 0, dialogThread: 0), 77, Dialog));
    }

    [Fact]
    public void NoWindowDoesNotCount() => Assert.False(LabelWindows.IsLabelWindow(Windows(0, 0), 0, Dialog));
}
