//! `connect` over the agent's Unix socket: stdin and stdout piped to the server unchanged.
mod support;
use desktop_mcp_core::bridge::{relay, Relayed};
use std::time::Duration;
use support::*;

/// `stdio` is what the client sees (its writes are the bridge's input), `socket` is the server's end.
fn start() -> (Pipe, Pipe, tokio::task::JoinHandle<Relayed>) {
    let (stdio, socket) = (Pipe::default(), Pipe::default());
    let task = tokio::task::spawn_local(relay(stdio.clone(), socket.clone()));
    (stdio, socket, task)
}

async fn eventually(check: impl Fn() -> bool) {
    for _ in 0..1000 {
        if check() {
            return;
        }
        tokio::time::sleep(Duration::from_millis(2)).await;
    }
    panic!("never happened");
}

#[test]
fn bytes_pass_both_ways_unchanged() {
    block_on(async {
        let (stdio, socket, _task) = start();
        stdio.send_raw(b"{\"a\":1}\n{\"b\"");
        socket.send_raw(b"reply one\npartial");
        eventually(|| socket.output() == b"{\"a\":1}\n{\"b\"" && stdio.output() == b"reply one\npartial").await;
    })
}

#[test]
fn input_eof_shuts_the_socket_then_the_servers_eof_finishes() {
    block_on(async {
        let (stdio, socket, task) = start();
        stdio.send_line("{}");
        stdio.close_input();
        eventually(|| socket.shut_down()).await;
        assert!(!task.is_finished(), "the server may still answer");
        socket.send_line("{\"answer\":1}");
        socket.close_input();
        assert_eq!(tokio::time::timeout(Duration::from_secs(2), task).await.unwrap().unwrap(), Relayed::Finished);
        assert_eq!(stdio.output(), b"{\"answer\":1}\n");
        assert!(stdio.shut_down(), "stdout is flushed before the relay returns");
    })
}

#[test]
fn the_server_closing_first_is_reported() {
    block_on(async {
        let (_stdio, socket, task) = start();
        socket.close_input();
        assert_eq!(tokio::time::timeout(Duration::from_secs(2), task).await.unwrap().unwrap(), Relayed::ServerClosed);
    })
}

#[test]
fn a_closed_stdout_ends_the_bridge() {
    block_on(async {
        let (stdio, socket, task) = start();
        stdio.close_output();
        socket.send_line("{}");
        assert_eq!(tokio::time::timeout(Duration::from_secs(2), task).await.unwrap().unwrap(), Relayed::OutputClosed);
    })
}
