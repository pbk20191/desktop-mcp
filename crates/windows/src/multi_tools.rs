//! `MultiSelect` and `MultiEdit` (Windows only): several clicks, ctrl held to select, and several fields typed in,
//! on the core's input sequences. Every location is checked before any input is sent.
use desktop_mcp_core::input::{click, received_by, type_at, Button, Caret, Event};
use desktop_mcp_core::mcp::Desktop;
use desktop_mcp_core::platform::PlatformTool;
use rmcp::model::{CallToolResult, ContentBlock, Tool, ToolAnnotations};
use serde_json::{json, Map, Value};
use std::rc::Rc;

fn failure(tool: &str, message: impl std::fmt::Display) -> CallToolResult {
    CallToolResult::error(vec![ContentBlock::text(format!("Error executing tool {tool}: {message}"))])
}

fn integer(value: &Value) -> Option<i64> {
    value.as_i64().or_else(|| value.as_f64().map(|number| number.round() as i64))
}

/// Releases ctrl when dropped, also when the call is abandoned midway.
struct CtrlUp(Rc<dyn Desktop>);

impl Drop for CtrlUp {
    fn drop(&mut self) {
        self.0.post(Event::KeyUp { key: "ctrl".into(), modifiers: vec![] });
    }
}

async fn select(desktop: Rc<dyn Desktop>, arguments: Map<String, Value>) -> CallToolResult {
    const TOOL: &str = "MultiSelect";
    let Some(locs) = arguments.get("locs").and_then(Value::as_array) else { return failure(TOOL, "locs is required") };
    if locs.is_empty() {
        return failure(TOOL, "locs is empty");
    }
    let mut points = Vec::new();
    for loc in locs {
        match loc.as_array().map(|pair| pair.iter().filter_map(integer).collect::<Vec<_>>()).as_deref() {
            Some([x, y]) => points.push((*x, *y)),
            _ => return failure(TOOL, "each location must be [x, y]"),
        }
    }
    for point in &points {
        if let Some(reason) = desktop.input_refusal(Some(*point)) {
            return failure(TOOL, reason);
        }
    }
    let press_ctrl = arguments.get("press_ctrl").and_then(Value::as_bool).unwrap_or(true);
    let held = press_ctrl.then(|| {
        desktop.post(Event::KeyDown { key: "ctrl".into(), modifiers: vec![] });
        CtrlUp(desktop.clone())
    });
    for (x, y) in &points {
        click(desktop.as_ref(), *x, *y, Button::Left, 1).await;
    }
    drop(held);
    if let Some(reason) = desktop.take_input_failure() {
        return failure(TOOL, reason);
    }
    let count = points.len();
    let window = received_by(desktop.as_ref(), points.last().copied());
    let text = if press_ctrl { format!("Selected {count} items with ctrl held{window}.") } else { format!("Clicked {count} locations{window}.") };
    CallToolResult::success(vec![ContentBlock::text(text)])
}

async fn edit(desktop: Rc<dyn Desktop>, arguments: Map<String, Value>) -> CallToolResult {
    const TOOL: &str = "MultiEdit";
    let Some(locs) = arguments.get("locs").and_then(Value::as_array) else { return failure(TOOL, "locs is required") };
    if locs.is_empty() {
        return failure(TOOL, "locs is empty");
    }
    let mut fields = Vec::new();
    for entry in locs {
        match entry.as_array().map(Vec::as_slice) {
            Some([x, y, text]) => match (integer(x), integer(y), text.as_str()) {
                (Some(x), Some(y), Some(text)) => fields.push((x, y, text.to_string())),
                _ => return failure(TOOL, "each entry must be [x, y, text]"),
            },
            _ => return failure(TOOL, "each entry must be [x, y, text]"),
        }
    }
    for (x, y, _) in &fields {
        if let Some(reason) = desktop.input_refusal(Some((*x, *y))) {
            return failure(TOOL, reason);
        }
    }
    let mut lines = Vec::new();
    for (x, y, text) in &fields {
        let secure = type_at(desktop.as_ref(), *x, *y, text, Caret::Idle, true, false).await;
        let window = received_by(desktop.as_ref(), Some((*x, *y)));
        lines.push(if secure { format!("Typed {} characters into a secure field at ({x},{y}){window}.", text.chars().count()) } else { format!("Typed {text} at ({x},{y}){window}.") });
    }
    if let Some(reason) = desktop.take_input_failure() {
        return failure(TOOL, reason);
    }
    CallToolResult::success(vec![ContentBlock::text(lines.join("\n"))])
}

/// `work` until the request is cancelled; dropping it then releases what it holds (ctrl, a held key).
async fn cancellable(_tool: &str, cancellation: tokio_util::sync::CancellationToken, work: impl std::future::Future<Output = CallToolResult>) -> CallToolResult {
    tokio::select! {
        result = work => result,
        _ = cancellation.cancelled() => CallToolResult::error(vec![ContentBlock::text("Cancelled.")]),
    }
}

fn tool(name: &'static str, description: &'static str, schema: Value) -> Tool {
    let mut tool = Tool::new(name, description, schema.as_object().cloned().unwrap_or_default());
    tool.annotations = Some(ToolAnnotations::with_title(name).read_only(false).destructive(true).idempotent(false).open_world(false));
    tool
}

pub fn tools(desktop: Rc<dyn Desktop>) -> Vec<PlatformTool> {
    let for_select = desktop.clone();
    let select_tool = tool(
        "MultiSelect",
        "Clicks several locations [[x, y], ...] in order. press_ctrl=true (default) holds ctrl throughout, to select several files, folders or list items; false clicks each one plainly.",
        json!({"type": "object", "properties": {"locs": {"type": "array", "items": {"type": "array", "items": {"type": "integer"}}}, "press_ctrl": {"type": "boolean", "default": true}}, "required": ["locs"]}),
    );
    let edit_tool = tool(
        "MultiEdit",
        "Types into several fields, locs=[[x, y, \"text\"], ...] in order: clicks each field, clears it, and types its text.",
        json!({"type": "object", "properties": {"locs": {"type": "array", "items": {"type": "array"}}}, "required": ["locs"]}),
    );
    vec![
        PlatformTool { tool: select_tool, call: Box::new(move |arguments, cancellation| Box::pin(cancellable("MultiSelect", cancellation, select(for_select.clone(), arguments)))) },
        PlatformTool { tool: edit_tool, call: Box::new(move |arguments, cancellation| Box::pin(cancellable("MultiEdit", cancellation, edit(desktop.clone(), arguments)))) },
    ]
}
