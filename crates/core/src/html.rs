//! HTML to Markdown, standing in for Python's markdownify. The platform parses (repairing unclosed and misnested
//! tags and decoding entities the way a browser would) into a [`Element`] tree; this module only walks it.
//!
//! Unlike the original (which passed `strip=["script", "style"]` and so kept their text), the contents of script,
//! style and similar non-content elements are dropped.

/// A parsed document's node.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Node {
    Text(String),
    Element(Element),
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Element {
    /// Lowercase.
    pub name: String,
    pub attributes: Vec<(String, String)>,
    pub children: Vec<Node>,
}

impl Element {
    pub fn attribute(&self, name: &str) -> &str {
        self.attributes.iter().find(|(key, _)| key.eq_ignore_ascii_case(name)).map(|(_, value)| value.as_str()).unwrap_or("")
    }

    fn child(&self, name: &str) -> Option<&Element> {
        self.children.iter().find_map(|node| match node {
            Node::Element(element) if element.name == name => Some(element),
            _ => None,
        })
    }

    /// Every text under the element, in document order (a loop: depth costs no stack).
    fn text_content(&self) -> String {
        let mut text = String::new();
        let mut pending: Vec<&Node> = self.children.iter().rev().collect();
        while let Some(node) = pending.pop() {
            match node {
                Node::Text(part) => text.push_str(part),
                Node::Element(element) => pending.extend(element.children.iter().rev()),
            }
        }
        text
    }
}

/// Dropped with a loop: a deeply nested page would otherwise drop by recursion, one stack frame per level.
impl Drop for Element {
    fn drop(&mut self) {
        let mut pending: Vec<Node> = std::mem::take(&mut self.children);
        while let Some(node) = pending.pop() {
            if let Node::Element(mut element) = node {
                pending.append(&mut element.children);
            }
        }
    }
}

const SKIPPED: &[&str] = &["script", "style", "noscript", "template", "svg", "head", "iframe", "object", "canvas"];
const BLOCKS: &[&str] = &[
    "p", "div", "section", "article", "header", "footer", "main", "nav", "aside", "form", "table", "thead", "tbody", "tfoot", "figure",
    "figcaption", "details", "summary", "dl", "dt", "dd", "fieldset", "address", "center",
];

/// The document under `root` (the `html` element) as Markdown, led by its title when it has no heading first.
pub fn to_markdown(root: &Element) -> String {
    let mut renderer = Renderer { frames: vec![Frame::new("#root", "")], lists: Vec::new(), pre_depth: 0 };
    renderer.render(root);
    let title = root.child("head").and_then(|head| head.child("title")).map(Element::text_content).unwrap_or_default();
    renderer.finish(&title)
}

/// An element whose rendered text is collected separately and then wrapped.
struct Frame {
    tag: String,
    text: String,
    href: String,
}

impl Frame {
    fn new(tag: &str, href: &str) -> Frame {
        Frame { tag: tag.to_string(), text: String::new(), href: href.to_string() }
    }
}

/// What follows an element's children (the second half of its rendering).
enum After {
    Nothing,
    BlankLine,
    List,
    Item,
    Frame(String),
    Pre,
    Row,
    Cell,
}

