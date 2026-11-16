//! The element walk over in-memory nodes: the walk cases of the Swift port's WaitToolTests.
use desktop_mcp_core::element::{walk, ElementAttributes, ElementNode, FoundElement, NotAnswering};
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

