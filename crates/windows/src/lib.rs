//! desktop-mcp on Windows: the main loop, the thread pool, the transports and the tools' Windows side.
#![cfg(windows)]
pub mod clipboard;
pub mod desktop;
pub mod html;
pub mod main_loop;
pub mod net;
pub mod notification;
pub mod platform;
pub mod pool;
pub mod process;
pub mod shell;
pub mod signals;
pub mod sta;
pub mod stdio;
pub mod tuning;
