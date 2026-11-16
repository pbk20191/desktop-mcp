//! Thin, typed helpers over the C Accessibility API. Attribute values are converted once into [`Value`], so the
//! tools never handle CoreFoundation types.
use objc2_application_services::{AXCopyMultipleAttributeOptions, AXError, AXUIElement, AXValue, AXValueType};
use objc2_core_foundation::{CFArray, CFAttributedString, CFBoolean, CFNumber, CFRange, CFRetained, CFString, CFType, CGPoint, CGRect, CGSize, Type, CFURL};
use std::ffi::c_void;
use std::ptr::NonNull;

extern "C-unwind" {
    fn CFURLGetString(url: &CFURL) -> Option<NonNull<CFString>>;
}

/// An accessibility element. HIServices lets any thread message one.
#[derive(Clone)]
pub struct Element(CFRetained<AXUIElement>);
unsafe impl Send for Element {}
unsafe impl Sync for Element {}

impl PartialEq for Element {
    fn eq(&self, other: &Element) -> bool {
        let (a, b): (&CFType, &CFType) = (&self.0, &other.0);
        a == b
    }
}

impl std::fmt::Debug for Element {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Element({:p})", &*self.0)
    }
}

/// An attribute's value.
#[derive(Clone, Debug, PartialEq)]
pub enum Value {
    Text(String),
    Bool(bool),
    Int(i64),
    Float(f64),
    Point(f64, f64),
    Size(f64, f64),
    Rect { x: f64, y: f64, width: f64, height: f64 },
    Range { location: i64, length: i64 },
    Element(Element),
    Array(Vec<Value>),
    /// Anything else, as CoreFoundation describes it.
    Other(String),
}

impl Value {
    /// Text of a string-ish value (text, attributed text, URL); "" otherwise.
    pub fn text(&self) -> &str {
        match self {
            Value::Text(text) => text,
            _ => "",
        }
    }

    pub fn is_true(&self) -> bool {
        matches!(self, Value::Bool(true)) || matches!(self, Value::Int(n) if *n != 0)
    }

    pub fn element(&self) -> Option<&Element> {
        match self {
            Value::Element(element) => Some(element),
            _ => None,
        }
    }

    pub fn elements(&self) -> Vec<Element> {
        match self {
            Value::Array(values) => values.iter().filter_map(Value::element).cloned().collect(),
            _ => vec![],
        }
    }
}

/// What reading a value gave: a value, nothing, or "the app did not answer".
enum Read {
    Value(Value),
    Missing,
    NotAnswering,
}

