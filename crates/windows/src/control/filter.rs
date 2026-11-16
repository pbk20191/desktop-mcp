//! What the low-level hooks do with each event, without the hooks: pass or swallow it, report it as the user's
//! input, notice the takeover chord or a long foreign pointer path. Fixed-size state only: the hooks call this
//! and must not allocate.
use super::machine::Physical;

/// `dwExtraInfo` of every event this server injects; such events are never the user's.
pub const SIGNATURE: usize = 0x444D_4350;

const VK_BACK: u32 = 0x08;
const MODIFIERS: [(u32, [u32; 3]); 3] = [(0x11, [0x11, 0xA2, 0xA3]), (0x12, [0x12, 0xA4, 0xA5]), (0x10, [0x10, 0xA0, 0xA1])];

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Kind {
    KeyDown(u32),
    KeyUp(u32),
    /// 1 left, 2 right, 3 middle, 4 and 5 the side buttons.
    ButtonDown(u8),
    ButtonUp(u8),
    Move { x: i32, y: i32 },
    Wheel,
}

#[derive(Clone, Copy, Debug)]
pub struct HookEvent {
    pub kind: Kind,
    /// The system's injected flag (any injected or lower-integrity-injected input).
    pub injected: bool,
    pub extra: usize,
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Verdict {
    pub swallow: bool,
    /// The user's input, for the state machine.
    pub report: Option<Physical>,
    /// The chord was pressed, or a foreign pointer travelled far enough.
    pub takeover: bool,
}

pub struct Filter {
    down: [bool; 256],
    swallowed: [bool; 256],
    buttons_swallowed: [bool; 8],
    /// The chord fired and its keys are swallowed until all are released.
    chord: bool,
    takeover_pixels: f64,
    path: f64,
    last_point: Option<(i32, i32)>,
    last_move: u64,
}

/// How long a pause in a foreign pointer path resets it, in the same ticks as `now` (milliseconds).
const PATH_PAUSE: u64 = 500;

impl Filter {
    pub fn new(takeover_pixels: f64) -> Filter {
        Filter { down: [false; 256], swallowed: [false; 256], buttons_swallowed: [false; 8], chord: false, takeover_pixels, path: 0.0, last_point: None, last_move: 0 }
    }

    fn held(&self, aliases: [u32; 3]) -> bool {
        aliases.iter().any(|vk| self.down[*vk as usize & 0xFF])
    }

    fn chord_keys_down(&self) -> bool {
        self.down[VK_BACK as usize] || MODIFIERS.iter().any(|(_, aliases)| self.held(*aliases))
    }

    /// `now` and `deadline` in milliseconds; input is swallowed while `now < deadline`.
    pub fn decide(&mut self, event: HookEvent, now: u64, deadline: u64) -> Verdict {
        if event.injected && event.extra == SIGNATURE {
            return Verdict::default();
        }
        let suppress = now < deadline;
        match event.kind {
            Kind::KeyDown(vk) => {
                let index = vk as usize & 0xFF;
                let repeat = self.down[index];
                self.down[index] = true;
                let mut verdict = Verdict { swallow: suppress, report: Some(Physical::Down(vk as i32)), takeover: false };
                if vk == VK_BACK && !repeat && !self.chord && MODIFIERS.iter().all(|(_, aliases)| self.held(*aliases)) {
                    self.chord = true;
                    verdict.takeover = true;
                }
                if self.chord && (vk == VK_BACK || MODIFIERS.iter().any(|(_, aliases)| aliases.contains(&vk))) {
                    verdict.swallow = true;
                }
                if verdict.swallow {
                    self.swallowed[index] = true;
                }
                verdict
            }
            Kind::KeyUp(vk) => {
                let index = vk as usize & 0xFF;
                self.down[index] = false;
                // A release follows its press: swallowed with it, passed with it, whatever the deadline says now.
                let swallow = std::mem::take(&mut self.swallowed[index]);
                if self.chord && !self.chord_keys_down() {
                    self.chord = false;
                }
                Verdict { swallow, report: Some(Physical::Up(vk as i32)), takeover: false }
            }
            Kind::ButtonDown(button) => {
                let index = button as usize & 7;
                self.buttons_swallowed[index] = suppress;
                Verdict { swallow: suppress, report: Some(Physical::Down(-(button as i32))), takeover: false }
            }
            Kind::ButtonUp(button) => {
                let swallow = std::mem::take(&mut self.buttons_swallowed[button as usize & 7]);
                Verdict { swallow, report: Some(Physical::Up(-(button as i32))), takeover: false }
            }
            Kind::Move { x, y } => {
                // Physical motion is measured from Raw Input; a foreign (remote) pointer is measured here, in pixels.
                let mut takeover = false;
                if event.injected {
                    if now.saturating_sub(self.last_move) > PATH_PAUSE {
                        self.path = 0.0;
                        self.last_point = None;
                    }
                    if let Some((last_x, last_y)) = self.last_point {
                        self.path += (((x - last_x) as f64).powi(2) + ((y - last_y) as f64).powi(2)).sqrt();
                    }
                    self.last_point = Some((x, y));
                    self.last_move = now;
                    if suppress && self.path >= self.takeover_pixels {
                        self.path = 0.0;
                        takeover = true;
                    }
                }
                Verdict { swallow: suppress, report: Some(Physical::Activity), takeover }
            }
            Kind::Wheel => Verdict { swallow: suppress, report: Some(Physical::Activity), takeover: false },
        }
    }
}
