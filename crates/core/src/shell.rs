//! The Shell tool's platform-independent part: the timeout cap and how a run is reported. The platform starts
//! the process ([`crate::mcp::Desktop::shell`]).

use rmcp::schemars;

/// Caps every timeout; `0` lifts the cap.
pub const MAX_TIMEOUT_ENV: &str = "DESKTOP_MCP_MAX_TIMEOUT";
const DEFAULT_MAX_TIMEOUT: i64 = 240;

// What runs the command; the platform picks the interpreter for each. (A plain comment: a doc comment would
// reach the schema the model reads.)
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ShellMode {
    #[default]
    Shell,
    Osascript,
}

/// A finished (or timed-out) process.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Run {
    pub stdout: String,
    pub stderr: String,
    /// Exit code, or the negated signal number when killed by a signal.
    pub status: i32,
    pub timed_out: bool,
}

/// Caps the timeout below the MCP client's own wall-clock ceiling, so the server always answers before the
/// client gives up on the call. `cap` is the value of [`MAX_TIMEOUT_ENV`].
pub fn clamp_timeout(timeout: i64, cap: Option<&str>) -> i64 {
    let cap = cap.and_then(|cap| cap.parse().ok()).unwrap_or(DEFAULT_MAX_TIMEOUT);
    if cap > 0 { timeout.min(cap) } else { timeout }
}

/// Both streams: `stdout or stderr` would hide errors behind partial success output.
pub fn combine(stdout: &str, stderr: &str) -> String {
    if !stdout.is_empty() && !stderr.is_empty() {
        return format!("{}\n{stderr}", stdout.trim_end_matches('\n'));
    }
    if stdout.is_empty() { stderr.to_string() } else { stdout.to_string() }
}

/// The output and status the model sees. `requested` is the caller's timeout, `timeout` the capped one.
pub fn report(run: Result<Run, String>, requested: i64, timeout: i64) -> (String, i32) {
    let run = match run {
        Ok(run) => run,
        Err(message) => return (message, -1),
    };
    let combined = combine(&run.stdout, &run.stderr);
    if !run.timed_out {
        return (combined.trim().to_string(), run.status);
    }
    let mut message = format!("Command timed out after {timeout} seconds");
    if requested > timeout {
        message += &format!(" (requested {requested}s, capped by {MAX_TIMEOUT_ENV})");
    }
    if !combined.trim().is_empty() {
        message += "\n--- partial output before timeout ---\n";
        message += combined.trim_end_matches('\n');
    }
    (message, -1)
}
