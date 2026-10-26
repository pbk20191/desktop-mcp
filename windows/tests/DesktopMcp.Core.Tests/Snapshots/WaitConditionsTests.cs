using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class WaitConditionsTests
{
    [Theory]
    [InlineData(" Enabled ", WaitCondition.ElementEnabled)]
    [InlineData("focused-element", WaitCondition.FocusedElement)]
    [InlineData("window", WaitCondition.ActiveWindow)]
    public void AliasesNormalise(string condition, WaitCondition expected) =>
        Assert.Equal(expected, WaitConditions.Validate(condition, "x", null, 10, 0.25));

    [Theory]
    [InlineData("nope", "x", null, 10.0, 0.25, "condition must be one of: text_exists, active_window, element_exists, element_enabled, focused_element")]
    [InlineData("text", null, null, 10.0, 0.25, "text is required when condition is text_exists")]
    [InlineData("window", null, null, 10.0, 0.25, "text or window_name is required when condition is active_window")]
    [InlineData("element", null, null, 10.0, 0.25, "text or window_name is required when condition is element_exists or element_enabled")]
    [InlineData("text", "x", null, 0.0, 0.25, "timeout must be greater than 0 and at most 120 seconds")]
    [InlineData("text", "x", null, 10.0, 6.0, "interval must be greater than 0 and at most 5 seconds")]
    [InlineData("text", "x", null, double.NaN, 0.25, "timeout must be a finite number")]
    public void ValidationFollowsUpstream(string c, string? text, string? window, double timeout, double interval, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => WaitConditions.Validate(c, text, window, timeout, interval)).Message);

    [Fact]
    public void ValidationChecksTheRangesBeforeTheTextAndAnEmptyTextIsMissing()
    {
        // upstream input.py:200-210: the ranges come first; `not text` treats "" as missing.
        Assert.Equal("timeout must be greater than 0 and at most 120 seconds",
            Assert.Throws<ArgumentException>(() => WaitConditions.Validate("text", null, null, 121, 0.25)).Message);
        Assert.Equal("text is required when condition is text_exists",
            Assert.Throws<ArgumentException>(() => WaitConditions.Validate("text", "", null, 10, 0.25)).Message);
        Assert.Equal(WaitCondition.FocusedElement, WaitConditions.Validate("focused", null, null, 120, 5));
    }

    [Fact]
    public void NamesAreUpstreamsConditionNames() =>
        Assert.Equal(["text_exists", "active_window", "element_exists", "element_enabled", "focused_element"],
            Enum.GetValues<WaitCondition>().Select(WaitConditions.Name));

    [Fact]
    public void TextMatchingIsACaseInsensitiveContainsAndNullExpectsNothing()
    {
        Assert.True(WaitConditions.TextMatches("Untitled - Notepad", "NOTE"));
        Assert.True(WaitConditions.TextMatches(null, null));
        Assert.False(WaitConditions.TextMatches(null, "a"));
        Assert.False(WaitConditions.TextMatches("abc", "x"));
    }

    [Fact]
    public void BooleanMetadataNeverMatches()
    {
        var element = new UiElement(0, "Save", "button", "Notepad", new(0, 0, 1, 1), new(0, 0), new UiMetadata { HasFocus = true, Value = "draft" });
        Assert.False(WaitConditions.ElementMatches(element, "true", null));
        Assert.True(WaitConditions.ElementMatches(element, "DRAFT", "note"));
        Assert.False(WaitConditions.ElementMatches(element, "save", "paint"));
    }

    [Fact]
    public void MetadataTextsAreTheTextValuedEntries()
    {
        var metadata = new UiMetadata
        {
            HasFocus = true, Value = "v", Minimum = "0.0", Maximum = "1.0", ToggleState = "on", ExpandCollapseState = "collapsed",
            Shortcut = "Ctrl+S", VerticallyScrollable = true, VerticalScrollPercent = 50,
        };
        Assert.Equal(["v", "0.0", "1.0", "on", "collapsed", "Ctrl+S"], WaitConditions.MetadataTexts(metadata));
        Assert.Empty(WaitConditions.MetadataTexts(UiMetadata.Empty));
    }
}
