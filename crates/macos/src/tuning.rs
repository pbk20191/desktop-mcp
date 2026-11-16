//! Values found by trying them on macOS, not given by any API: timeouts for other apps, polling, run loop ticks.
//! Kept together so they can be reviewed and retuned in one place; each says where it came from.
use std::time::Duration;

/// The Accessibility messaging timeout set before reading another app's elements (the system default is about
/// 6 s). Long enough for a busy app to answer one read, short enough that a hung app costs a call half a second
/// rather than freezing it (Swift port).
pub const AX_MESSAGING_TIMEOUT: f32 = 0.5;

/// A menu bar item whose menu is built only when it opens is pressed once, then its items are looked for this
/// often, this many times (Swift port; Safari and Xcode take one or two polls).
pub const MENU_POPULATE_POLL: Duration = Duration::from_millis(50);
pub const MENU_POPULATE_POLLS: usize = 10;

/// The share of a Spotlight search's time its MDQueryExecute gets before it is stopped, leaving the rest to read
/// the first results (Swift port).
pub const SPOTLIGHT_EXECUTE_SHARE: f64 = 0.8;

/// How often a child process is looked for without waiting, in case its exit event was lost or came too early
/// (seen under load: stalls 1 to 3 per 300 servers before this backstop, none in 960 after).
pub const REAP_BACKSTOP: Duration = Duration::from_secs(1);

/// Run loop turns of the guest runtime while a request is in flight, and while a connection or session merely
/// exists (spikes in async-swift-rs; the slow tick bounds a forgotten timer to one second).
pub const FAST_TICK: f64 = 0.05;
pub const SLOW_TICK: f64 = 1.0;

/// Bytes queued for writing before a stream stops accepting more (spike: enough for an SSE burst, small enough to
/// keep a slow client's backlog bounded).
pub const WRITE_HIGH_WATER: usize = 256 * 1024;

/// How long `open -a` / `open -b` may take to launch an app (Swift port).
pub const LAUNCH_TIMEOUT: Duration = Duration::from_secs(15);
/// How long `osascript` may take to post a notification (Swift port).
pub const NOTIFY_TIMEOUT: Duration = Duration::from_secs(10);
