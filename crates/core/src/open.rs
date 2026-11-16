//! The Open tool: what a target means and how the outcome reads. The platform opens, reveals and finds apps.
use crate::mcp::Desktop;
use std::path::PathBuf;

/// What to open.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Target {
    /// A URL with a scheme other than `file:`, as given.
    Url(String),
    /// An existing file or folder.
    File(PathBuf),
}

/// An application found by name or bundle id.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Application {
    pub path: PathBuf,
    /// The name a person sees, without `.app`.
    pub name: String,
}

/// A target with a URL scheme of two or more letters is a URL (`file:` ones are paths); anything else is a path,
/// `~` expanded, which must exist.
pub fn resolve(target: &str) -> Result<Target, String> {
    let trimmed = target.trim();
    if trimmed.is_empty() {
        return Err("target is required.".to_string());
    }
    let mut path = trimmed.to_string();
    if let Some((scheme, rest)) = url_scheme(trimmed) {
        if !scheme.eq_ignore_ascii_case("file") {
            return Ok(Target::Url(trimmed.to_string()));
        }
        path = file_url_path(rest);
    }
    let path = crate::clipboard::expand_tilde(&path);
    if !path.exists() {
        return Err(format!("No such file: {}", path.display()));
    }
    Ok(Target::File(path))
}

/// The scheme and the rest, when `text` is a URL: a scheme of two or more letters (so `C:` is not one) and
/// nothing a URL cannot hold.
fn url_scheme(text: &str) -> Option<(&str, &str)> {
    let (scheme, rest) = text.split_once(':')?;
    let valid = scheme.len() >= 2 && scheme.starts_with(|c: char| c.is_ascii_alphabetic()) && scheme.chars().all(|c| c.is_ascii_alphanumeric() || "+-.".contains(c));
    (valid && !text.chars().any(|c| c.is_whitespace() || c.is_control() || "\"<>\\^`{|}".contains(c))).then_some((scheme, rest))
}

/// The path of a `file:` URL's remainder: any authority dropped, percent escapes decoded.
fn file_url_path(rest: &str) -> String {
    let path = match rest.strip_prefix("//") {
        Some(authority_and_path) => authority_and_path.find('/').map(|slash| &authority_and_path[slash..]).unwrap_or("/"),
        None => rest,
    };
    let path = path.split(['?', '#']).next().unwrap_or(path);
    let bytes = path.as_bytes();
    let mut decoded = Vec::with_capacity(bytes.len());
    let mut index = 0;
    while index < bytes.len() {
        let hex = |at: usize| bytes.get(at).and_then(|byte| (*byte as char).to_digit(16));
        match (bytes[index], hex(index + 1), hex(index + 2)) {
            (b'%', Some(high), Some(low)) => {
                decoded.push((high * 16 + low) as u8);
                index += 3;
            }
            (byte, _, _) => {
                decoded.push(byte);
                index += 1;
            }
        }
    }
    let path = String::from_utf8_lossy(&decoded).into_owned();
    // A Windows file URL carries "/C:/dir/file"; the path is "C:\dir\file".
    if cfg!(windows) {
        let bytes = path.as_bytes();
        if bytes.len() >= 3 && bytes[0] == b'/' && bytes[1].is_ascii_alphabetic() && bytes[2] == b':' {
            return path[1..].replace('/', "\\");
        }
    }
    path
}

/// Opens `target`, or with `reveal` shows it in the file manager.
pub async fn run(desktop: &dyn Desktop, target: &str, reveal: bool, app: Option<&str>) -> Result<String, String> {
    let resolved = resolve(target)?;
    if reveal {
        let Target::File(path) = resolved else { return Err("reveal needs an existing file or folder path.".to_string()) };
        desktop.reveal(&path).await;
        return Ok(format!("Revealed {} in Finder.", path.display()));
    }
    let application = match app {
        Some(app) => Some(desktop.find_application(app).await.ok_or_else(|| format!("Application '{app}' not found."))?),
        None => None,
    };
    let shown = match &resolved {
        Target::Url(_) => target.trim().to_string(),
        Target::File(path) => path.display().to_string(),
    };
    desktop.open(&resolved, application.as_ref().map(|application| application.path.as_path())).await?;
    Ok(match application {
        Some(application) => format!("Opened {shown} with {}.", application.name),
        None => format!("Opened {shown}."),
    })
}
