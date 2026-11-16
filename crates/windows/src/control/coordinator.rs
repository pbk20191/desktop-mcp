//! The heartbeat: every 50 ms on a thread-pool timer it drains what the hooks saw into the state machine, ticks it,
//! checks the input desktop, and — only while the machine wants it — moves the hooks' deadline 1 s ahead. Nothing
//! else renews the deadline, so if the heartbeat stops (a panic, a stuck pool, a held lock), input passes again
//! within a second.
use super::machine::{Code, Lease, Machine, Physical, State};
use super::monitor::{self, Seen};
use super::policy::Policy;
use super::status::Status;
use std::ffi::c_void;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::Receiver;
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant};
use windows::Win32::Foundation::FILETIME;
use windows::Win32::System::StationsAndDesktops::{CloseDesktop, GetUserObjectInformationW, OpenInputDesktop, DESKTOP_ACCESS_FLAGS, DESKTOP_CONTROL_FLAGS, UOI_NAME};
use windows::Win32::System::Threading::{CreateThreadpoolTimer, SetThreadpoolTimer, PTP_CALLBACK_INSTANCE, PTP_TIMER};

const BEAT: Duration = Duration::from_millis(50);
/// How far ahead each beat moves the deadline.
const SUPPRESSION: u64 = 1000;
/// Events drained per beat.
const DRAIN: usize = 128;
/// A pause in a mouse's motion that restarts its takeover distance.
const MOTION_PAUSE: Duration = Duration::from_millis(500);

struct Motion {
    device: isize,
    units: f64,
    last: Instant,
}

struct Inner {
    machine: Machine,
    receiver: Receiver<Seen>,
    motion: Option<Motion>,
    beats: u64,
    published: u64,
    /// The running call's generation: input posted under another generation is dropped.
    call: Option<u64>,
}

pub struct Coordinator {
    inner: Mutex<Inner>,
    takeover_units: f64,
    steps: AtomicU64,
    changes: tokio::sync::watch::Sender<Status>,
}

/// One gated call's hold on control.
pub struct CallLease {
    lease: Lease,
}

/// The process's coordinator, once control has started.
static ACTIVE: OnceLock<Arc<Coordinator>> = OnceLock::new();

pub fn active() -> Option<&'static Arc<Coordinator>> {
    ACTIVE.get()
}

fn input_desktop_is_ours() -> bool {
    let Ok(desktop) = (unsafe { OpenInputDesktop(DESKTOP_CONTROL_FLAGS(0), false, DESKTOP_ACCESS_FLAGS(0x0001)) }) else { return false };
    let mut name = [0u16; 64];
    let mut needed = 0u32;
    let read = unsafe { GetUserObjectInformationW(windows::Win32::Foundation::HANDLE(desktop.0), UOI_NAME, Some(name.as_mut_ptr() as *mut c_void), (name.len() * 2) as u32, Some(&mut needed)) };
    unsafe {
        let _ = CloseDesktop(desktop);
    }
    let length = name.iter().position(|unit| *unit == 0).unwrap_or(name.len());
    read.is_ok() && String::from_utf16_lossy(&name[..length]).eq_ignore_ascii_case("Default")
}

impl Coordinator {
    pub fn new(policy: Policy, idle_timeout: Duration, takeover_units: f64, receiver: Receiver<Seen>) -> Arc<Coordinator> {
        let start = Instant::now();
        let machine = Machine::new(policy, idle_timeout, Box::new(move || start.elapsed()));
        let status = machine.status();
        Arc::new(Coordinator {
            inner: Mutex::new(Inner { machine, receiver, motion: None, beats: 0, published: 0, call: None }),
            takeover_units,
            steps: AtomicU64::new(0),
            changes: tokio::sync::watch::channel(status).0,
        })
    }

