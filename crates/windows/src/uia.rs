//! UI Automation as a [`TreeSource`]: one window's control view, with every property the walk reads fetched in
//! the same round trip (a cache request). Runs on pool threads in the multithreaded apartment.
use crate::tree::{ElementProps, TreeSource};
use std::sync::Once;
use windows::core::BSTR;
use windows::Win32::System::Variant::VARIANT;
use windows::Win32::Foundation::HWND;
use windows::Win32::System::Com::{CoCreateInstance, CoIncrementMTAUsage, CLSCTX_INPROC_SERVER};
use windows::Win32::UI::Accessibility::{
    CUIAutomation8, IUIAutomation, IUIAutomationCacheRequest, IUIAutomationCondition, IUIAutomationElement, TreeScope_Children, UIA_BoundingRectanglePropertyId,
    UIA_ControlTypePropertyId, UIA_ExpandCollapseExpandCollapseStatePropertyId, UIA_IsControlElementPropertyId, UIA_IsEnabledPropertyId, UIA_IsKeyboardFocusablePropertyId,
    UIA_IsOffscreenPropertyId, UIA_LegacyIAccessibleDefaultActionPropertyId, UIA_LegacyIAccessibleRolePropertyId, UIA_NamePropertyId, UIA_PROPERTY_ID,
    UIA_ScrollHorizontalScrollPercentPropertyId, UIA_ScrollHorizontallyScrollablePropertyId, UIA_ScrollVerticalScrollPercentPropertyId, UIA_ScrollVerticallyScrollablePropertyId,
};

/// Pool threads join the process's multithreaded apartment, which this keeps alive.
pub fn ensure_mta() {
    static MTA: Once = Once::new();
    MTA.call_once(|| {
        // The cookie is never released: the apartment lives as long as the process.
        let _ = unsafe { CoIncrementMTAUsage() };
    });
}

const CONTROL_TYPES: [&str; 41] = [
    "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image", "ListItem", "List", "Menu", "MenuBar", "MenuItem", "ProgressBar", "RadioButton", "ScrollBar", "Slider",
    "Spinner", "StatusBar", "Tab", "TabItem", "Text", "ToolBar", "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb", "DataGrid", "DataItem", "Document", "SplitButton", "Window",
    "Pane", "Header", "HeaderItem", "Table", "TitleBar", "Separator", "SemanticZoom", "AppBar",
];

/// A UIA control type id's name without "Control" (`Button`); `Unknown` outside the known range.
pub fn control_type_name(id: i32) -> &'static str {
    usize::try_from(id - 50000).ok().and_then(|index| CONTROL_TYPES.get(index)).copied().unwrap_or("Unknown")
}

const PROPERTIES: [UIA_PROPERTY_ID; 14] = [
    UIA_ControlTypePropertyId,
    UIA_NamePropertyId,
    UIA_IsOffscreenPropertyId,
    UIA_IsEnabledPropertyId,
    UIA_IsControlElementPropertyId,
    UIA_IsKeyboardFocusablePropertyId,
    UIA_BoundingRectanglePropertyId,
    UIA_LegacyIAccessibleRolePropertyId,
    UIA_LegacyIAccessibleDefaultActionPropertyId,
    UIA_ExpandCollapseExpandCollapseStatePropertyId,
    UIA_ScrollVerticallyScrollablePropertyId,
    UIA_ScrollHorizontallyScrollablePropertyId,
    UIA_ScrollVerticalScrollPercentPropertyId,
    UIA_ScrollHorizontalScrollPercentPropertyId,
];

pub struct WindowTree {
    cache: IUIAutomationCacheRequest,
    condition: IUIAutomationCondition,
    root: IUIAutomationElement,
}

impl WindowTree {
    /// The tree of the window `hwnd`; `None` when UI Automation cannot reach it (an elevated window, one that
    /// closed).
    pub fn new(hwnd: isize) -> Option<WindowTree> {
        ensure_mta();
        let automation: IUIAutomation = unsafe { CoCreateInstance(&CUIAutomation8, None, CLSCTX_INPROC_SERVER) }.ok()?;
        let cache = unsafe { automation.CreateCacheRequest() }.ok()?;
        for property in PROPERTIES {
            unsafe { cache.AddProperty(property) }.ok()?;
        }
        let condition = unsafe { automation.ControlViewCondition() }.ok()?;
        let root = unsafe { automation.ElementFromHandleBuildCache(HWND(hwnd as *mut _), &cache) }.ok()?;
        Some(WindowTree { cache, condition, root })
    }
}

