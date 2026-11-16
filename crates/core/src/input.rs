//! Synthesised mouse and keyboard input: the event sequences behind Click, Type, Scroll, Move and Shortcut. The
//! platform posts each event and maps key names to its key codes.
use crate::mcp::Desktop;
use rmcp::schemars;
use std::time::Duration;

// A mouse button. (A plain comment: a doc comment would reach the schema the model reads.)
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum Button {
    #[default]
    Left,
    Right,
    Middle,
}

impl Button {
    pub fn name(self) -> &'static str {
        match self {
            Button::Left => "left",
            Button::Right => "right",
            Button::Middle => "middle",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Modifier {
    Command,
    Shift,
    Option,
    Control,
    Fn,
}

/// One input event. Coordinates are the platform's input points (top-left origin of the main display).
#[derive(Clone, Debug, PartialEq)]
pub enum Event {
    Move { x: f64, y: f64 },
    /// `click` is the click state: 2 for the second press of a double click.
    Down { x: f64, y: f64, button: Button, click: i64 },
    Up { x: f64, y: f64, button: Button, click: i64 },
    /// The pointer moved with the left button held.
    Drag { x: f64, y: f64 },
    /// Wheel notches, in lines; positive is up or left.
    Wheel { vertical: i32, horizontal: i32 },
    /// `key` is one of [`KEYS`]. The modifiers are set explicitly: none means none, whatever is physically held.
    KeyDown { key: String, modifiers: Vec<Modifier> },
    KeyUp { key: String, modifiers: Vec<Modifier> },
    /// Releases every modifier key, left and right.
    ReleaseModifiers,
    /// A character typed as its Unicode text, independent of the keyboard layout.
    Text(String),
}

/// The key names Shortcut accepts; the platform maps each to a key code.
pub const KEYS: &[&str] = &[
    "a", "s", "d", "f", "h", "g", "z", "x", "c", "v", "b", "q", "w", "e", "r", "y", "t", "1", "2", "3", "4", "6", "5", "=", "9", "7", "-", "8", "0",
    "]", "o", "u", "[", "i", "p", "l", "j", "'", "k", ";", "\\", ",", "/", "n", "m", ".", "`", "return", "enter", "tab", "space", "delete", "backspace",
    "forwarddelete", "escape", "esc", "command", "cmd", "shift", "capslock", "option", "alt", "control", "ctrl", "fn", "left", "right", "down", "up",
    "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12", "home", "end", "pageup", "pagedown", "volumeup", "volumedown", "mute",
];

fn modifier(name: &str) -> Option<Modifier> {
    Some(match name {
        "command" | "cmd" => Modifier::Command,
        "shift" => Modifier::Shift,
        "option" | "alt" => Modifier::Option,
        "control" | "ctrl" => Modifier::Control,
        "fn" => Modifier::Fn,
        _ => return None,
    })
}

/// Refuses before any event when Accessibility is missing: posted events would go nowhere.
pub fn require_accessibility(desktop: &dyn Desktop) -> Result<(), String> {
    if desktop.accessibility_trusted() { Ok(()) } else { Err(crate::permissions::ACCESSIBILITY_MESSAGE.to_string()) }
}

/// Posts `event` when dropped: the release that must follow a press even when the call is abandoned.
struct Release<'a> {
    desktop: &'a dyn Desktop,
    event: Option<Event>,
}

impl Release<'_> {
    fn now(mut self) {
        if let Some(event) = self.event.take() {
            self.desktop.post(event);
        }
    }
}

impl Drop for Release<'_> {
    fn drop(&mut self) {
        if let Some(event) = self.event.take() {
            self.desktop.post(event);
        }
    }
}

async fn pause(desktop: &dyn Desktop, millis: u64) {
    desktop.sleep(Duration::from_millis(millis)).await;
}

pub fn move_to(desktop: &dyn Desktop, x: i64, y: i64) {
    desktop.post(Event::Move { x: x as f64, y: y as f64 });
}

/// Clicks `count` times (at least once) at a point. Each press carries its click state, so a count of 2 is a real
/// double click.
pub async fn click(desktop: &dyn Desktop, x: i64, y: i64, button: Button, count: i64) {
    let (px, py) = (x as f64, y as f64);
    move_to(desktop, x, y);
    for click in 1..=count.max(1) {
        desktop.post(Event::Down { x: px, y: py, button, click });
        let release = Release { desktop, event: Some(Event::Up { x: px, y: py, button, click }) };
        pause(desktop, 50).await;
        release.now();
        if click < count {
            pause(desktop, 30).await;
        }
    }
}

/// Drags with the left button from the pointer's position to the point, in 20 steps over half a second.
pub async fn drag(desktop: &dyn Desktop, x: i64, y: i64) {
    let (sx, sy) = desktop.cursor_position();
    let (sx, sy) = (sx.trunc(), sy.trunc());
    let (ex, ey) = (x as f64, y as f64);
    desktop.post(Event::Down { x: sx, y: sy, button: Button::Left, click: 1 });
    let _release = Release { desktop, event: Some(Event::Up { x: ex, y: ey, button: Button::Left, click: 1 }) };
    pause(desktop, 50).await;
    const STEPS: u32 = 20;
    for step in 1..=STEPS {
        let progress = f64::from(step) / f64::from(STEPS);
        desktop.post(Event::Drag { x: sx + (ex - sx) * progress, y: sy + (ey - sy) * progress });
        pause(desktop, 25).await;
    }
}

