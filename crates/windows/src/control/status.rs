//! The control status as the client sees it: ControlStatus's compact JSON and the rejection text (Python's
//! `json.dumps` with its default separators, as upstream).
use super::machine::Code;

#[derive(Clone, Debug, PartialEq)]
pub struct Status {
    pub state: &'static str,
    pub generation: u64,
    pub wait_seconds: f64,
    pub enabled: bool,
    pub policy: &'static str,
    pub idle_timeout_seconds: f64,
}

/// A float as Python prints it: `10.0`, `3.5`.
fn py_float(value: f64) -> String {
    let text = format!("{value}");
    if text.contains(['.', 'e', 'E']) || !value.is_finite() { text } else { format!("{text}.0") }
}

impl Status {
    /// The status object, keys in upstream's order, with `separator` after commas and colons.
    fn object(&self, comma: &str, colon: &str) -> String {
        let fields = [
            format!("\"state\"{colon}\"{}\"", self.state),
            format!("\"generation\"{colon}{}", self.generation),
            format!("\"wait_seconds\"{colon}{}", py_float(self.wait_seconds)),
            format!("\"enabled\"{colon}{}", self.enabled),
            format!("\"policy\"{colon}\"{}\"", self.policy),
            format!("\"idle_timeout_seconds\"{colon}{}", py_float(self.idle_timeout_seconds)),
        ];
        format!("{{{}}}", fields.join(comma))
    }

    /// ControlStatus's text: compact JSON.
    pub fn to_json(&self) -> String {
        self.object(",", ":")
    }

    pub fn to_value(&self) -> serde_json::Value {
        serde_json::from_str(&self.to_json()).expect("the status is JSON")
    }
}

impl Code {
    pub fn name(self) -> &'static str {
        match self {
            Code::UserControl => "USER_CONTROL",
            Code::ControlUnavailable => "CONTROL_UNAVAILABLE",
            Code::ControlPreempted => "CONTROL_PREEMPTED",
            Code::PhysicalInputHeld => "PHYSICAL_INPUT_HELD",
        }
    }
}

/// A refused or interrupted call's whole text: `{"code": C, "status": {...}}`, and `executed_steps` when preempted.
pub fn rejection(code: Code, status: &Status, executed_steps: Option<u64>) -> String {
    let steps = executed_steps.map(|steps| format!(", \"executed_steps\": {steps}")).unwrap_or_default();
    format!("{{\"code\": \"{}\", \"status\": {}{steps}}}", code.name(), status.object(", ", ": "))
}
