//! HTML parsing for Scrape. Windows has no HTML parser in WinRT, so html5ever parses the way a browser does
//! (repairing unclosed and misnested tags, decoding entities).
use desktop_mcp_core::html::{Element, Node};
use html5ever::tendril::TendrilSink;
use markup5ever_rcdom::{Handle, NodeData, RcDom};

/// Elements nested deeper than this are left out: a hostile page could nest deep enough to overflow the stack,
/// and nothing a reader needs sits that deep.
const MAX_DEPTH: usize = 256;

/// The `html` element of `body`, decoded from `charset` (UTF-8 when absent or unknown).
pub fn parse(body: &[u8], charset: Option<&str>) -> Option<Element> {
    let encoding = charset.and_then(|label| encoding_rs::Encoding::for_label(label.trim().as_bytes())).unwrap_or(encoding_rs::UTF_8);
    let (text, _, _) = encoding.decode(body);
    let dom = html5ever::parse_document(RcDom::default(), Default::default()).one(text.as_ref());
    let document = dom.document;
    let root = document.children.borrow().iter().find(|child| matches!(child.data, NodeData::Element { .. })).cloned()?;
    match convert(&root) {
        Some(Node::Element(element)) => Some(element),
        _ => None,
    }
}

/// One element being built: its handle's data, the children still to visit, and the nodes built so far.
struct Frame {
    name: String,
    attributes: Vec<(String, String)>,
    pending: std::vec::IntoIter<Handle>,
    children: Vec<Node>,
}

fn frame(handle: &Handle) -> Option<Frame> {
    let NodeData::Element { name, attrs, template_contents, .. } = &handle.data else { return None };
    let mut pending: Vec<Handle> = handle.children.borrow().clone();
    if let Some(contents) = template_contents.borrow().as_ref() {
        pending.extend(contents.children.borrow().iter().cloned());
    }
    Some(Frame {
        name: name.local.to_lowercase(),
        attributes: attrs.borrow().iter().map(|attribute| (attribute.name.local.to_string(), attribute.value.to_string())).collect(),
        pending: pending.into_iter(),
        children: Vec::new(),
    })
}

/// The tree under `handle`, built with a loop rather than recursion, so a deep page needs no stack; elements
/// deeper than [`MAX_DEPTH`] are still left out, because the core walks the result recursively.
fn convert(handle: &Handle) -> Option<Node> {
    if let NodeData::Text { contents } = &handle.data {
        return Some(Node::Text(contents.borrow().to_string()));
    }
    let mut stack = vec![frame(handle)?];
    loop {
        let depth = stack.len();
        let top = stack.last_mut().expect("the root stays until it is returned");
        match top.pending.next() {
            Some(child) => match &child.data {
                NodeData::Text { contents } => top.children.push(Node::Text(contents.borrow().to_string())),
                NodeData::Element { .. } if depth < MAX_DEPTH => stack.extend(frame(&child)),
                _ => {}
            },
            None => {
                let done = stack.pop().expect("a frame is on the stack");
                let element = Node::Element(Element { name: done.name, attributes: done.attributes, children: done.children });
                match stack.last_mut() {
                    Some(parent) => parent.children.push(element),
                    None => return Some(element),
                }
            }
        }
    }
}
