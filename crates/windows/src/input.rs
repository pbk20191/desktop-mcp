//! The core's input events as `SendInput` structures: absolute pointer positions over the virtual screen, buttons,
//! wheel notches, keys with their modifiers, and text as Unicode key events (independent of the keyboard layout).
use desktop_mcp_core::input::{Button, EditingKeys, Event, Modifier};
use windows::Win32::UI::Input::KeyboardAndMouse::*;

/// Key names Windows accepts besides the core's.
pub const EXTRA_KEYS: &[&str] = &[
    "win", "windows", "super", "meta", "insert", "printscreen", "scrolllock", "pause", "numlock", "menu", "apps", "f13", "f14", "f15", "f16", "f17", "f18", "f19", "f20", "f21", "f22",
    "f23", "f24", "browserback", "browserforward", "playpause", "nexttrack", "prevtrack",
];

/// Windows's editing chords: ctrl+a, and ctrl+home / ctrl+end for the start and end of the field.
pub const EDITING: EditingKeys = EditingKeys { select_all: &["ctrl", "a"], line_start: &["ctrl", "home"], line_end: &["ctrl", "end"] };

const WHEEL_NOTCH: i32 = 120;

/// The virtual key for a core or Windows key name.
pub fn virtual_key(name: &str) -> Option<VIRTUAL_KEY> {
    let key = match name {
        letter if letter.len() == 1 && letter.as_bytes()[0].is_ascii_alphanumeric() => VIRTUAL_KEY(letter.as_bytes()[0].to_ascii_uppercase() as u16),
        "=" => VK_OEM_PLUS,
        "-" => VK_OEM_MINUS,
        "]" => VK_OEM_6,
        "[" => VK_OEM_4,
        "'" => VK_OEM_7,
        ";" => VK_OEM_1,
        "\\" => VK_OEM_5,
        "," => VK_OEM_COMMA,
        "/" => VK_OEM_2,
        "." => VK_OEM_PERIOD,
        "`" => VK_OEM_3,
        "return" | "enter" => VK_RETURN,
        "tab" => VK_TAB,
        "space" => VK_SPACE,
        "backspace" => VK_BACK,
        "delete" | "forwarddelete" => VK_DELETE,
        "escape" | "esc" => VK_ESCAPE,
        "command" | "cmd" | "control" | "ctrl" => VK_CONTROL,
        "shift" => VK_SHIFT,
        "capslock" => VK_CAPITAL,
        "option" | "alt" => VK_MENU,
        "left" => VK_LEFT,
        "right" => VK_RIGHT,
        "up" => VK_UP,
        "down" => VK_DOWN,
        "home" => VK_HOME,
        "end" => VK_END,
        "pageup" => VK_PRIOR,
        "pagedown" => VK_NEXT,
        "volumeup" => VK_VOLUME_UP,
        "volumedown" => VK_VOLUME_DOWN,
        "mute" => VK_VOLUME_MUTE,
        "win" | "windows" | "super" | "meta" => VK_LWIN,
        "insert" => VK_INSERT,
        "printscreen" => VK_SNAPSHOT,
        "scrolllock" => VK_SCROLL,
        "pause" => VK_PAUSE,
        "numlock" => VK_NUMLOCK,
        "menu" | "apps" => VK_APPS,
        "browserback" => VK_BROWSER_BACK,
        "browserforward" => VK_BROWSER_FORWARD,
        "playpause" => VK_MEDIA_PLAY_PAUSE,
        "nexttrack" => VK_MEDIA_NEXT_TRACK,
        "prevtrack" => VK_MEDIA_PREV_TRACK,
        function if function.starts_with('f') => {
            let number: u16 = function[1..].parse().ok().filter(|number| (1..=24).contains(number))?;
            VIRTUAL_KEY(VK_F1.0 + number - 1)
        }
        _ => return None,
    };
    Some(key)
}

fn modifier_key(modifier: Modifier) -> Option<VIRTUAL_KEY> {
    match modifier {
        Modifier::Command | Modifier::Control => Some(VK_CONTROL),
        Modifier::Shift => Some(VK_SHIFT),
        Modifier::Option => Some(VK_MENU),
        Modifier::Meta => Some(VK_LWIN),
        Modifier::Fn => None,
    }
}

/// Keys whose scan codes carry the E0 prefix: the extended keypad, the right-hand ctrl and alt, and the media keys.
fn extended(key: VIRTUAL_KEY) -> bool {
    [
        VK_LEFT, VK_RIGHT, VK_UP, VK_DOWN, VK_HOME, VK_END, VK_PRIOR, VK_NEXT, VK_INSERT, VK_DELETE, VK_LWIN, VK_RWIN, VK_APPS, VK_SNAPSHOT, VK_NUMLOCK, VK_RCONTROL, VK_RMENU,
        VK_VOLUME_UP, VK_VOLUME_DOWN, VK_VOLUME_MUTE, VK_MEDIA_PLAY_PAUSE, VK_MEDIA_NEXT_TRACK, VK_MEDIA_PREV_TRACK, VK_BROWSER_BACK, VK_BROWSER_FORWARD,
    ]
    .contains(&key)
}

