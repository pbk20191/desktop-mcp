//! Property lists, read with CoreFoundation: whatever format the file is in (XML or binary), as launchd reads it.
use objc2_core_foundation::{CFArray, CFBoolean, CFData, CFDictionary, CFNumber, CFPropertyListCreateWithData, CFRetained, CFString, CFType};
use serde_json::Value;
use std::path::Path;

/// The dictionary property list in `path` (a Launch Agent's), as JSON (dictionaries, arrays, strings, numbers
/// and booleans; dates and data become `null`).
pub fn read(path: &Path) -> Result<Value, String> {
    let bytes = std::fs::read(path).map_err(|error| format!("cannot read {}: {error}", path.display()))?;
    parse(&bytes).filter(Value::is_object).ok_or_else(|| format!("{} is not a property list dictionary", path.display()))
}

pub fn parse(bytes: &[u8]) -> Option<Value> {
    let data = CFData::from_bytes(bytes);
    let list: CFRetained<CFType> = unsafe { CFPropertyListCreateWithData(None, Some(&data), 0, std::ptr::null_mut(), std::ptr::null_mut()) }?;
    Some(to_json(&list))
}

fn to_json(object: &CFType) -> Value {
    if let Some(string) = object.downcast_ref::<CFString>() {
        Value::String(string.to_string())
    } else if let Some(boolean) = object.downcast_ref::<CFBoolean>() {
        Value::Bool(boolean.as_bool())
    } else if let Some(number) = object.downcast_ref::<CFNumber>() {
        if number.is_float_type() {
            number.as_f64().and_then(serde_json::Number::from_f64).map_or(Value::Null, Value::Number)
        } else {
            number.as_i64().map_or(Value::Null, Value::from)
        }
    } else if let Some(array) = object.downcast_ref::<CFArray>() {
        Value::Array((0..array.count()).map(|index| unsafe { &*(array.value_at_index(index) as *const CFType) }).map(to_json).collect())
    } else if let Some(dictionary) = object.downcast_ref::<CFDictionary>() {
        let count = dictionary.count() as usize;
        let (mut keys, mut values) = (vec![std::ptr::null(); count], vec![std::ptr::null(); count]);
        unsafe { dictionary.keys_and_values(keys.as_mut_ptr(), values.as_mut_ptr()) };
        let entries = keys.into_iter().zip(values).filter_map(|(key, value)| {
            let key = unsafe { &*(key as *const CFType) }.downcast_ref::<CFString>()?.to_string();
            Some((key, to_json(unsafe { &*(value as *const CFType) })))
        });
        Value::Object(entries.collect())
    } else {
        Value::Null
    }
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
        // A bare word is a valid old-style plist (a string), but not a dictionary.
        assert_eq!(parse(b"nope"), Some(Value::String("nope".into())));
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
