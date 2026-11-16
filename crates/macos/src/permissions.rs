//! The privacy grants desktop-mcp depends on. The checks never prompt; `request` does.
use desktop_mcp_core::permissions::{Automation, Permission, PermissionState};
use objc2_app_kit::NSWorkspace;
use objc2_core_foundation::{CFBoolean, CFDictionary, CFString};
use objc2_core_services::{errAEEventNotPermitted, typeWildCard, AEDeterminePermissionToAutomateTarget};
use objc2_foundation::{NSAppleEventDescriptor, NSString, NSURL};

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

/// Asks for `permission`: the system's prompt (shown once; it also lists desktop-mcp on the settings page), then
/// that page, where the grant is turned on either way.
pub fn request(permission: Permission) {
    let page = match permission {
        Permission::Accessibility => {
            let prompt = unsafe { objc2_application_services::kAXTrustedCheckOptionPrompt };
            let yes = unsafe { objc2_core_foundation::kCFBooleanTrue }.expect("a constant");
            let options = CFDictionary::<CFString, CFBoolean>::from_slices(&[prompt], &[yes]);
            unsafe { objc2_application_services::AXIsProcessTrustedWithOptions(Some(options.as_opaque())) };
            "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"
        }
        Permission::ScreenRecording => {
            objc2_core_graphics::CGRequestScreenCaptureAccess();
            "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"
        }
    };
    if let Some(url) = NSURL::URLWithString(&NSString::from_str(page)) {
        NSWorkspace::sharedWorkspace().openURL(&url);
    }
}
