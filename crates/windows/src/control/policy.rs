//! Which tool calls control gates: the ones that act on the desktop (`relaxed`), or every call (`strict`).
use serde_json::{Map, Value};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Policy {
    Relaxed,
    Strict,
}

impl Policy {
    pub fn name(self) -> &'static str {
        match self {
            Policy::Relaxed => "relaxed",
            Policy::Strict => "strict",
        }
    }

    pub fn parse(name: &str) -> Option<Policy> {
        match name {
            "relaxed" => Some(Policy::Relaxed),
            "strict" => Some(Policy::Strict),
            _ => None,
        }
    }
}

fn mode<'a>(arguments: &'a Map<String, Value>) -> Option<&'a str> {
    arguments.get("mode").and_then(Value::as_str)
}

/// Whether a call of `tool` with `arguments` waits for and holds control. ControlStatus never does.
pub fn gated(policy: Policy, tool: &str, arguments: &Map<String, Value>) -> bool {
    if tool == "ControlStatus" {
        return false;
    }
    if policy == Policy::Strict {
        return true;
    }
    match tool {
        "Click" | "Type" | "Scroll" | "Move" | "Shortcut" | "MultiSelect" | "MultiEdit" | "App" => true,
        // Listing windows and displays reads; every other mode acts.
        "Window" => !matches!(mode(arguments).unwrap_or("list"), "list" | "displays"),
        "Clipboard" => mode(arguments) == Some("write"),
        _ => false,
    }
}
