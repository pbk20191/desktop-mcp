using Windows.Win32.UI.Accessibility;

namespace DesktopMcp.Desktop.Uia;

/// <summary>
/// One UI Automation client (CUIAutomation8 as IUIAutomation6) and the cache requests the tree walk uses. Create it on
/// the UIA worker thread (MTA) and use it only there (spec §5: never on the dispatcher thread).
/// </summary>
internal sealed class UiaSession
{
    /// <summary>IUIAutomation2 connection timeout: how long UIA waits for a provider to accept a call (spec §5).</summary>
    public const uint ConnectionTimeoutMs = 2000;

    /// <summary>IUIAutomation2 transaction timeout: how long one cross-process UIA call may take (spec §5).</summary>
    public const uint TransactionTimeoutMs = 5000;

    // upstream tree/cache_utils.py:55-124, the property set spec §6 lists.
    private static readonly UIA_PROPERTY_ID[] TraversalProperties =
    [
        UIA_PROPERTY_ID.UIA_NamePropertyId,
        UIA_PROPERTY_ID.UIA_AutomationIdPropertyId,
        UIA_PROPERTY_ID.UIA_LocalizedControlTypePropertyId,
        UIA_PROPERTY_ID.UIA_AcceleratorKeyPropertyId,
        UIA_PROPERTY_ID.UIA_ClassNamePropertyId,
        UIA_PROPERTY_ID.UIA_ControlTypePropertyId,
        UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId,
        UIA_PROPERTY_ID.UIA_IsContentElementPropertyId,
        UIA_PROPERTY_ID.UIA_FrameworkIdPropertyId,
        UIA_PROPERTY_ID.UIA_ClickablePointPropertyId,
        UIA_PROPERTY_ID.UIA_IsEnabledPropertyId,
        UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId,
        UIA_PROPERTY_ID.UIA_IsControlElementPropertyId,
        UIA_PROPERTY_ID.UIA_HasKeyboardFocusPropertyId,
        UIA_PROPERTY_ID.UIA_IsKeyboardFocusablePropertyId,
        UIA_PROPERTY_ID.UIA_IsPasswordPropertyId,
        UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId,
        UIA_PROPERTY_ID.UIA_HelpTextPropertyId,
        UIA_PROPERTY_ID.UIA_LegacyIAccessibleRolePropertyId,
        UIA_PROPERTY_ID.UIA_LegacyIAccessibleValuePropertyId,
        UIA_PROPERTY_ID.UIA_LegacyIAccessibleDefaultActionPropertyId,
        UIA_PROPERTY_ID.UIA_LegacyIAccessibleStatePropertyId,
        UIA_PROPERTY_ID.UIA_ScrollHorizontallyScrollablePropertyId,
        UIA_PROPERTY_ID.UIA_ScrollVerticallyScrollablePropertyId,
        UIA_PROPERTY_ID.UIA_ScrollHorizontalScrollPercentPropertyId,
        UIA_PROPERTY_ID.UIA_ScrollVerticalScrollPercentPropertyId,
        UIA_PROPERTY_ID.UIA_ExpandCollapseExpandCollapseStatePropertyId,
        UIA_PROPERTY_ID.UIA_SelectionCanSelectMultiplePropertyId,
        UIA_PROPERTY_ID.UIA_SelectionIsSelectionRequiredPropertyId,
        UIA_PROPERTY_ID.UIA_SelectionItemIsSelectedPropertyId,
        UIA_PROPERTY_ID.UIA_WindowIsModalPropertyId,
        UIA_PROPERTY_ID.UIA_ToggleToggleStatePropertyId,
        UIA_PROPERTY_ID.UIA_RangeValueValuePropertyId,
        UIA_PROPERTY_ID.UIA_RangeValueMinimumPropertyId,
        UIA_PROPERTY_ID.UIA_RangeValueMaximumPropertyId,
    ];

    // The LegacyIAccessible property ids above must be in the request, or get_Cached* fails with E_INVALIDARG (spec §6).
    private static readonly UIA_PATTERN_ID[] TraversalPatterns =
    [
        UIA_PATTERN_ID.UIA_LegacyIAccessiblePatternId,
        UIA_PATTERN_ID.UIA_ScrollPatternId,
        UIA_PATTERN_ID.UIA_WindowPatternId,
    ];

    // What the window list needs from each top-level window (upstream desktop/service.py:1192-1245).
    private static readonly UIA_PROPERTY_ID[] ProbeProperties =
    [
        UIA_PROPERTY_ID.UIA_NamePropertyId,
        UIA_PROPERTY_ID.UIA_ControlTypePropertyId,
        UIA_PROPERTY_ID.UIA_ClassNamePropertyId,
        UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId,
        UIA_PROPERTY_ID.UIA_ProcessIdPropertyId,
        UIA_PROPERTY_ID.UIA_IsWindowPatternAvailablePropertyId,
        UIA_PROPERTY_ID.UIA_WindowCanMinimizePropertyId,
        UIA_PROPERTY_ID.UIA_WindowCanMaximizePropertyId,
    ];

    private UiaSession(IUIAutomation6 automation)
    {
        Automation = automation;
        ElementRequest = CreateRequest(automation, TraversalProperties, TraversalPatterns, TreeScope.TreeScope_Element);
        // Children only: BuildUpdatedCache with this request returns a placeholder whose cached children carry the
        // properties (upstream tree/cache_utils.py:173-186).
        ChildrenRequest = CreateRequest(automation, TraversalProperties, TraversalPatterns, TreeScope.TreeScope_Children);
        ProbeRequest = CreateRequest(automation, ProbeProperties, [UIA_PATTERN_ID.UIA_WindowPatternId], TreeScope.TreeScope_Element);
        ControlView = automation.get_ControlViewWalker();
    }

    public IUIAutomation6 Automation { get; }
    public IUIAutomationCacheRequest ElementRequest { get; }
    public IUIAutomationCacheRequest ChildrenRequest { get; }
    public IUIAutomationCacheRequest ProbeRequest { get; }
    public IUIAutomationTreeWalker ControlView { get; }

    /// <summary>Creates the client with the IUIAutomation2 timeouts set (spec §5, §12 "UIA calls into hung apps").</summary>
    public static UiaSession Create()
    {
        var automation = CUIAutomation8.CreateInstance<IUIAutomation6>();
        automation.put_ConnectionTimeout(ConnectionTimeoutMs);
        automation.put_TransactionTimeout(TransactionTimeoutMs);
        return new UiaSession(automation);
    }

    private static IUIAutomationCacheRequest CreateRequest(
        IUIAutomation6 automation, UIA_PROPERTY_ID[] properties, UIA_PATTERN_ID[] patterns, TreeScope scope)
    {
        var request = automation.CreateCacheRequest();
        foreach (var property in properties) request.AddProperty(property);
        foreach (var pattern in patterns) request.AddPattern(pattern);
        request.put_TreeScope(scope);
        return request;
    }
}
