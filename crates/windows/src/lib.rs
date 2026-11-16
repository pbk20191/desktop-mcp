//! desktop-mcp on Windows: the main loop, the thread pool, the transports and the tools' Windows side.
#![cfg(windows)]
pub mod main_loop;
pub mod pool;
pub mod stdio;
pub mod tuning;
