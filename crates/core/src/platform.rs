//! What a platform adds to the shared server: its own text for the shared tools (descriptions, system-specific
//! enum values) and the tools only it has. The core's own text is macOS's.
use rmcp::model::{CallToolResult, Tool};
use std::future::Future;
use std::pin::Pin;
use tokio_util::sync::CancellationToken;

/// A platform's replacement text for one shared tool.
pub struct ToolText {
    /// Replaces the tool's description.
    pub description: Option<&'static str>,
    /// `(pointer, fragment)`: the input schema's value at the JSON pointer is replaced by the fragment.
    pub schema_patches: Vec<(&'static str, serde_json::Value)>,
}

/// Calls a platform tool with its arguments; the token is cancelled with the request.
pub type PlatformToolCall = Box<dyn Fn(serde_json::Map<String, serde_json::Value>, CancellationToken) -> Pin<Box<dyn Future<Output = CallToolResult>>>>;

/// A tool only this platform has.
pub struct PlatformTool {
    pub tool: Tool,
    pub call: PlatformToolCall,
}

/// What a gate decided for one tool call.
pub enum Admission {
    /// Run it; the lease is handed back to [`CallGate::finish`] (or dropped, when the call is cancelled or fails).
    Run(Box<dyn std::any::Any>),
    /// Answer this instead of running it.
    Refuse(CallToolResult),
}

/// Stands in front of every tool call (Windows: desktop control).
pub trait CallGate: 'static {
    /// Before the call. `peer` lets the gate notify the client later.
    fn admit(&self, tool: &str, arguments: &serde_json::Map<String, serde_json::Value>, peer: rmcp::Peer<rmcp::RoleServer>) -> Pin<Box<dyn Future<Output = Admission>>>;
    /// After the call: the result as the client sees it. A lease dropped without `finish` must end the call too.
    fn finish(&self, lease: Box<dyn std::any::Any>, result: CallToolResult) -> CallToolResult;
}

pub trait Platform: 'static {
    /// Text replacing the core's for the shared tool `tool`; `None` keeps the core's.
    fn text(&self, _tool: &str) -> Option<ToolText> {
        None
    }
    /// Tools only this platform has.
    fn tools(&self) -> Vec<PlatformTool> {
        Vec::new()
    }
    /// The gate in front of every tool call; none on macOS.
    fn gate(&self) -> Option<std::rc::Rc<dyn CallGate>> {
        None
    }
    /// Whether the server sends `notifications/message` (and so declares the logging capability).
    fn logging(&self) -> bool {
        false
    }
}

/// The core's own text and no extra tools.
pub struct CoreText;

impl Platform for CoreText {}

/// `tool` with `text` applied.
pub(crate) fn apply(tool: &mut Tool, text: ToolText) {
    if let Some(description) = text.description {
        tool.description = Some(description.into());
    }
    if text.schema_patches.is_empty() {
        return;
    }
    let mut schema = serde_json::Value::Object((*tool.input_schema).clone());
    for (pointer, fragment) in text.schema_patches {
        match schema.pointer_mut(pointer) {
            Some(slot) => *slot = fragment,
            None => panic!("no {pointer} in {}'s schema", tool.name),
        }
    }
    if let serde_json::Value::Object(object) = schema {
        tool.input_schema = std::sync::Arc::new(object);
    }
}
