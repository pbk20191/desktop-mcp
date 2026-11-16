//! Property lists, read with CoreFoundation (XML or binary, as launchd reads them) and handed on as JSON by
//! `NSJSONSerialization`: a property list's objects are toll-free bridged Foundation objects.
use objc2::runtime::AnyObject;
use objc2_core_foundation::{CFData, CFPropertyListCreateWithData, CFRetained, CFType};
use objc2_foundation::{NSJSONSerialization, NSJSONWritingOptions};
use serde_json::Value;
use std::path::Path;

/// The dictionary property list in `path` (a Launch Agent's), as JSON.
pub fn read(path: &Path) -> Result<Value, String> {
    let bytes = std::fs::read(path).map_err(|error| format!("cannot read {}: {error}", path.display()))?;
    parse(&bytes).filter(Value::is_object).ok_or_else(|| format!("{} is not a property list dictionary", path.display()))
}

/// A property list whose top level is a dictionary or an array, as JSON; `None` for anything else, or one
/// holding what JSON cannot (dates, data).
pub fn parse(bytes: &[u8]) -> Option<Value> {
    let data = CFData::from_bytes(bytes);
    let list: CFRetained<CFType> = unsafe { CFPropertyListCreateWithData(None, Some(&data), 0, std::ptr::null_mut(), std::ptr::null_mut()) }?;
    to_json(&list)
}

/// A property-list object (a dictionary or an array of strings, numbers and booleans) as JSON; `None` when it
/// holds what JSON cannot (dates, data).
pub fn to_json(list: &CFType) -> Option<Value> {
    // SAFETY: every Core Foundation property list object is a toll-free bridged Objective-C object.
    let object = unsafe { &*(list as *const CFType as *const AnyObject) };
    if !unsafe { NSJSONSerialization::isValidJSONObject(object) } {
        return None;
    }
    let json = unsafe { NSJSONSerialization::dataWithJSONObject_options_error(object, NSJSONWritingOptions::empty()) }.ok()?;
    serde_json::from_slice(&json.to_vec()).ok()
}

/// The `ProgramArguments` of a Launch Agent plist; empty when it has none.
pub fn program_arguments(plist: &Value) -> Vec<String> {
    plist["ProgramArguments"].as_array().map(|arguments| arguments.iter().filter_map(|argument| argument.as_str().map(str::to_string)).collect()).unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;
    use desktop_mcp_core::launch_agent::{plist_xml, program_arguments as arguments_for, LABEL};

    #[test]
    fn the_agents_own_plist_reads_back() {
        let arguments = arguments_for("/path with space/desktop-mcp", "streamable-http", "127.0.0.1", 8123, Some("/s & t.sock"));
        let plist = parse(plist_xml(&arguments, Path::new("/logs"), Some("/s & t.sock")).as_bytes()).expect("a valid plist");
        assert_eq!(plist["Label"], LABEL);
        assert_eq!(program_arguments(&plist), arguments);
        assert_eq!(plist["RunAtLoad"], true);
        assert_eq!(plist["KeepAlive"], true);
        assert_eq!(plist["StandardOutPath"], "/logs/server.log");
        assert_eq!(plist["Sockets"]["Listener"]["SockPathName"], "/s & t.sock");
        assert_eq!(plist["Sockets"]["Listener"]["SockPathMode"], 0o600);
    }

    #[test]
    fn something_else_is_not_an_agent_plist() {
        // A bare word is a valid old-style plist, but a string: neither a dictionary nor JSON's top level.
        assert_eq!(parse(b"nope"), None);
        let dated = b"<plist version=\"1.0\"><dict><key>When</key><date>2026-11-15T00:00:00Z</date></dict></plist>";
        assert_eq!(parse(dated), None, "a date has no JSON form");
        assert_eq!(parse(b"<plist><dict>"), None);
        let directory = std::env::temp_dir().join(format!("dmcp-plist-{}", std::process::id()));
        std::fs::create_dir_all(&directory).unwrap();
        std::fs::write(directory.join("word.plist"), "nope").unwrap();
        assert!(read(&directory.join("word.plist")).unwrap_err().contains("not a property list dictionary"));
        assert!(read(&directory.join("missing.plist")).unwrap_err().contains("cannot read"));
        assert!(program_arguments(&serde_json::json!({})).is_empty());
        let _ = std::fs::remove_dir_all(directory);
    }
}
