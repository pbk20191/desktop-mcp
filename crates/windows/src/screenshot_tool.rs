//! `Screenshot` (Windows only): a display, or a region of the screen, as an image, without reading the UI tree.
use crate::imaging::encode_png;
use desktop_mcp_core::platform::PlatformTool;
use desktop_mcp_core::window::Frame;
use rmcp::model::{CallToolResult, ContentBlock, Tool};
use serde_json::{json, Map, Value};

const MAX_SIDE: u32 = 1568;
pub const DESCRIPTION: &str = "Captures a display, or a region of the screen, as an image, without reading the UI tree (faster than Snapshot with use_vision; use Snapshot for element ids). 'display' is a display index from Window mode='displays' (default: the display of the focused window, else the primary). 'region' [x, y, width, height] in screen pixels captures only that rectangle and takes precedence over 'display'. The image is at most 1568 pixels on its longer side; when it was scaled down, the text says by how much, so image coordinates can be turned into the screen coordinates Click takes.";

fn failure(message: impl std::fmt::Display) -> CallToolResult {
    CallToolResult::error(vec![ContentBlock::text(format!("Error executing tool Screenshot: {message}"))])
}

fn region(arguments: &Map<String, Value>) -> Result<Option<Frame>, String> {
    let Some(value) = arguments.get("region").filter(|value| !value.is_null()) else { return Ok(None) };
    let numbers: Vec<i64> = value.as_array().map(|items| items.iter().filter_map(Value::as_i64).collect()).unwrap_or_default();
    match numbers[..] {
        [x, y, width, height] if width > 0 && height > 0 => Ok(Some(Frame::new(x as f64, y as f64, width as f64, height as f64))),
        _ => Err(desktop_mcp_core::ocr::REGION_ERROR.to_string()),
    }
}

async fn run(arguments: Map<String, Value>) -> CallToolResult {
    let region = match region(&arguments) {
        Ok(region) => region,
        Err(message) => return failure(message),
    };
    let (displays, windows) = crate::pool::offload(|| (crate::displays::displays(), crate::windows_list::top_windows())).await;
    let display = if let Some(region) = region {
        crate::ocr::display_of(&displays, region)
    } else if let Some(index) = arguments.get("display").and_then(Value::as_u64) {
        match displays.get(index as usize) {
            Some(display) => Some(display),
            None => return failure(format!("there is no display {index}; Window mode='displays' lists them")),
        }
    } else {
        let focused = windows.iter().find(|window| window.focused).map(|window| window.frame);
        focused.and_then(|frame| crate::ocr::display_of(&displays, frame)).or_else(|| displays.iter().find(|display| display.main))
    };
    let Some(display) = display.cloned() else { return failure(desktop_mcp_core::ocr::OUTSIDE_ERROR) };
    let mut shot = match crate::capture::capture_display(&display).await {
        Ok(shot) => shot,
        Err(reason) => return failure(format!("the screen could not be captured: {reason}")),
    };
    if let Some(region) = region {
        match crate::ocr::crop(&shot, region) {
            Some(cropped) => shot = cropped,
            None => return failure(desktop_mcp_core::ocr::OUTSIDE_ERROR),
        }
    }
    let (width, height, origin) = (shot.width, shot.height, shot.origin);
    let (scaled_width, scaled_height) = crate::clipboard::scaled_size(width, height, MAX_SIDE);
    let png = match crate::pool::offload(move || encode_png(&shot, Some(MAX_SIDE))).await {
        Ok(png) => png,
        Err(reason) => return failure(reason),
    };
    let mut text = format!("Screenshot of display {} at [{},{},{},{}] (screen pixels).", display.index, origin.0, origin.1, width, height);
    if (scaled_width, scaled_height) != (width, height) {
        text += &format!(" The image is scaled to {scaled_width}x{scaled_height}: multiply image coordinates by {:.4} and add [{},{}] for screen coordinates.", width as f64 / scaled_width as f64, origin.0, origin.1);
    }
    use base64::Engine;
    CallToolResult::success(vec![ContentBlock::text(text), ContentBlock::image(base64::engine::general_purpose::STANDARD.encode(png), "image/png")])
}

pub fn tool() -> PlatformTool {
    let schema = json!({
        "type": "object",
        "properties": {
            "display": {"type": ["integer", "null"], "minimum": 0},
            "region": {"type": ["array", "null"], "items": {"type": "integer"}}
        }
    });
    let mut tool = Tool::new("Screenshot", DESCRIPTION, schema.as_object().cloned().unwrap_or_default());
    tool.annotations = Some(rmcp::model::ToolAnnotations::with_title("Screenshot").read_only(true).destructive(false).idempotent(false).open_world(false));
    PlatformTool { tool, call: Box::new(|arguments, _cancellation| Box::pin(run(arguments))) }
}
