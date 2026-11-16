//! The element walk over in-memory nodes: the walk cases of the Swift port's WaitToolTests.
use desktop_mcp_core::element::{label, walk, ElementAttributes, ElementNode, FoundElement, NotAnswering};
use std::cell::Cell;
use std::rc::Rc;
use std::time::{Duration, Instant};

#[derive(Clone, Default)]
struct Node {
    role: String,
    subrole: Option<String>,
    title: String,
    value: String,
    frame: Option<(f64, f64, f64, f64)>,
    kids: Vec<Node>,
    hangs: bool,
    visits: Rc<Cell<usize>>,
    /// Called with the visit count after each visit.
    on_visit: Option<Rc<dyn Fn(usize)>>,
}

impl ElementNode for Node {
    fn attributes(&self) -> Result<ElementAttributes, NotAnswering> {
        self.visits.set(self.visits.get() + 1);
        if let Some(on_visit) = &self.on_visit {
            on_visit(self.visits.get());
        }
        if self.hangs {
            return Err(NotAnswering);
        }
        Ok(ElementAttributes { role: self.role.clone(), subrole: self.subrole.clone(), title: self.title.clone(), description: String::new(), frame: self.frame })
    }
    fn value(&self) -> String {
        assert!(!self.role.is_empty() && self.role != "AXSecureTextField" && self.subrole.as_deref() != Some("AXSecureTextField"), "read a secure or unreadable value");
        self.value.clone()
    }
    fn children(&self) -> Result<Vec<Node>, NotAnswering> {
        Ok(self.kids.clone())
    }
}

fn group(visits: &Rc<Cell<usize>>) -> Node {
    Node { role: "AXGroup".into(), frame: Some((0.0, 0.0, 1.0, 1.0)), visits: visits.clone(), ..Node::default() }
}
fn button(title: &str, visits: &Rc<Cell<usize>>) -> Node {
    Node { role: "AXButton".into(), title: title.into(), frame: Some((100.0, 300.0, 40.0, 80.0)), visits: visits.clone(), ..Node::default() }
}
/// A window holding `count` plain groups, then `last`.
fn window(visits: &Rc<Cell<usize>>, count: usize, last: Vec<Node>) -> Node {
    Node { role: "AXWindow".into(), kids: (0..count).map(|_| group(visits)).chain(last).collect(), visits: visits.clone(), ..Node::default() }
}
fn find(roots: Vec<Node>, title: Option<&str>, role: Option<&str>) -> desktop_mcp_core::element::WalkResult {
    walk(roots, title, role, &mut 5000, Instant::now() + Duration::from_secs(60), &|| false)
}

#[test]
fn the_walk_stops_at_the_first_exact_match() {
    let visits = Rc::new(Cell::new(0));
    let root = window(&visits, 3, std::iter::once(button("Save", &visits)).chain((0..50).map(|_| group(&visits))).collect());
    let result = find(vec![root], Some("save"), Some("AXButton"));
    assert_eq!(result.exact, Some(FoundElement { role: "AXButton".into(), title: "Save".into(), center: (120.0, 340.0) }));
    assert_eq!(visits.get(), 5, "the window, three groups, the button");
}

#[test]
fn an_exact_title_wins_over_an_earlier_substring() {
    let visits = Rc::new(Cell::new(0));
    let result = find(vec![window(&visits, 0, vec![button("Save As…", &visits), button("SAVE", &visits)])], Some("save"), None);
    assert_eq!(result.exact.unwrap().title, "SAVE");
    let only = find(vec![window(&visits, 0, vec![button("Save As…", &visits)])], Some("save"), None);
    assert_eq!((only.exact, only.partial.unwrap().title), (None, "Save As…".to_string()));
    // The first partial match is kept.
    let two = find(vec![window(&visits, 0, vec![button("Save As…", &visits), button("Save All", &visits)])], Some("save"), None);
    assert_eq!(two.partial.unwrap().title, "Save As…");
}

