//! Watching for `Wait`: an AXObserver on each app the wait reads, and NSWorkspace's launch, quit and activation
//! notifications, raise the wait's signal so it checks again at once instead of at its next poll. Everything here
//! runs on the main thread: the observers' run loop sources are on the main run loop, and NSWorkspace posts there.
use desktop_mcp_core::wait::{Changes, Signal, Watch};
use objc2::rc::Retained;
use objc2::runtime::{AnyObject, ProtocolObject};
use objc2_app_kit::{NSWorkspace, NSWorkspaceDidActivateApplicationNotification, NSWorkspaceDidLaunchApplicationNotification, NSWorkspaceDidTerminateApplicationNotification};
use objc2_application_services::{AXError, AXObserver, AXUIElement};
use objc2_core_foundation::{kCFRunLoopCommonModes, CFRetained, CFRunLoop, CFRunLoopSource, CFString};
use objc2_foundation::{NSNotification, NSObjectProtocol};
use std::cell::RefCell;
use std::collections::hash_map::Entry;
use std::collections::HashMap;
use std::ffi::c_void;
use std::ptr::NonNull;
use std::rc::{Rc, Weak};
use std::sync::Arc;

/// What window waits listen for. The element of a closed window answers nothing, so a destroyed element only
/// says "look again".
const WINDOW_NOTIFICATIONS: &[&str] = &["AXWindowCreated", "AXUIElementDestroyed", "AXTitleChanged", "AXMainWindowChanged", "AXFocusedWindowChanged", "AXWindowMiniaturized", "AXWindowDeminiaturized"];
/// What element waits listen for. Not AXValueChanged: a terminal or Finder sends it several times a second
/// (spike, 2026-11-23), and each would cost a walk of the whole tree.
const ELEMENT_NOTIFICATIONS: &[&str] = &["AXCreated", "AXUIElementDestroyed", "AXLayoutChanged", "AXTitleChanged", "AXWindowCreated", "AXMainWindowChanged", "AXFocusedWindowChanged"];

/// Which apps get an AXObserver.
enum Apps {
    None,
    Every,
    Named(String),
    MenuBarOwner,
}

struct Observed {
    /// Kept alive while its source is on the run loop.
    _observer: CFRetained<AXObserver>,
    source: CFRetained<CFRunLoopSource>,
}

struct Watching {
    signal: Arc<Signal>,
    apps: Apps,
    notifications: &'static [&'static str],
    observed: RefCell<HashMap<i32, Observed>>,
    workspace: RefCell<Vec<Retained<ProtocolObject<dyn NSObjectProtocol>>>>,
}

unsafe extern "C-unwind" fn raised(_: NonNull<AXObserver>, _: NonNull<AXUIElement>, _: NonNull<CFString>, signal: *mut c_void) {
    // `signal` is the `Signal` the `Watching` holding this observer keeps alive; the observer is removed first.
    unsafe { &*(signal as *const Signal) }.raise();
}

impl Watching {
    fn wanted(&self) -> Vec<i32> {
        match &self.apps {
            Apps::None => vec![],
            Apps::Every => crate::open::running_apps().into_iter().map(|app| app.pid).collect(),
            Apps::Named(app) => crate::open::running_apps().into_iter().filter(|running| running.is(app)).map(|running| running.pid).collect(),
            Apps::MenuBarOwner => crate::menu::menu_bar_owner().into_iter().collect(),
        }
    }

    /// Observes the apps now wanted and drops the rest; whether that changed anything.
    fn update(&self) -> bool {
        let wanted = self.wanted();
        let mut observed = self.observed.borrow_mut();
        let before = observed.len();
        observed.retain(|pid, observed| {
            let keep = wanted.contains(pid);
            if !keep {
                remove(observed);
            }
            keep
        });
        let mut changed = observed.len() != before;
        for pid in wanted {
            if let Entry::Vacant(slot) = observed.entry(pid) {
                // An app still starting up accepts nothing yet; the next workspace notification tries again.
                if let Some(added) = self.observe(pid) {
                    slot.insert(added);
                    changed = true;
                }
            }
        }
        changed
    }

