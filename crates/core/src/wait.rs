//! `Wait` until a condition holds: checks with backoff on the platform's exact timers, and at once (after a short
//! settle) when the platform reports that what the wait reads may have changed.
use crate::mcp::Desktop;
use std::any::Any;
use std::cell::{Cell, RefCell};
use std::future::{poll_fn, Future};
use std::pin::pin;
use std::rc::Rc;
use std::task::{Poll, Waker};
use std::time::{Duration, Instant};

/// What a wait reads, for the platform to watch.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Watch<'a> {
    /// Apps launching and quitting.
    Apps,
    /// Windows of `app` (every app when `None`) opening, closing, retitled or minimized.
    Windows(Option<&'a str>),
    /// Elements of `app` (the app owning the menu bar when `None`) appearing, going away or moving.
    Elements(Option<&'a str>),
}

/// Raised by the platform when something a wait watches may have changed.
#[derive(Default)]
pub struct Signal {
    raised: Cell<bool>,
    waker: RefCell<Option<Waker>>,
}

impl Signal {
    pub fn raise(&self) {
        self.raised.set(true);
        if let Some(waker) = self.waker.take() {
            waker.wake();
        }
    }
}

/// A wait's watching: raises `signal` while `watching` (the platform's observers) lives. [`Changes::none`] when the
/// platform does not watch; the wait then only polls.
pub struct Changes {
    signal: Rc<Signal>,
    _watching: Option<Box<dyn Any>>,
}

impl Changes {
    pub fn none() -> Changes {
        Changes { signal: Rc::default(), _watching: None }
    }

    pub fn new(signal: Rc<Signal>, watching: Box<dyn Any>) -> Changes {
        Changes { signal, _watching: Some(watching) }
    }

    /// Completes when the signal is raised (at once if it already was), lowering it.
    fn changed(&self) -> impl Future<Output = ()> + '_ {
        poll_fn(|cx| {
            if self.signal.raised.replace(false) {
                return Poll::Ready(());
            }
            *self.signal.waker.borrow_mut() = Some(cx.waker().clone());
            Poll::Pending
        })
    }
}

/// A running application, as the app conditions see it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RunningApp {
    pub pid: i32,
    pub name: Option<String>,
    pub bundle_id: Option<String>,
    /// The app's English name where the system shows a translated one (macOS: its bundle's file name, TextEdit
    /// for 텍스트 편집기), so a model naming it in English finds it too.
    pub english_name: Option<String>,
}

impl RunningApp {
    /// Whether `query` names this app: [`matches`] by its shown name or bundle id, or by its English name.
    pub fn is(&self, query: &str) -> bool {
        matches(self.name.as_deref(), self.bundle_id.as_deref(), query) || matches(self.english_name.as_deref(), None, query)
    }
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
/// passes (the last sleep ends at the deadline, and one last check follows). `None` on timeout. When the platform
/// reports a change in what `watch` names, the next check comes after a short settle instead (a burst of
/// notifications costs one check); the backoff stays for apps that report nothing.
async fn poll<T>(desktop: &dyn Desktop, deadline: Instant, watch: Watch<'_>, mut check: impl AsyncFnMut() -> Option<T>) -> Option<T> {
    let changes = desktop.watch(watch);
    let settle = match watch {
        Watch::Elements(_) => crate::tuning::wait::ELEMENT_SETTLE,
        Watch::Apps | Watch::Windows(_) => crate::tuning::wait::SETTLE,
    };
    let mut delay = crate::tuning::wait::FIRST_DELAY;
    loop {
        // What changed before this check is covered by it.
        changes.signal.raised.set(false);
        if let Some(value) = check().await {
            return Some(value);
        }
        let now = Instant::now();
        if now >= deadline {
            return None;
        }
        let (mut sleep, mut changed) = (desktop.sleep(delay.min(deadline - now)), pin!(changes.changed()));
        let woken = poll_fn(|cx| match changed.as_mut().poll(cx) {
            Poll::Ready(()) => Poll::Ready(true),
            Poll::Pending => sleep.as_mut().poll(cx).map(|()| false),
        })
        .await;
        if woken {
            desktop.sleep(settle.min(deadline.saturating_duration_since(Instant::now()))).await;
        } else {
            delay = (delay * 2).min(crate::tuning::wait::MAX_DELAY);
        }
    }
}

/// Waits until `app` (name or bundle id) runs, or with `running` false, until it quits.
pub async fn until_app(desktop: &dyn Desktop, app: Option<&str>, running: bool, timeout: i64) -> Result<String, String> {
    let condition = if running { "app_running" } else { "app_quit" };
    let app = given(app).ok_or_else(|| format!("app required for {condition}."))?;
    let timeout = crate::shell::clamp_timeout(timeout, std::env::var(crate::shell::MAX_TIMEOUT_ENV).ok().as_deref()).max(0);
    let deadline = Instant::now() + Duration::from_secs(timeout as u64);
    let mut first = true;
    let found = poll(desktop, deadline, Watch::Apps, async || {
        // As App matches it: a running app's name, else the installed app it names (a Start menu name on Windows).
        let is_running = desktop.running_apps().await.iter().any(|running| running.is(app)) || !desktop.installed_app_pids(app).await.is_empty();
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
    let found = poll(desktop, deadline, Watch::Windows(app), async || {
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


/// Waits until an element with `title` and/or `role` appears in `app` (or the app owning the menu bar); answers
/// `<role> "<title>" at [x,y]`, the centre in Click coordinates.
pub async fn until_element(desktop: &dyn Desktop, app: Option<&str>, title: Option<&str>, role: Option<&str>, timeout: i64) -> Result<String, String> {
    let (app, title, role) = (given(app), given(title), given(role));
    if title.is_none() && role.is_none() {
        return Err("title or role required for element.".to_string());
    }
    crate::input::require_accessibility(desktop)?;
    let (timeout, deadline) = deadline(timeout);
    let found = poll(desktop, deadline, Watch::Elements(app), async || {
        // Every check gets a real walk, even at or past the deadline (timeout 0).
        let walk_deadline = deadline.max(Instant::now() + crate::tuning::wait::MINIMUM_WALK);
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
