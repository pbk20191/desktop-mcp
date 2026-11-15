//! `Wait` until a condition holds: checks with backoff on the platform's exact timers.
use crate::mcp::Desktop;
use std::time::{Duration, Instant};

/// A running application, as the app conditions see it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RunningApp {
    pub pid: i32,
    pub name: Option<String>,
    pub bundle_id: Option<String>,
}

/// The shared app filter: a bundle id exactly, or a localized name ignoring case and surrounding spaces. A blank
/// query or a missing name never matches.
pub fn matches(name: Option<&str>, bundle_id: Option<&str>, query: &str) -> bool {
    let query = query.trim();
    if query.is_empty() {
        return false;
    }
    bundle_id == Some(query) || name.is_some_and(|name| name.to_lowercase() == query.to_lowercase())
}

/// Calls `check` at once, then after 0.1 s, doubling to at most 1 s between checks, until it answers or `deadline`
/// passes (the last sleep ends at the deadline, and one last check follows). `None` on timeout.
async fn poll<T>(desktop: &dyn Desktop, deadline: Instant, mut check: impl AsyncFnMut() -> Option<T>) -> Option<T> {
    let mut delay = Duration::from_millis(100);
    loop {
        if let Some(value) = check().await {
            return Some(value);
        }
        let now = Instant::now();
        if now >= deadline {
            return None;
        }
        desktop.sleep(delay.min(deadline - now)).await;
        delay = (delay * 2).min(Duration::from_secs(1));
    }
}

/// Waits until `app` (name or bundle id) runs, or with `running` false, until it quits.
pub async fn until_app(desktop: &dyn Desktop, app: Option<&str>, running: bool, timeout: i64) -> Result<String, String> {
    let condition = if running { "app_running" } else { "app_quit" };
    let app = app.filter(|app| !app.trim().is_empty()).ok_or_else(|| format!("app required for {condition}."))?;
    let timeout = crate::shell::clamp_timeout(timeout, std::env::var(crate::shell::MAX_TIMEOUT_ENV).ok().as_deref()).max(0);
    let deadline = Instant::now() + Duration::from_secs(timeout as u64);
    let mut first = true;
    let found = poll(desktop, deadline, async || {
        let is_running = desktop.running_apps().await.iter().any(|running| matches(running.name.as_deref(), running.bundle_id.as_deref(), app));
        let was_first = std::mem::replace(&mut first, false);
        if is_running != running {
            return None;
        }
        Some(match (running, was_first) {
            (true, _) => format!("App {app} is running."),
            // Not running at the first look (a typo, say) is not a quit.
            (false, true) => format!("App {app} is not running."),
            (false, false) => format!("App {app} quit."),
        })
    })
    .await;
    found.ok_or_else(|| format!("Timed out after {timeout}s waiting for app {app} to {}.", if running { "run" } else { "quit" }))
}
