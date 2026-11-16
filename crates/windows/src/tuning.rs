//! How often the main loop turns the guest runtime.
use std::time::Duration;

/// While a request is in flight.
pub const FAST_TICK: Duration = Duration::from_millis(50);
/// While a connection or session exists and nothing is in flight.
pub const SLOW_TICK: Duration = Duration::from_secs(1);
