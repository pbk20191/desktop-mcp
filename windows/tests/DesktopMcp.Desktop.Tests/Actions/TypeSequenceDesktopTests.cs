using DesktopMcp.Desktop.Actions;
using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Desktop.WindowControl;

namespace DesktopMcp.Desktop.Tests.Actions;

/// <summary>
/// The Type tool's sequence (<see cref="TypeSequence"/>: click, foreground check, keys, read-back) against the test
/// window's edit, through <see cref="GuardedInjector"/>. Run with hands off the mouse and keyboard: the window takes the
/// foreground.
/// </summary>
[Trait("Category", "Desktop")]
public class TypeSequenceDesktopTests
{
    [Fact]
    public async Task TypeToolTypesIntoTheTestWindowAndReadsItBack()
    {
        using var target = new ActiveTestWindow();
        // Under 20 characters: the per-character path, so the clipboard is never touched.
        const string Text = "typed back";
        var input = new DesktopInput(new InputLedger(), new SystemClipboard(), NoCheckpoint.Instance,
            new GuardedInjector(new SendInputInjector(), target.Handle));
        var sequence = new TypeSequence(input, new ForegroundService(), new UiaTypeReadBack(DesktopMachine.CreateDefault()),
            static (delay, ct) => Task.Delay(delay, ct));
        var edit = new ActionTarget(TestForeground.Center(target.Window.EditHandle), target.Handle, target.Window.Title);

        var warning = await sequence.RunAsync(edit, Text, CaretPosition.Idle, clear: true, pressEnter: false, warningAt: null, default);

        Assert.Null(warning);
        Assert.Equal(Text, target.Window.ReadEditText());
    }
}
