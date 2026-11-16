//! Notifications as toasts through `Windows.UI.Notifications`, under Windows PowerShell's app id: an unpackaged
//! process has none of its own, and borrowing one registers nothing.
use desktop_mcp_core::platform::ToolText;
use windows::core::HSTRING;
use windows::Data::Xml::Dom::XmlDocument;
use windows::UI::Notifications::{ToastNotification, ToastNotificationManager};

/// Windows PowerShell's AUMID, present on every Windows 11.
pub const APP_ID: &str = r"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

/// The sounds a toast can play (`ms-winsoundevent:Notification.<name>`).
pub const SOUNDS: [&str; 5] = ["Default", "IM", "Mail", "Reminder", "SMS"];

pub const DESCRIPTION: &str = "Sends a Windows toast notification with a message, title, optional subtitle, and optional sound.";

fn escape(text: &str) -> String {
    text.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;").replace('\'', "&apos;")
}

/// The toast's XML: title, the subtitle as a second line, the message, and the sound.
pub fn toast_xml(message: &str, title: &str, subtitle: Option<&str>, sound: Option<&str>) -> String {
    let mut lines = format!("<text>{}</text>", escape(title));
    if let Some(subtitle) = subtitle {
        lines += &format!("<text>{}</text>", escape(subtitle));
    }
    lines += &format!("<text>{}</text>", escape(message));
    let audio = sound.map(|sound| format!(r#"<audio src="ms-winsoundevent:Notification.{sound}"/>"#)).unwrap_or_default();
    format!(r#"<toast><visual><binding template="ToastGeneric">{lines}</binding></visual>{audio}</toast>"#)
}

pub fn notify(message: &str, title: &str, subtitle: Option<&str>, sound: Option<&str>) -> Result<(), String> {
    if let Some(sound) = sound {
        if !SOUNDS.contains(&sound) {
            return Err(format!("unknown sound '{sound}'; use one of {}", SOUNDS.join(", ")));
        }
    }
    let document = XmlDocument::new().map_err(|error| error.message())?;
    document.LoadXml(&HSTRING::from(toast_xml(message, title, subtitle, sound))).map_err(|error| error.message())?;
    let toast = ToastNotification::CreateToastNotification(&document).map_err(|error| error.message())?;
    let notifier = ToastNotificationManager::CreateToastNotifierWithId(&HSTRING::from(APP_ID)).map_err(|error| error.message())?;
    notifier.Show(&toast).map_err(|error| error.message())
}

/// Notification's Windows text: the description and the toast sounds.
pub fn text() -> ToolText {
    ToolText { description: Some(DESCRIPTION), schema_patches: vec![("/$defs/NotificationSound", serde_json::json!({"type": "string", "enum": SOUNDS}))] }
}
