//! The Quartz events behind each input event, built and inspected, never posted; and the password-field climb.
use desktop_mcp_core::input::{Button, Event, Modifier};
use desktop_mcp_macos::input::events;
use desktop_mcp_macos::secure_field::{receives_secure_input, Node};
use objc2_core_graphics::{CGEvent, CGEventField, CGEventFlags, CGEventType};
use std::cell::Cell;
use std::rc::Rc;

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

