//! desktop-mcp on macOS.
#![cfg(target_os = "macos")]
pub mod main_loop;
pub mod nw;
pub mod desktop;
pub mod process;
pub mod html;
pub mod clipboard;
pub mod open;
pub mod notification;
pub mod shortcuts;
pub mod signals;
pub mod tls;
pub mod fd;
pub mod plist;
