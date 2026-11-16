//! The App tool: launch, switch to, resize, move and quit applications.
use crate::mcp::Desktop;
use crate::wait::{matches, RunningApp};
use rmcp::schemars;

const LOGINWINDOW: &str = "com.apple.loginwindow";

// What the App tool does. (A plain comment: a doc comment would reach the schema the model reads.)
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum Mode {
    #[default]
    Launch,
    Resize,
    Move,
    Switch,
    Quit,
    ForceQuit,
}

/// A change to the frontmost window, in points.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum FrameChange {
    Size(f64, f64),
    Position(f64, f64),
}

/// Every process matching `name` (a bundle id exactly, or a name ignoring case), except this one and loginwindow.
pub fn quit_targets(name: &str, running: &[RunningApp], own_pid: i32) -> Result<Vec<i32>, String> {
    let found: Vec<i32> = running
        .iter()
        .filter(|app| app.pid != own_pid && app.bundle_id.as_deref() != Some(LOGINWINDOW) && matches(app.name.as_deref(), app.bundle_id.as_deref(), name))
        .map(|app| app.pid)
        .collect();
    if found.is_empty() { Err(format!("Application '{name}' is not running.")) } else { Ok(found) }
}

/// The name, when given and not blank.
fn given(name: Option<&str>) -> Option<&str> {
    name.filter(|name| !name.trim().is_empty())
}

pub async fn run(desktop: &dyn Desktop, mode: Mode, name: Option<&str>, window_loc: Option<&[i64]>, window_size: Option<&[i64]>) -> Result<String, String> {
    match mode {
        Mode::Launch => {
            let Some(name) = given(name) else { return Ok("App name or bundle ID required for launch.".to_string()) };
            Ok(if desktop.launch_app(name).await { format!("Launched {name}.") } else { format!("Failed to launch {name}.") })
        }
        Mode::Switch => {
            let Some(name) = given(name) else { return Ok("App name or bundle ID required for switch.".to_string()) };
            if !desktop.activate_app(name).await {
                return Ok(format!("Application '{name}' not found."));
            }
            desktop.sleep(crate::tuning::SWITCH_SETTLE).await;
            Ok(format!("Switched to {name}."))
        }
        Mode::Resize | Mode::Move => {
            crate::input::require_accessibility(desktop)?;
            let (verb, argument, expected) = if mode == Mode::Resize { ("resize", window_size, "[width, height]") } else { ("move", window_loc, "[x, y]") };
            let field = if mode == Mode::Resize { "window_size" } else { "window_loc" };
            if !desktop.change_front_window(None).await {
                return Ok(format!("No frontmost window to {verb}."));
            }
            let Some(values) = argument else { return Ok(format!("{field} required for {verb} mode.")) };
            let [a, b] = values else { return Err(format!("{field} must be a list of exactly 2 integers {expected}")) };
            let change = if mode == Mode::Resize { FrameChange::Size(*a as f64, *b as f64) } else { FrameChange::Position(*a as f64, *b as f64) };
            desktop.change_front_window(Some(change)).await;
            Ok(if mode == Mode::Resize { "Window resized." } else { "Window moved." }.to_string())
        }
        Mode::Quit | Mode::ForceQuit => {
            let force = mode == Mode::ForceQuit;
            let mode_name = if force { "force_quit" } else { "quit" };
            let name = given(name).map(str::trim).ok_or_else(|| format!("App name or bundle ID required for {mode_name}."))?;
            let own_pid = std::process::id() as i32;
            let pids = match quit_targets(name, &desktop.running_apps().await, own_pid) {
                Ok(pids) => pids,
                Err(not_running) => {
                    // A name no running app goes by may still name an installed one (a Start menu name).
                    let installed: Vec<i32> = desktop.installed_app_pids(name).await.into_iter().filter(|pid| *pid != own_pid).collect();
                    if installed.is_empty() {
                        return Err(not_running);
                    }
                    installed
                }
            };
            if !desktop.terminate(&pids, force).await {
                return Err(if force { format!("Could not force-quit {name}.") } else { format!("Could not quit {name}.") });
            }
            Ok(if force { format!("Force-quit {name}.") } else { format!("Quit requested for {name}.") })
        }
    }
}
