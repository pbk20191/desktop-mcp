//! The Shell tool's platform-independent logic.
use desktop_mcp_core::shell::{clamp_timeout, combine, report, Run};

#[test]
fn timeout_is_capped_by_default_at_240_seconds() {
    assert_eq!(clamp_timeout(10, None), 10);
    assert_eq!(clamp_timeout(10_000, None), 240);
}

#[test]
fn the_cap_comes_from_the_environment_and_zero_lifts_it() {
    assert_eq!(clamp_timeout(100, Some("30")), 30);
    assert_eq!(clamp_timeout(10_000, Some("0")), 10_000);
    assert_eq!(clamp_timeout(10_000, Some("nonsense")), 240);
}

#[test]
fn both_streams_are_kept() {
    assert_eq!(combine("out\n\n", "err\n"), "out\nerr\n");
    assert_eq!(combine("", "err"), "err");
    assert_eq!(combine("out", ""), "out");
}

fn run(stdout: &str, stderr: &str, status: i32, timed_out: bool) -> Run {
    Run { stdout: stdout.into(), stderr: stderr.into(), status, timed_out }
}

#[test]
fn a_finished_command_reports_trimmed_output_and_its_status() {
    assert_eq!(report(Ok(run("out\n", "err\n", 3, false)), 10, 10), ("out\nerr".to_string(), 3));
}

#[test]
fn a_timeout_reports_the_partial_output() {
    let (text, status) = report(Ok(run("before\n\n", "", -9, true)), 1, 1);
    assert_eq!(text, "Command timed out after 1 seconds\n--- partial output before timeout ---\nbefore");
    assert_eq!(status, -1);
    assert_eq!(report(Ok(run(" \n", "", -9, true)), 1, 1).0, "Command timed out after 1 seconds");
}

#[test]
fn a_capped_timeout_says_so() {
    let (text, _) = report(Ok(run("", "", -9, true)), 500, 240);
    assert_eq!(text, "Command timed out after 240 seconds (requested 500s, capped by DESKTOP_MCP_MAX_TIMEOUT)");
}

#[test]
fn a_command_that_cannot_start_reports_why() {
    assert_eq!(report(Err("Failed to start /nope: No such file or directory".into()), 10, 10), ("Failed to start /nope: No such file or directory".to_string(), -1));
}
