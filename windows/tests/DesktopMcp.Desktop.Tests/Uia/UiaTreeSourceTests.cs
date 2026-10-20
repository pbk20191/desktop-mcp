using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;

namespace DesktopMcp.Desktop.Tests.Uia;

public class UiaTreeSourceTests
{
    [Fact]
    public void ShrinksALineBoxToTheFontHeightOnItsBottomEdge()
    {
        var line = new ScreenRect(10, 100, 60, 130);
        Assert.Equal(new ScreenRect(10, 114, 60, 130), UiaTreeSource.ShrinkToFontSize(line, 12, 96));
        Assert.Equal(line, UiaTreeSource.ShrinkToFontSize(line, 40, 96));
        Assert.Equal(line, UiaTreeSource.ShrinkToFontSize(line, 0, 96));
    }

    [Fact]
    public void TextRangeRectanglesTruncateLikePythonInt() =>
        Assert.Equal([new ScreenRect(10, 20, 40, 32), new ScreenRect(-3, 0, 2, 9)],
            UiaTreeSource.ToRects([10.9, 20.2, 30.7, 12.9, -3.5, 0.4, 5.9, 9.1]));
}

[Trait("Category", "Desktop")]
public sealed class UiaTreeSourceDesktopTests : IDisposable
{
    // Off-screen, so nothing flashes on the user's desktop: these reads need no visible pixels (the window list and the
    // capture tests of later tasks do, and use the on-screen default).
    private readonly TestWindow _window = new(left: -20000, top: -20000);
    private readonly UiaWorker<UiaSession> _worker = new(UiaSession.Create);

    public void Dispose()
    {
        try { _worker.Dispose(); }
        finally { _window.Dispose(); }
    }

    private Task<T> Run<T>(Func<UiaTreeSource, T> job) =>
        _worker.RunAsync((session, _) => job(new UiaTreeSource(session)), TimeSpan.FromSeconds(20), default);

    [Fact]
    public async Task ProbesTheTestWindow()
    {
        var probe = await Run(source => source.ProbeWindow(_window.Handle));
        Assert.Equal(_window.Title, probe.Name);
        Assert.Equal(50032, probe.ControlType); // UIA_WindowControlTypeId
        Assert.Equal(Environment.ProcessId, probe.ProcessId);
        Assert.True(probe.HasWindowPattern && probe.CanMinimize && probe.CanMaximize);
        Assert.True(probe.Bounds.Width > 0 && probe.Bounds.Height > 0);
    }

    [Fact]
    public async Task ReadsTheWindowAndItsCachedChildren()
    {
        var (window, children) = await Run(source =>
        {
            var root = source.ReadWindow(_window.Handle);
            return (root, source.GetChildren(root));
        });
        Assert.Equal(_window.Title, window.Name);
        Assert.NotNull(window.RuntimeId);
        var button = Assert.Single(children, c => c.Name == TestWindow.ButtonText);
        Assert.Equal(50000, button.ControlType); // UIA_ButtonControlTypeId
        Assert.Equal(0x2B, button.LegacyRole);    // ROLE_SYSTEM_PUSHBUTTON
        var edit = Assert.Single(children, c => c.ControlType == 50004); // UIA_EditControlTypeId
        Assert.Equal(TestWindow.EditText, edit.LegacyValue);
    }

    [Fact]
    public async Task ReadsTheVisibleWordsOfTheEdit()
    {
        var words = await Run(source =>
        {
            var root = source.ReadWindow(_window.Handle);
            var edit = source.GetChildren(root).Single(c => c.ControlType == 50004);
            return source.GetWords(edit, maxWords: 100);
        });
        Assert.Equal(["hello", "world"], words.Select(w => w.Word));
        Assert.All(words, w => Assert.True(w.Box.Width > 0 && w.Box.Height > 0));
    }

    [Fact]
    public async Task StopsAtTheWordLimit()
    {
        var words = await Run(source =>
        {
            var root = source.ReadWindow(_window.Handle);
            return source.GetWords(source.GetChildren(root).Single(c => c.ControlType == 50004), maxWords: 1);
        });
        Assert.Equal("hello", Assert.Single(words).Word);
    }
}
