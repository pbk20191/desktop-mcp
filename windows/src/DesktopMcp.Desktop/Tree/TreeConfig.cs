namespace DesktopMcp.Desktop.Tree;

/// <summary>upstream tree/config.py, desktop/config.py and the classification constants of tree/service.py (spec §6).</summary>
internal static class TreeConfig
{
    public const int Button = 50000;
    public const int CheckBox = 50002;
    public const int ComboBox = 50003;
    public const int Edit = 50004;
    public const int Image = 50006;
    public const int ListItem = 50007;
    public const int RadioButton = 50013;
    public const int Slider = 50015;
    public const int TabItem = 50019;
    public const int Group = 50026;
    public const int DataItem = 50029;
    public const int Document = 50030;
    public const int Window = 50032;
    public const int Pane = 50033;

    // UIA_*ControlTypeId 50000-50040 in order; upstream names them "<Name>Control" (uia/enums.py ControlTypeNames).
    private static readonly string[] Names =
    [
        "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image", "ListItem", "List", "Menu", "MenuBar",
        "MenuItem", "ProgressBar", "RadioButton", "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab", "TabItem", "Text",
        "ToolBar", "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb", "DataGrid", "DataItem", "Document",
        "SplitButton", "Window", "Pane", "Header", "HeaderItem", "Table", "TitleBar", "Separator", "SemanticZoom", "AppBar",
    ];

    /// <summary>upstream uia/controls.py:521-526 CachedControlTypeName.</summary>
    public static string ControlTypeName(int controlType) =>
        controlType is >= 50000 and <= 50040 ? Names[controlType - 50000] + "Control" : "Unknown";

    /// <summary>upstream tree/config.py:3-35 (TextBoxControl never matches a real control type; kept as upstream has it).</summary>
    public static readonly IReadOnlySet<string> InteractiveControlTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ButtonControl", "ListItemControl", "MenuItemControl", "EditControl", "CheckBoxControl", "RadioButtonControl",
        "ComboBoxControl", "HyperlinkControl", "SplitButtonControl", "TabItemControl", "TreeItemControl", "DataItemControl",
        "HeaderItemControl", "TextBoxControl", "SpinnerControl", "SliderControl", "ScrollBarControl",
    };

    /// <summary>upstream tree/config.py:73.</summary>
    public static readonly IReadOnlySet<string> DocumentControlTypes = new HashSet<string>(StringComparer.Ordinal) { "DocumentControl" };

    /// <summary>upstream tree/config.py:75-82.</summary>
    public static readonly IReadOnlySet<string> StructuralControlTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "PaneControl", "GroupControl", "CustomControl", "ToolBarControl", "TabControl", "MenuBarControl",
    };

    /// <summary>upstream tree/config.py:84-96.</summary>
    public static readonly IReadOnlySet<string> InformativeControlTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "TextControl", "ImageControl", "StatusBarControl",
    };

    /// <summary>upstream tree/config.py:37-71 INTERACTIVE_ROLES, as MSAA ROLE_SYSTEM_* values (uia/enums.py:584-656).</summary>
    public static readonly IReadOnlySet<int> InteractiveRoles = new HashSet<int>
    {
        0x2B, 0x3E, 0x38, 0x39, 0x3A, 0x40, // PushButton, SplitButton, ButtonDropDown, ButtonMenu, ButtonDropDownGrid, OutlineButton
        0x1E,                               // Link
        0x2A, 0x3F, 0x32, 0x2E, 0x2F, 0x2C, 0x2D, // Text, IpAddress, HotkeyField, ComboBox, DropList, CheckButton, RadioButton
        0x0C, 0x22, 0x25,                   // MenuItem, ListItem, PageTab
        0x24,                               // OutlineItem
        0x33, 0x34, 0x31, 0x03, 0x04,       // Slider, SpinButton, Dial, ScrollBar, Grip
        0x19, 0x1A, 0x1D,                   // ColumnHeader, RowHeader, Cell
    };

    /// <summary>upstream tree/config.py:98, compared after Python's str.title().</summary>
    public static readonly IReadOnlySet<string> DefaultActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "Click", "Press", "Jump", "Check", "Uncheck", "Double Click",
    };

    /// <summary>upstream tree/service.py:551: control types treated as keyboard focusable without asking.</summary>
    public static readonly IReadOnlySet<string> FocusableControlTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "EditControl", "ButtonControl", "CheckBoxControl", "RadioButtonControl", "TabItemControl", "ListItemControl",
    };

    /// <summary>upstream desktop/config.py:3 and desktop/views.py:8-17.</summary>
    public static bool IsBrowser(string? processName) =>
        processName?.ToLowerInvariant() is "chrome.exe" or "msedge.exe" or "firefox.exe";

    /// <summary>
    /// upstream tree/service.py:911-920 app_name_correction. Upstream compares the window title, which never equals these
    /// class names for the shell windows; the fixed names apply by class name, then by title (Decisions 8).
    /// </summary>
    public static string WindowName(string name, string className) => FixedWindowName(className) ?? FixedWindowName(name) ?? name;

    private static string? FixedWindowName(string value) => value switch
    {
        "Progman" => "Desktop",
        "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" => "Taskbar",
        "Microsoft.UI.Content.PopupWindowSiteBridge" => "Context Menu",
        _ => null,
    };

    /// <summary>Python's str.title() for the default action check: each word capitalised.</summary>
    public static string Title(string value)
    {
        var chars = value.ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (char.IsLetter(chars[i]) && (i == 0 || !char.IsLetter(chars[i - 1]))) chars[i] = char.ToUpperInvariant(chars[i]);
        return new string(chars);
    }
}
