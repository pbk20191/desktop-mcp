//! The Menu tool: read and choose commands from an app's menu bar. Path matching, listing and selecting work on
//! [`MenuNode`], so they are tested with an in-memory tree; the platform supplies the real items.
use crate::mcp::Desktop;
use rmcp::schemars;

pub const MAX_DEPTH: i64 = 4;

/// One item of an app's menu bar: a bar item or a menu item.
pub trait MenuNode: Sized {
    fn title(&self) -> String;
    fn enabled(&self) -> bool;
    /// The keyboard equivalent as the menu shows it (e.g. "⇧⌘E").
    fn shortcut(&self) -> Option<String>;
    fn is_separator(&self) -> bool;
    /// The items of this item's menu, separators included; empty for a leaf.
    fn children(&self) -> Vec<Self>;
    fn press(&self) -> bool;
    /// Whether `children` pressed this bar item to populate its menu and items appeared, so the menu is open.
    fn opened_menu(&self) -> bool;
    /// Closes the menu this item opened.
    fn cancel(&self);
}

/// A keyboard equivalent as a menu shows it: modifiers in the order 🌐⌃⌥⇧⌘. Modifier bits: 1 shift, 2 option,
/// 4 control, 8 no command, 16 function.
pub fn shortcut_label(character: Option<&str>, modifiers: Option<i64>, virtual_key: Option<i64>) -> String {
    let named_key = |code: i64| {
        Some(match code {
            36 => "\u{21a9}", 48 => "\u{21e5}", 49 => "Space", 51 => "\u{232b}", 53 => "\u{238b}", 76 => "\u{2324}",
            115 => "\u{2196}", 116 => "\u{21de}", 117 => "\u{2326}", 119 => "\u{2198}", 121 => "\u{21df}",
            123 => "\u{2190}", 124 => "\u{2192}", 125 => "\u{2193}", 126 => "\u{2191}",
            122 => "F1", 120 => "F2", 99 => "F3", 118 => "F4", 96 => "F5", 97 => "F6",
            98 => "F7", 100 => "F8", 101 => "F9", 109 => "F10", 103 => "F11", 111 => "F12",
            _ => return None,
        })
    };
    let control = |text: &str| {
        Some(match text {
            "\u{08}" => "\u{232b}", "\u{1b}" => "\u{238b}", "\r" => "\u{21a9}", "\t" => "\u{21e5}", "\u{03}" => "\u{2324}",
            _ => return None,
        })
    };
    let mut key = virtual_key.and_then(named_key).map(str::to_string);
    if key.is_none() {
        if let Some(text) = character.filter(|text| !text.is_empty()) {
            if let Some(mapped) = control(text) {
                key = Some(mapped.to_string());
            } else if !text.chars().any(char::is_control) && !text.trim().is_empty() {
                key = Some(text.to_string());
            }
        }
    }
    let Some(key) = key.filter(|key| !key.is_empty()) else { return String::new() };
    let mask = modifiers.unwrap_or(0);
    let mut label = String::new();
    for (bit, symbol) in [(16, "\u{1f310}"), (4, "\u{2303}"), (2, "\u{2325}"), (1, "\u{21e7}")] {
        if mask & bit != 0 {
            label.push_str(symbol);
        }
    }
    if mask & 8 == 0 {
        label.push('\u{2318}');
    }
    label + &if key.chars().count() == 1 { key.to_uppercase() } else { key }
}

/// Lowercased, `…` as `...`, trailing whitespace dropped.
fn normalized(text: &str) -> String {
    text.to_lowercase().replace('…', "...").trim_end().to_string()
}

fn describe<N: MenuNode>(chain: &[N]) -> String {
    chain.iter().map(MenuNode::title).collect::<Vec<_>>().join(" > ")
}

/// The item `element` names among `siblings`: exact title, then ignoring case, then normalized; more than one
/// match at the first rule that matches is an error.
fn matching<N: MenuNode>(element: &str, siblings: Vec<N>, parent: &str) -> Result<N, String> {
    let mut items: Vec<N> = siblings.into_iter().filter(|item| !item.is_separator()).collect();
    let rules: [&dyn Fn(&str) -> bool; 3] = [&|title| title == element, &|title| title.to_lowercase() == element.to_lowercase(), &|title| normalized(title) == normalized(element)];
    for rule in rules {
        let found: Vec<usize> = items.iter().enumerate().filter(|(_, item)| rule(&item.title())).map(|(i, _)| i).collect();
        match found.len() {
            0 => continue,
            1 => return Ok(items.swap_remove(found[0])),
            _ => return Err(format!("Menu item '{element}' is ambiguous: {}", found.iter().map(|&i| items[i].title()).collect::<Vec<_>>().join(", "))),
        }
    }
    let available = if items.is_empty() { "; it has no items.".to_string() } else { format!(". Available: {}", items.iter().map(MenuNode::title).collect::<Vec<_>>().join(", ")) };
    Err(format!("No menu item '{element}' under {parent}{available}"))
}

