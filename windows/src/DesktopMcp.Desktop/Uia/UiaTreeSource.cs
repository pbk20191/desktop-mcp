using System.Runtime.InteropServices.Marshalling;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Interop;
using DesktopMcp.Desktop.Tree;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace DesktopMcp.Desktop.Uia;

/// <summary>What the window list needs from a top-level window (upstream desktop/service.py:1054-1066 and 1192-1245).</summary>
internal sealed record WindowProbe(
    string Name, int ControlType, string ClassName, ScreenRect Bounds, int ProcessId,
    bool HasWindowPattern, bool CanMinimize, bool CanMaximize, bool HasChildren);

/// <summary>
/// <see cref="ITreeSource"/> over a <see cref="UiaSession"/>. Runs only on the UIA worker thread that owns the session.
/// <paramref name="window"/> is the walked top-level window whose UI thread <see cref="CheckWindow"/> checks; 0 (the
/// window probe) skips the check.
/// </summary>
internal sealed unsafe class UiaTreeSource(UiaSession session, long window = 0) : ITreeSource
{
    /// <summary>
    /// The most children read from one element: the first ones in document order, one sibling step each (Decisions 37).
    /// One cached TreeScope_Children fetch would make the provider build every child first (P2 Task 21).
    /// </summary>
    public const int MaxChildren = 300;

    /// <summary>The most word moves through one control's text, so a provider that never advances cannot spin forever.</summary>
    public const int MaxWordSteps = 10_000;

    /// <summary>
    /// The most characters read from one word range. Upstream reads the whole range (GetText(-1)); a provider whose word
    /// unit never ends (one huge "word") would copy all of its text into one string.
    /// </summary>
    public const int MaxWordLength = 1_000;

    /// <summary>The top-level window with the traversal properties cached.</summary>
    public UiaElementData ReadWindow(long window) =>
        Read(session.Automation.ElementFromHandleBuildCache((HWND)(nint)window, session.ElementRequest));

