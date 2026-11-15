//! HTML parsing by the system: Foundation's `NSXMLDocument` in tidy-HTML mode (libxml2), which repairs unclosed
//! and misnested tags and decodes entities the way a browser would. The body is decoded first with CoreFoundation's
//! charset table.
use desktop_mcp_core::html::{Element, Node};
use objc2::AnyThread;
use objc2_core_foundation::{kCFStringEncodingInvalidId, CFString};
use objc2_foundation::{NSData, NSString, NSXMLDocument, NSXMLElement, NSXMLNode, NSXMLNodeKind, NSXMLNodeOptions};

/// The `html` element of `body` decoded from `charset` (UTF-8 when absent or unknown).
pub fn parse(body: &[u8], charset: Option<&str>) -> Option<Element> {
    let text = decode(body, charset);
    // The text is already decoded, so the parser must not decode it again from a charset the page declares: a
    // string input fixes the encoding to UTF-8, which makes it ignore `<meta charset>`; an XML declaration would
    // still override that, so it is dropped.
    let text = NSString::from_str(without_xml_declaration(&text));
    let options = NSXMLNodeOptions::DocumentTidyHTML | NSXMLNodeOptions::NodeLoadExternalEntitiesNever;
    let document = NSXMLDocument::initWithXMLString_options_error(NSXMLDocument::alloc(), &text, options).ok()?;
    let root = document.rootElement()?;
    Some(element(&root))
}

fn decode(body: &[u8], charset: Option<&str>) -> String {
    if let Some(charset) = charset {
        let encoding = CFString::convert_iana_char_set_name_to_encoding(&CFString::from_str(charset));
        if encoding != kCFStringEncodingInvalidId {
            let encoding = CFString::convert_encoding_to_ns_string_encoding(encoding);
            if let Some(text) = NSString::initWithData_encoding(NSString::alloc(), &NSData::with_bytes(body), encoding as _) {
                return text.to_string();
            }
        }
    }
    String::from_utf8_lossy(body).into_owned()
}

/// `text` without a leading `<?xml ...?>`.
fn without_xml_declaration(text: &str) -> &str {
    let trimmed = text.trim_start();
    let is_declaration = trimmed.get(..5).is_some_and(|start| start.eq_ignore_ascii_case("<?xml"))
        && !trimmed[5..].starts_with(|c: char| c.is_alphanumeric() || c == '_');
    match trimmed.find('>') {
        Some(end) if is_declaration && trimmed[..end].ends_with('?') => &trimmed[end + 1..],
        _ => text,
    }
}

fn element(node: &NSXMLElement) -> Element {
    let name = node.name().map(|name| name.to_string().to_lowercase()).unwrap_or_default();
    let attributes = node.attributes()
        .map(|attributes| {
            attributes
                .iter()
                .map(|attribute| (attribute.name().map(|n| n.to_string()).unwrap_or_default(), attribute.stringValue().map(|v| v.to_string()).unwrap_or_default()))
                .collect()
        })
        .unwrap_or_default();
    let children = node.children().map(|children| children.iter().filter_map(|child| self::node(&child)).collect()).unwrap_or_default();
    Element { name, attributes, children }
}

/// Text and elements; comments, processing instructions and the DTD are left out.
fn node(node: &NSXMLNode) -> Option<Node> {
    match node.kind() {
        NSXMLNodeKind::TextKind => Some(Node::Text(node.stringValue().map(|text| text.to_string()).unwrap_or_default())),
        NSXMLNodeKind::ElementKind => {
            let element: &NSXMLElement = unsafe { &*(node as *const NSXMLNode as *const NSXMLElement) };
            Some(Node::Element(self::element(element)))
        }
        _ => None,
    }
}
