//! UI Automation as a [`TreeSource`]: one window's control view, with every property the walk reads fetched in
//! the same round trip (a cache request). Runs on pool threads in the multithreaded apartment.
use crate::tree::{ElementProps, TreeSource};
use std::sync::Once;
use windows::core::{Interface, BSTR};
use windows::Win32::System::Variant::VARIANT;
use windows::Win32::Foundation::HWND;
use windows::Win32::System::Com::{CoCreateInstance, CoIncrementMTAUsage, CLSCTX_INPROC_SERVER};
use windows::Win32::UI::Accessibility::{
    CUIAutomation8, IUIAutomation, IUIAutomation2, IUIAutomationCacheRequest, IUIAutomationElement, IUIAutomationTreeWalker, UIA_AutomationIdPropertyId, UIA_BoundingRectanglePropertyId,
    UIA_ControlTypePropertyId, UIA_ExpandCollapseExpandCollapseStatePropertyId, UIA_IsControlElementPropertyId, UIA_IsEnabledPropertyId, UIA_IsKeyboardFocusablePropertyId,
    UIA_IsOffscreenPropertyId, UIA_LegacyIAccessibleDefaultActionPropertyId, UIA_LegacyIAccessibleRolePropertyId, UIA_NamePropertyId, UIA_PROPERTY_ID,
    UIA_RuntimeIdPropertyId, UIA_ScrollHorizontalScrollPercentPropertyId, UIA_ScrollHorizontallyScrollablePropertyId, UIA_ScrollVerticalScrollPercentPropertyId, UIA_ScrollVerticallyScrollablePropertyId,
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
    id.checked_sub(50000).and_then(|index| usize::try_from(index).ok()).and_then(|index| CONTROL_TYPES.get(index)).copied().unwrap_or("Unknown")
}

const PROPERTIES: [UIA_PROPERTY_ID; 16] = [
    UIA_RuntimeIdPropertyId,
    UIA_ControlTypePropertyId,
    UIA_NamePropertyId,
    UIA_AutomationIdPropertyId,
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
    walker: IUIAutomationTreeWalker,
    root: IUIAutomationElement,
    /// The whole control view came in one call: children are read from the cache.
    prefetched: bool,
}

/// How long one UI Automation call may wait for a provider: an app that hangs mid-walk costs at most this.
const CONNECTION_TIMEOUT_MS: u32 = 1000;
const TRANSACTION_TIMEOUT_MS: u32 = 2000;

/// A UI Automation client whose calls give up on a provider after the timeouts above.
pub fn automation() -> Option<IUIAutomation> {
    ensure_mta();
    let automation: IUIAutomation = unsafe { CoCreateInstance(&CUIAutomation8, None, CLSCTX_INPROC_SERVER) }.ok()?;
    if let Ok(bounded) = automation.cast::<IUIAutomation2>() {
        unsafe {
            let _ = bounded.SetConnectionTimeout(CONNECTION_TIMEOUT_MS);
            let _ = bounded.SetTransactionTimeout(TRANSACTION_TIMEOUT_MS);
        }
    }
    Some(automation)
}

impl WindowTree {
    /// The tree of the window `hwnd`; `None` when UI Automation cannot reach it (an elevated window, one that
    /// closed).
    pub fn new(hwnd: isize) -> Option<WindowTree> {
        let automation = automation()?;
        let cache = unsafe { automation.CreateCacheRequest() }.ok()?;
        for property in PROPERTIES {
            unsafe { cache.AddProperty(property) }.ok()?;
        }
        let walker = unsafe { automation.ControlViewWalker() }.ok()?;
        let root = unsafe { automation.ElementFromHandleBuildCache(HWND(hwnd as *mut _), &cache) }.ok()?;
        Some(WindowTree { cache, walker, root, prefetched: false })
    }

