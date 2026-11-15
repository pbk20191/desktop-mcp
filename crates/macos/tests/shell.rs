//! Shell commands as child processes: the cases of the Swift port's ShellTests.
mod support;
use desktop_mcp_core::mcp::Desktop;
use desktop_mcp_core::shell::{report, Run, ShellMode};
use desktop_mcp_macos::desktop::{osascript_arguments, MacDesktop};
use desktop_mcp_macos::main_loop::run;
use desktop_mcp_macos::process;
use std::time::{Duration, Instant};

fn shell(command: &str, mode: ShellMode, timeout: u64) -> Run {
    let command = command.to_string();
    run(move |_| async move { MacDesktop::new().shell(&command, mode, Duration::from_secs(timeout)).await.expect("started") })
}

/// The text the model sees, as the tool reports it.
fn output(command: &str, mode: ShellMode, timeout: u64) -> (String, i32) {
    report(Ok(shell(command, mode, timeout)), timeout as i64, timeout as i64)
}

fn combines_stdout_and_stderr_and_reports_the_exit_code() {
    assert_eq!(output("echo out; echo err >&2; exit 3", ShellMode::Shell, 10), ("out\nerr".to_string(), 3));
}

fn timeout_kills_the_process_group_and_keeps_partial_output() {
    let started = Instant::now();
    let run = shell("echo before; sleep 30", ShellMode::Shell, 1);
    assert!(started.elapsed() < Duration::from_secs(5), "took {:?}", started.elapsed());
    assert!(run.timed_out);
    let (text, status) = report(Ok(run), 1, 1);
    assert_eq!(status, -1);
    assert_eq!(text, "Command timed out after 1 seconds\n--- partial output before timeout ---\nbefore");
}

fn children_do_not_inherit_ignored_signals() {
    // The server ignores SIGPIPE (and handles SIGINT/SIGTERM itself); a command it runs must still be killable
    // the ordinary way.
    let previous = unsafe { libc::signal(libc::SIGTERM, libc::SIG_IGN) };
    let (text, status) = output("kill -TERM $$; echo survived", ShellMode::Shell, 10);
    unsafe { libc::signal(libc::SIGTERM, previous) };
    assert_eq!(status, -15, "{text}");
    assert!(!text.contains("survived"), "{text}");
}

fn backgrounded_grandchild_does_not_hold_the_call_open() {
    let started = Instant::now();
    assert_eq!(output("sleep 30 & echo done", ShellMode::Shell, 10), ("done".to_string(), 0));
    assert!(started.elapsed() < Duration::from_secs(5), "took {:?}", started.elapsed());
}

fn stdin_is_not_inherited() {
    // `cat` would block (or eat the protocol stream) if stdin were inherited.
    assert_eq!(output("cat; echo eof", ShellMode::Shell, 5).0, "eof");
}

fn dropping_the_call_kills_the_child() {
    let marker = std::env::temp_dir().join(format!("desktop-mcp-cancel-{}", std::process::id()));
    let command = format!("sleep 1; touch '{}'", marker.display());
    let finished = run(move |_| async move {
        let desktop = MacDesktop::new();
        tokio::select! {
            _ = desktop.shell(&command, ShellMode::Shell, Duration::from_secs(30)) => true,
            _ = desktop.sleep(Duration::from_millis(300)) => false,
        }
    });
    assert!(!finished);
    // The main queue keeps running meanwhile: the abandoned child is reaped there.
    run(|_| MacDesktop::new().sleep(Duration::from_millis(1500)));
    assert!(!marker.exists(), "the shell kept running after the call was dropped");
    let mut status = 0;
    let zombie = unsafe { libc::waitpid(-1, &mut status, libc::WNOHANG) };
    assert_eq!(zombie, -1, "an abandoned child was left unreaped");
}

fn a_missing_interpreter_is_an_error() {
    // Not reachable through the tool (its interpreters are fixed), so through the process runner directly.
    let error = run(|_| process::run(vec!["/nonexistent/interpreter".to_string()], Duration::from_secs(5)));
    assert_eq!(error, Err("Failed to start /nonexistent/interpreter: No such file or directory".to_string()));
}

fn osascript_mode() {
    assert_eq!(output("return 1 + 2", ShellMode::Osascript, 10), ("3".to_string(), 0));
}

fn osascript_language_is_chosen_by_the_first_line() {
    assert_eq!(osascript_arguments("return 1"), ["-e", "return 1"]);
    assert_eq!(osascript_arguments("#!/usr/bin/osascript\nreturn 1"), ["-e", "return 1"]);
    assert_eq!(osascript_arguments("#!/usr/bin/osascript -l JavaScript\n1 + 1"), ["-l", "JavaScript", "-e", "1 + 1"]);
    assert_eq!(osascript_arguments("#!/usr/bin/env osascript -l javascript\r\n1 + 1"), ["-l", "JavaScript", "-e", "1 + 1"]);
    assert_eq!(osascript_arguments("#!/usr/bin/osascript -l JavaScript"), ["-l", "JavaScript", "-e", ""]);
    // A comment stays in the source: it is valid JavaScript, and offsets in errors stay right.
    assert_eq!(osascript_arguments("// language: JavaScript\n1 + 1"), ["-l", "JavaScript", "-e", "// language: JavaScript\n1 + 1"]);
    assert_eq!(osascript_arguments("-- language: JavaScript\nreturn 1"), ["-e", "-- language: JavaScript\nreturn 1"]);
}

fn osascript_runs_javascript_when_asked() {
    assert_eq!(output("#!/usr/bin/osascript -l JavaScript\n[1, 2, 3].map(x => x * 2)", ShellMode::Osascript, 10), ("2, 4, 6".to_string(), 0));
    assert_eq!(output("// language: JavaScript\nJSON.stringify({a: 1})", ShellMode::Osascript, 10), ("{\"a\":1}".to_string(), 0));
    assert_eq!(
        output("// language: JavaScript\nthrow new Error(\"js boom\")", ShellMode::Osascript, 10),
        ("execution error: Error: Error: js boom (-2700)".to_string(), 1)
    );
}

fn huge_output_is_truncated() {
    let run = shell("head -c 11000000 /dev/zero | tr '\\0' a", ShellMode::Shell, 20);
    assert_eq!(run.status, 0);
    assert!(run.stdout.ends_with("\n...[output truncated at 10485760 bytes]"), "{}", &run.stdout[run.stdout.len() - 60..]);
    assert_eq!(run.stdout.len(), 10 * 1024 * 1024 + "\n...[output truncated at 10485760 bytes]".len());
}

fn main() {
    support::run(&[
        ("combines_stdout_and_stderr_and_reports_the_exit_code", combines_stdout_and_stderr_and_reports_the_exit_code),
        ("timeout_kills_the_process_group_and_keeps_partial_output", timeout_kills_the_process_group_and_keeps_partial_output),
        ("children_do_not_inherit_ignored_signals", children_do_not_inherit_ignored_signals),
        ("backgrounded_grandchild_does_not_hold_the_call_open", backgrounded_grandchild_does_not_hold_the_call_open),
        ("stdin_is_not_inherited", stdin_is_not_inherited),
        ("dropping_the_call_kills_the_child", dropping_the_call_kills_the_child),
        ("a_missing_interpreter_is_an_error", a_missing_interpreter_is_an_error),
        ("osascript_mode", osascript_mode),
        ("osascript_language_is_chosen_by_the_first_line", osascript_language_is_chosen_by_the_first_line),
        ("osascript_runs_javascript_when_asked", osascript_runs_javascript_when_asked),
        ("huge_output_is_truncated", huge_output_is_truncated),
    ])
}
