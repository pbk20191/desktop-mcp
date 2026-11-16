//! Quartz events for the input tools, the key-code table, and the keyboard layout switch while typing.
use desktop_mcp_core::input::{Button, Event, Modifier};
use objc2_core_foundation::{CFBoolean, CFRetained, CFString, CGPoint};
use objc2_core_graphics::{CGEvent, CGEventField, CGEventFlags, CGEventTapLocation, CGEventType, CGMouseButton, CGScrollEventUnit};
use std::ffi::c_void;
use std::ptr::NonNull;

/// The virtual key code (ANSI layout) of a key name from `desktop_mcp_core::input::KEYS`.
pub fn key_code(name: &str) -> Option<u16> {
    Some(match name {
        "a" => 0, "s" => 1, "d" => 2, "f" => 3, "h" => 4, "g" => 5, "z" => 6, "x" => 7, "c" => 8, "v" => 9,
        "b" => 11, "q" => 12, "w" => 13, "e" => 14, "r" => 15, "y" => 16, "t" => 17,
        "1" => 18, "2" => 19, "3" => 20, "4" => 21, "6" => 22, "5" => 23, "=" => 24, "9" => 25, "7" => 26,
        "-" => 27, "8" => 28, "0" => 29, "]" => 30, "o" => 31, "u" => 32, "[" => 33, "i" => 34, "p" => 35,
        "l" => 37, "j" => 38, "'" => 39, "k" => 40, ";" => 41, "\\" => 42, "," => 43, "/" => 44,
        "n" => 45, "m" => 46, "." => 47, "`" => 50,
        "return" | "enter" => 36, "tab" => 48, "space" => 49,
        "delete" | "backspace" => 51, "forwarddelete" => 117,
        "escape" | "esc" => 53,
        "command" | "cmd" => 55, "shift" => 56, "capslock" => 57, "option" | "alt" => 58,
        "control" | "ctrl" => 59, "fn" => 63,
        "left" => 123, "right" => 124, "down" => 125, "up" => 126,
        "f1" => 122, "f2" => 120, "f3" => 99, "f4" => 118, "f5" => 96, "f6" => 97,
        "f7" => 98, "f8" => 100, "f9" => 101, "f10" => 109, "f11" => 103, "f12" => 111,
        "home" => 115, "end" => 119, "pageup" => 116, "pagedown" => 121,
        "volumeup" => 72, "volumedown" => 73, "mute" => 74,
        _ => return None,
    })
}

/// Left and right command, shift, option and control.
const MODIFIER_KEYS: [u16; 8] = [55, 56, 58, 59, 54, 60, 61, 62];

fn flags(modifiers: &[Modifier]) -> CGEventFlags {
    modifiers.iter().fold(CGEventFlags::empty(), |flags, modifier| {
        flags
            | match modifier {
                Modifier::Command => CGEventFlags::MaskCommand,
                Modifier::Shift => CGEventFlags::MaskShift,
                Modifier::Option => CGEventFlags::MaskAlternate,
                Modifier::Control => CGEventFlags::MaskControl,
                Modifier::Fn => CGEventFlags::MaskSecondaryFn,
            }
    })
}

fn mouse(kind: CGEventType, x: f64, y: f64, button: CGMouseButton) -> Option<CFRetained<CGEvent>> {
    CGEvent::new_mouse_event(None, kind, CGPoint { x, y }, button)
}

fn key(code: u16, down: bool, modifiers: &[Modifier]) -> Option<CFRetained<CGEvent>> {
    let event = CGEvent::new_keyboard_event(None, code, down)?;
    // Set explicitly: an event without a source inherits whatever modifiers are physically held.
    CGEvent::set_flags(Some(&event), flags(modifiers));
    Some(event)
}

fn unicode(text: &str, down: bool) -> Option<CFRetained<CGEvent>> {
    let units: Vec<u16> = text.encode_utf16().collect();
    let event = CGEvent::new_keyboard_event(None, 0, down)?;
    CGEvent::set_flags(Some(&event), CGEventFlags::empty());
    unsafe { CGEvent::keyboard_set_unicode_string(Some(&event), units.len() as _, units.as_ptr()) };
    Some(event)
}

fn buttons(button: Button) -> (CGMouseButton, CGEventType, CGEventType) {
    match button {
        Button::Left => (CGMouseButton::Left, CGEventType::LeftMouseDown, CGEventType::LeftMouseUp),
        Button::Right => (CGMouseButton::Right, CGEventType::RightMouseDown, CGEventType::RightMouseUp),
        Button::Middle => (CGMouseButton::Center, CGEventType::OtherMouseDown, CGEventType::OtherMouseUp),
    }
}

