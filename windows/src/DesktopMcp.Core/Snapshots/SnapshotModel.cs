namespace DesktopMcp.Core.Snapshots;

/// <summary>upstream desktop/views.py:20-24.</summary>
public enum WindowStatus
{
    Maximized,
    Minimized,
    Normal,
    Hidden,
}

public static class WindowStatusText
{
    /// <summary>The value upstream prints in the window tables.</summary>
    public static string ToText(this WindowStatus status) => status switch
    {
        WindowStatus.Maximized => "Maximized",
        WindowStatus.Minimized => "Minimized",
        WindowStatus.Normal => "Normal",
        _ => "Hidden",
    };
}

/// <summary>A top-level window (upstream desktop/views.py:27-45). Depth is its index in the z-ordered window scan.</summary>
public sealed record WindowInfo(string Name, int Depth, WindowStatus Status, ScreenRect Bounds, long Handle, int ProcessId, bool IsBrowser);

/// <summary>An active display (upstream uia/core.py:624-635 DisplayInfo). Index is the zero-based active display index.</summary>
public sealed record DisplayInfo(
    int Index, string DeviceName, ScreenRect Bounds, bool Primary, ScreenRect? WorkArea, int? EffectiveDpi, double? Scale, string Orientation);

/// <summary>A virtual desktop (upstream vdm/core.py:621-689); Id is the GUID in braces, upper case.</summary>
public sealed record VirtualDesktopInfo(string Id, string Name)
{
    /// <summary>upstream desktop/service.py:154-162 and vdm/core.py:627-634: the fallback when the desktops cannot be read.</summary>
    public static VirtualDesktopInfo Default { get; } = new("00000000-0000-0000-0000-000000000000", "Default Desktop");
}

/// <summary>
/// The metadata upstream's tree text renders (tree/views.py:23-66). Value, Minimum and Maximum hold Python's str() of
/// upstream's value: a stripped string, "(empty)", or a float repr for sliders.
/// </summary>
public sealed record UiMetadata
{
    public static UiMetadata Empty { get; } = new();

    public bool HasFocus { get; init; }
    public bool IsPassword { get; init; }
    public string? Value { get; init; }
    public string? Minimum { get; init; }
    public string? Maximum { get; init; }
    public string? ToggleState { get; init; }
    public string? ExpandCollapseState { get; init; }
    public string? Shortcut { get; init; }
    public bool HorizontallyScrollable { get; init; }
    public double HorizontalScrollPercent { get; init; }
    public bool VerticallyScrollable { get; init; }
    public double VerticalScrollPercent { get; init; }
}

/// <summary>
/// An interactive or scrollable element (upstream TreeElementNode / ScrollElementNode, tree/views.py:257-282). Id is
/// unique within one capture and links the element to its node in the semantic tree.
/// </summary>
public sealed record UiElement(int Id, string Name, string ControlType, string WindowName, ScreenRect Box, ScreenPoint Center, UiMetadata Metadata);

/// <summary>upstream tree/views.py:104-112 element_type.</summary>
public enum SemanticKind
{
    Desktop,
    Window,
    Structural,
    Interactive,
    Scrollable,
    Informative,
}

/// <summary>
/// A node of the semantic UI tree (upstream tree/views.py:102-123). Its fields are fixed at construction; only the
/// child list is filled while the tree is walked and rearranged by <see cref="SemanticTree"/>.
/// </summary>
public sealed class SemanticNode(SemanticKind kind, string controlType, string name)
{
    public SemanticKind Kind { get; } = kind;
    public string ControlType { get; } = controlType;
    public string Name { get; } = name;
    public ScreenRect? Box { get; init; }
    public ScreenPoint? Center { get; init; }

    /// <summary>The <see cref="UiElement.Id"/> of an interactive or scrollable node.</summary>
    public int? ElementId { get; init; }

    public UiMetadata Metadata { get; init; } = UiMetadata.Empty;
    public List<SemanticNode> Children { get; } = [];
}

/// <summary>upstream TreeState (tree/views.py:172-209).</summary>
public sealed record TreeSnapshot(
    IReadOnlyList<UiElement> Interactive, IReadOnlyList<UiElement> Scrollable, SemanticNode? Root, bool Truncated, int ElementLimit)
{
    /// <summary>
    /// The per-parent child cap when at least one element had more children than it (only the first ones were read),
    /// else null. Port-only (Decisions 37): upstream reads every child.
    /// </summary>
    public int? ChildLimit { get; init; }

    /// <summary>No UI tree (Screenshot, or Snapshot with use_ui_tree=False).</summary>
    public static TreeSnapshot Empty { get; } = new([], [], null, false, 0);
}

/// <summary>upstream DesktopState (desktop/views.py:65-103) plus the PNG.</summary>
public sealed record DesktopSnapshot
{
    public required VirtualDesktopInfo ActiveDesktop { get; init; }
    public required IReadOnlyList<VirtualDesktopInfo> AllDesktops { get; init; }
    public WindowInfo? ActiveWindow { get; init; }
    public required IReadOnlyList<WindowInfo> Windows { get; init; }

    /// <summary>Null when a region excludes the cursor; upstream then prints "None".</summary>
    public ScreenPoint? Cursor { get; init; }

    public PixelSize? OriginalSize { get; init; }
    public double? Scale { get; init; }
    public ScreenRect? Region { get; init; }
    public IReadOnlyList<long>? SelectedDisplays { get; init; }
    public required IReadOnlyList<DisplayInfo> Displays { get; init; }
    public string? Backend { get; init; }
    public required TreeSnapshot Tree { get; init; }
    public byte[]? Png { get; init; }
}