    /// Makes this the process's coordinator and starts its heartbeat on the thread pool.
    pub fn start(self: &Arc<Coordinator>) {
        let _ = ACTIVE.set(self.clone());
        unsafe extern "system" fn fired(_instance: PTP_CALLBACK_INSTANCE, context: *mut c_void, _timer: PTP_TIMER) {
            let coordinator = unsafe { &*(context as *const Coordinator) };
            // A panic must not cross into the pool; the deadline simply lapses.
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| coordinator.beat()));
        }
        // The context lives as long as the process: the coordinator is never stopped.
        let context = Arc::into_raw(self.clone()) as *mut c_void;
        if let Ok(timer) = unsafe { CreateThreadpoolTimer(Some(fired), Some(context), None) } {
            let due = -((BEAT.as_nanos() / 100) as i64);
            let due = FILETIME { dwLowDateTime: due as u32, dwHighDateTime: (due >> 32) as u32 };
            unsafe { SetThreadpoolTimer(timer, Some(&due), BEAT.as_millis() as u32, None) };
        }
    }

    /// One heartbeat. Skipped (no renewal) when the state is busy elsewhere.
    pub fn beat(&self) {
        let Ok(mut inner) = self.inner.try_lock() else { return };
        let inner = &mut *inner;
        let was_ai = inner.machine.state() == State::Ai;
        for _ in 0..DRAIN {
            let Ok(seen) = inner.receiver.try_recv() else { break };
            match seen {
                Seen::Input(physical) => inner.machine.physical(physical),
                Seen::Takeover => inner.machine.takeover(),
                Seen::Motion { device, dx, dy } => {
                    let now = Instant::now();
                    let motion = inner.motion.get_or_insert(Motion { device, units: 0.0, last: now });
                    if motion.device != device || now - motion.last > MOTION_PAUSE {
                        *motion = Motion { device, units: 0.0, last: now };
                    }
                    motion.units += ((dx as f64).powi(2) + (dy as f64).powi(2)).sqrt();
                    motion.last = now;
                    if motion.units >= self.takeover_units && inner.machine.wants_suppression() {
                        motion.units = 0.0;
                        inner.machine.takeover();
                    }
                    inner.machine.physical(Physical::Activity);
                }
            }
        }
        if monitor::take_fail_open() {
            inner.machine.fault();
        }
        inner.beats += 1;
        if inner.beats % 10 == 0 {
            if input_desktop_is_ours() { inner.machine.desktop_back() } else { inner.machine.desktop_lost() }
        }
        inner.machine.tick();
        monitor::set_deadline(if inner.machine.wants_suppression() { monitor::now_ms() + SUPPRESSION } else { 0 });
        if was_ai && inner.machine.state() != State::Ai {
            // Whatever the AI was holding goes up when it loses control.
            crate::input::send(&crate::input::release_modifiers());
        }
        if inner.machine.generation() != inner.published {
            inner.published = inner.machine.generation();
            let _ = self.changes.send(inner.machine.status());
        }
    }

    /// A gated call starts.
    pub fn begin(&self, _tool: &str) -> Result<CallLease, (Code, Status)> {
        let mut inner = self.inner.lock().unwrap();
        match inner.machine.begin_call() {
            Ok(lease) => {
                inner.call = Some(inner.machine.generation());
                self.steps.store(0, Ordering::Relaxed);
                Ok(CallLease { lease })
            }
            Err(code) => Err((code, inner.machine.status())),
        }
    }

    /// The indicator is on screen: suppression may start.
    pub fn overlay_shown(&self) {
        self.inner.lock().unwrap().machine.overlay_shown();
    }

    /// The indicator could not be shown.
    pub fn fault(&self) {
        self.inner.lock().unwrap().machine.fault();
    }

    pub fn end(&self, lease: CallLease) {
        let mut inner = self.inner.lock().unwrap();
        inner.call = None;
        inner.machine.end_call(lease.lease);
    }

    pub fn preempted(&self, lease: &CallLease) -> bool {
        self.inner.lock().unwrap().machine.preempted(&lease.lease)
    }

    /// Whether input may be posted now: not while a running call has lost control.
    pub fn allow_input(&self) -> bool {
        let inner = self.inner.lock().unwrap();
        match inner.call {
            Some(generation) => inner.machine.state() == State::Ai && inner.machine.generation() == generation,
            None => true,
        }
    }

    /// One more input event posted by the running call.
    pub fn count_step(&self) {
        self.steps.fetch_add(1, Ordering::Relaxed);
    }

    pub fn executed_steps(&self) -> u64 {
        self.steps.load(Ordering::Relaxed)
    }

    pub fn state(&self) -> State {
        self.inner.lock().unwrap().machine.state()
    }

    pub fn status(&self) -> Status {
        self.inner.lock().unwrap().machine.status()
    }

    /// The hooks' current deadline (milliseconds since boot; 0 when not suppressing).
    pub fn deadline(&self) -> u64 {
        monitor::deadline()
    }

    /// Status changes, for the notifier.
    pub fn subscribe(&self) -> tokio::sync::watch::Receiver<Status> {
        self.changes.subscribe()
    }
}
