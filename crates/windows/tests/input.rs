//! Input events as SendInput structures (built, never sent), and integrity levels.
#![cfg(windows)]
use desktop_mcp_core::input::{Button, Event, Modifier};
use desktop_mcp_windows::input::{inputs, virtual_key};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    INPUT, INPUT_KEYBOARD, INPUT_MOUSE, KEYEVENTF_KEYUP, KEYEVENTF_UNICODE, MOUSEEVENTF_ABSOLUTE, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_VIRTUALDESK, MOUSEEVENTF_WHEEL, VK_CONTROL, VK_F12,
    VK_LWIN, VK_MENU, VK_NEXT, VK_RETURN, VK_SNAPSHOT,
};

const SCREEN: (i32, i32, i32, i32) = (0, 0, 2560, 1600);

#[test]
fn keys_map_to_virtual_keys() {
    assert_eq!(virtual_key("ctrl"), Some(VK_CONTROL));
    assert_eq!(virtual_key("alt"), Some(VK_MENU));
    assert_eq!(virtual_key("win"), Some(VK_LWIN));
    assert_eq!(virtual_key("a").map(|key| key.0), Some(b'A' as u16));
    assert_eq!(virtual_key("f12"), Some(VK_F12));
    assert_eq!(virtual_key("enter"), Some(VK_RETURN));
    assert_eq!(virtual_key("pagedown"), Some(VK_NEXT));
    assert_eq!(virtual_key("printscreen"), Some(VK_SNAPSHOT));
    assert_eq!(virtual_key("nonsense"), None);
}

fn mouse(input: &INPUT) -> (i32, i32, u32, i32) {
    assert_eq!(input.r#type, INPUT_MOUSE);
    let mouse = unsafe { input.Anonymous.mi };
    (mouse.dx, mouse.dy, mouse.dwFlags.0, mouse.mouseData as i32)
}

#[test]
fn a_press_moves_there_then_presses_at_normalized_coordinates() {
    let built = inputs(&Event::Down { x: 1280.0, y: 800.0, button: Button::Left, click: 1 }, SCREEN);
    let (dx, dy, flags, _) = mouse(built.last().unwrap());
    assert!((dx - 32768).abs() <= 16 && (dy - 32768).abs() <= 24, "{dx},{dy}");
    assert!(flags & MOUSEEVENTF_ABSOLUTE.0 != 0 && flags & MOUSEEVENTF_VIRTUALDESK.0 != 0 && flags & MOUSEEVENTF_LEFTDOWN.0 != 0);
}

#[test]
fn unicode_text_becomes_surrogate_pairs() {
    let built = inputs(&Event::Text("가😀".into()), SCREEN);
    assert_eq!(built.len(), 6, "three UTF-16 units, each down and up");
    let units: Vec<u16> = built.iter().step_by(2).map(|input| unsafe { input.Anonymous.ki.wScan }).collect();
    assert_eq!(units, "가😀".encode_utf16().collect::<Vec<_>>());
    for (index, input) in built.iter().enumerate() {
        assert_eq!(input.r#type, INPUT_KEYBOARD);
        let flags = unsafe { input.Anonymous.ki.dwFlags };
        assert!(flags.contains(KEYEVENTF_UNICODE));
        assert_eq!(flags.contains(KEYEVENTF_KEYUP), index % 2 == 1);
    }
}

#[test]
fn wheel_signs_follow_the_core() {
    let (_, _, flags, data) = mouse(&inputs(&Event::Wheel { vertical: 2, horizontal: 0 }, SCREEN)[0]);
    assert!(flags & MOUSEEVENTF_WHEEL.0 != 0);
    assert_eq!(data, 240, "positive is up");
    let (_, _, _, data) = mouse(&inputs(&Event::Wheel { vertical: 0, horizontal: 1 }, SCREEN)[0]);
    assert_eq!(data, -120, "positive is left, which Windows counts as negative");
}

#[test]
fn modifiers_wrap_the_key() {
    let down = inputs(&Event::KeyDown { key: "c".into(), modifiers: vec![Modifier::Control] }, SCREEN);
    let keys: Vec<u16> = down.iter().map(|input| unsafe { input.Anonymous.ki.wVk.0 }).collect();
    assert_eq!(keys, [VK_CONTROL.0, b'C' as u16]);
    let up = inputs(&Event::KeyUp { key: "c".into(), modifiers: vec![Modifier::Control] }, SCREEN);
    let keys: Vec<u16> = up.iter().map(|input| unsafe { input.Anonymous.ki.wVk.0 }).collect();
    assert_eq!(keys, [b'C' as u16, VK_CONTROL.0]);
}

#[test]
fn this_process_has_a_readable_integrity_level() {
    let own = desktop_mcp_windows::integrity::level_of(std::process::id()).expect("our own token is readable");
    assert!(own >= 0x1000, "at least low integrity: {own:#x}");
}
