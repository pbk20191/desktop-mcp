# Rust port: where the work runs (2026-11-16)

Decision: keep strategy **A** for now. This note records the options considered, so the choice can be revisited
when desktop-mcp is hosted inside an app with a UI (SwiftUI, ExtensionKit), where the main thread is the UI thread.

## Today (strategy A)

tokio never starts a thread: a `current_thread` runtime with only the timer driver, no I/O driver, no
`spawn_blocking`.

- The server is one `!Send` future on a `LocalSet`, created on the main thread. A waker signals a CFRunLoopSource
  on the main run loop (and wakes it); the source's callback polls the `LocalSet`.
- tokio's timers advance on run loop timers that call `rt.block_on(yield_now())`: every 50 ms while a request is in
  flight, every 1 s while a connection or session exists, never otherwise (`crates/macos/src/tuning.rs`).
- Network.framework and dispatch_io deliver on the main queue, into state touched from that one thread (`Rc`,
  `RefCell`, `Cell`). Dispatch sources whose handlers need nothing of the main thread (signals, the exact sleep
  timer, the process exit watcher and its backstop, the Unix socket's accept loop) deliver on a private serial
  queue, `desktop-mcp.sources`, and hand their outcome on through channels (`crates/macos/src/source.rs`).
- Per-app work inside one blocking job (the snapshot's scans) fans out with `dispatch_apply_f` and
  `DISPATCH_APPLY_AUTO` (`main_loop::concurrent_map`).
- Work goes to a libdispatch worker (`main_loop::offload`, `Desktop::background`, `mcp::off_loop`) when it
  1. crosses into another process (Accessibility, AppleEvents, Spotlight, the pasteboard): always;
  2. grows with its input (parsing, converting, encoding, image scaling);
  3. does I/O that is not already asynchronous (`getaddrinfo`, reading files).

  What stays on the main thread is light: receiving bytes, HTTP parsing of small requests, rmcp dispatch, timers
  and exit events.

## Options for freeing the main thread

The owner's aim: the main thread should only dispatch, since system frameworks need its run loop. Either way, tokio
needs a place to be woken; that place need not be the main run loop. Main-thread-only calls (NSScreen, Text Input
Sources, the NSWorkspace application list and menu bar owner) would hop to the main queue and back by a oneshot.

### B1: a dedicated server thread with its own run loop

- The structure above moves unchanged to one named OS thread (not a tokio thread): runtime and `LocalSet` created
  there, the source and tick timers on its run loop (`CFRunLoopGetCurrent`), `CFRunLoopRun`.
- Dispatch queues cannot be bound to a thread, so Network.framework, dispatch_io and process callbacks run on a
  private serial queue and forward to the server thread with `CFRunLoopPerformBlock` plus a wake-up; state stays
  single-threaded and `!Send`.
- Cost: the callback paths of `main_loop`, `nw`, `fd`, `process` and `desktop`, plus a main-thread hop for the
  main-only calls. Core barely changes: it reaches its event loop only through the `MainLoop` trait.
- Rejected for now by the owner ("not needed yet").

### B2: no thread at all, a dispatch serial queue drives tokio

- `LocalSet` cannot be used: it is bound to the thread that made it (a debug assertion, and its wake path compares
  thread ids), while a serial queue's blocks run on varying worker threads.
- A `current_thread` runtime is `Send + Sync` and may be `block_on`'d from any thread, one at a time. A "turn" is a
  block on the serial queue calling `rt.block_on(...)`; tasks are `Send` futures made with `tokio::spawn`. A waker
  schedules a turn (coalesced by an atomic flag or a `DATA_OR` dispatch source); a dispatch timer source on the
  same queue gives the 50 ms / 1 s ticks. Network.framework, dispatch_io and process callbacks target the same
  queue, so they never overlap a turn, and the queue orders memory.
- Cost: every future must be `Send`. rmcp without its `local` feature (handlers and tool futures `Send + Sync`);
  core's `Rc`/`RefCell`/`Cell` to `Arc`/`Mutex`/atomics; `Desktop` becomes `Send + Sync`; no thread-locals (the
  Window cache); `!Send` objc2 objects wrapped or fetched where used; block state through `Arc` only.
- Benefit beyond the main thread: running Rust futures as Swift Tasks through async-swift-rs needs `Send` futures
  too (the cooperative pool resumes a Task on any thread), so this conversion is work that path needs anyway.
- To check first, in a spike (in async-swift-rs, beside the tokio-guest spikes): `block_on` from different threads
  in turn (no debug assertion or thread-local trips), `tokio::spawn`ed rmcp tasks progressing under queue turns,
  and turn coalescing without lost wake-ups.

## When to revisit

- A measurement shows the main thread held by our code (a run loop observer logging stretches over 16 ms would
  show it), and offloading does not fix it.
- desktop-mcp is hosted in an app with a UI: then request handling must leave the main thread, by B2 (preferred if
  the spike holds, and on the way to async-swift-rs) or B1.
