//! The RunShortcut tool: the user's Shortcuts (the Shortcuts app, not keyboard shortcuts). The platform runs them.
use crate::mcp::Desktop;
use crate::shell::{self, Run};
use std::time::Duration;

const MAX_CLOSE_MATCHES: usize = 20;

/// A shortcut's run.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ShortcutRun {
    pub status: i32,
    /// Waiting stopped; the shortcut may still be running.
    pub timed_out: bool,
    /// What the shortcut returned.
    pub output: String,
    pub stderr: String,
}

/// Names that start with `name` first (in list order), then names that merely contain it, case-insensitively,
/// without repeats, at most 20.
pub fn close_matches(name: &str, names: &[String]) -> Vec<String> {
    let query = name.to_lowercase();
    let mut unique: Vec<&String> = Vec::new();
    for name in names {
        if !unique.contains(&name) {
            unique.push(name);
        }
    }
    let prefixed = unique.iter().filter(|name| name.to_lowercase().starts_with(&query));
    let containing = unique.iter().filter(|name| !name.to_lowercase().starts_with(&query) && name.to_lowercase().contains(&query));
    prefixed.chain(containing).take(MAX_CLOSE_MATCHES).map(|name| name.to_string()).collect()
}

/// The capped timeout, in seconds.
pub fn timeout(requested: i64) -> i64 {
    shell::clamp_timeout(requested, std::env::var(shell::MAX_TIMEOUT_ENV).ok().as_deref())
}

fn seconds(timeout: i64) -> Duration {
    Duration::from_secs(timeout.max(0) as u64)
}

/// The shortcut names, optionally only those in `folder`.
pub async fn list(desktop: &dyn Desktop, folder: Option<&str>, timeout: i64) -> Result<Vec<String>, String> {
    let run: Run = desktop.list_shortcuts(folder, seconds(timeout)).await?;
    if run.timed_out {
        return Err("shortcuts list timed out.".to_string());
    }
    if run.status != 0 {
        return Err(format!("shortcuts list failed: {}", run.stderr.trim()));
    }
    Ok(run.stdout.lines().map(str::trim).filter(|name| !name.is_empty()).map(str::to_string).collect())
}

/// Runs the shortcut named exactly `name`, passing `input`, and reports its output.
pub async fn run(desktop: &dyn Desktop, name: &str, input: Option<&str>, timeout: i64) -> Result<String, String> {
    let names = list(desktop, None, timeout).await?;
    if !names.iter().any(|known| known == name) {
        let mut message = format!("No shortcut named '{name}'.");
        let close = close_matches(name, &names);
        if !close.is_empty() {
            message += &format!(" Close matches: {}", close.join(", "));
        }
        return Err(message);
    }
    let run = desktop.run_shortcut(name, input, seconds(timeout)).await?;
    if run.timed_out {
        return Ok(format!("Stopped waiting for shortcut '{name}' after {timeout}s; it may still be running."));
    }
    let mut text = format!("Shortcut '{name}' finished with status {}.\n", run.status);
    if !run.output.is_empty() {
        text += &run.output;
    } else {
        text += "(no output)";
        let stderr = run.stderr.trim();
        if !stderr.is_empty() {
            text += &format!("\nstderr:\n{stderr}");
        }
    }
    Ok(text)
}