/// One step of the walk: an element or text to visit, or what follows an element's children.
enum Step<'a> {
    Element(&'a Element),
    Text(&'a str),
    After(After),
}

struct List {
    ordered: bool,
    counter: usize,
}

struct Renderer {
    frames: Vec<Frame>,
    lists: Vec<List>,
    pre_depth: usize,
}

fn is_newline(c: char) -> bool {
    matches!(c, '\n' | '\r' | '\u{0B}' | '\u{0C}' | '\u{85}' | '\u{2028}' | '\u{2029}')
}

impl Renderer {
    fn current(&self) -> &str {
        &self.frames.last().expect("the root frame").text
    }

    fn emit(&mut self, text: &str) {
        self.frames.last_mut().expect("the root frame").text.push_str(text);
    }

    fn ensure_blank_line(&mut self) {
        let current = self.current();
        if current.is_empty() || current.ends_with("\n\n") {
            return;
        }
        let ending = if current.ends_with('\n') { "\n" } else { "\n\n" };
        self.emit(ending);
    }

    fn ensure_newline(&mut self) {
        if !self.current().is_empty() && !self.current().ends_with('\n') {
            self.emit("\n");
        }
    }

    fn trim_trailing_spaces(&mut self) {
        let text = &mut self.frames.last_mut().expect("the root frame").text;
        text.truncate(text.trim_end_matches(' ').len());
    }

    fn close_frame(&mut self, tag: &str) {
        if self.frames.len() <= 1 || self.frames.last().expect("the root frame").tag != tag {
            return;
        }
        let frame = self.frames.pop().expect("checked above");
        let inner = frame.text;
        let trimmed = inner.trim();
        match tag {
            "a" => {
                if trimmed.is_empty() {
                    return;
                }
                let href = frame.href;
                if href.is_empty() || href.starts_with("javascript:") || href.starts_with('#') {
                    self.emit(trimmed);
                } else {
                    self.emit(&format!("[{}]({href})", trimmed.replace('\n', " ")));
                }
            }
            "strong" | "b" if !trimmed.is_empty() => self.emit(&format!("**{trimmed}**")),
            "em" | "i" if !trimmed.is_empty() => self.emit(&format!("*{trimmed}*")),
            "code" if !trimmed.is_empty() => self.emit(&format!("`{trimmed}`")),
            "strong" | "b" | "em" | "i" | "code" => {}
            "pre" => {
                self.ensure_blank_line();
                self.emit(&format!("```\n{}\n```", inner.trim_matches(is_newline)));
                self.ensure_blank_line();
            }
            _ => self.emit(&inner),
        }
    }

    fn text(&mut self, raw: &str) {
        if self.pre_depth > 0 {
            self.emit(raw);
            return;
        }
        let mut collapsed = String::with_capacity(raw.len());
        for c in raw.chars().map(|c| if c == '\u{A0}' { ' ' } else { c }) {
            if matches!(c, ' ' | '\t' | '\r' | '\n' | '\u{0C}') {
                if !collapsed.ends_with(' ') {
                    collapsed.push(' ');
                }
            } else {
                collapsed.push(c);
            }
        }
        let current = self.current();
        let at_line_start = current.is_empty() || current.ends_with(' ') || current.ends_with('\n');
        if collapsed == " " && at_line_start {
            return;
        }
        match collapsed.strip_prefix(' ') {
            Some(rest) if at_line_start => self.emit(rest),
            _ => self.emit(&collapsed),
        }
    }

    /// The document under `root`, walked with an explicit stack (no recursion, whatever the depth).
    fn render(&mut self, root: &Element) {
        let mut steps = vec![Step::Element(root)];
        while let Some(step) = steps.pop() {
            match step {
                Step::Text(text) => self.text(text),
                Step::After(after) => self.after(after),
                Step::Element(element) => {
                    if let Some(after) = self.open(element) {
                        steps.push(Step::After(after));
                        for child in element.children.iter().rev() {
                            steps.push(match child {
                                Node::Text(text) => Step::Text(text),
                                Node::Element(element) => Step::Element(element),
                            });
                        }
                    }
                }
            }
        }
    }

    /// What comes before an element's children, and what must follow them; `None` when its children are not
    /// rendered.
    fn open(&mut self, element: &Element) -> Option<After> {
        let tag = element.name.as_str();
        if SKIPPED.contains(&tag) {
            return None;
        }
        Some(match tag {
            "br" => {
                self.emit("  \n");
                return None;
            }
            "hr" => {
                self.ensure_blank_line();
                self.emit("---");
                self.ensure_blank_line();
                return None;
            }
            "img" => {
                let src = element.attribute("src");
                if !src.is_empty() {
                    self.emit(&format!("![{}]({src})", element.attribute("alt")));
                }
                return None;
            }
            "h1" | "h2" | "h3" | "h4" | "h5" | "h6" => {
                self.ensure_blank_line();
                let level: usize = tag[1..].parse().expect("a digit");
                self.emit(&format!("{} ", "#".repeat(level)));
                After::BlankLine
            }
            "ul" | "ol" => {
                if self.lists.is_empty() { self.ensure_blank_line() } else { self.ensure_newline() }
                self.lists.push(List { ordered: tag == "ol", counter: 0 });
                After::List
            }
            "li" => {
                self.ensure_newline();
                let indent = "  ".repeat(self.lists.len().max(1) - 1);
                match self.lists.last_mut() {
                    Some(list) if list.ordered => {
                        list.counter += 1;
                        let marker = format!("{indent}{}. ", list.counter);
                        self.emit(&marker);
                    }
                    _ => self.emit(&format!("{indent}- ")),
                }
                After::Item
            }
            "blockquote" => {
                self.ensure_blank_line();
                self.emit("> ");
                After::BlankLine
            }
            "a" => {
                self.frames.push(Frame::new("a", element.attribute("href")));
                After::Frame("a".to_string())
            }
            "strong" | "b" | "em" | "i" => {
                self.frames.push(Frame::new(tag, ""));
                After::Frame(tag.to_string())
            }
            "code" if self.pre_depth > 0 => After::Nothing,
            "code" => {
                self.frames.push(Frame::new("code", ""));
                After::Frame("code".to_string())
            }
            "pre" => {
                self.pre_depth += 1;
                self.frames.push(Frame::new("pre", ""));
                After::Pre
            }
            "tr" => {
                self.ensure_newline();
                self.emit("| ");
                After::Row
            }
            "td" | "th" => After::Cell,
            _ if BLOCKS.contains(&tag) => {
                self.ensure_blank_line();
                After::BlankLine
            }
            _ => After::Nothing,
        })
    }

    fn after(&mut self, after: After) {
        match after {
            After::Nothing => {}
            After::BlankLine => self.ensure_blank_line(),
            After::List => {
                self.lists.pop();
                if self.lists.is_empty() { self.ensure_blank_line() } else { self.ensure_newline() }
            }
            After::Item | After::Row => self.ensure_newline(),
            After::Frame(tag) => self.close_frame(&tag),
            After::Pre => {
                self.pre_depth -= 1;
                self.close_frame("pre");
            }
            After::Cell => {
                self.trim_trailing_spaces();
                self.emit(" | ");
            }
        }
    }

    fn finish(mut self, title: &str) -> String {
        while self.frames.len() > 1 {
            let inner = self.frames.pop().expect("checked above");
            self.emit(&inner.text);
        }
        // Tidy: trailing spaces (except Markdown line breaks), runs of blank lines.
        let lines: Vec<String> = self.frames[0]
            .text
            .split('\n')
            .map(|line| {
                let stripped = line.trim_end();
                if line.ends_with("  ") && !stripped.is_empty() { format!("{stripped}  ") } else { stripped.to_string() }
            })
            .collect();
        let mut body = String::new();
        let mut blank_run = 0;
        for (index, line) in lines.iter().enumerate() {
            blank_run = if line.is_empty() { blank_run + 1 } else { 0 };
            if blank_run >= 2 {
                continue;
            }
            if index > 0 {
                body.push('\n');
            }
            body.push_str(line);
        }
        let body = body.trim();
        let title = title.trim();
        if !title.is_empty() && !body.starts_with("# ") { format!("{title}\n\n{body}") } else { body.to_string() }
    }
}
