//! Menu paths, listing and selecting over an in-memory menu: the cases of the Swift port's MenuToolTests.
use desktop_mcp_core::menu::{list, resolve, select, shortcut_label, MenuNode};
use std::cell::Cell;
use std::rc::Rc;

/// An in-memory item; its counters are shared by its copies. A `lazy` item reports no children until pressed,
/// like a bar item whose menu is built when it opens.
#[derive(Clone, Default)]
struct Node {
    title: String,
    enabled: bool,
    shortcut: Option<String>,
    separator: bool,
    kids: Vec<Node>,
    lazy: bool,
    presses: Rc<Cell<usize>>,
    cancels: Rc<Cell<usize>>,
    populated: Rc<Cell<bool>>,
}

fn node(title: &str, kids: Vec<Node>) -> Node {
    Node { title: title.into(), enabled: true, kids, ..Node::default() }
}
fn with_shortcut(title: &str, shortcut: &str) -> Node {
    Node { shortcut: Some(shortcut.into()), ..node(title, vec![]) }
}
fn disabled(title: &str) -> Node {
    Node { enabled: false, ..node(title, vec![]) }
}
fn separator() -> Node {
    Node { separator: true, ..Node::default() }
}

impl MenuNode for Node {
    fn title(&self) -> String {
        self.title.clone()
    }
    fn enabled(&self) -> bool {
        self.enabled
    }
    fn shortcut(&self) -> Option<String> {
        self.shortcut.clone()
    }
    fn is_separator(&self) -> bool {
        self.separator
    }
    fn children(&self) -> Vec<Node> {
        if self.lazy && !self.populated.get() && self.press() && !self.kids.is_empty() {
            self.populated.set(true);
        }
        self.kids.clone()
    }
    fn press(&self) -> bool {
        self.presses.set(self.presses.get() + 1);
        true
    }
    fn opened_menu(&self) -> bool {
        self.populated.get()
    }
    fn cancel(&self) {
        self.cancels.set(self.cancels.get() + 1);
    }
}

/// Apple / File[New ⌘N, —, Export… (disabled), Open Recent > [a, b]] / Edit[Copy, copy]
fn bar() -> Vec<Node> {
    vec![
        node("Apple", vec![node("About This Mac", vec![])]),
        node("File", vec![with_shortcut("New", "⌘N"), separator(), disabled("Export…"), node("Open Recent", vec![node("a", vec![]), node("b", vec![])])]),
        node("Edit", vec![with_shortcut("Copy", "⌘C"), node("copy", vec![])]),
    ]
}
fn lazy_bar() -> Vec<Node> {
    let mut roots = bar();
    roots[1].lazy = true;
    roots
}
fn path(items: &[&str]) -> Vec<String> {
    items.iter().map(|s| s.to_string()).collect()
}
fn titles(path: &[&str], roots: Vec<Node>) -> Result<Vec<String>, String> {
    resolve(&self::path(path), roots).map(|chain| chain.iter().map(|n| n.title.clone()).collect())
}

#[test]
fn exact_then_case_insensitive_then_ellipsis() {
    assert_eq!(titles(&["File", "Export…"], bar()), Ok(path(&["File", "Export…"])));
    assert_eq!(titles(&["file", "export…"], bar()), Ok(path(&["File", "Export…"])));
    assert_eq!(titles(&["file", "Export..."], bar()), Ok(path(&["File", "Export…"])));
    assert_eq!(titles(&["FILE ", "open recent", "B"], bar()), Ok(path(&["File", "Open Recent", "b"])));
}

#[test]
fn apple_names_the_first_bar_item() {
    assert_eq!(titles(&["Apple"], bar()), Ok(path(&["Apple"])));
    assert_eq!(titles(&["apple", "About This Mac"], bar()), Ok(path(&["Apple", "About This Mac"])));
    let mut other = bar();
    other[0].title = String::new();
    assert_eq!(titles(&["Apple", "About This Mac"], other), Ok(path(&["", "About This Mac"])));
}

#[test]
fn ambiguity_and_exact_wins() {
    assert_eq!(titles(&["Edit", "COPY"], bar()), Err("Menu item 'COPY' is ambiguous: Copy, copy".into()));
    let chain = resolve(&path(&["Edit", "copy"]), bar()).unwrap();
    assert_eq!((chain[1].title.as_str(), chain[1].shortcut.clone()), ("copy", None));
}

#[test]
fn a_missing_item_lists_its_siblings() {
    assert_eq!(titles(&["File", "Save"], bar()), Err("No menu item 'Save' under File. Available: New, Export…, Open Recent".into()));
    assert_eq!(titles(&["File", "Open Recent", "c"], bar()), Err("No menu item 'c' under File > Open Recent. Available: a, b".into()));
    assert_eq!(titles(&["File", "New", "x"], bar()), Err("No menu item 'x' under File > New; it has no items.".into()));
    assert_eq!(titles(&["View"], bar()), Err("No menu item 'View' under the menu bar. Available: Apple, File, Edit".into()));
}

