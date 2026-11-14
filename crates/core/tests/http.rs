//! The HTTP app's routing, without a socket.
use bytes::Bytes;
use desktop_mcp_core::guest::{Activity, MainLoop, Tick};
use desktop_mcp_core::http::{handle, App};
use desktop_mcp_core::mcp::{Desktop, Server};
use http::{Request, Response, StatusCode};
use http_body_util::{BodyExt, Full};
use serde_json::{json, Value};
use std::cell::{Cell, RefCell};
use std::future::Future;
use std::pin::Pin;
use std::rc::Rc;
use std::time::Duration;

#[derive(Default)]
struct FakeLoop { tick: Cell<Option<Tick>> }
impl MainLoop for FakeLoop {
    fn kick(&self) {}
    fn set_tick(&self, rate: Tick) { self.tick.set(Some(rate)) }
}
/// A desktop whose `sleep` ends when the test says so.
#[derive(Default)]
struct FakeDesktop { release: RefCell<Option<tokio::sync::oneshot::Receiver<()>>> }
impl Desktop for FakeDesktop {
    fn sleep(&self, _duration: Duration) -> Pin<Box<dyn Future<Output = ()>>> {
        let gate = self.release.borrow_mut().take();
        Box::pin(async move {
            if let Some(gate) = gate {
                let _ = gate.await;
            }
        })
    }
}
struct Fixture { app: Rc<App>, main_loop: Rc<FakeLoop>, desktop: Rc<FakeDesktop> }
fn fixture() -> Fixture {
    let (main_loop, desktop) = (Rc::new(FakeLoop::default()), Rc::new(FakeDesktop::default()));
    let for_server = desktop.clone();
    let app = Rc::new(App { make_server: Box::new(move || Server::new(for_server.clone(), &["Wait"])), activity: Activity::new(main_loop.clone()) });
    Fixture { app, main_loop, desktop }
}
fn block_on<T>(future: impl Future<Output = T>) -> T {
    let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
    tokio::task::LocalSet::new().block_on(&runtime, future)
}
fn post(path: &str) -> http::request::Builder {
    Request::builder().method("POST").uri(path).header("Accept", "application/json").header("Content-Type", "application/json")
}
fn body(text: &str) -> Full<Bytes> {
    Full::new(Bytes::from(text.to_string()))
}
async fn json_of(response: Response<Full<Bytes>>) -> Value {
    serde_json::from_slice(&response.into_body().collect().await.unwrap().to_bytes()).expect("a JSON body")
}
const LIST: &str = r#"{"jsonrpc":"2.0","id":1,"method":"tools/list"}"#;

#[test]
fn post_tools_list_is_json_200() {
    block_on(async {
        for path in ["/mcp", "/mcp/"] {
            let response = handle(&fixture().app, post(path).body(body(LIST)).unwrap()).await;
            assert_eq!(response.status(), StatusCode::OK, "{path}");
            assert_eq!(response.headers()["content-type"], "application/json");
            assert_eq!(json_of(response).await["result"]["tools"][0]["name"], "Wait");
        }
    })
}

#[test]
fn missing_accept_is_406() {
    block_on(async {
        let request = Request::builder().method("POST").uri("/mcp").header("Content-Type", "application/json").body(body(LIST)).unwrap();
        assert_eq!(handle(&fixture().app, request).await.status(), StatusCode::NOT_ACCEPTABLE);
    })
}

#[test]
fn accept_without_json_is_406() {
    block_on(async {
        let request = Request::builder().method("POST").uri("/mcp").header("Accept", "text/event-stream").header("Content-Type", "application/json").body(body(LIST)).unwrap();
        assert_eq!(handle(&fixture().app, request).await.status(), StatusCode::NOT_ACCEPTABLE);
    })
}

/// As the Swift port: the header has to name `application/json`; a wildcard does not count.
#[test]
fn wildcard_accept_is_406() {
    block_on(async {
        for accept in ["*/*", "application/*"] {
            let request = Request::builder().method("POST").uri("/mcp").header("Accept", accept).header("Content-Type", "application/json").body(body(LIST)).unwrap();
            assert_eq!(handle(&fixture().app, request).await.status(), StatusCode::NOT_ACCEPTABLE, "{accept}");
        }
    })
}

#[test]
fn accept_with_json_among_others_is_ok() {
    block_on(async {
        for accept in ["application/json, text/event-stream", "text/event-stream,Application/JSON;q=0.9"] {
            let request = Request::builder().method("POST").uri("/mcp").header("Accept", accept).header("Content-Type", "application/json").body(body(LIST)).unwrap();
            assert_eq!(handle(&fixture().app, request).await.status(), StatusCode::OK, "{accept}");
        }
    })
}

