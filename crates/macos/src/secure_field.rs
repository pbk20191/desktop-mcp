//! Password fields: whether the keys typed now go to one. Only roles and subroles are read, never a value.
use crate::ax::Element;

pub const ROLE: &str = "AXSecureTextField";
/// How far the check climbs from the element under the point.
pub const MAX_ANCESTORS: usize = 5;
/// Where the climb stops: past these is no longer the field.
const CLIMB_STOPS: [&str; 2] = ["AXWindow", "AXWebArea"];

pub fn is_secure(role: Option<&str>, subrole: Option<&str>) -> bool {
    role == Some(ROLE) || subrole == Some(ROLE)
}

/// What the check reads of an element: its role and subrole, and its parent.
pub trait Node: Sized {
    fn roles(&self) -> (Option<String>, Option<String>);
    fn parent(&self) -> Option<Self>;
}

/// The focused element is a password field, or the element under the point is, or one of its first
/// [`MAX_ANCESTORS`] ancestors below the window or web area (a click often lands inside a web input).
pub fn receives_secure_input<N: Node>(focused: Option<N>, hit: Option<N>) -> bool {
    if let Some(focused) = focused {
        let (role, subrole) = focused.roles();
        if is_secure(role.as_deref(), subrole.as_deref()) {
            return true;
        }
    }
    let mut node = hit;
    let mut climbed = 0;
    while let Some(current) = node {
        let (role, subrole) = current.roles();
        if is_secure(role.as_deref(), subrole.as_deref()) {
            return true;
        }
        if role.as_deref().is_some_and(|role| CLIMB_STOPS.contains(&role)) || climbed == MAX_ANCESTORS {
            return false;
        }
        node = current.parent();
        climbed += 1;
    }
    false
}

/// An element of another app, with a 0.5 s messaging timeout set before its first read.
pub struct AxNode(Element);

impl AxNode {
    pub fn new(element: Element) -> AxNode {
        element.set_messaging_timeout(0.5);
        AxNode(element)
    }
}

impl Node for AxNode {
    fn roles(&self) -> (Option<String>, Option<String>) {
        let found = self.0.attributes(&["AXRole", "AXSubrole"]).unwrap_or_default();
        let get = |name: &str| found.iter().find(|(key, _)| key == name).map(|(_, value)| value.text().to_string());
        (get("AXRole"), get("AXSubrole"))
    }

    fn parent(&self) -> Option<AxNode> {
        self.0.attribute("AXParent")?.element().cloned().map(AxNode::new)
    }
}

/// Whether keys typed at the point go to a password field. Blocks on other apps: call it off the main thread.
pub fn at(x: i64, y: i64) -> bool {
    let system = Element::system_wide();
    let hit = system.at(x as f64, y as f64).map(AxNode::new);
    let focused = system.attribute("AXFocusedUIElement").and_then(|value| value.element().cloned()).map(AxNode::new);
    receives_secure_input(focused, hit)
}
