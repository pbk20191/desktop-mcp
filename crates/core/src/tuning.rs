//! Values found by trying them, not given by any API or by the tool contract: pauses that let other apps keep
//! up, backoffs, and budgets. Kept together so they can be reviewed and retuned in one place; each says where it
//! came from. (The tool-contract limits, such as Scrape's 20,000 characters, stay with their tools.)
use std::time::Duration;

/// Synthesised input. From the Python original and the Swift port, where these timings were found to let apps
/// register each event (a click held shorter than this is missed by some apps).
pub mod input {
    use super::Duration;

    /// How long a mouse button or key is held down.
    pub const HOLD: Duration = Duration::from_millis(50);
    /// Between the presses of a double click.
    pub const CLICK_GAP: Duration = Duration::from_millis(30);
    /// After a press, before the pointer starts dragging.
    pub const DRAG_SETTLE: Duration = Duration::from_millis(50);
    /// A drag moves in this many steps, one per `DRAG_STEP_GAP` (half a second in all).
    pub const DRAG_STEPS: u32 = 20;
    pub const DRAG_STEP_GAP: Duration = Duration::from_millis(25);
    /// Lines per wheel notch (the original's delta).
    pub const WHEEL_LINES: i32 = 3;
    /// After moving the pointer to scroll, and between notches.
    pub const SCROLL_GAP: Duration = Duration::from_millis(50);
    /// After clicking a field, before checking it and typing: focus moves.
    pub const FOCUS_SETTLE: Duration = Duration::from_millis(100);
    /// After select-all and after delete, when clearing a field.
    pub const CLEAR_SETTLE: Duration = Duration::from_millis(50);
    /// After moving the caret to the start or end.
    pub const CARET_SETTLE: Duration = Duration::from_millis(20);
    /// After releasing the modifiers, before the first character.
    pub const RELEASE_SETTLE: Duration = Duration::from_millis(20);
    /// Around typing after switching the keyboard layout: the text system needs to see the switch.
    pub const LAYOUT_SETTLE: Duration = Duration::from_millis(50);
    /// Between typed characters, and how long a line break or tab key is held.
    pub const CHARACTER_GAP: Duration = Duration::from_millis(10);
}

/// `Wait until`: checks at once, then after `FIRST_DELAY`, doubling up to `MAX_DELAY` (Swift port).
pub mod wait {
    use super::Duration;

    pub const FIRST_DELAY: Duration = Duration::from_millis(100);
    pub const MAX_DELAY: Duration = Duration::from_secs(1);
    /// The least time one element check may walk, whatever is left of the timeout: a large app's tree takes a
    /// few hundred milliseconds to read (Swift port).
    pub const MINIMUM_WALK: Duration = Duration::from_millis(500);
    /// After the platform reports a change, before checking: notifications come in bursts (a window opening sends
    /// a handful within a millisecond; Chromium apps send a pair on every focus change). Spike, 2026-11-23.
    pub const SETTLE: Duration = Duration::from_millis(50);
    /// The same before an element check, which walks a whole tree: Finder sent 158 layout and value notifications a
    /// minute while browsing, so element checks are spaced further apart (spike, 2026-11-23). 100 ms: on Windows an
    /// element wait returned 119–145 ms after the element appeared, against 262–271 ms at 250 ms (2026-11-23).
    pub const ELEMENT_SETTLE: Duration = Duration::from_millis(100);
}

/// After asking an app to come forward, before answering: activation is asynchronous (Swift port).
pub const SWITCH_SETTLE: Duration = Duration::from_millis(200);