fn convert(value: &CFType) -> Read {
    if let Some(ax) = value.downcast_ref::<AXValue>() {
        let kind = unsafe { ax.r#type() };
        let read = |kind: AXValueType, out: *mut c_void| unsafe { ax.value(kind, NonNull::new_unchecked(out)) };
        return match kind {
            AXValueType::CGPoint => {
                let mut p = CGPoint { x: 0.0, y: 0.0 };
                if read(kind, (&raw mut p).cast()) { Read::Value(Value::Point(p.x, p.y)) } else { Read::Missing }
            }
            AXValueType::CGSize => {
                let mut s = CGSize { width: 0.0, height: 0.0 };
                if read(kind, (&raw mut s).cast()) { Read::Value(Value::Size(s.width, s.height)) } else { Read::Missing }
            }
            AXValueType::CGRect => {
                let mut r = CGRect { origin: CGPoint { x: 0.0, y: 0.0 }, size: CGSize { width: 0.0, height: 0.0 } };
                if read(kind, (&raw mut r).cast()) { Read::Value(Value::Rect { x: r.origin.x, y: r.origin.y, width: r.size.width, height: r.size.height }) } else { Read::Missing }
            }
            AXValueType::CFRange => {
                let mut r = CFRange { location: 0, length: 0 };
                if read(kind, (&raw mut r).cast()) { Read::Value(Value::Range { location: r.location as i64, length: r.length as i64 }) } else { Read::Missing }
            }
            AXValueType::AXError => {
                let mut error = AXError::Success;
                if read(kind, (&raw mut error).cast()) && error == AXError::CannotComplete { Read::NotAnswering } else { Read::Missing }
            }
            _ => Read::Missing,
        };
    }
    Read::Value(if let Some(text) = value.downcast_ref::<CFString>() {
        Value::Text(text.to_string())
    } else if let Some(text) = value.downcast_ref::<CFAttributedString>() {
        Value::Text(text.string().map(|s| s.to_string()).unwrap_or_default())
    } else if let Some(url) = value.downcast_ref::<CFURL>() {
        Value::Text(unsafe { CFURLGetString(url) }.map(|s| unsafe { s.as_ref() }.to_string()).unwrap_or_default())
    } else if let Some(boolean) = value.downcast_ref::<CFBoolean>() {
        Value::Bool(boolean.value())
    } else if let Some(number) = value.downcast_ref::<CFNumber>() {
        if number.is_float_type() { Value::Float(number.as_f64().unwrap_or_default()) } else { Value::Int(number.as_i64().unwrap_or_default()) }
    } else if let Some(element) = value.downcast_ref::<AXUIElement>() {
        Value::Element(Element(element.retain()))
    } else if let Some(array) = value.downcast_ref::<CFArray>() {
        let array: &CFArray<CFType> = unsafe { &*(array as *const CFArray as *const CFArray<CFType>) };
        Value::Array((0..array.len()).filter_map(|i| match convert(unsafe { array.get_unchecked(i as _) }) { Read::Value(v) => Some(v), _ => None }).collect())
    } else {
        Value::Other(format!("{value:?}"))
    })
}

impl Element {
    /// The `AXUIElementRef`, for calls the binding does not cover.
    pub fn as_ptr(&self) -> *const std::ffi::c_void {
        CFRetained::as_ptr(&self.0).as_ptr().cast_const().cast()
    }

    pub fn system_wide() -> Element {
        Element(unsafe { AXUIElement::new_system_wide() })
    }

    pub fn application(pid: i32) -> Element {
        Element(unsafe { AXUIElement::new_application(pid) })
    }

    /// One attribute; `None` when missing, unsupported or unreadable.
    pub fn attribute(&self, name: &str) -> Option<Value> {
        match self.read(name) {
            Ok(Read::Value(value)) => Some(value),
            _ => None,
        }
    }

    fn read(&self, name: &str) -> Result<Read, ()> {
        let mut value: *const CFType = std::ptr::null();
        let error = unsafe { self.0.copy_attribute_value(&CFString::from_str(name), NonNull::from(&mut value)) };
        if error == AXError::CannotComplete {
            return Ok(Read::NotAnswering);
        }
        if error != AXError::Success || value.is_null() {
            return Ok(Read::Missing);
        }
        let value = unsafe { CFRetained::from_raw(NonNull::new_unchecked(value.cast_mut())) };
        Ok(convert(&value))
    }

    /// Several attributes in one round trip, by name; missing ones are absent. `None` when the app does not answer
    /// (`kAXErrorCannotComplete`, which a messaging timeout also gives), for the call or for any one attribute.
    pub fn attributes(&self, names: &[&str]) -> Option<Vec<(String, Value)>> {
        let keys: Vec<CFRetained<CFString>> = names.iter().map(|name| CFString::from_str(name)).collect();
        let keys = CFArray::from_retained_objects(&keys);
        let mut values: *const CFArray = std::ptr::null();
        let error = unsafe { self.0.copy_multiple_attribute_values(keys.as_opaque(), AXCopyMultipleAttributeOptions(0), NonNull::from(&mut values)) };
        if error == AXError::CannotComplete {
            return None;
        }
        if error != AXError::Success || values.is_null() {
            return Some(vec![]);
        }
        let values = unsafe { CFRetained::from_raw(NonNull::new_unchecked(values.cast_mut())) };
        let values: &CFArray<CFType> = unsafe { &*(&*values as *const CFArray as *const CFArray<CFType>) };
        let mut found = Vec::new();
        for (index, name) in names.iter().enumerate().take(values.len()) {
            match convert(unsafe { values.get_unchecked(index as _) }) {
                Read::NotAnswering => return None,
                Read::Value(value) => found.push((name.to_string(), value)),
                Read::Missing => {}
            }
        }
        Some(found)
    }

    /// The elements of a list attribute (children, windows); `None` when the app does not answer.
    pub fn elements(&self, name: &str) -> Option<Vec<Element>> {
        match self.read(name) {
            Ok(Read::NotAnswering) => None,
            Ok(Read::Value(value)) => Some(value.elements()),
            _ => Some(vec![]),
        }
    }

    pub fn children(&self) -> Vec<Element> {
        self.elements("AXChildren").unwrap_or_default()
    }

    pub fn pid(&self) -> Option<i32> {
        let mut pid = 0;
        (unsafe { self.0.pid(NonNull::from(&mut pid)) } == AXError::Success).then_some(pid)
    }

    /// The element at a point of the screen, asked of the system-wide element.
    pub fn at(&self, x: f64, y: f64) -> Option<Element> {
        let mut found: *const AXUIElement = std::ptr::null();
        let error = unsafe { self.0.copy_element_at_position(x as f32, y as f32, NonNull::from(&mut found)) };
        (error == AXError::Success && !found.is_null()).then(|| Element(unsafe { CFRetained::from_raw(NonNull::new_unchecked(found.cast_mut())) }))
    }

    pub fn set_messaging_timeout(&self, seconds: f32) {
        unsafe { self.0.set_messaging_timeout(seconds) };
    }

    pub fn is_settable(&self, name: &str) -> bool {
        let mut settable = 0u8;
        unsafe { self.0.is_attribute_settable(&CFString::from_str(name), NonNull::from(&mut settable).cast()) == AXError::Success && settable != 0 }
    }

    fn set(&self, name: &str, value: &CFType) -> bool {
        unsafe { self.0.set_attribute_value(&CFString::from_str(name), value) == AXError::Success }
    }

    pub fn set_bool(&self, name: &str, value: bool) -> bool {
        let value: &CFType = if value { unsafe { objc2_core_foundation::kCFBooleanTrue }.expect("a constant") } else { unsafe { objc2_core_foundation::kCFBooleanFalse }.expect("a constant") };
        self.set(name, value)
    }

    pub fn set_position(&self, x: f64, y: f64) -> bool {
        let mut point = CGPoint { x, y };
        let Some(value) = (unsafe { AXValue::new(AXValueType::CGPoint, NonNull::from(&mut point).cast()) }) else { return false };
        self.set("AXPosition", &value)
    }

    pub fn set_size(&self, width: f64, height: f64) -> bool {
        let mut size = CGSize { width, height };
        let Some(value) = (unsafe { AXValue::new(AXValueType::CGSize, NonNull::from(&mut size).cast()) }) else { return false };
        self.set("AXSize", &value)
    }

    /// `perform`, also counting a reply that timed out (`kAXErrorCannotComplete`): the action was delivered.
    pub fn perform_answering(&self, action: &str) -> bool {
        let error = unsafe { self.0.perform_action(&CFString::from_str(action)) };
        error == AXError::Success || error == AXError::CannotComplete
    }

    pub fn perform(&self, action: &str) -> bool {
        unsafe { self.0.perform_action(&CFString::from_str(action)) == AXError::Success }
    }
}

/// Whether Accessibility is granted, without prompting.
pub fn trusted() -> bool {
    unsafe { objc2_application_services::AXIsProcessTrusted() }
}