    /// The window's whole control view, fetched in one cross-process call (several times faster than asking for
    /// each element: Chrome's 2,700 elements in 0.3 s, not 1.5 s); `None` also when the call fails or times out (a
    /// tree too big for one transaction), for the caller to walk it element by element.
    pub fn prefetched(hwnd: isize) -> Option<WindowTree> {
        let automation = automation()?;
        let cache = unsafe { automation.CreateCacheRequest() }.ok()?;
        for property in PROPERTIES {
            unsafe { cache.AddProperty(property) }.ok()?;
        }
        unsafe {
            cache.SetTreeScope(windows::Win32::UI::Accessibility::TreeScope_Subtree).ok()?;
            cache.SetTreeFilter(&automation.ControlViewCondition().ok()?).ok()?;
        }
        let walker = unsafe { automation.ControlViewWalker() }.ok()?;
        let root = unsafe { automation.ElementFromHandleBuildCache(HWND(hwnd as *mut _), &cache) }.ok()?;
        Some(WindowTree { cache, walker, root, prefetched: true })
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

/// The element's cached runtime id (a `VT_ARRAY | VT_I4`); `None` when the provider gives none.
fn cached_runtime_id(element: &IUIAutomationElement) -> Option<Vec<i32>> {
    use windows::Win32::System::Ole::{SafeArrayAccessData, SafeArrayGetLBound, SafeArrayGetUBound, SafeArrayUnaccessData};
    use windows::Win32::System::Variant::{VT_ARRAY, VT_I4};
    let value = cached(element, UIA_RuntimeIdPropertyId)?;
    let inner = unsafe { &value.Anonymous.Anonymous };
    let array = unsafe { inner.Anonymous.parray };
    if inner.vt != (VT_ARRAY | VT_I4) || array.is_null() {
        return None;
    }
    let (low, high) = unsafe { (SafeArrayGetLBound(array, 1).ok()?, SafeArrayGetUBound(array, 1).ok()?) };
    let length = usize::try_from(high - low + 1).ok().filter(|&length| length > 0)?;
    let mut data = std::ptr::null_mut();
    unsafe { SafeArrayAccessData(array, &mut data) }.ok()?;
    let id = unsafe { std::slice::from_raw_parts(data as *const i32, length) }.to_vec();
    let _ = unsafe { SafeArrayUnaccessData(array) };
    Some(id)
}

impl TreeSource for WindowTree {
    type Node = IUIAutomationElement;
    type Identity = Vec<i32>;

    fn identity(&self, node: &IUIAutomationElement) -> Option<Vec<i32>> {
        cached_runtime_id(node)
    }

    fn root(&self) -> Option<IUIAutomationElement> {
        Some(self.root.clone())
    }

    /// One sibling at a time, so the cap also bounds what the provider is asked for.
    fn children(&self, node: &IUIAutomationElement, limit: usize) -> Vec<IUIAutomationElement> {
        let mut children = Vec::new();
        if self.prefetched {
            let Ok(array) = (unsafe { node.GetCachedChildren() }) else { return children };
            let length = unsafe { array.Length() }.unwrap_or(0).max(0) as usize;
            return (0..length.min(limit)).filter_map(|i| unsafe { array.GetElement(i as i32) }.ok()).collect();
        }
        let mut next = unsafe { self.walker.GetFirstChildElementBuildCache(node, &self.cache) }.ok();
        while let Some(child) = next {
            if children.len() >= limit {
                break;
            }
            next = unsafe { self.walker.GetNextSiblingElementBuildCache(&child, &self.cache) }.ok();
            children.push(child);
        }
        children
    }

    fn props(&self, node: &IUIAutomationElement) -> ElementProps {
        let rect = unsafe { node.CachedBoundingRectangle() }.unwrap_or_default();
        ElementProps {
            control_type: control_type_name(cached_i32(node, UIA_ControlTypePropertyId).unwrap_or(0)).to_string(),
            name: cached_text(node, UIA_NamePropertyId),
            automation_id: cached_text(node, UIA_AutomationIdPropertyId),
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
    let Some(automation) = automation() else { return false };
    let point = windows::Win32::Foundation::POINT { x, y };
    unsafe { automation.ElementFromPoint(point) }.and_then(|element| unsafe { element.CurrentIsPassword() }).map(|password| password.as_bool()).unwrap_or(false)
}