/// The items `path` names, from the bar item down. The Apple menu (root 0) is also "Apple".
pub fn resolve<N: MenuNode>(path: &[String], roots: Vec<N>) -> Result<Vec<N>, String> {
    let mut chain = Vec::new();
    resolve_into(path, roots, &mut chain)?;
    Ok(chain)
}

/// `resolve`, appending to `chain` as it goes so a caller sees how far it got on an error.
fn resolve_into<N: MenuNode>(path: &[String], mut roots: Vec<N>, chain: &mut Vec<N>) -> Result<(), String> {
    for element in path {
        let node = match chain.last() {
            Some(parent) => matching(element, parent.children(), &describe(chain))?,
            None if normalized(element) == "apple" && !roots.is_empty() => roots.swap_remove(0),
            None => matching(element, std::mem::take(&mut roots), "the menu bar")?,
        };
        chain.push(node);
    }
    Ok(())
}

/// Presses the item `path` names, after checking every item on the way is enabled and the last one is a command
/// rather than a submenu. On an error, a bar item menu this call opened to populate it is cancelled.
pub fn select<N: MenuNode>(path: &[String], roots: Vec<N>) -> Result<String, String> {
    let mut chain = Vec::new();
    let outcome = resolve_into(path, roots, &mut chain).and_then(|()| press(&chain));
    if outcome.is_err() {
        if let Some(bar) = chain.first().filter(|bar| bar.opened_menu()) {
            bar.cancel();
        }
    }
    outcome
}

fn press<N: MenuNode>(chain: &[N]) -> Result<String, String> {
    if let Some(end) = chain.iter().position(|item| !item.enabled()) {
        return Err(format!("Menu item {} is disabled.", describe(&chain[..=end])));
    }
    let item = chain.last().ok_or("path required for select mode.")?;
    if item.children().iter().any(|child| !child.is_separator()) {
        return Err(format!("Menu item {} opens a submenu; add the item to select.", describe(chain)));
    }
    if !item.press() {
        return Err(format!("Could not press menu item {}.", describe(chain)));
    }
    Ok(format!("Selected {}.", describe(chain)))
}

/// An indented tree of titles, `depth` levels deep (1 to 4), with shortcuts and `[disabled]`; separators left out.
pub fn list<N: MenuNode>(roots: Vec<N>, depth: i64) -> String {
    fn walk<N: MenuNode>(nodes: Vec<N>, level: i64, depth: i64, lines: &mut Vec<String>) {
        for node in nodes.into_iter().filter(|node| !node.is_separator()) {
            let mut line = "  ".repeat(level as usize) + &crate::window::flat(&node.title());
            if let Some(shortcut) = node.shortcut() {
                line += &format!("  {shortcut}");
            }
            if !node.enabled() {
                line += "  [disabled]";
            }
            lines.push(line);
            if level + 1 < depth {
                walk(node.children(), level + 1, depth, lines);
            }
        }
    }
    let mut lines = Vec::new();
    walk(roots, 0, depth.clamp(1, MAX_DEPTH), &mut lines);
    if lines.is_empty() { "No menu items.".to_string() } else { lines.join("\n") }
}

/// What the platform is asked to do with the menu bar.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Request {
    /// The tree below `path` (the whole bar when empty), `depth` levels deep.
    List { path: Vec<String>, depth: i64 },
    Select { path: Vec<String> },
}

// What the Menu tool does. (A plain comment: a doc comment would reach the schema the model reads.)
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum Mode {
    #[default]
    List,
    Select,
}

pub async fn run(desktop: &dyn Desktop, mode: Mode, app: Option<String>, path: Vec<String>, depth: i64) -> Result<String, String> {
    if mode == Mode::Select && path.is_empty() {
        return Err("path required for select mode.".to_string());
    }
    crate::input::require_accessibility(desktop)?;
    let request = match mode {
        Mode::List => Request::List { path, depth },
        Mode::Select => Request::Select { path },
    };
    desktop.menu(app, request).await
}