    /// <summary>The window-list properties of a top-level window.</summary>
    public WindowProbe ProbeWindow(long window)
    {
        var element = session.Automation.ElementFromHandleBuildCache((HWND)(nint)window, session.ProbeRequest);
        var name = UiaValues.Take(element.get_CachedName());
        // upstream desktop/service.py:1063-1066: only a nameless window is checked for children, in the raw view
        // (uia/core.py:77), which also sees the elements the control view hides.
        var hasChildren = name.Trim().Length > 0 || session.RawView.GetFirstChildElement(element) is not null;
        return new WindowProbe(
            name,
            (int)element.get_CachedControlType(),
            UiaValues.Take(element.get_CachedClassName()),
            element.get_CachedBoundingRectangle().ToScreenRect(),
            element.get_CachedProcessId(),
            UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_IsWindowPatternAvailablePropertyId) ?? false,
            UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_WindowCanMinimizePropertyId) ?? false,
            UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_WindowCanMaximizePropertyId) ?? false,
            hasChildren);
    }

    // upstream tree/cache_utils.py:173-213 reads all children with one cached TreeScope_Children fetch; this port steps
    // through the control view instead, so the cap also bounds the provider's work (Decisions 37).
    public IReadOnlyList<UiaElementData> GetChildren(UiaElementData parent, out bool capped) => ReadSiblings(
        Element(parent),
        element => session.ControlView.GetFirstChildElementBuildCache(element, session.ElementRequest),
        child => session.ControlView.GetNextSiblingElementBuildCache(child, session.ElementRequest),
        Read,
        MaxChildren,
        out capped);

    /// <summary>
    /// The first <paramref name="max"/> children in document order, one step each, plus one step past the cap that sets
    /// <paramref name="capped"/> when a further child exists (it is not read). A dead parent has no children; a sibling
    /// that dies during the steps ends the list with what was read (upstream tree/service.py:902-905 keeps what was
    /// found). Timeouts and other failures propagate.
    /// </summary>
    internal static List<UiaElementData> ReadSiblings<T>(
        T parent, Func<T, T?> first, Func<T, T?> next, Func<T, UiaElementData> read, int max, out bool capped)
        where T : class
    {
        capped = false;
        var result = new List<UiaElementData>();
        try
        {
            var child = first(parent);
            while (child is not null)
            {
                result.Add(read(child));
                if (result.Count >= max)
                {
                    // One more step only confirms that a child beyond the cap exists; it is not read.
                    capped = next(child) is not null;
                    break;
                }
                child = next(child);
            }
        }
        catch (Exception e) when (ComErrors.IsDeadElement(e))
        {
        }
        return result;
    }

    // upstream uia/controls.py:1240-1294 GetAllWordBoundingBoxes and _iter_word_ranges.
    public IReadOnlyList<WordBox> GetWords(UiaElementData element, int maxWords)
    {
        var words = new List<WordBox>();
        if (maxWords <= 0) return words;
        var iid = typeof(IUIAutomationTextPattern).GUID;
        Element(element).GetCurrentPatternAs(UIA_PATTERN_ID.UIA_TextPatternId, &iid, out var raw);
        if (raw is null) return words;
        var pattern = ComInterfaceMarshaller<IUIAutomationTextPattern>.ConvertToManaged(raw)!;
        ComInterfaceMarshaller<IUIAutomationTextPattern>.Free(raw);

        var dpi = (int)PInvoke.GetDpiForSystem();
        if (dpi <= 0) dpi = 96;
        var ranges = pattern.GetVisibleRanges();
        var steps = 0;
        for (var r = 0; ranges is not null && r < ranges.get_Length(); r++)
        {
            var visible = ranges.GetElement(r);
            // Upstream reads the uniform size once on DocumentRange; on a huge document that attribute scan can exceed the
            // transaction timeout (and block the window for 90 s), so it is read on each visible range instead. Visible
            // words get the same size either way.
            var uniformSize = FontSize(visible);
            var word = visible.Clone();
            word.ExpandToEnclosingUnit(TextUnit.TextUnit_Word);
            while (word.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, visible,
                       TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) < 0)
            {
                if (++steps > MaxWordSteps || words.Count >= maxWords) return words;
                var text = UiaValues.Take(word.GetText(MaxWordLength)).Trim();
                if (text.Length > 0)
                {
                    var size = uniformSize ?? FontSize(word);
                    foreach (var box in BoundingRectangles(word))
                    {
                        // maxWords counts boxes (the walker's word elements); a wrapped word's later lines are cut at the limit.
                        if (words.Count >= maxWords) return words;
                        words.Add(new WordBox(text, size is { } points ? ShrinkToFontSize(box, points, dpi) : box));
                    }
                }
                if (word.Move(TextUnit.TextUnit_Word, 1) == 0) break;
            }
        }
        return words;
    }

    /// <summary>
    /// Nothing to do: <c>ComObject.FinalRelease</c> is a no-op on the shared wrappers the generated marshallers return, so
    /// the element is released by its finalizer, which <see cref="UiaWorker{TSession}"/> runs soon with a gen1 collection
    /// after a job that allocated much (P2 Task 10).
    /// </summary>
    public void Release(UiaElementData element)
    {
    }

    /// <summary>The walked window answers WM_NULL within <see cref="Win32WindowSystem.ResponseTimeout"/> (Decisions 38).</summary>
    public WindowResponse CheckWindow() => window == 0 ? WindowResponse.Responding : Win32WindowSystem.CheckResponding(window);

    /// <summary>upstream uia/controls.py:1396-1411: the line box shrunk to the font's pixel height, kept on its bottom edge.</summary>
    internal static ScreenRect ShrinkToFontSize(ScreenRect box, double points, int dpi)
    {
        var pixels = points * dpi / 72.0;
        if (pixels <= 0 || pixels >= box.Height) return box;
        return box with { Top = (int)Math.Round(box.Bottom - pixels, MidpointRounding.ToEven) };
    }

    /// <summary>upstream uia/patterns.py:1578-1599: left, top, width, height doubles, each truncated to int.</summary>
    internal static IReadOnlyList<ScreenRect> ToRects(double[] values)
    {
        var rects = new List<ScreenRect>(values.Length / 4);
        for (var i = 0; i + 3 < values.Length; i += 4)
        {
            var left = (int)values[i];
            var top = (int)values[i + 1];
            rects.Add(new ScreenRect(left, top, left + (int)values[i + 2], top + (int)values[i + 3]));
        }
        return rects;
    }

    private static IReadOnlyList<ScreenRect> BoundingRectangles(IUIAutomationTextRange range)
    {
        var array = range.GetBoundingRectangles();
        try { return ToRects(UiaValues.ReadDoubles(array)); }
        finally { if (array is not null) PInvoke.SafeArrayDestroy(array); }
    }

    private static double? FontSize(IUIAutomationTextRange range)
    {
        using var value = range.GetAttributeValue(UIA_TEXTATTRIBUTE_ID.UIA_FontSizeAttributeId);
        return value.VarType switch
        {
            System.Runtime.InteropServices.VarEnum.VT_R8 => value.As<double>(),
            System.Runtime.InteropServices.VarEnum.VT_I4 => value.As<int>(),
            _ => null, // mixed or not supported
        };
    }

    private static IUIAutomationElement Element(UiaElementData data) =>
        data.Handle as IUIAutomationElement ?? throw new InvalidOperationException("The element has no UIA handle.");

    /// <summary><see cref="Read"/> for an element fetched with <see cref="UiaSession.ActionRequest"/>, which also caches the native window handle.</summary>
    internal static UiaElementData ReadAction(IUIAutomationElement element) =>
        Read(element) with { NativeWindowHandle = (nint)element.get_CachedNativeWindowHandle() };

    // upstream tree/service.py reads these through Cached* accessors and GetCachedPropertyValue.
    internal static UiaElementData Read(IUIAutomationElement element) => new()
    {
        Name = UiaValues.Take(element.get_CachedName()),
        AutomationId = UiaValues.Take(element.get_CachedAutomationId()),
        LocalizedControlType = UiaValues.Take(element.get_CachedLocalizedControlType()),
        AcceleratorKey = UiaValues.Take(element.get_CachedAcceleratorKey()),
        ClassName = UiaValues.Take(element.get_CachedClassName()),
        FrameworkId = UiaValues.Take(element.get_CachedFrameworkId()),
        ControlType = (int)element.get_CachedControlType(),
        RuntimeId = UiaValues.CachedRuntimeId(element),
        IsOffscreen = element.get_CachedIsOffscreen(),
        IsControlElement = element.get_CachedIsControlElement(),
        IsEnabled = element.get_CachedIsEnabled(),
        HasKeyboardFocus = element.get_CachedHasKeyboardFocus(),
        IsKeyboardFocusable = element.get_CachedIsKeyboardFocusable(),
        IsPassword = element.get_CachedIsPassword(),
        Bounds = element.get_CachedBoundingRectangle().ToScreenRect(),
        ClickablePoint = UiaValues.CachedPoint(element, UIA_PROPERTY_ID.UIA_ClickablePointPropertyId),
        IsSelected = UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_SelectionItemIsSelectedPropertyId),
        LegacyRole = UiaValues.CachedInt(element, UIA_PROPERTY_ID.UIA_LegacyIAccessibleRolePropertyId),
        LegacyValue = UiaValues.CachedString(element, UIA_PROPERTY_ID.UIA_LegacyIAccessibleValuePropertyId),
        LegacyDefaultAction = UiaValues.CachedString(element, UIA_PROPERTY_ID.UIA_LegacyIAccessibleDefaultActionPropertyId),
        HorizontallyScrollable = UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_ScrollHorizontallyScrollablePropertyId) ?? false,
        VerticallyScrollable = UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_ScrollVerticallyScrollablePropertyId) ?? false,
        HorizontalScrollPercent = UiaValues.CachedDouble(element, UIA_PROPERTY_ID.UIA_ScrollHorizontalScrollPercentPropertyId),
        VerticalScrollPercent = UiaValues.CachedDouble(element, UIA_PROPERTY_ID.UIA_ScrollVerticalScrollPercentPropertyId),
        ExpandCollapseState = UiaValues.CachedInt(element, UIA_PROPERTY_ID.UIA_ExpandCollapseExpandCollapseStatePropertyId),
        ToggleState = UiaValues.CachedInt(element, UIA_PROPERTY_ID.UIA_ToggleToggleStatePropertyId),
        WindowIsModal = UiaValues.CachedBool(element, UIA_PROPERTY_ID.UIA_WindowIsModalPropertyId),
        RangeValue = UiaValues.CachedDouble(element, UIA_PROPERTY_ID.UIA_RangeValueValuePropertyId),
        RangeMinimum = UiaValues.CachedDouble(element, UIA_PROPERTY_ID.UIA_RangeValueMinimumPropertyId),
        RangeMaximum = UiaValues.CachedDouble(element, UIA_PROPERTY_ID.UIA_RangeValueMaximumPropertyId),
        Handle = element,
    };
}
