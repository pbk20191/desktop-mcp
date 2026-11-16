//! Desktop control: who drives the mouse and keyboard, the AI or the user. The model (`machine`, `policy`,
//! `status`) is pure; the hooks, the heartbeat and the overlay drive it.
pub mod coordinator;
pub mod filter;
pub mod machine;
pub mod monitor;
pub mod policy;
pub mod status;
