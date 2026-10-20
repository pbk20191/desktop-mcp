using DesktopMcp.Desktop.Tree;

namespace DesktopMcp.Desktop.Tests.Tree;

public class TreeConfigTests
{
    [Theory]
    [InlineData("Program Manager", "Progman", "Desktop")]
    [InlineData("", "Shell_TrayWnd", "Taskbar")]
    [InlineData("", "Shell_SecondaryTrayWnd", "Taskbar")]
    [InlineData("Popup", "Microsoft.UI.Content.PopupWindowSiteBridge", "Context Menu")]
    [InlineData("Shell_TrayWnd", "SomeClass", "Taskbar")]
    [InlineData("Progman", "", "Desktop")]
    [InlineData("Notepad", "Notepad", "Notepad")]
    public void WindowNamesAreFixedByClassNameThenByTitle(string name, string className, string expected) =>
        Assert.Equal(expected, TreeConfig.WindowName(name, className));

    [Fact]
    public void ControlTypeNamesFollowUpstream()
    {
        Assert.Equal("ButtonControl", TreeConfig.ControlTypeName(TreeConfig.Button));
        Assert.Equal("PaneControl", TreeConfig.ControlTypeName(TreeConfig.Pane));
        Assert.Equal("AppBarControl", TreeConfig.ControlTypeName(50040));
        Assert.Equal("Unknown", TreeConfig.ControlTypeName(50041));
    }

    [Fact]
    public void TitleCapitalisesEachWordLikePython() => Assert.Equal("Double Click", TreeConfig.Title("dOUBLE cLICK"));

    [Theory]
    [InlineData("chrome.exe", true)]
    [InlineData("MSEdge.EXE", true)]
    [InlineData("firefox.exe", true)]
    [InlineData("notepad.exe", false)]
    [InlineData(null, false)]
    public void BrowsersAreKnownByProcessName(string? process, bool expected) => Assert.Equal(expected, TreeConfig.IsBrowser(process));
}