/// Scrolls `times` notches; `Some` explains a direction that does not fit the type.
pub async fn scroll(desktop: &dyn Desktop, at: Option<(i64, i64)>, horizontal: bool, direction: &str, times: i64) -> Option<String> {
    if !horizontal && !matches!(direction, "up" | "down") {
        return Some("Use direction 'up' or 'down' for vertical scroll.".to_string());
    }
    if horizontal && !matches!(direction, "left" | "right") {
        return Some("Use direction 'left' or 'right' for horizontal scroll.".to_string());
    }
    if let Some((x, y)) = at {
        move_to(desktop, x, y);
        pause(desktop, 50).await;
    }
    let (vertical, horizontal) = match direction {
        "up" => (3, 0),
        "down" => (-3, 0),
        "left" => (0, 3),
        _ => (0, -3),
    };
    for _ in 0..times.max(0) {
        desktop.post(Event::Wheel { vertical, horizontal });
        pause(desktop, 50).await;
    }
    None
}

async fn key_press(desktop: &dyn Desktop, key: &str, modifiers: &[Modifier], hold: u64) {
    desktop.post(Event::KeyDown { key: key.to_string(), modifiers: modifiers.to_vec() });
    let release = Release { desktop, event: Some(Event::KeyUp { key: key.to_string(), modifiers: modifiers.to_vec() }) };
    pause(desktop, hold).await;
    release.now();
}

/// Presses a combination such as `command+shift+t`. Malformed or unknown names are refused before any key is
/// pressed.
pub async fn shortcut(desktop: &dyn Desktop, shortcut: &str) -> Result<(), String> {
    let keys: Vec<String> = shortcut.split('+').map(|key| key.trim().to_lowercase()).collect();
    if keys.iter().any(String::is_empty) {
        return Err("shortcut must contain non-empty key names separated by '+'".to_string());
    }
    hot_key(desktop, &keys).await
}

async fn hot_key(desktop: &dyn Desktop, keys: &[String]) -> Result<(), String> {
    let mut modifiers = Vec::new();
    let mut main_key = None;
    for key in keys {
        if let Some(modifier) = modifier(key) {
            modifiers.push(modifier);
        } else if KEYS.contains(&key.as_str()) {
            main_key = Some(key.as_str());
        } else {
            return Err(format!("Unknown key: {key}"));
        }
    }
    match main_key {
        Some(key) => key_press(desktop, key, &modifiers, 50).await,
        None => {
            // Modifiers alone: pressed in order, released in reverse.
            let mut releases = Vec::new();
            for key in keys {
                desktop.post(Event::KeyDown { key: key.clone(), modifiers: vec![] });
                releases.push(Release { desktop, event: Some(Event::KeyUp { key: key.clone(), modifiers: vec![] }) });
            }
            pause(desktop, 50).await;
            while let Some(release) = releases.pop() {
                release.now();
            }
        }
    }
    Ok(())
}

async fn chord(desktop: &dyn Desktop, keys: &[&str]) {
    let keys: Vec<String> = keys.iter().map(|key| key.to_string()).collect();
    hot_key(desktop, &keys).await.expect("known keys");
}

/// Types text as Unicode events, independent of the keyboard layout; an input method that would intercept key
/// events is swapped for a plain layout while typing. Line breaks and tabs are their keys.
pub async fn type_text(desktop: &dyn Desktop, text: &str) {
    if text.is_empty() {
        return;
    }
    desktop.post(Event::ReleaseModifiers);
    pause(desktop, 20).await;
    let restore = desktop.select_typing_layout();
    if restore.is_some() {
        // Give the text system a moment to see the switch before events arrive.
        pause(desktop, 50).await;
    }
    let restore = Restore(restore);
    let mut characters = text.chars().peekable();
    while let Some(character) = characters.next() {
        match character {
            '\r' | '\n' => {
                if character == '\r' && characters.peek() == Some(&'\n') {
                    characters.next();
                }
                key_press(desktop, "return", &[], 10).await;
            }
            '\t' => key_press(desktop, "tab", &[], 10).await,
            _ => {
                desktop.post(Event::Text(character.to_string()));
                pause(desktop, 10).await;
            }
        }
    }
    if restore.0.is_some() {
        pause(desktop, 50).await;
    }
}

/// Puts the keyboard layout back, also when typing is abandoned.
struct Restore(Option<Box<dyn FnOnce()>>);

impl Drop for Restore {
    fn drop(&mut self) {
        if let Some(restore) = self.0.take() {
            restore();
        }
    }
}

/// Clicks at the point, then types. Returns whether the text went into a password field, checked after the click
/// (which moves focus) and before the first key.
pub async fn type_at(desktop: &dyn Desktop, x: i64, y: i64, text: &str, caret: Caret, clear: bool, press_enter: bool) -> bool {
    click(desktop, x, y, Button::Left, 1).await;
    pause(desktop, 100).await;
    let secure = desktop.secure_input_at(x, y).await;
    if clear {
        chord(desktop, &["command", "a"]).await;
        pause(desktop, 50).await;
        chord(desktop, &["delete"]).await;
        pause(desktop, 50).await;
    }
    match caret {
        Caret::Start => {
            chord(desktop, &["command", "left"]).await;
            pause(desktop, 20).await;
        }
        Caret::End => {
            chord(desktop, &["command", "right"]).await;
            pause(desktop, 20).await;
        }
        Caret::Idle => {}
    }
    type_text(desktop, text).await;
    if press_enter {
        key_press(desktop, "return", &[], 50).await;
    }
    secure
}

// Where the caret goes before typing.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum Caret {
    Start,
    #[default]
    Idle,
    End,
}