/// The Quartz events for one input event.
pub fn events(event: &Event) -> Vec<CFRetained<CGEvent>> {
    let pressed = |kind_of: fn(&(CGMouseButton, CGEventType, CGEventType)) -> CGEventType, x: f64, y: f64, button: Button, click: i64| {
        let kinds = buttons(button);
        let event = mouse(kind_of(&kinds), x, y, kinds.0);
        if let Some(event) = &event {
            CGEvent::set_integer_value_field(Some(event), CGEventField::MouseEventClickState, click);
        }
        event
    };
    let made = match event {
        Event::Move { x, y } => vec![mouse(CGEventType::MouseMoved, *x, *y, CGMouseButton::Left)],
        Event::Down { x, y, button, click } => vec![pressed(|k| k.1, *x, *y, *button, *click)],
        Event::Up { x, y, button, click } => vec![pressed(|k| k.2, *x, *y, *button, *click)],
        Event::Drag { x, y } => vec![mouse(CGEventType::LeftMouseDragged, *x, *y, CGMouseButton::Left)],
        Event::Wheel { vertical, horizontal } => vec![CGEvent::new_scroll_wheel_event2(None, CGScrollEventUnit::Line, 2, *vertical, *horizontal, 0)],
        Event::KeyDown { key: name, modifiers } => vec![key_code(name).and_then(|code| key(code, true, modifiers))],
        Event::KeyUp { key: name, modifiers } => vec![key_code(name).and_then(|code| key(code, false, modifiers))],
        Event::ReleaseModifiers => MODIFIER_KEYS.iter().map(|code| key(*code, false, &[])).collect(),
        Event::Text(text) => vec![unicode(text, true), unicode(text, false)],
    };
    made.into_iter().flatten().collect()
}

pub fn post(event: &Event) {
    for event in events(event) {
        CGEvent::post(CGEventTapLocation::HIDEventTap, Some(&event));
    }
}

/// Where the pointer is, in input points.
pub fn cursor_position() -> (f64, f64) {
    let point = CGEvent::new(None).map(|event| CGEvent::location(Some(&event))).unwrap_or(CGPoint { x: 0.0, y: 0.0 });
    (point.x, point.y)
}

// Text Input Sources (HIToolbox, in Carbon), which has no binding crate.
#[link(name = "Carbon", kind = "framework")]
extern "C" {
    fn TISCopyCurrentKeyboardInputSource() -> *mut c_void;
    fn TISCopyCurrentASCIICapableKeyboardLayoutInputSource() -> *mut c_void;
    fn TISSelectInputSource(source: *mut c_void) -> i32;
    fn TISGetInputSourceProperty(source: *mut c_void, key: &CFString) -> *const c_void;
    static kTISPropertyInputSourceIsASCIICapable: &'static CFString;
    static kTISPropertyInputSourceType: &'static CFString;
    static kTISTypeKeyboardLayout: &'static CFString;
}

/// An input source reference, owned (a CF object: released when dropped).
struct Source(CFRetained<objc2_core_foundation::CFType>);

impl Source {
    /// Takes ownership of a reference returned by a TIS "Copy" function.
    fn copied(raw: *mut c_void) -> Option<Source> {
        NonNull::new(raw).map(|raw| Source(unsafe { CFRetained::from_raw(raw.cast()) }))
    }

    fn as_ptr(&self) -> *mut c_void {
        CFRetained::as_ptr(&self.0).as_ptr().cast()
    }
}

/// Whether events pass straight through `source`: an ASCII-capable plain keyboard layout. An input method (Korean,
/// Japanese, Chinese) can report ASCII-capable while still composing key events.
fn passes_unicode_through(source: &Source) -> bool {
    let capable = unsafe { TISGetInputSourceProperty(source.as_ptr(), kTISPropertyInputSourceIsASCIICapable) };
    if capable.is_null() || !unsafe { &*(capable as *const CFBoolean) }.value() {
        return false;
    }
    let kind = unsafe { TISGetInputSourceProperty(source.as_ptr(), kTISPropertyInputSourceType) };
    kind.is_null() || unsafe { &*(kind as *const CFString) } == unsafe { kTISTypeKeyboardLayout }
}

/// Selects an ASCII-capable keyboard layout when the current input source would compose key events; returns what
/// selects the previous one again. Text Input Source calls assert the main thread: call it there.
pub fn select_typing_layout() -> Option<Box<dyn FnOnce()>> {
    let current = Source::copied(unsafe { TISCopyCurrentKeyboardInputSource() })?;
    if passes_unicode_through(&current) {
        return None;
    }
    let ascii = Source::copied(unsafe { TISCopyCurrentASCIICapableKeyboardLayoutInputSource() })?;
    if unsafe { TISSelectInputSource(ascii.as_ptr()) } != 0 {
        return None;
    }
    Some(Box::new(move || {
        unsafe { TISSelectInputSource(current.as_ptr()) };
    }))
}
