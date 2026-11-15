//! Notification banners, posted by `osascript` (the server is not an app bundle, so it cannot post through
//! UserNotifications itself).
use std::time::Duration;

/// The `display notification` command. A JSON string literal is also a valid AppleScript string literal.
pub fn script(message: &str, title: &str, subtitle: Option<&str>, sound: Option<&str>) -> String {
    let quoted = |text: &str| serde_json::to_string(text).expect("a string serializes");
    let mut script = format!("display notification {} with title {}", quoted(message), quoted(title));
    if let Some(subtitle) = subtitle {
        script += &format!(" subtitle {}", quoted(subtitle));
    }
    if let Some(sound) = sound {
        script += &format!(" sound name {}", quoted(sound));
    }
    script
}

/// Posts the banner; `Err` carries osascript's complaint.
pub async fn notify(message: &str, title: &str, subtitle: Option<&str>, sound: Option<&str>) -> Result<(), String> {
    let argv = vec!["/usr/bin/osascript".to_string(), "-e".to_string(), script(message, title, subtitle, sound)];
    let run = crate::process::run(argv, Duration::from_secs(10)).await?;
    if run.status == 0 { Ok(()) } else { Err(run.stderr.trim().to_string()) }
}
