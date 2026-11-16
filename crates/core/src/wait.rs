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
    let app = given(app).ok_or_else(|| format!("app required for {condition}."))?;
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

/// The argument, or `None` when absent or blank.
pub fn given(value: Option<&str>) -> Option<&str> {
    value.filter(|value| !value.trim().is_empty())
}

fn deadline(timeout: i64) -> (i64, Instant) {
    let timeout = crate::shell::clamp_timeout(timeout, std::env::var(crate::shell::MAX_TIMEOUT_ENV).ok().as_deref()).max(0);
    (timeout, Instant::now() + Duration::from_secs(timeout as u64))
}

/// Waits until a window matching `app` and/or `title` (a case-insensitive substring) appears (its Window list
/// lines), or with `gone` until none is left. An app that does not answer may still have the window: not gone yet.
pub async fn until_window(desktop: &dyn Desktop, app: Option<&str>, title: Option<&str>, gone: bool, timeout: i64) -> Result<String, String> {
    let (app, title) = (given(app), given(title));
    let condition = if gone { "window_gone" } else { "window" };
    if app.is_none() && title.is_none() {
        return Err(format!("app or title required for {condition}."));
    }
    crate::input::require_accessibility(desktop)?;
    let (timeout, deadline) = deadline(timeout);
    let source = desktop.window_source();
    let wanted = title.map(str::to_lowercase);
    let found = poll(desktop, deadline, async || {
        let reading = source.reading(app).await;
        let matching: Vec<_> = reading.windows.into_iter().filter(|w| wanted.as_ref().is_none_or(|t| w.title.to_lowercase().contains(t))).collect();
        if gone {
            (matching.is_empty() && reading.complete).then(|| "No window matches any more.".to_string())
        } else {
            (!matching.is_empty()).then(|| crate::window::format(&matching))
        }
    })
    .await;
    let mut parts = vec!["window".to_string()];
    if let Some(title) = title {
        parts.push(format!("\"{title}\""));
    }
    if let Some(app) = app {
        parts.push(format!("in {app}"));
    }
    if gone {
        parts.push("to close".to_string());
    }
    found.ok_or_else(|| format!("Timed out after {timeout}s waiting for {}.", parts.join(" ")))
}

/// The least time one element check may walk, whatever is left of the timeout.
const MINIMUM_WALK: Duration = Duration::from_millis(500);

/// Waits until an element with `title` and/or `role` appears in `app` (or the app owning the menu bar); answers
/// `<role> "<title>" at [x,y]`, the centre in Click coordinates.
pub async fn until_element(desktop: &dyn Desktop, app: Option<&str>, title: Option<&str>, role: Option<&str>, timeout: i64) -> Result<String, String> {
    let (app, title, role) = (given(app), given(title), given(role));
    if title.is_none() && role.is_none() {
        return Err("title or role required for element.".to_string());
    }
    crate::input::require_accessibility(desktop)?;
    let (timeout, deadline) = deadline(timeout);
    let found = poll(desktop, deadline, async || {
        // Every check gets a real walk, even at or past the deadline (timeout 0).
        let walk_deadline = deadline.max(Instant::now() + MINIMUM_WALK);
        let element = desktop.find_element(app, title, role, walk_deadline).await?;
        Some(format!("{} \"{}\" at [{},{}]", element.role, crate::window::flat(&element.title), element.center.0.round() as i64, element.center.1.round() as i64))
    })
    .await;
    let mut parts = vec!["element".to_string()];
    if let Some(role) = role {
        parts.push(role.to_string());
    }
    if let Some(title) = title {
        parts.push(format!("\"{title}\""));
    }
    found.ok_or_else(|| format!("Timed out after {timeout}s waiting for {}.", parts.join(" ")))
}
