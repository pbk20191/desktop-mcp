using DesktopMcp.Core.Input;

namespace DesktopMcp.Core.Tests.Input;

public class SendKeysPlanTests
{
    private static readonly TimeSpan Ms10 = TimeSpan.FromMilliseconds(10);

    // uia/core.py:1702-1744: hotkey pause after a down and between ups; interval after the last event; waitTime at the end.
    [Fact]
    public void ShortcutPausesFollowUpstream()
    {
        var steps = SendKeysPlan.Build(ShortcutParser.Parse("ctrl+c"), Ms10, TimeSpan.FromMilliseconds(500));
        Assert.Equal(4, steps.Count);
        Assert.Equal([new KeyInput(0x11, '\0', false)], steps[0].Inputs);
        Assert.Equal([Ms10, Ms10, Ms10, TimeSpan.FromMilliseconds(510)], steps.Select(s => s.PauseAfter));
    }

    [Fact]
    public void AnAstralCharacterIsTwoUnicodeUnitsInOneStep()
    {
        var step = Assert.Single(SendKeysPlan.Build([new KeyToken(0, false, "😀")], TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(50)));
        Assert.Equal([new KeyInput(0, '\uD83D', false), new KeyInput(0, '\uD83D', true), new KeyInput(0, '\uDE00', false), new KeyInput(0, '\uDE00', true)], step.Inputs);
        Assert.Equal(TimeSpan.FromMilliseconds(90), step.PauseAfter);
    }

    [Fact]
    public void NoTokensGiveNoSteps() => Assert.Empty(SendKeysPlan.Build([], Ms10, TimeSpan.FromMilliseconds(500)));
}
