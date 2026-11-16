//! The control state machine (upstream `desktop/control.py`, by way of the C# port's P4). Pure: the clock is
//! passed in, and the hooks, heartbeat and overlay report to it.
//!
//! `ready`: the AI may act. A gated call moves it to `ai`; the indicator shows, and only once it is shown does
//! input get suppressed. The user takes over by moving the mouse far enough or with the chord (`user`); a key or
//! button pressed in `ready` hands control to the user too. After the idle timeout without input, `user` returns
//! to `ready`; `ai` returns to `ready` when its last call ended 15 s ago. Losing the input desktop (Win+L, UAC) or a
//! fault makes it `unavailable`, and coming back starts in `user`.
use super::policy::Policy;
use super::status::Status;
use std::collections::HashSet;
use std::time::Duration;

/// How long `ai` lasts after its last call, so a sequence of calls is not interrupted between them.
pub const LEASE: Duration = Duration::from_secs(15);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum State {
    Disabled,
    Unavailable,
    User,
    Ready,
    Ai,
}

impl State {
    pub fn name(self) -> &'static str {
        match self {
            State::Disabled => "disabled",
            State::Unavailable => "unavailable",
            State::User => "user",
            State::Ready => "ready",
            State::Ai => "ai",
        }
    }
}

/// Why a call cannot run (or stopped).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Code {
    UserControl,
    ControlUnavailable,
    ControlPreempted,
    PhysicalInputHeld,
}

/// Physical input the hooks saw (a virtual key or a mouse button as a negative number).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Physical {
    Down(i32),
    Up(i32),
    /// Movement or wheel: counts as activity, not as a takeover by itself.
    Activity,
}

/// One gated call's hold on `ai`.
#[derive(Debug)]
pub struct Lease {
    generation: u64,
}

pub struct Machine {
    clock: Box<dyn Fn() -> Duration>,
    policy: Policy,
    idle_timeout: Duration,
    state: State,
    generation: u64,
    calls: usize,
    lease_until: Duration,
    last_physical: Duration,
    held: HashSet<i32>,
    /// Keys the system reports down although no down was seen (sampled while idle).
    held_elsewhere: bool,
    /// In `ai`: the indicator is shown and input may be suppressed.
    armed: bool,
}

impl Machine {
    pub fn new(policy: Policy, idle_timeout: Duration, clock: Box<dyn Fn() -> Duration>) -> Machine {
        let now = clock();
        Machine { clock, policy, idle_timeout, state: State::Ready, generation: 0, calls: 0, lease_until: now, last_physical: now, held: HashSet::new(), held_elsewhere: false, armed: false }
    }

    /// A machine for a server with control turned off.
    pub fn disabled(policy: Policy, idle_timeout: Duration, clock: Box<dyn Fn() -> Duration>) -> Machine {
        Machine { state: State::Disabled, ..Machine::new(policy, idle_timeout, clock) }
    }

    pub fn state(&self) -> State {
        self.state
    }

    pub fn generation(&self) -> u64 {
        self.generation
    }

    fn set(&mut self, state: State) {
        if self.state != state {
            self.state = state;
            self.generation += 1;
            self.armed = false;
        }
    }

    /// A gated call starts: `ai`, or why not.
    pub fn begin_call(&mut self) -> Result<Lease, Code> {
        match self.state {
            State::Disabled => return Ok(Lease { generation: self.generation }),
            State::Unavailable => return Err(Code::ControlUnavailable),
            State::User => return Err(Code::UserControl),
            State::Ready | State::Ai => {}
        }
        if !self.held.is_empty() || self.held_elsewhere {
            return Err(Code::PhysicalInputHeld);
        }
        self.set(State::Ai);
        self.calls += 1;
        Ok(Lease { generation: self.generation })
    }

    /// A gated call ended (or was cancelled); `ai` lasts [`LEASE`] longer.
    pub fn end_call(&mut self, _lease: Lease) {
        self.calls = self.calls.saturating_sub(1);
        if self.calls == 0 {
            self.lease_until = (self.clock)() + LEASE;
        }
    }

    /// Whether the call holding `lease` lost control since it began.
    pub fn preempted(&self, lease: &Lease) -> bool {
        self.state != State::Disabled && (self.state != State::Ai || self.generation != lease.generation)
    }

    /// Time passes: `ai` and `user` return to `ready` when their time is up.
    pub fn tick(&mut self) {
        let now = (self.clock)();
        match self.state {
            State::Ai if self.calls == 0 && now >= self.lease_until => self.set(State::Ready),
            State::User if self.held.is_empty() && !self.held_elsewhere && now >= self.last_physical + self.idle_timeout => self.set(State::Ready),
            _ => {}
        }
    }

    /// Physical input reached the hooks.
    pub fn physical(&mut self, event: Physical) {
        self.last_physical = (self.clock)();
        match event {
            Physical::Down(key) => {
                self.held.insert(key);
                match self.state {
                    State::Ready => self.set(State::User),
                    // Before the indicator is shown, input is not suppressed: the user is acting, so they get control.
                    State::Ai if !self.armed => self.set(State::User),
                    _ => {}
                }
            }
            Physical::Up(key) => {
                self.held.remove(&key);
            }
            Physical::Activity => {}
        }
    }

    /// What the system reports about keys whose release never arrived (after five all-up samples: none).
    pub fn physical_held(&mut self, held: bool) {
        self.held_elsewhere = held;
        if !held {
            self.held.clear();
        }
    }

    /// The chord or a long enough mouse movement.
    pub fn takeover(&mut self) {
        self.last_physical = (self.clock)();
        if matches!(self.state, State::Ai | State::Ready) {
            self.set(State::User);
        }
    }

    /// The indicator is on screen: input may now be suppressed.
    pub fn overlay_shown(&mut self) {
        if self.state == State::Ai {
            self.armed = true;
        }
    }

    /// The hooks failed, the indicator could not show, or the queue overflowed.
    pub fn fault(&mut self) {
        if self.state != State::Disabled {
            self.set(State::Unavailable);
        }
    }

    /// The input desktop is not ours (locked, a UAC prompt, another session).
    pub fn desktop_lost(&mut self) {
        self.fault();
    }

    /// Back on the input desktop: the user is there, so they have control first.
    pub fn desktop_back(&mut self) {
        if self.state == State::Unavailable {
            self.last_physical = (self.clock)();
            self.set(State::User);
        }
    }

    /// Whether the hooks should swallow physical input now.
    pub fn wants_suppression(&self) -> bool {
        self.state == State::Ai && self.armed
    }

    pub fn status(&self) -> Status {
        let now = (self.clock)();
        let wait = match self.state {
            State::User if self.held.is_empty() && !self.held_elsewhere => (self.last_physical + self.idle_timeout).saturating_sub(now),
            State::User => self.idle_timeout,
            _ => Duration::ZERO,
        };
        Status {
            state: self.state.name(),
            generation: self.generation,
            wait_seconds: (wait.as_secs_f64() * 1000.0).round() / 1000.0,
            enabled: self.state != State::Disabled,
            policy: self.policy.name(),
            idle_timeout_seconds: self.idle_timeout.as_secs_f64(),
        }
    }
}
