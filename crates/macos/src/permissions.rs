//! The privacy grants desktop-mcp depends on. None of these checks prompts.
use desktop_mcp_core::permissions::{Automation, PermissionState};
use objc2_core_services::{errAEEventNotPermitted, typeWildCard, AEDeterminePermissionToAutomateTarget};
use objc2_foundation::{NSAppleEventDescriptor, NSString};

/// The current state. The Apple Events check is a round trip to tccd: call it off the main thread.
pub fn check() -> PermissionState {
    PermissionState {
        accessibility: accessibility(),
        screen_recording: objc2_core_graphics::CGPreflightScreenCaptureAccess(),
        automation: automation_of_system_events(),
    }
}

/// Whether Accessibility is granted, without prompting.
pub fn accessibility() -> bool {
    unsafe { objc2_application_services::AXIsProcessTrusted() }
}

fn automation_of_system_events() -> Automation {
    let target = NSAppleEventDescriptor::descriptorWithBundleIdentifier(&NSString::from_str("com.apple.systemevents"));
    let description = target.aeDesc();
    if description.is_null() {
        return Automation::NotDetermined;
    }
    match unsafe { AEDeterminePermissionToAutomateTarget(description.cast(), typeWildCard, typeWildCard, false) } {
        0 => Automation::Granted,
        status if status == errAEEventNotPermitted => Automation::Denied,
        // Would require consent, or System Events is not running, among others.
        _ => Automation::NotDetermined,
    }
}
