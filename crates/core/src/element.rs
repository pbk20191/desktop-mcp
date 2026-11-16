//! Finding a UI element by title and role (`Wait until element`): a breadth-first walk over [`ElementNode`]s,
//! depth and count limited, that never reads a password field's value.
use std::time::Instant;

pub const MAX_DEPTH: usize = 25;
pub const MAX_ELEMENTS: usize = 5000;
const SECURE_ROLE: &str = "AXSecureTextField";

/// An element found: its role, the title or description that matched (or the query, when only the value did),
/// and its centre in input points.
#[derive(Clone, Debug, PartialEq)]
pub struct FoundElement {
    pub role: String,
    pub title: String,
    pub center: (f64, f64),
}

/// What the walk reads of an element in one round trip. Never the value.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ElementAttributes {
    pub role: String,
    pub subrole: Option<String>,
    pub title: String,
    pub description: String,
    /// `(x, y, width, height)`.
    pub frame: Option<(f64, f64, f64, f64)>,
}

/// The app stopped answering.
#[derive(Debug)]
pub struct NotAnswering;

/// One node of the tree the walk visits.
pub trait ElementNode: Sized {
    fn attributes(&self) -> Result<ElementAttributes, NotAnswering>;
    /// The value as text; only asked of a non-secure element.
    fn value(&self) -> String;
    fn children(&self) -> Result<Vec<Self>, NotAnswering>;
}

/// What one walk found: an exact match (the walk ended there), else the first partial match. `stopped` when the
/// deadline passed, it was cancelled or the budget ran out.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct WalkResult {
    pub exact: Option<FoundElement>,
    pub partial: Option<FoundElement>,
    pub stopped: bool,
}

/// What to call a matching element, or `None` when it does not match: `role` exactly when given, `query` as a
/// case-insensitive substring of the title, the description, or the value (`value` is asked only of a non-secure
/// element, and only when the others do not match). The name is the matched title or description, else the
/// element's title or description for a role-only match, else the query; never the value. Exact when the matched
/// text equals the query ignoring case (always for a role-only match).
pub fn label(role: &str, subrole: Option<&str>, title: &str, description: &str, value: impl FnOnce() -> String, query: Option<&str>, wanted: Option<&str>) -> Option<(String, bool)> {
    if wanted.is_some_and(|wanted| role != wanted) {
        return None;
    }
    let Some(query) = query else { return Some((if title.is_empty() { description } else { title }.to_string(), true)) };
    let lowered = query.to_lowercase();
    let contains = |text: &str| text.to_lowercase().contains(&lowered);
    let equals = |text: &str| text.to_lowercase() == lowered;
    if contains(title) {
        return Some((title.to_string(), equals(title)));
    }
    if contains(description) {
        return Some((description.to_string(), equals(description)));
    }
    if role == SECURE_ROLE || subrole == Some(SECURE_ROLE) {
        return None;
    }
    let value = value();
    contains(&value).then(|| (query.to_string(), equals(&value)))
}

/// Breadth-first from `roots` to depth [`MAX_DEPTH`], visiting at most `budget` nodes (the rest is left in
/// `budget`). Ends at the first exact match, when `deadline` passes, when `cancelled` says so, or when the app
/// stops answering.
pub fn walk<N: ElementNode>(roots: Vec<N>, title: Option<&str>, role: Option<&str>, budget: &mut usize, deadline: Instant, cancelled: &dyn Fn() -> bool) -> WalkResult {
    let mut result = WalkResult::default();
    let mut queue: std::collections::VecDeque<(N, usize)> = roots.into_iter().take(*budget).map(|node| (node, 0)).collect();
    while let Some((node, depth)) = queue.pop_front() {
        if cancelled() || Instant::now() >= deadline {
            result.stopped = true;
            return result;
        }
        *budget = budget.saturating_sub(1);
        let Ok(attributes) = node.attributes() else { return result };
        // An element whose role could not be read is treated as secure: its value is not read.
        let subrole = if attributes.role.is_empty() { Some(SECURE_ROLE) } else { attributes.subrole.as_deref() };
        if let (Some((name, exact)), Some((x, y, width, height))) = (label(&attributes.role, subrole, &attributes.title, &attributes.description, || node.value(), title, role), attributes.frame) {
            let found = FoundElement { role: attributes.role.clone(), title: name, center: (x + width / 2.0, y + height / 2.0) };
            if exact {
                result.exact = Some(found);
                return result;
            }
            result.partial.get_or_insert(found);
        }
        if depth < MAX_DEPTH {
            // Enqueued nodes count against the budget, so the queue stays bounded.
            let room = budget.saturating_sub(queue.len());
            if room > 0 {
                let Ok(children) = node.children() else { return result };
                queue.extend(children.into_iter().take(room).map(|child| (child, depth + 1)));
            }
        }
    }
    if *budget == 0 {
        result.stopped = true;
    }
    result
}
