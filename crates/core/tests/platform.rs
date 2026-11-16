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

/// Refuses Shell; admits everything else and marks its result.
struct Gate;

impl desktop_mcp_core::platform::CallGate for Gate {
    fn admit(&self, tool: &str, _arguments: &serde_json::Map<String, Value>, _peer: rmcp::Peer<rmcp::RoleServer>) -> std::pin::Pin<Box<dyn std::future::Future<Output = desktop_mcp_core::platform::Admission>>> {
        let refuse = tool == "Shell";
        Box::pin(async move {
            if refuse {
                desktop_mcp_core::platform::Admission::Refuse(CallToolResult::error(vec![ContentBlock::text("refused")]))
            } else {
                desktop_mcp_core::platform::Admission::Run(Box::new("lease"))
            }
        })
    }
    fn finish(&self, lease: Box<dyn std::any::Any>, mut result: CallToolResult) -> CallToolResult {
        let lease = *lease.downcast::<&str>().unwrap();
        result.content.push(ContentBlock::text(format!("finished with {lease}")));
        result
    }
}

struct Gated;

impl Platform for Gated {
    fn gate(&self) -> Option<Rc<dyn desktop_mcp_core::platform::CallGate>> {
        Some(Rc::new(Gate))
    }
    fn logging(&self) -> bool {
        true
    }
}

fn ask_gated(message: Value) -> Value {
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
    let server = Server::with_platform(Rc::new(FakeDesktop::default()), Rc::new(Gated), &["Shell", "Wait"]);
    let answer = tokio::task::LocalSet::new().block_on(&runtime, answer_stateless(server, serde_json::from_value(message).unwrap()));
    serde_json::to_value(answer.expect("an answer")).unwrap()
}

#[test]
fn a_gate_refuses_and_wraps() {
    let refused = ask_gated(json!({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Shell","arguments":{"command":"x"}}}));
    assert_eq!(refused["result"]["content"][0]["text"], "refused");
    let wrapped = ask_gated(json!({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":0}}}));
    assert_eq!(wrapped["result"]["content"][1]["text"], "finished with lease", "{wrapped}");
}

#[test]
fn logging_is_declared_when_the_platform_asks() {
    let initialize = json!({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"0"}}});
    assert!(ask_gated(initialize.clone())["result"]["capabilities"]["logging"].is_object());
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
    let plain = Server::new(Rc::new(FakeDesktop::default()), &["Wait"]);
    let answer = tokio::task::LocalSet::new().block_on(&runtime, answer_stateless(plain, serde_json::from_value(initialize).unwrap()));
    assert!(serde_json::to_value(answer.unwrap()).unwrap()["result"]["capabilities"].get("logging").is_none());
}
