//! Watching for `Wait` with UI Automation events: windows opening and closing anywhere, top-level windows renamed
//! or minimized, and the element search's windows changing structure or names, raise the wait's signal so it
//! checks again at once instead of at its next poll.
//!
//! UI Automation wants handlers added and removed off the UI thread, so both happen on the pool (in the
//! multithreaded apartment), and it calls the handlers on its own threads; the signal is thread-safe. Each watch
//! has its own automation object, so removing every handler it added is one call.
use desktop_mcp_core::wait::{Changes, Signal, Watch};
use std::sync::{Arc, Mutex};
use windows::core::{implement, Interface, Ref, Result};
use windows::Win32::Foundation::HWND;
use windows::Win32::System::Com::SAFEARRAY;
use windows::Win32::System::Variant::VARIANT;
use windows::Win32::UI::Accessibility::{
    IUIAutomation, IUIAutomationElement, IUIAutomationEventHandler, IUIAutomationEventHandler_Impl, IUIAutomationPropertyChangedEventHandler,
    IUIAutomationPropertyChangedEventHandler_Impl, IUIAutomationStructureChangedEventHandler, IUIAutomationStructureChangedEventHandler_Impl, StructureChangeType, TreeScope_Children,
    TreeScope_Subtree, UIA_EVENT_ID, UIA_NamePropertyId, UIA_PROPERTY_ID, UIA_WindowWindowVisualStatePropertyId, UIA_Window_WindowClosedEventId, UIA_Window_WindowOpenedEventId,
};

/// Raises the signal on any event it is given.
#[implement(IUIAutomationEventHandler, IUIAutomationStructureChangedEventHandler, IUIAutomationPropertyChangedEventHandler)]
struct Raise(Arc<Signal>);

impl IUIAutomationEventHandler_Impl for Raise_Impl {
    fn HandleAutomationEvent(&self, _sender: Ref<IUIAutomationElement>, _event: UIA_EVENT_ID) -> Result<()> {
        self.0.raise();
        Ok(())
    }
}

impl IUIAutomationStructureChangedEventHandler_Impl for Raise_Impl {
    fn HandleStructureChangedEvent(&self, _sender: Ref<IUIAutomationElement>, _change: StructureChangeType, _runtime_id: *const SAFEARRAY) -> Result<()> {
        self.0.raise();
        Ok(())
    }
}

impl IUIAutomationPropertyChangedEventHandler_Impl for Raise_Impl {
    fn HandlePropertyChangedEvent(&self, _sender: Ref<IUIAutomationElement>, _property: UIA_PROPERTY_ID, _value: &VARIANT) -> Result<()> {
        self.0.raise();
        Ok(())
    }
}

/// An automation object of the multithreaded apartment: only ever called on pool threads, which join it.
struct Automation(IUIAutomation);

// SAFETY: the object belongs to the multithreaded apartment and is only called from pool threads (in that
// apartment); the main thread merely hands it from the registering job to the removing one.
unsafe impl Send for Automation {}

#[derive(Default)]
struct State {
    /// Set once the handlers are added; taken to remove them.
    automation: Option<Automation>,
    /// The wait ended: a registration finishing later removes its handlers itself.
    ended: bool,
}

/// Keeps a wait's handlers; removes them, on the pool, when the wait ends.
struct Watching(Arc<Mutex<State>>);

impl Drop for Watching {
    fn drop(&mut self) {
        let mut state = self.0.lock().expect("the watch lock is never poisoned");
        state.ended = true;
        if let Some(automation) = state.automation.take() {
            drop(crate::pool::offload(move || {
                let automation = automation;
                unsafe { automation.0.RemoveAllEventHandlers() }
            }));
        }
    }
}

fn register(automation: &IUIAutomation, what: &Owned, signal: Arc<Signal>) -> Result<()> {
    // One handler object answers all three interfaces.
    let events: IUIAutomationEventHandler = Raise(signal).into();
    let root = unsafe { automation.GetRootElement() }?;
    // Opened and closed windows, wherever they appear: they change the window list, the apps running (Windows
    // lists the apps that have a window) and which windows an element search walks.
    for event in [UIA_Window_WindowOpenedEventId, UIA_Window_WindowClosedEventId] {
        unsafe { automation.AddAutomationEventHandler(event, &root, TreeScope_Subtree, None, &events) }?;
    }
    match what {
        Owned::Apps => {}
        Owned::Windows => {
            let properties: IUIAutomationPropertyChangedEventHandler = events.cast()?;
            unsafe { automation.AddPropertyChangedEventHandlerNativeArray(&root, TreeScope_Children, None, &properties, &[UIA_NamePropertyId, UIA_WindowWindowVisualStatePropertyId]) }?;
        }
        Owned::Elements(app) => {
            let structure: IUIAutomationStructureChangedEventHandler = events.cast()?;
            let properties: IUIAutomationPropertyChangedEventHandler = events.cast()?;
            for window in crate::windows_list::element_windows(app.as_deref()) {
                // A window that closed or does not answer is skipped; the polling still covers it.
                let Ok(element) = (unsafe { automation.ElementFromHandle(HWND(window.hwnd as *mut _)) }) else { continue };
                let _ = unsafe { automation.AddStructureChangedEventHandler(&element, TreeScope_Subtree, None, &structure) };
                let _ = unsafe { automation.AddPropertyChangedEventHandlerNativeArray(&element, TreeScope_Subtree, None, &properties, &[UIA_NamePropertyId]) };
            }
        }
    }
    Ok(())
}

/// What a watch covers, owned for the pool.
enum Owned {
    Apps,
    Windows,
    Elements(Option<String>),
}

/// Starts watching `what` for a wait; the watching ends when the returned `Changes` is dropped. The handlers are
/// added on the pool, so a change in the first moments of the wait may be seen by the polling only.
pub fn watch(what: Watch<'_>) -> Changes {
    // Window events are not filtered by app: they are few, and a check after one costs a window list.
    let owned = match what {
        Watch::Apps => Owned::Apps,
        Watch::Windows(_) => Owned::Windows,
        Watch::Elements(app) => Owned::Elements(app.map(str::to_string)),
    };
    let signal = Arc::new(Signal::default());
    let state = Arc::new(Mutex::new(State::default()));
    let (registering, raising) = (state.clone(), signal.clone());
    drop(crate::pool::offload(move || {
        let Some(automation) = crate::uia::automation() else { return };
        let added = register(&automation, &owned, raising);
        let mut state = registering.lock().expect("the watch lock is never poisoned");
        if state.ended || added.is_err() {
            let _ = unsafe { automation.RemoveAllEventHandlers() };
        } else {
            state.automation = Some(Automation(automation));
        }
    }));
    Changes::new(signal, Box::new(Watching(state)))
}
