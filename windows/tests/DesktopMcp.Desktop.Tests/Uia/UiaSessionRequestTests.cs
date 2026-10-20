using DesktopMcp.Desktop.Uia;
using Windows.Win32.UI.Accessibility;

namespace DesktopMcp.Desktop.Tests.Uia;

/// <summary>The cache request sets, checked without a UIA client.</summary>
public class UiaSessionRequestTests
{
    // Label walks and the locator's reads never marshal element values; only Type's read-back caches them.
    [Fact]
    public void TheActionRequestCachesNoValue()
    {
        Assert.DoesNotContain(UIA_PROPERTY_ID.UIA_ValueValuePropertyId, UiaSession.ActionProperties);
        Assert.DoesNotContain(UIA_PATTERN_ID.UIA_ValuePatternId, UiaSession.ActionPatterns);
        Assert.Contains(UIA_PROPERTY_ID.UIA_NativeWindowHandlePropertyId, UiaSession.ActionProperties);
        Assert.Contains(UIA_PATTERN_ID.UIA_ScrollItemPatternId, UiaSession.ActionPatterns);
    }

    [Fact]
    public void TheReadBackRequestCachesWhatTheReadBackReads()
    {
        Assert.Equal(
            [UIA_PROPERTY_ID.UIA_IsPasswordPropertyId, UIA_PROPERTY_ID.UIA_IsValuePatternAvailablePropertyId, UIA_PROPERTY_ID.UIA_ValueValuePropertyId],
            UiaSession.ReadBackProperties);
        Assert.Equal([UIA_PATTERN_ID.UIA_ValuePatternId], UiaSession.ReadBackPatterns);
    }
}
