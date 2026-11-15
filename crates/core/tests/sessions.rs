//! Opt-in sessions on `/mcp`, as the Swift port serves them (macos/README.md, "Endpoints").
mod support;
use desktop_mcp_core::guest::Tick;
use desktop_mcp_core::http::handle;
use desktop_mcp_core::session::SessionSettings;
use http::StatusCode;
use http_body_util::BodyExt;
use serde_json::json;
use std::time::Duration;
use support::*;

/// Opens a session the way a client does: `initialize`, then `notifications/initialized`.
async fn open(fixture: &Fixture) -> String {
    let response = handle(&fixture.app, request("POST", BOTH, None, &initialize())).await;
    assert_eq!(response.status(), StatusCode::OK);
    let id = response.headers()["mcp-session-id"].to_str().unwrap().to_string();
    sse_message(response).await;
    let initialized = handle(&fixture.app, request("POST", BOTH, Some(&id), r#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#)).await;
    assert_eq!(initialized.status(), StatusCode::ACCEPTED);
    id
}

fn list(id: u64) -> String {
    json!({"jsonrpc":"2.0","id":id,"method":"tools/list"}).to_string()
}

fn wait(id: u64, seconds: u64) -> String {
    json!({"jsonrpc":"2.0","id":id,"method":"tools/call","params":{"name":"Wait","arguments":{"duration":seconds}}}).to_string()
}

#[test]
fn initialize_with_sse_accept_opens_a_session() {
    block_on(async {
        let fixture = fixture();
        let response = handle(&fixture.app, request("POST", BOTH, None, &initialize())).await;
        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(response.headers()["content-type"], "text/event-stream");
        let id = response.headers()["mcp-session-id"].to_str().unwrap().to_string();
        assert!(id.len() >= 16);
        let text = text_of(response).await;
        let events = events(&text);
        assert_eq!(events.len(), 2, "{text:?}");
        assert_eq!(events[0].1, "", "an empty-data event comes first");
        assert_eq!(events[1].0.as_deref(), Some("message"));
        let answer: serde_json::Value = serde_json::from_str(&events[1].1).unwrap();
        assert_eq!(answer["result"]["serverInfo"]["name"], "desktop-mcp");
        assert_eq!(fixture.app.sessions.count(), 1);
    })
}

#[test]
fn initialize_with_json_only_accept_stays_stateless() {
    block_on(async {
        let fixture = fixture();
        let response = handle(&fixture.app, request("POST", "application/json", None, &initialize())).await;
        assert_eq!(response.status(), StatusCode::OK);
        assert!(response.headers().get("mcp-session-id").is_none());
        assert_eq!(response.headers()["content-type"], "application/json");
        assert_eq!(fixture.app.sessions.count(), 0);
    })
}

#[test]
fn a_session_initialize_needs_both_accept_types() {
    block_on(async {
        let fixture = fixture();
        let response = handle(&fixture.app, request("POST", "text/event-stream", None, &initialize())).await;
        assert_eq!(response.status(), StatusCode::NOT_ACCEPTABLE);
        assert_eq!(fixture.app.sessions.count(), 0, "a refused initialize keeps no session");
    })
}

#[test]
fn session_requests_are_answered_as_sse() {
    block_on(async {
        let fixture = fixture();
        let id = open(&fixture).await;
        let response = handle(&fixture.app, request("POST", BOTH, Some(&id), &list(2))).await;
        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(response.headers()["content-type"], "text/event-stream");
        assert_eq!(response.headers()["mcp-session-id"], id.as_str());
        let answer = sse_message(response).await;
        assert_eq!(answer["id"], 2);
        assert_eq!(answer["result"]["tools"][0]["name"], "Wait");
        let called = sse_message(handle(&fixture.app, request("POST", BOTH, Some(&id), &wait(3, 1))).await).await;
        assert_eq!(called["result"]["content"][0]["text"], "Waited for 1 seconds.");
    })
}

#[test]
fn session_posts_need_both_accept_types_and_json() {
    block_on(async {
        let fixture = fixture();
        let id = open(&fixture).await;
        for accept in ["application/json", "text/event-stream"] {
            assert_eq!(handle(&fixture.app, request("POST", accept, Some(&id), &list(2))).await.status(), StatusCode::NOT_ACCEPTABLE, "{accept}");
        }
        let text = http::Request::builder().method("POST").uri("/mcp").header("Accept", BOTH).header("Content-Type", "text/plain").header("Mcp-Session-Id", &id).body(body(&list(2))).unwrap();
        assert_eq!(handle(&fixture.app, text).await.status(), StatusCode::UNSUPPORTED_MEDIA_TYPE);
        assert_eq!(handle(&fixture.app, request("POST", BOTH, Some(&id), "{nope")).await.status(), StatusCode::BAD_REQUEST);
        assert_eq!(handle(&fixture.app, request("POST", BOTH, Some(&id), &format!("[{}]", list(2)))).await.status(), StatusCode::BAD_REQUEST);
    })
}

#[test]
fn initialize_inside_a_session_is_400() {
    block_on(async {
        let fixture = fixture();
        let id = open(&fixture).await;
        assert_eq!(handle(&fixture.app, request("POST", BOTH, Some(&id), &initialize())).await.status(), StatusCode::BAD_REQUEST);
    })
}

#[test]
fn unknown_session_is_404() {
    block_on(async {
        let fixture = fixture();
        for method in ["POST", "GET", "DELETE"] {
            let response = handle(&fixture.app, request(method, BOTH, Some("no-such-session"), &list(2))).await;
            assert_eq!(response.status(), StatusCode::NOT_FOUND, "{method}");
        }
    })
}

#[test]
fn other_methods_in_a_session_are_405() {
    block_on(async {
        let fixture = fixture();
        let id = open(&fixture).await;
        let response = handle(&fixture.app, request("PUT", BOTH, Some(&id), "")).await;
        assert_eq!(response.status(), StatusCode::METHOD_NOT_ALLOWED);
        assert_eq!(response.headers()["allow"], "GET, POST, DELETE");
    })
}

#[test]
fn delete_ends_the_session() {
    block_on(async {
        let fixture = fixture();
        let id = open(&fixture).await;
        let deleted = handle(&fixture.app, request("DELETE", BOTH, Some(&id), "")).await;
        assert_eq!(deleted.status(), StatusCode::OK);
        assert_eq!(fixture.app.sessions.count(), 0);
        assert_eq!(handle(&fixture.app, request("POST", BOTH, Some(&id), &list(2))).await.status(), StatusCode::NOT_FOUND);
        assert_eq!(fixture.main_loop.tick.get(), Some(Tick::Off), "nothing is left to tick for");
    })
}

#[test]
fn get_opens_one_stream() {
    block_on(async {
        let fixture = fixture();
        let id = open(&fixture).await;
        assert_eq!(handle(&fixture.app, request("GET", "application/json", Some(&id), "")).await.status(), StatusCode::NOT_ACCEPTABLE);
        let stream = handle(&fixture.app, request("GET", "text/event-stream", Some(&id), "")).await;
        assert_eq!(stream.status(), StatusCode::OK);
        assert_eq!(stream.headers()["content-type"], "text/event-stream");
        assert_eq!(handle(&fixture.app, request("GET", "text/event-stream", Some(&id), "")).await.status(), StatusCode::CONFLICT, "a second stream");
        drop(stream); // the reader went away
        assert_eq!(handle(&fixture.app, request("GET", "text/event-stream", Some(&id), "")).await.status(), StatusCode::OK, "a reopened stream");
    })
}

#[test]
fn server_initiated_messages_travel_on_the_get_stream() {
    block_on(async {
        let fixture = fixture();
        let id = open(&fixture).await;
        let peer = fixture.app.sessions.peer(&id).expect("a running session has a peer");
        // Sent before the stream is open: kept until it opens.
        peer.notify_tool_list_changed().await.unwrap();
        let stream = handle(&fixture.app, request("GET", "text/event-stream", Some(&id), "")).await;
        let mut body = stream.into_body();
        let mut received = String::new();
        while !received.contains("list_changed") {
            let frame = tokio::time::timeout(Duration::from_secs(2), body.frame()).await.expect("an event within 2 s").expect("the stream is open").unwrap();
            received += std::str::from_utf8(frame.data_ref().unwrap()).unwrap();
        }
        let messages: Vec<_> = events(&received).into_iter().filter(|(_, data)| !data.is_empty()).collect();
        let message: serde_json::Value = serde_json::from_str(&messages[0].1).unwrap();
        assert_eq!(message["method"], "notifications/tools/list_changed");
    })
}

#[test]
fn ending_a_session_ends_its_get_stream() {
    block_on(async {
        let fixture = fixture();
        let id = open(&fixture).await;
        let stream = handle(&fixture.app, request("GET", "text/event-stream", Some(&id), "")).await;
        let reader = tokio::task::spawn_local(async move { stream.into_body().collect().await.map(|_| ()) });
        tokio::time::sleep(Duration::from_millis(20)).await;
        assert_eq!(handle(&fixture.app, request("DELETE", BOTH, Some(&id), "")).await.status(), StatusCode::OK);
        tokio::time::timeout(Duration::from_secs(2), reader).await.expect("the GET stream ended").unwrap().unwrap();
    })
}

#[test]
fn a_dropped_call_is_cancelled_and_the_session_lives_on() {
    block_on(async {
        let fixture = fixture();
        let id = open(&fixture).await;
        let mut release = fixture.desktop.hold_next_sleep();
        let call = handle(&fixture.app, request("POST", BOTH, Some(&id), &wait(5, 1))).await;
        // The head comes at once; the answer follows when the tool is done.
        assert_eq!(call.status(), StatusCode::OK);
        let reader = tokio::task::spawn_local(async move { call.into_body().collect().await.map(|_| ()) });
        while !fixture.desktop.sleeping() {
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
        assert_eq!(fixture.main_loop.tick.get(), Some(Tick::Fast), "a call in flight asks for the fast tick");
        reader.abort(); // the client went away
        let _ = reader.await;
        tokio::time::timeout(Duration::from_secs(2), release.closed()).await.expect("the tool call was still alive 2 s after its client left");
        let answer = sse_message(handle(&fixture.app, request("POST", BOTH, Some(&id), &list(6))).await).await;
        assert_eq!(answer["id"], 6, "the session still serves");
        assert_eq!(fixture.main_loop.tick.get(), Some(Tick::Slow), "a live session keeps the slow tick only");
    })
}

#[test]
fn a_cancelled_notification_cancels_the_call_and_ends_its_stream() {
    block_on(async {
        let fixture = fixture();
        let id = open(&fixture).await;
        let mut release = fixture.desktop.hold_next_sleep();
        let call = handle(&fixture.app, request("POST", BOTH, Some(&id), &wait(7, 1))).await;
        let reader = tokio::task::spawn_local(async move { text_of(call).await });
        while !fixture.desktop.sleeping() {
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
        let cancel = json!({"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7,"reason":"test"}}).to_string();
        assert_eq!(handle(&fixture.app, request("POST", BOTH, Some(&id), &cancel)).await.status(), StatusCode::ACCEPTED);
        tokio::time::timeout(Duration::from_secs(2), release.closed()).await.expect("the tool call was still alive after notifications/cancelled");
        let text = tokio::time::timeout(Duration::from_secs(2), reader).await.expect("the call's stream ended").unwrap();
        assert!(!text.contains("Waited"), "{text:?}");
    })
}

#[test]
fn idle_sessions_are_evicted_but_busy_ones_are_not() {
    block_on(async {
        let fixture = fixture_with(SessionSettings { idle_timeout: Some(Duration::from_millis(60)), ..SessionSettings::default() });
        let idle = open(&fixture).await;
        let busy = open(&fixture).await;
        let release = fixture.desktop.hold_next_sleep();
        let call = handle(&fixture.app, request("POST", BOTH, Some(&busy), &wait(2, 1))).await;
        let reader = tokio::task::spawn_local(async move { sse_message(call).await });
        tokio::time::sleep(Duration::from_millis(300)).await;
        assert_eq!(handle(&fixture.app, request("POST", BOTH, Some(&idle), &list(3))).await.status(), StatusCode::NOT_FOUND, "the idle session is gone");
        release.send(()).unwrap();
        assert_eq!(reader.await.unwrap()["result"]["content"][0]["text"], "Waited for 1 seconds.");
        let answer = sse_message(handle(&fixture.app, request("POST", BOTH, Some(&busy), &list(4))).await).await;
        assert_eq!(answer["id"], 4, "a session with a call in flight was kept");
        tokio::time::sleep(Duration::from_millis(300)).await;
        assert_eq!(fixture.app.sessions.count(), 0);
        assert_eq!(fixture.main_loop.tick.get(), Some(Tick::Off));
    })
}

#[test]
fn the_least_recently_used_idle_session_makes_room() {
    block_on(async {
        let fixture = fixture_with(SessionSettings { max_sessions: 2, ..SessionSettings::default() });
        let first = open(&fixture).await;
        let second = open(&fixture).await;
        sse_message(handle(&fixture.app, request("POST", BOTH, Some(&first), &list(2))).await).await; // first is now the more recent
        let third = open(&fixture).await;
        assert_eq!(fixture.app.sessions.count(), 2);
        assert_eq!(handle(&fixture.app, request("POST", BOTH, Some(&second), &list(3))).await.status(), StatusCode::NOT_FOUND);
        for id in [&first, &third] {
            assert_eq!(handle(&fixture.app, request("POST", BOTH, Some(id), &list(4))).await.status(), StatusCode::OK);
        }
    })
}

#[test]
fn no_room_when_every_session_is_busy_is_503() {
    block_on(async {
        let fixture = fixture_with(SessionSettings { max_sessions: 1, ..SessionSettings::default() });
        let only = open(&fixture).await;
        let _release = fixture.desktop.hold_next_sleep();
        let call = handle(&fixture.app, request("POST", BOTH, Some(&only), &wait(2, 1))).await;
        let _reader = tokio::task::spawn_local(async move { text_of(call).await });
        while !fixture.desktop.sleeping() {
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
        let refused = handle(&fixture.app, request("POST", BOTH, None, &initialize())).await;
        assert_eq!(refused.status(), StatusCode::SERVICE_UNAVAILABLE);
    })
}

#[test]
fn a_session_over_its_byte_budget_ends() {
    block_on(async {
        let fixture = fixture_with(SessionSettings { byte_budget: 4096, ..SessionSettings::default() });
        let id = open(&fixture).await;
        let mut served = 0;
        for request_id in 2..20 {
            let response = handle(&fixture.app, request("POST", BOTH, Some(&id), &list(request_id))).await;
            if response.status() == StatusCode::NOT_FOUND {
                break;
            }
            sse_message(response).await;
            served += 1;
        }
        assert!((1..10).contains(&served), "served {served} tools/list answers of about 1.5 KB on a 4 KB budget");
        assert_eq!(fixture.app.sessions.count(), 0);
    })
}

#[test]
fn deleting_a_session_ends_its_calls() {
    block_on(async {
        let fixture = fixture();
        let id = open(&fixture).await;
        let mut release = fixture.desktop.hold_next_sleep();
        let call = handle(&fixture.app, request("POST", BOTH, Some(&id), &wait(2, 1))).await;
        let reader = tokio::task::spawn_local(async move { text_of(call).await });
        while !fixture.desktop.sleeping() {
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
        assert_eq!(handle(&fixture.app, request("DELETE", BOTH, Some(&id), "")).await.status(), StatusCode::OK);
        tokio::time::timeout(Duration::from_secs(2), release.closed()).await.expect("the tool call outlived its session");
        let text = tokio::time::timeout(Duration::from_secs(2), reader).await.expect("the call's stream outlived its session").unwrap();
        assert!(!text.contains("Waited"), "{text:?}");
        assert_eq!(fixture.main_loop.tick.get(), Some(Tick::Off));
    })
}
