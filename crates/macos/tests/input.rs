//! The Quartz events behind each input event, built and inspected, never posted; and the password-field climb.
use desktop_mcp_core::input::{Button, Event, Modifier, KEYS};
use desktop_mcp_macos::input::{events, key_code};
use desktop_mcp_macos::secure_field::{receives_secure_input, Node};
use objc2_core_graphics::{CGEvent, CGEventField, CGEventFlags, CGEventType};
use std::cell::Cell;
use std::rc::Rc;

#[test]
fn every_key_name_has_a_code() {
    for name in KEYS {
        assert!(key_code(name).is_some(), "{name}");
    }
    assert_eq!((key_code("c"), key_code("space"), key_code("up"), key_code("f12")), (Some(8), Some(49), Some(126), Some(111)));
}

#[test]
fn mouse_events_carry_their_type_place_and_click_state() {
    let made = events(&Event::Down { x: 10.0, y: 20.0, button: Button::Right, click: 2 });
    assert_eq!(made.len(), 1);
    let event = Some(&*made[0]);
    assert_eq!(CGEvent::r#type(event), CGEventType::RightMouseDown);
    let at = CGEvent::location(event);
    assert_eq!((at.x, at.y), (10.0, 20.0));
    assert_eq!(CGEvent::integer_value_field(event, CGEventField::MouseEventClickState), 2);
    assert_eq!(CGEvent::r#type(Some(&*events(&Event::Up { x: 0.0, y: 0.0, button: Button::Middle, click: 1 })[0])), CGEventType::OtherMouseUp);
    assert_eq!(CGEvent::r#type(Some(&*events(&Event::Drag { x: 1.0, y: 1.0 })[0])), CGEventType::LeftMouseDragged);
    assert_eq!(CGEvent::r#type(Some(&*events(&Event::Move { x: 1.0, y: 1.0 })[0])), CGEventType::MouseMoved);
    assert_eq!(CGEvent::r#type(Some(&*events(&Event::Wheel { vertical: -3, horizontal: 0 })[0])), CGEventType::ScrollWheel);
}

#[test]
fn key_events_set_their_modifiers_explicitly() {
    let made = events(&Event::KeyDown { key: "t".into(), modifiers: vec![Modifier::Command, Modifier::Shift] });
    let event = Some(&*made[0]);
    assert_eq!(CGEvent::r#type(event), CGEventType::KeyDown);
    assert_eq!(CGEvent::integer_value_field(event, CGEventField::KeyboardEventKeycode), 17);
    assert_eq!(CGEvent::flags(event), CGEventFlags::MaskCommand | CGEventFlags::MaskShift);
    let plain = events(&Event::KeyUp { key: "return".into(), modifiers: vec![] });
    assert_eq!(CGEvent::flags(Some(&*plain[0])), CGEventFlags::empty());
    assert_eq!(events(&Event::ReleaseModifiers).len(), 8);
    assert!(events(&Event::KeyDown { key: "nosuchkey".into(), modifiers: vec![] }).is_empty());
}

#[test]
fn text_is_its_own_unicode_string() {
    let made = events(&Event::Text("가".into()));
    assert_eq!(made.len(), 2);
    for event in &made {
        let mut units = [0u16; 4];
        let mut length = 0;
        unsafe { CGEvent::keyboard_get_unicode_string(Some(event), 4, &mut length, units.as_mut_ptr()) };
        assert_eq!(String::from_utf16(&units[..length as usize]).unwrap(), "가");
    }
    assert_eq!(CGEvent::r#type(Some(&*made[1])), CGEventType::KeyUp);
}

/// A fake element: its roles, its parent, and a shared count of reads.
#[derive(Clone)]
struct Fake {
    role: &'static str,
    subrole: Option<&'static str>,
    parent: Option<Rc<Fake>>,
    reads: Rc<Cell<usize>>,
}

impl Node for Fake {
    fn roles(&self) -> (Option<String>, Option<String>) {
        self.reads.set(self.reads.get() + 1);
        (Some(self.role.to_string()), self.subrole.map(str::to_string))
    }
    fn parent(&self) -> Option<Fake> {
        self.parent.as_deref().cloned()
    }
}

fn node(role: &'static str, subrole: Option<&'static str>, parent: Option<Fake>, reads: &Rc<Cell<usize>>) -> Fake {
    Fake { role, subrole, parent: parent.map(Rc::new), reads: reads.clone() }
}

fn chain(depth: usize, top: Fake) -> Fake {
    let reads = top.reads.clone();
    (0..depth).fold(top, |parent, _| node("AXGroup", None, Some(parent), &reads))
}

#[test]
fn the_climb_finds_a_password_field_up_to_five_levels() {
    let reads = Rc::new(Cell::new(0));
    let field = node("AXTextField", Some("AXSecureTextField"), Some(node("AXWebArea", None, None, &reads)), &reads);
    assert!(receives_secure_input(None, Some(chain(3, field.clone()))));
    assert!(!receives_secure_input(None, Some(chain(3, node("AXTextField", None, None, &reads)))));
    assert!(receives_secure_input(None, Some(chain(5, field.clone()))));
    assert!(!receives_secure_input(None, Some(chain(6, field))));
}

#[test]
fn the_climb_stops_at_the_window_or_web_area() {
    for stop in ["AXWindow", "AXWebArea"] {
        let reads = Rc::new(Cell::new(0));
        let field = node("AXSecureTextField", None, None, &reads);
        let hit = chain(1, node(stop, None, Some(field), &reads));
        assert!(!receives_secure_input(None, Some(hit)), "{stop}");
        assert_eq!(reads.get(), 2, "the group and the stop, never the field above: {stop}");
    }
}

#[test]
fn a_focused_password_field_wins() {
    let reads = Rc::new(Cell::new(0));
    let hit = node("AXTextField", None, Some(node("AXWindow", None, None, &reads)), &reads);
    assert!(receives_secure_input(Some(node("AXSecureTextField", None, None, &reads)), Some(hit.clone())));
    assert!(receives_secure_input(Some(node("AXTextField", Some("AXSecureTextField"), None, &reads)), None));
    assert!(!receives_secure_input(Some(node("AXTextField", None, None, &reads)), Some(hit)));
    assert!(!receives_secure_input(None::<Fake>, None));
}