#[test]
fn a_disabled_item_or_a_submenu_is_not_pressed() {
    let roots = bar();
    let export = roots[1].kids[2].presses.clone();
    let recent = roots[1].kids[3].presses.clone();
    assert_eq!(select(&path(&["File", "Export..."]), roots.clone()), Err("Menu item File > Export… is disabled.".into()));
    assert_eq!(select(&path(&["File", "Open Recent"]), roots), Err("Menu item File > Open Recent opens a submenu; add the item to select.".into()));
    assert_eq!((export.get(), recent.get()), (0, 0));
}

#[test]
fn a_disabled_parent_stops_the_path() {
    let mut roots = bar();
    roots[1].kids[3].enabled = false;
    let b = roots[1].kids[3].kids[1].presses.clone();
    assert_eq!(select(&path(&["File", "Open Recent", "b"]), roots), Err("Menu item File > Open Recent is disabled.".into()));
    assert_eq!(b.get(), 0);
}

#[test]
fn select_presses_the_leaf_only() {
    let roots = bar();
    let (file, recent, a, b) = (roots[1].presses.clone(), roots[1].kids[3].presses.clone(), roots[1].kids[3].kids[0].presses.clone(), roots[1].kids[3].kids[1].presses.clone());
    assert_eq!(select(&path(&["File", "Open Recent", "b"]), roots), Ok("Selected File > Open Recent > b.".into()));
    assert_eq!((file.get(), recent.get(), a.get(), b.get()), (0, 0, 0, 1));
}

#[test]
fn a_failure_after_populating_cancels_once() {
    let roots = lazy_bar();
    let (presses, cancels) = (roots[1].presses.clone(), roots[1].cancels.clone());
    assert_eq!(select(&path(&["File", "Save"]), roots), Err("No menu item 'Save' under File. Available: New, Export…, Open Recent".into()));
    assert_eq!((presses.get(), cancels.get()), (1, 1));
    let roots = lazy_bar();
    let cancels = roots[1].cancels.clone();
    assert_eq!(select(&path(&["File", "Export…"]), roots), Err("Menu item File > Export… is disabled.".into()));
    assert_eq!(cancels.get(), 1);
}

#[test]
fn no_cancel_without_populating_or_on_success() {
    let roots = bar();
    let cancels: Vec<_> = roots.iter().map(|r| r.cancels.clone()).collect();
    assert!(select(&path(&["File", "Save"]), roots).is_err());
    let lazy = lazy_bar();
    let lazy_cancels: Vec<_> = lazy.iter().map(|r| r.cancels.clone()).collect();
    let (file, new) = (lazy[1].presses.clone(), lazy[1].kids[0].presses.clone());
    assert_eq!(select(&path(&["File", "New"]), lazy), Ok("Selected File > New.".into()));
    assert_eq!((file.get(), new.get()), (1, 1));
    let top = lazy_bar();
    let top_cancels: Vec<_> = top.iter().map(|r| r.cancels.clone()).collect();
    assert!(select(&path(&["View"]), top).is_err());
    assert!(cancels.iter().chain(&lazy_cancels).chain(&top_cancels).all(|c| c.get() == 0));
}

#[test]
fn the_list_indents_and_marks() {
    assert_eq!(list(bar(), 2), "Apple\n  About This Mac\nFile\n  New  ⌘N\n  Export…  [disabled]\n  Open Recent\nEdit\n  Copy  ⌘C\n  copy");
    assert_eq!(list(bar()[1].kids.clone(), 2), "New  ⌘N\nExport…  [disabled]\nOpen Recent\n  a\n  b");
    assert_eq!(list(bar(), 1), "Apple\nFile\nEdit");
    assert_eq!(list(Vec::<Node>::new(), 2), "No menu items.");
}

#[test]
fn depth_is_capped() {
    let deep = vec![node("1", vec![node("2", vec![node("3", vec![node("4", vec![node("5", vec![node("6", vec![])])])])])])];
    assert_eq!(list(deep.clone(), 9), list(deep.clone(), 4));
    assert_eq!(list(deep.clone(), 9), "1\n  2\n    3\n      4");
    assert_eq!(list(deep, 0), "1");
}

#[test]
fn shortcut_labels() {
    assert_eq!(shortcut_label(Some("E"), Some(0), None), "⌘E");
    assert_eq!(shortcut_label(Some("E"), Some(1), None), "⇧⌘E");
    assert_eq!(shortcut_label(Some("E"), Some(6), None), "⌃⌥⌘E");
    assert_eq!(shortcut_label(Some("E"), Some(8), None), "E");
    assert_eq!(shortcut_label(Some("c"), Some(0), None), "⌘C");
    assert_eq!(shortcut_label(Some("N"), Some(1), None), "⇧⌘N");
    assert_eq!(shortcut_label(Some("F"), Some(8 | 4 | 16), None), "🌐⌃F");
    assert_eq!(shortcut_label(None, Some(2), Some(36)), "⌥⌘↩");
    assert_eq!(shortcut_label(Some("\u{1b}"), None, None), "⌘⎋");
    assert_eq!(shortcut_label(None, None, None), "");
    assert_eq!(shortcut_label(Some(" "), Some(0), None), "");
}