#[test]
fn the_walk_honours_a_deadline_mid_walk() {
    let visits = Rc::new(Cell::new(0));
    let mut root = window(&visits, 200, vec![]);
    let slow: Rc<dyn Fn(usize)> = Rc::new(|_| std::thread::sleep(Duration::from_millis(20)));
    root.on_visit = Some(slow.clone());
    root.kids.iter_mut().for_each(|kid| kid.on_visit = Some(slow.clone()));
    let start = Instant::now();
    let result = walk(vec![root], Some("missing"), None, &mut 5000, Instant::now() + Duration::from_millis(100), &|| false);
    assert!(result.stopped && result.exact.is_none());
    assert!(visits.get() < 20 && start.elapsed() < Duration::from_secs(1));
    let past = Rc::new(Cell::new(0));
    walk(vec![window(&past, 5, vec![])], Some("x"), None, &mut 5000, Instant::now() - Duration::from_secs(1), &|| false);
    assert_eq!(past.get(), 0);
}

#[test]
fn the_walk_honours_cancellation_mid_walk() {
    let visits = Rc::new(Cell::new(0));
    let watched = visits.clone();
    let result = walk(vec![window(&visits, 100, vec![])], Some("missing"), None, &mut 5000, Instant::now() + Duration::from_secs(60), &move || watched.get() >= 7);
    assert!(result.stopped);
    assert_eq!(visits.get(), 7);
}

#[test]
fn the_walk_ends_when_the_app_stops_answering() {
    let visits = Rc::new(Cell::new(0));
    let hung = Node { hangs: true, visits: visits.clone(), ..Node::default() };
    let root = window(&visits, 2, std::iter::once(hung).chain((0..10).map(|_| group(&visits))).collect());
    assert_eq!(find(vec![root], Some("missing"), None).exact, None);
    assert_eq!(visits.get(), 4);
}

#[test]
fn the_budget_bounds_visits_and_queue() {
    let visits = Rc::new(Cell::new(0));
    let mut budget = 10;
    let result = walk(vec![window(&visits, 100, vec![])], Some("missing"), None, &mut budget, Instant::now() + Duration::from_secs(60), &|| false);
    assert_eq!((visits.get(), budget, result.stopped), (10, 0, true));
}

#[test]
fn secure_and_unreadable_values_are_never_read() {
    let visits = Rc::new(Cell::new(0));
    let secure = Node { role: "AXTextField".into(), subrole: Some("AXSecureTextField".into()), value: "Save".into(), frame: Some((0.0, 0.0, 1.0, 1.0)), visits: visits.clone(), ..Node::default() };
    let secure_role = Node { role: "AXSecureTextField".into(), value: "Save".into(), frame: Some((0.0, 0.0, 1.0, 1.0)), visits: visits.clone(), ..Node::default() };
    let unreadable = Node { value: "Save".into(), frame: Some((0.0, 0.0, 1.0, 1.0)), visits: visits.clone(), ..Node::default() };
    let result = find(vec![window(&visits, 0, vec![secure, secure_role, unreadable])], Some("Save"), None);
    assert_eq!((result.exact, result.partial, visits.get()), (None, None, 4));
    let plain = Node { role: "AXStaticText".into(), value: "save".into(), frame: Some((0.0, 0.0, 1.0, 1.0)), ..Node::default() };
    assert_eq!(find(vec![window(&visits, 0, vec![plain])], Some("Save"), None).exact.unwrap().title, "Save");
}

#[test]
fn labels_name_titles_never_values() {
    let never = || -> String { panic!("read a secure value") };
    assert_eq!(label("AXTextField", Some("AXSecureTextField"), "", "", never, Some("Save"), None), None);
    assert_eq!(label("AXSecureTextField", None, "", "", never, Some("save"), Some("AXSecureTextField")), None);
    assert_eq!(label("AXSecureTextField", None, "", "Password", never, Some("pass"), None), Some(("Password".into(), false)));
    assert_eq!(label("AXStaticText", None, "", "", || "Save changes?".into(), Some("SAVE"), None), Some(("SAVE".into(), false)));
    assert_eq!(label("AXButton", None, "Save", "", String::new, Some("sav"), Some("AXButton")), Some(("Save".into(), false)));
    assert_eq!(label("AXButton", None, "Save", "", String::new, Some("save"), Some("AXCheckBox")), None);
    assert_eq!(label("AXTextField", None, "", "", || panic!("read a value for a role-only match"), None, Some("AXTextField")), Some((String::new(), true)));
}
