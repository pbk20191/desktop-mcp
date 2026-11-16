//! The Shell tool's platform-independent logic.
use desktop_mcp_core::shell::{clamp_timeout, report, Run};

#[test]
fn timeout_is_capped_by_default_at_240_seconds() {
    assert_eq!(clamp_timeout(10, None), 10);
    assert_eq!(clamp_timeout(10_000, None), 240);
}

fn run(stdout: &str, stderr: &str, status: i32, timed_out: bool) -> Run {
    Run { stdout: stdout.into(), stderr: stderr.into(), status, timed_out }
}

#[test]
fn a_timeout_reports_the_partial_output() {
    let (text, status) = report(Ok(run("before\n\n", "", -9, true)), 1, 1);
    assert_eq!(text, "Command timed out after 1 seconds\n--- partial output before timeout ---\nbefore");
    assert_eq!(status, -1);
    assert_eq!(report(Ok(run(" \n", "", -9, true)), 1, 1).0, "Command timed out after 1 seconds");
}