fn key(key: VIRTUAL_KEY, up: bool) -> INPUT {
    let mut flags = if up { KEYEVENTF_KEYUP } else { KEYBD_EVENT_FLAGS(0) };
    if extended(key) {
        flags |= KEYEVENTF_EXTENDEDKEY;
    }
    let scan = unsafe { MapVirtualKeyW(key.0 as u32, MAPVK_VK_TO_VSC) } as u16;
    INPUT { r#type: INPUT_KEYBOARD, Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: key, wScan: scan, dwFlags: flags, time: 0, dwExtraInfo: 0 } } }
}

fn unicode(unit: u16, up: bool) -> INPUT {
    let flags = if up { KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } else { KEYEVENTF_UNICODE };
    INPUT { r#type: INPUT_KEYBOARD, Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: VIRTUAL_KEY(0), wScan: unit, dwFlags: flags, time: 0, dwExtraInfo: 0 } } }
}

fn mouse(dx: i32, dy: i32, data: i32, flags: MOUSE_EVENT_FLAGS) -> INPUT {
    INPUT { r#type: INPUT_MOUSE, Anonymous: INPUT_0 { mi: MOUSEINPUT { dx, dy, mouseData: data as u32, dwFlags: flags, time: 0, dwExtraInfo: 0 } } }
}

/// An absolute move to (x, y) in screen pixels, normalized over the virtual screen `(x, y, width, height)`.
fn absolute(x: f64, y: f64, screen: (i32, i32, i32, i32), extra: MOUSE_EVENT_FLAGS) -> INPUT {
    let scale = |value: f64, origin: i32, size: i32| (((value - origin as f64) * 65535.0) / (size.max(2) - 1) as f64).round() as i32;
    mouse(scale(x, screen.0, screen.2), scale(y, screen.1, screen.3), 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | extra)
}

fn button_flags(button: Button, up: bool) -> MOUSE_EVENT_FLAGS {
    match (button, up) {
        (Button::Left, false) => MOUSEEVENTF_LEFTDOWN,
        (Button::Left, true) => MOUSEEVENTF_LEFTUP,
        (Button::Right, false) => MOUSEEVENTF_RIGHTDOWN,
        (Button::Right, true) => MOUSEEVENTF_RIGHTUP,
        (Button::Middle, false) => MOUSEEVENTF_MIDDLEDOWN,
        (Button::Middle, true) => MOUSEEVENTF_MIDDLEUP,
    }
}

/// The `INPUT`s for one event; `screen` is the virtual screen (x, y, width, height) in physical pixels.
pub fn inputs(event: &Event, screen: (i32, i32, i32, i32)) -> Vec<INPUT> {
    match event {
        Event::Move { x, y } | Event::Drag { x, y } => vec![absolute(*x, *y, screen, MOUSE_EVENT_FLAGS(0))],
        Event::Down { x, y, button, .. } => vec![absolute(*x, *y, screen, button_flags(*button, false))],
        Event::Up { x, y, button, .. } => vec![absolute(*x, *y, screen, button_flags(*button, true))],
        Event::Wheel { vertical, horizontal } => {
            let mut built = Vec::new();
            if *vertical != 0 {
                built.push(mouse(0, 0, vertical * WHEEL_NOTCH, MOUSEEVENTF_WHEEL));
            }
            if *horizontal != 0 {
                // The core's positive is left; Windows's horizontal wheel counts right as positive.
                built.push(mouse(0, 0, -horizontal * WHEEL_NOTCH, MOUSEEVENTF_HWHEEL));
            }
            built
        }
        Event::KeyDown { key: name, modifiers } => {
            let mut built: Vec<INPUT> = modifiers.iter().filter_map(|modifier| modifier_key(*modifier)).map(|modifier| key(modifier, false)).collect();
            built.extend(virtual_key(name).map(|main| key(main, false)));
            built
        }
        Event::KeyUp { key: name, modifiers } => {
            let mut built: Vec<INPUT> = virtual_key(name).map(|main| key(main, true)).into_iter().collect();
            built.extend(modifiers.iter().rev().filter_map(|modifier| modifier_key(*modifier)).map(|modifier| key(modifier, true)));
            built
        }
        Event::ReleaseModifiers => release_modifiers(|modifier| unsafe { GetAsyncKeyState(modifier.0 as i32) } as u16 & 0x8000 != 0),
        Event::Text(text) => text.encode_utf16().flat_map(|unit| [unicode(unit, false), unicode(unit, true)]).collect(),
    }
}

/// An unassigned virtual key, pressed before an Alt key-up so the release is not taken as an Alt tap (AutoHotkey's
/// MenuMaskKey).
const MASK_KEY: VIRTUAL_KEY = VIRTUAL_KEY(0xE8);

/// Key-ups for the modifiers `held` reports down, and nothing for the others: a key-up for an Alt that is not down
/// is a lone Alt tap, which puts the focused window in menu or access-key mode and turns the typed letters into
/// commands. An Alt that is down is released behind the mask key for the same reason.
pub fn release_modifiers(held: impl Fn(VIRTUAL_KEY) -> bool) -> Vec<INPUT> {
    let mut built = Vec::new();
    for modifier in [VK_LCONTROL, VK_RCONTROL, VK_LSHIFT, VK_RSHIFT, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN].into_iter().filter(|modifier| held(*modifier)) {
        if modifier == VK_LMENU || modifier == VK_RMENU {
            built.extend([key(MASK_KEY, false), key(MASK_KEY, true)]);
        }
        built.push(key(modifier, true));
    }
    built
}

/// The virtual screen: every display's bounding box, in physical pixels.
pub fn virtual_screen() -> (i32, i32, i32, i32) {
    use windows::Win32::UI::WindowsAndMessaging::{GetSystemMetrics, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN};
    unsafe { (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN), GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN)) }
}

/// Sends one event; false when the system took fewer events than were sent (UIPI, a secure desktop).
pub fn send(event: &Event) -> bool {
    let built = inputs(event, virtual_screen());
    built.is_empty() || unsafe { SendInput(&built, size_of::<INPUT>() as i32) } as usize == built.len()
}
