//! Ctrl+C, Ctrl+Break and the console closing, as one future: the server then ends with status 0.
use std::sync::OnceLock;
use tokio::sync::Notify;
use windows::core::BOOL;
use windows::Win32::System::Console::SetConsoleCtrlHandler;

static TERMINATION: OnceLock<Notify> = OnceLock::new();

unsafe extern "system" fn handler(_event: u32) -> BOOL {
    // Runs on a thread the system starts; the waiter is woken on the main loop.
    TERMINATION.get_or_init(Notify::new).notify_one();
    true.into()
}

/// Completes when the console asks the process to end.
pub async fn termination() {
    let notify = TERMINATION.get_or_init(Notify::new);
    if unsafe { SetConsoleCtrlHandler(Some(handler), true) }.is_err() {
        std::future::pending::<()>().await;
    }
    notify.notified().await;
}