fn cached(element: &IUIAutomationElement, property: UIA_PROPERTY_ID) -> Option<VARIANT> {
    unsafe { element.GetCachedPropertyValue(property) }.ok()
}

fn cached_bool(element: &IUIAutomationElement, property: UIA_PROPERTY_ID) -> bool {
    cached(element, property).and_then(|value| bool::try_from(&value).ok()).unwrap_or(false)
}

fn cached_f64(element: &IUIAutomationElement, property: UIA_PROPERTY_ID) -> f64 {
    cached(element, property).and_then(|value| f64::try_from(&value).ok()).unwrap_or(0.0)
}

fn cached_i32(element: &IUIAutomationElement, property: UIA_PROPERTY_ID) -> Option<i32> {
    cached(element, property).and_then(|value| i32::try_from(&value).ok())
}

fn cached_text(element: &IUIAutomationElement, property: UIA_PROPERTY_ID) -> String {
    cached(element, property).and_then(|value| BSTR::try_from(&value).ok()).map(|text| text.to_string()).unwrap_or_default()
}

impl TreeSource for WindowTree {
    type Node = IUIAutomationElement;

    fn root(&self) -> Option<IUIAutomationElement> {
        Some(self.root.clone())
    }

    fn children(&self, node: &IUIAutomationElement, limit: usize) -> Vec<IUIAutomationElement> {
        let Ok(found) = (unsafe { node.FindAllBuildCache(TreeScope_Children, &self.condition, &self.cache) }) else { return Vec::new() };
        let count = unsafe { found.Length() }.unwrap_or(0).max(0) as usize;
        (0..count.min(limit)).filter_map(|index| unsafe { found.GetElement(index as i32) }.ok()).collect()
    }

    fn props(&self, node: &IUIAutomationElement) -> ElementProps {
        let rect = unsafe { node.CachedBoundingRectangle() }.unwrap_or_default();
        ElementProps {
            control_type: control_type_name(cached_i32(node, UIA_ControlTypePropertyId).unwrap_or(0)).to_string(),
            name: cached_text(node, UIA_NamePropertyId),
            enabled: cached_bool(node, UIA_IsEnabledPropertyId),
            offscreen: cached_bool(node, UIA_IsOffscreenPropertyId),
            control_element: cached_bool(node, UIA_IsControlElementPropertyId),
            keyboard_focusable: cached_bool(node, UIA_IsKeyboardFocusablePropertyId),
            rect: (rect.left as f64, rect.top as f64, rect.right as f64, rect.bottom as f64),
            legacy_role: cached_i32(node, UIA_LegacyIAccessibleRolePropertyId).map(|role| role as u32),
            legacy_default_action: cached_text(node, UIA_LegacyIAccessibleDefaultActionPropertyId),
            expand_collapse: cached_i32(node, UIA_ExpandCollapseExpandCollapseStatePropertyId),
            vertically_scrollable: cached_bool(node, UIA_ScrollVerticallyScrollablePropertyId),
            horizontally_scrollable: cached_bool(node, UIA_ScrollHorizontallyScrollablePropertyId),
            vertical_percent: cached_f64(node, UIA_ScrollVerticalScrollPercentPropertyId),
            horizontal_percent: cached_f64(node, UIA_ScrollHorizontalScrollPercentPropertyId),
        }
    }
}

/// Whether the element at (x, y) in screen pixels is a password field; false when nothing can be read there.
pub fn is_password_at(x: i32, y: i32) -> bool {
    ensure_mta();
    let Ok(automation) = (unsafe { CoCreateInstance::<_, IUIAutomation>(&CUIAutomation8, None, CLSCTX_INPROC_SERVER) }) else { return false };
    let point = windows::Win32::Foundation::POINT { x, y };
    unsafe { automation.ElementFromPoint(point) }.and_then(|element| unsafe { element.CurrentIsPassword() }).map(|password| password.as_bool()).unwrap_or(false)
}
