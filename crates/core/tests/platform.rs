//! A platform replacing a shared tool's text and adding a tool of its own.
mod support;
use desktop_mcp_core::mcp::{answer_stateless, Server};
use desktop_mcp_core::platform::{Platform, PlatformTool, ToolText};
use rmcp::model::{CallToolResult, ContentBlock, Tool};
use serde_json::{json, Value};
use std::rc::Rc;
use support::FakeDesktop;

struct Windowsish;

impl Platform for Windowsish {
    fn text(&self, tool: &str) -> Option<ToolText> {
        (tool == "Shell").then(|| ToolText {
            description: Some("Runs PowerShell."),
            schema_patches: vec![("/properties/mode", json!({"type": "string", "enum": ["powershell"], "default": "powershell"}))],
        })
    }

    fn tools(&self) -> Vec<PlatformTool> {
        let schema = json!({"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]});
        let tool = Tool::new("Echo", "Echoes text.", schema.as_object().unwrap().clone());
        vec![PlatformTool {
            tool,
            call: Box::new(|arguments, _cancellation| {
                let text = arguments.get("text").and_then(Value::as_str).unwrap_or_default().to_string();
                Box::pin(async move { CallToolResult::success(vec![ContentBlock::text(text)]) })
            }),
        }]
    }
}

fn ask(tools: &[&str], message: Value) -> Value {
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
    let server = Server::with_platform(Rc::new(FakeDesktop::default()), Rc::new(Windowsish), tools);
    let answer = tokio::task::LocalSet::new().block_on(&runtime, answer_stateless(server, serde_json::from_value(message).unwrap()));
    serde_json::to_value(answer.expect("an answer")).unwrap()
}

#[test]
fn platform_text_and_tools_are_listed_and_called() {
    let listed = ask(&["Shell", "Echo"], json!({"jsonrpc":"2.0","id":1,"method":"tools/list"}));
    let tools = listed["result"]["tools"].as_array().unwrap();
    let shell = tools.iter().find(|tool| tool["name"] == "Shell").unwrap();
    assert_eq!(shell["description"], "Runs PowerShell.");
    assert_eq!(shell["inputSchema"]["properties"]["mode"]["enum"], json!(["powershell"]));
    assert!(tools.iter().any(|tool| tool["name"] == "Echo"));

    let called = ask(&["Echo"], json!({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Echo","arguments":{"text":"hi"}}}));
    assert_eq!(called["result"]["content"][0]["text"], "hi");

    let filtered = ask(&["Shell"], json!({"jsonrpc":"2.0","id":1,"method":"tools/list"}));
    assert!(!filtered["result"]["tools"].as_array().unwrap().iter().any(|tool| tool["name"] == "Echo"));
}