    fn observe(&self, pid: i32) -> Option<Observed> {
        let mut raw: *mut AXObserver = std::ptr::null_mut();
        if unsafe { AXObserver::create(pid, Some(raised), NonNull::from(&mut raw)) } != AXError::Success || raw.is_null() {
            return None;
        }
        let observer = unsafe { CFRetained::from_raw(NonNull::new_unchecked(raw)) };
        let app = unsafe { AXUIElement::new_application(pid) };
        // Registering messages the app: a hung one costs this much once, not the system's 6 s per notification.
        unsafe { app.set_messaging_timeout(crate::tuning::AX_MESSAGING_TIMEOUT) };
        let refcon = Arc::as_ptr(&self.signal) as *mut c_void;
        let mut any = false;
        for notification in self.notifications {
            match unsafe { observer.add_notification(&app, &CFString::from_static_str(notification), refcon) } {
                AXError::Success | AXError::NotificationAlreadyRegistered => any = true,
                AXError::CannotComplete => break,
                _ => {}
            }
        }
        if !any {
            return None;
        }
        let source = unsafe { observer.run_loop_source() };
        CFRunLoop::main()?.add_source(Some(&source), unsafe { kCFRunLoopCommonModes });
        Some(Observed { _observer: observer, source })
    }

    fn listen(watching: &Rc<Watching>, names: &[&objc2_foundation::NSNotificationName], always_raise: bool) {
        let center = NSWorkspace::sharedWorkspace().notificationCenter();
        for name in names {
            let weak: Weak<Watching> = Rc::downgrade(watching);
            let block = block2::RcBlock::new(move |_: NonNull<NSNotification>| {
                if let Some(watching) = weak.upgrade() {
                    if watching.update() || always_raise {
                        watching.signal.raise();
                    }
                }
            });
            // No queue: NSWorkspace posts on the main thread, where the block then runs.
            let token = unsafe { center.addObserverForName_object_queue_usingBlock(Some(name), None, None, &block) };
            watching.workspace.borrow_mut().push(token);
        }
    }
}

fn remove(observed: &Observed) {
    if let Some(main) = CFRunLoop::main() {
        main.remove_source(Some(&observed.source), unsafe { kCFRunLoopCommonModes });
    }
}

impl Drop for Watching {
    fn drop(&mut self) {
        let center = NSWorkspace::sharedWorkspace().notificationCenter();
        for token in self.workspace.take() {
            let object: &AnyObject = (*token).as_ref();
            unsafe { center.removeObserver(object) };
        }
        for observed in self.observed.take().values() {
            remove(observed);
        }
    }
}

/// Starts watching `what` for a wait; the watching ends when the returned `Changes` is dropped.
pub fn watch(what: Watch<'_>) -> Changes {
    let (apps, notifications) = match what {
        Watch::Apps => (Apps::None, &[][..]),
        Watch::Windows(None) => (Apps::Every, WINDOW_NOTIFICATIONS),
        Watch::Windows(Some(app)) => (Apps::Named(app.to_string()), WINDOW_NOTIFICATIONS),
        Watch::Elements(None) => (Apps::MenuBarOwner, ELEMENT_NOTIFICATIONS),
        Watch::Elements(Some(app)) => (Apps::Named(app.to_string()), ELEMENT_NOTIFICATIONS),
    };
    let menu_bar = matches!(apps, Apps::MenuBarOwner);
    let signal = Arc::new(Signal::default());
    let watching = Rc::new(Watching { signal: signal.clone(), apps, notifications, observed: RefCell::default(), workspace: RefCell::default() });
    if crate::ax::trusted() {
        watching.update();
    }
    let (launch, quit, activate) = unsafe { (NSWorkspaceDidLaunchApplicationNotification, NSWorkspaceDidTerminateApplicationNotification, NSWorkspaceDidActivateApplicationNotification) };
    // An app wait cares about every launch and quit; the others about the apps they observe (and, for the menu
    // bar's owner, about which app that is).
    Watching::listen(&watching, &[launch, quit], matches!(what, Watch::Apps));
    if menu_bar {
        Watching::listen(&watching, &[activate], false);
    }
    Changes::new(signal, Box::new(watching))
}
