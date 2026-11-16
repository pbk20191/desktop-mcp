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
    match convert(&root, 0) {
        Some(Node::Element(element)) => Some(element),
        _ => None,
    }
}

fn convert(handle: &Handle, depth: usize) -> Option<Node> {
    match &handle.data {
        NodeData::Text { contents } => Some(Node::Text(contents.borrow().to_string())),
        NodeData::Element { .. } if depth >= MAX_DEPTH => None,
        NodeData::Element { name, attrs, template_contents, .. } => {
            let mut children: Vec<Node> = handle.children.borrow().iter().filter_map(|child| convert(child, depth + 1)).collect();
            if let Some(contents) = template_contents.borrow().as_ref() {
                children.extend(contents.children.borrow().iter().filter_map(|child| convert(child, depth + 1)));
            }
            Some(Node::Element(Element {
                name: name.local.to_lowercase(),
                attributes: attrs.borrow().iter().map(|attribute| (attribute.name.local.to_string(), attribute.value.to_string())).collect(),
                children,
            }))
        }
        _ => None,
    }
}
