using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Desktop.Tree;

/// <summary>
/// One UIA element's cached properties, read once into plain values (spec §6). <see cref="Handle"/> is the COM element
/// the source needs for children and words; tests leave it null.
/// </summary>
internal sealed record UiaElementData
{
    public string Name { get; init; } = "";
    public string AutomationId { get; init; } = "";
    public string LocalizedControlType { get; init; } = "";
    public string AcceleratorKey { get; init; } = "";
    public string ClassName { get; init; } = "";

    /// <summary>The UI framework ("Chrome" for Chromium content, "Win32", "WPF", ...), or empty.</summary>
    public string FrameworkId { get; init; } = "";
    public int ControlType { get; init; }

    /// <summary>The RuntimeId as "a.b.c", or null when the provider gave none.</summary>
    public string? RuntimeId { get; init; }

    public bool IsOffscreen { get; init; }
    public bool IsControlElement { get; init; } = true;
    public bool IsEnabled { get; init; } = true;
    public bool HasKeyboardFocus { get; init; }
    public bool IsKeyboardFocusable { get; init; }
    public bool IsPassword { get; init; }
    public ScreenRect Bounds { get; init; }

    /// <summary>UIA's clickable point (physical pixels), or null when the element has none.</summary>
    public ScreenPoint? ClickablePoint { get; init; }

    /// <summary>SelectionItem.IsSelected, or null without the pattern.</summary>
    public bool? IsSelected { get; init; }
    public int? LegacyRole { get; init; }
    public string? LegacyValue { get; init; }
    public string? LegacyDefaultAction { get; init; }
    public bool HorizontallyScrollable { get; init; }
    public bool VerticallyScrollable { get; init; }
    public double? HorizontalScrollPercent { get; init; }
    public double? VerticalScrollPercent { get; init; }
    public int? ExpandCollapseState { get; init; }
    public int? ToggleState { get; init; }
    public bool? WindowIsModal { get; init; }
    public double? RangeValue { get; init; }
    public double? RangeMinimum { get; init; }
    public double? RangeMaximum { get; init; }
    public object? Handle { get; init; }
}

/// <summary>A word of a text control and one of its line boxes (upstream uia/controls.py:1267-1294).</summary>
internal readonly record struct WordBox(string Word, ScreenRect Box);

/// <summary>How the walked window answered the responsiveness check (Decisions 38).</summary>
internal enum WindowResponse
{
    /// <summary>It answered, or the check could not tell (the window is gone, or another error).</summary>
    Responding,

    /// <summary>WM_NULL timed out: the UI thread is busy, but Windows does not consider it hung.</summary>
    NotResponding,

    /// <summary>Windows considers the window hung (IsHungAppWindow).</summary>
    Hung,
}

/// <summary>What the tree walk reads beyond an element's cached properties; the walker's only external dependency.</summary>
internal interface ITreeSource
{
    /// <summary>
    /// The element's children in the control view with their properties cached; empty when it is gone.
    /// <paramref name="capped"/> is true when the element has more children than the source reads.
    /// </summary>
    IReadOnlyList<UiaElementData> GetChildren(UiaElementData parent, out bool capped);

    /// <summary>
    /// The visible words with their boxes, one entry per box (a word wrapped over two lines gives two), up to
    /// <paramref name="maxWords"/> entries: the limit counts boxes, since each becomes one word element of the walk.
    /// Empty without a TextPattern.
    /// </summary>
    IReadOnlyList<WordBox> GetWords(UiaElementData element, int maxWords);

    /// <summary>The walk is done with the element: the source may drop what it holds for it.</summary>
    void Release(UiaElementData element);

    /// <summary>Whether the walked window's UI thread still answers; checked before the window and every children and word read.</summary>
    WindowResponse CheckWindow();
}
