//! The Clipboard tool: what is read and written, and how it is reported. The platform owns the pasteboard.
use crate::mcp::Desktop;
use base64::Engine;
use rmcp::model::ContentBlock;
use std::path::PathBuf;

pub const TEXT_LIMIT: usize = 100_000;
/// The longest side of an image handed to the model.
pub const IMAGE_MAX_SIDE: u32 = 1568;

/// What the clipboard holds, as far as the tool reports it.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Clipboard {
    pub text: Option<String>,
    /// Paths of copied files.
    pub files: Vec<String>,
    pub image: Option<Image>,
}

/// A PNG at most [`IMAGE_MAX_SIDE`] on its longer side.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Image {
    pub png: Vec<u8>,
    pub width: u32,
    pub height: u32,
}

/// What replaces the clipboard's contents.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ClipboardWrite {
    Text(String),
    /// Existing files, as absolute paths.
    Files(Vec<PathBuf>),
}

/// The text, files and image on the clipboard.
pub async fn read(desktop: &dyn Desktop) -> Vec<ContentBlock> {
    let clipboard = desktop.read_clipboard(IMAGE_MAX_SIDE).await;
    let mut sections = Vec::new();
    if let Some(text) = clipboard.text.filter(|text| !text.is_empty()) {
        let count = text.chars().count();
        sections.push(if count > TEXT_LIMIT { format!("{}… (truncated, {count} characters)", text.chars().take(TEXT_LIMIT).collect::<String>()) } else { text });
    }
    if !clipboard.files.is_empty() {
        sections.push(format!("Files:\n{}", clipboard.files.join("\n")));
    }
    match clipboard.image {
        Some(image) => {
            if sections.is_empty() {
                sections.push(format!("Image ({}×{}).", image.width, image.height));
            }
            let png = image.png;
            let data = crate::mcp::off_loop(desktop, move || base64::engine::general_purpose::STANDARD.encode(png)).await;
            vec![ContentBlock::text(sections.join("\n\n")), ContentBlock::image(data, "image/png")]
        }
        None if sections.is_empty() => vec![ContentBlock::text("The clipboard is empty.")],
        None => vec![ContentBlock::text(sections.join("\n\n"))],
    }
}

/// Replaces the clipboard with `text` or with the files at `paths` (`~` is the home directory).
pub async fn write(desktop: &dyn Desktop, text: Option<String>, paths: Option<Vec<String>>) -> Result<String, String> {
    let paths = paths.filter(|paths| !paths.is_empty());
    let contents = match (text, paths) {
        (Some(_), Some(_)) => return Err("Pass either text or paths, not both.".to_string()),
        (None, None) => return Err("write needs text or paths.".to_string()),
        (Some(text), None) => ClipboardWrite::Text(text),
        (None, Some(paths)) => {
            let files = paths
                .iter()
                .map(|path| {
                    let expanded = expand_tilde(path);
                    if expanded.exists() { Ok(expanded) } else { Err(format!("No such file: {}", expanded.display())) }
                })
                .collect::<Result<Vec<_>, _>>()?;
            ClipboardWrite::Files(files)
        }
    };
    let report = match &contents {
        ClipboardWrite::Text(text) => format!("Copied {} characters.", text.chars().count()),
        ClipboardWrite::Files(files) => format!("Copied {} {}.", files.len(), if files.len() == 1 { "file" } else { "files" }),
    };
    desktop.write_clipboard(contents).await?;
    Ok(report)
}

/// `path` with a leading `~` or `~/` replaced by the home directory.
pub fn expand_tilde(path: &str) -> PathBuf {
    let home = || std::env::var_os("HOME").map(PathBuf::from);
    match path.strip_prefix('~') {
        Some("") => home().unwrap_or_else(|| PathBuf::from(path)),
        Some(rest) if rest.starts_with('/') => home().map(|home| home.join(&rest[1..])).unwrap_or_else(|| PathBuf::from(path)),
        _ => PathBuf::from(path),
    }
}
