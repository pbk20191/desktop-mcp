//! Virtual desktops. Windows on other desktops are cloaked, and `top_windows` already leaves cloaked windows out,
//! so only the current desktop's name and place are read here, from Explorer's registry entries (documented
//! behaviour, nothing that changes its interface IDs from one Windows build to the next).
use windows::core::PCWSTR;
use windows::Win32::System::Registry::{RegGetValueW, HKEY_CURRENT_USER, RRF_RT_REG_BINARY, RRF_RT_REG_SZ};

const KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

/// "Virtual desktop: <name> (<n> of <count>)"; unnamed desktops are "Desktop <n>". `None` when `current` is not
/// among `ids`, or there is only one desktop (nothing to tell apart).
pub fn desktop_line(ids: &[u128], names: &[(u128, String)], current: u128) -> Option<String> {
    if ids.len() < 2 {
        return None;
    }
    let position = ids.iter().position(|id| *id == current)?;
    let name = names.iter().find(|(id, _)| *id == current).map(|(_, name)| name.clone()).filter(|name| !name.is_empty()).unwrap_or_else(|| format!("Desktop {}", position + 1));
    Some(format!("Virtual desktop: {name} ({} of {})", position + 1, ids.len()))
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

fn read_binary(key: &str, value: &str) -> Option<Vec<u8>> {
    let (key, value) = (wide(key), wide(value));
    let mut size = 0u32;
    unsafe { RegGetValueW(HKEY_CURRENT_USER, PCWSTR(key.as_ptr()), PCWSTR(value.as_ptr()), RRF_RT_REG_BINARY, None, None, Some(&mut size)) }.ok().ok()?;
    let mut data = vec![0u8; size as usize];
    unsafe { RegGetValueW(HKEY_CURRENT_USER, PCWSTR(key.as_ptr()), PCWSTR(value.as_ptr()), RRF_RT_REG_BINARY, None, Some(data.as_mut_ptr() as *mut _), Some(&mut size)) }.ok().ok()?;
    data.truncate(size as usize);
    Some(data)
}

fn read_string(key: &str, value: &str) -> Option<String> {
    let (key, value) = (wide(key), wide(value));
    let mut size = 0u32;
    unsafe { RegGetValueW(HKEY_CURRENT_USER, PCWSTR(key.as_ptr()), PCWSTR(value.as_ptr()), RRF_RT_REG_SZ, None, None, Some(&mut size)) }.ok().ok()?;
    let mut data = vec![0u16; size as usize / 2];
    unsafe { RegGetValueW(HKEY_CURRENT_USER, PCWSTR(key.as_ptr()), PCWSTR(value.as_ptr()), RRF_RT_REG_SZ, None, Some(data.as_mut_ptr() as *mut _), Some(&mut size)) }.ok().ok()?;
    Some(String::from_utf16_lossy(&data).trim_end_matches('\0').to_string())
}

/// A GUID's 16 bytes as stored (little-endian fields), and its registry key name `{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}`.
fn guid_key(bytes: &[u8]) -> String {
    let data1 = u32::from_le_bytes(bytes[0..4].try_into().unwrap());
    let data2 = u16::from_le_bytes(bytes[4..6].try_into().unwrap());
    let data3 = u16::from_le_bytes(bytes[6..8].try_into().unwrap());
    let tail: String = bytes[10..16].iter().map(|byte| format!("{byte:02X}")).collect();
    format!("{{{data1:08X}-{data2:04X}-{data3:04X}-{:02X}{:02X}-{tail}}}", bytes[8], bytes[9])
}

/// The snapshot's line for the current virtual desktop; `None` when Explorer's entries cannot be read.
pub fn current_desktop_line() -> Option<String> {
    let ids_bytes = read_binary(KEY, "VirtualDesktopIDs")?;
    let current = read_binary(KEY, "CurrentVirtualDesktop").filter(|bytes| bytes.len() == 16)?;
    let as_id = |bytes: &[u8]| u128::from_le_bytes(bytes.try_into().unwrap());
    let ids: Vec<u128> = ids_bytes.chunks_exact(16).map(as_id).collect();
    let names: Vec<(u128, String)> = ids_bytes
        .chunks_exact(16)
        .filter_map(|bytes| read_string(&format!(r"{KEY}\Desktops\{}", guid_key(bytes)), "Name").map(|name| (as_id(bytes), name)))
        .collect();
    desktop_line(&ids, &names, as_id(&current))
}
