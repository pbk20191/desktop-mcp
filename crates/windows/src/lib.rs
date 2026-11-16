//! desktop-mcp on Windows: the main loop, the thread pool, the transports and the tools' Windows side.
#![cfg(windows)]
pub mod clipboard;
pub mod desktop;
pub mod displays;
pub mod html;
pub mod imaging;
pub mod capture;
pub mod listener;
pub mod main_loop;
pub mod net;
pub mod notification;
pub mod ocr;
pub mod platform;
pub mod pool;
pub mod process;
pub mod screenshot_tool;
pub mod shell;
pub mod signals;
pub mod stdio;
pub mod tls;
pub mod tree;
pub mod uia;
mod tls_keys;
pub mod tls_io;
pub mod virtual_desktops;
pub mod windows_list;
pub mod tuning;