#[test]
fn non_json_content_type_is_415() {
    block_on(async {
        for content_type in [Some("text/plain"), None] {
            let mut request = Request::builder().method("POST").uri("/mcp").header("Accept", "application/json");
            if let Some(value) = content_type {
                request = request.header("Content-Type", value);
            }
            assert_eq!(handle(&fixture().app, request.body(body(LIST)).unwrap()).await.status(), StatusCode::UNSUPPORTED_MEDIA_TYPE, "{content_type:?}");
        }
        let with_charset = Request::builder().method("POST").uri("/mcp").header("Accept", "application/json").header("Content-Type", "application/json; charset=utf-8").body(body(LIST)).unwrap();
        assert_eq!(handle(&fixture().app, with_charset).await.status(), StatusCode::OK);
    })
}

#[test]
fn batch_is_400() {
    block_on(async {
        let batch = format!("[{LIST}]");
        let response = handle(&fixture().app, post("/mcp").body(body(&batch)).unwrap()).await;
        assert_eq!(response.status(), StatusCode::BAD_REQUEST);
        assert_eq!(json_of(response).await["error"]["code"], -32600);
    })
}

#[test]
fn invalid_json_is_400() {
    block_on(async {
        let response = handle(&fixture().app, post("/mcp").body(body("{nope")).unwrap()).await;
        assert_eq!(response.status(), StatusCode::BAD_REQUEST);
        assert_eq!(json_of(response).await["error"]["code"], -32700);
    })
}

#[test]
fn get_and_delete_are_405_with_allow_post() {
    block_on(async {
        for method in ["GET", "DELETE"] {
            let response = handle(&fixture().app, Request::builder().method(method).uri("/mcp").body(body("")).unwrap()).await;
            assert_eq!(response.status(), StatusCode::METHOD_NOT_ALLOWED, "{method}");
            assert_eq!(response.headers()["allow"], "POST");
        }
    })
}

#[test]
fn notification_is_202_with_empty_body() {
    block_on(async {
        let message = r#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#;
        let response = handle(&fixture().app, post("/mcp").body(body(message)).unwrap()).await;
        assert_eq!(response.status(), StatusCode::ACCEPTED);
        assert!(response.into_body().collect().await.unwrap().to_bytes().is_empty());
    })
}

#[test]
fn other_path_is_404() {
    block_on(async {
        let response = handle(&fixture().app, post("/nope").body(body(LIST)).unwrap()).await;
        assert_eq!(response.status(), StatusCode::NOT_FOUND);
    })
}

#[test]
fn request_guard_is_held_only_during_the_call() {
    block_on(async {
        let fixture = fixture();
        let (release, gate) = tokio::sync::oneshot::channel();
        *fixture.desktop.release.borrow_mut() = Some(gate);
        let wait = json!({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":1}}}).to_string();
        let app = fixture.app.clone();
        let call = tokio::task::spawn_local(async move { handle(&app, post("/mcp").body(body(&wait)).unwrap()).await });
        // Let the call reach the tool.
        for _ in 0..50 {
            tokio::time::sleep(Duration::from_millis(2)).await;
            if fixture.desktop.release.borrow().is_none() {
                break;
            }
        }
        assert_eq!(fixture.main_loop.tick.get(), Some(Tick::Fast), "a request in flight asks for the fast tick");
        release.send(()).unwrap();
        let response = call.await.unwrap();
        assert_eq!(json_of(response).await["result"]["content"][0]["text"], "Waited for 1 seconds.");
        assert_eq!(fixture.main_loop.tick.get(), Some(Tick::Off));
    })
}

#[test]
fn dropping_the_request_ends_the_tool_call() {
    block_on(async {
        let fixture = fixture();
        let (release, gate) = tokio::sync::oneshot::channel::<()>();
        *fixture.desktop.release.borrow_mut() = Some(gate);
        let wait = json!({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":1}}}).to_string();
        let app = fixture.app.clone();
        let call = tokio::task::spawn_local(async move { handle(&app, post("/mcp").body(body(&wait)).unwrap()).await });
        for _ in 0..50 {
            tokio::time::sleep(Duration::from_millis(2)).await;
            if fixture.desktop.release.borrow().is_none() {
                break;
            }
        }
        // The client goes away: hyper drops the request's future.
        call.abort();
        let _ = call.await;
        // The tool's future is dropped with it: its end of the gate closes.
        let mut release = release;
        tokio::time::timeout(Duration::from_secs(2), release.closed()).await.expect("the tool call was still alive 2 s after the request was dropped");
        assert_eq!(fixture.main_loop.tick.get(), Some(Tick::Off));
    })
}
