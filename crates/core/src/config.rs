//! `~/.desktop-mcp/config.toml`, in the shape the Python server and the Swift port read.
use std::path::{Component, Path, PathBuf};

pub const LEGACY_TRANSPORT_WARNING: &str = "transport \"sse\" is no longer supported; serving streamable-http (POST /mcp) instead. Re-run `desktop-mcp install` or `desktop-mcp auth` to update the stored setting.";
pub const TRANSPORTS: [&str; 3] = ["stdio", "socket", "streamable-http"];

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ServerSection {
    pub transport: String,
    pub host: String,
    pub port: u16,
    pub allow_insecure_remote: bool,
    /// HTTP is served only when this (or `--allow-http`) says so.
    pub allow_http: bool,
    pub auth_key: Option<String>,
    /// Resolved against the config file's directory.
    pub ssl_certfile: Option<String>,
    pub ssl_keyfile: Option<String>,
}

impl Default for ServerSection {
    fn default() -> Self {
        ServerSection { transport: "stdio".into(), host: "localhost".into(), port: 8000, allow_insecure_remote: false, allow_http: false, auth_key: None, ssl_certfile: None, ssl_keyfile: None }
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct SecuritySection {
    pub ip_allowlist: Vec<String>,
    pub oauth_client_id: Option<String>,
    pub oauth_client_secret: Option<String>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct ToolsSection {
    pub exclude: Vec<String>,
    /// Opt-in tools to register.
    pub include: Vec<String>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Config {
    pub server: ServerSection,
    pub security: SecuritySection,
    pub tools: ToolsSection,
    /// Non-fatal notes from `load` (a stored value that was mapped rather than rejected).
    pub warnings: Vec<String>,
}

/// The user's home folder: the profile folder on Windows (where `HOME` is usually unset, or set by a Unix-like
/// shell to something else), `HOME` elsewhere.
pub fn home_directory() -> Option<PathBuf> {
    let variable = if cfg!(windows) { "USERPROFILE" } else { "HOME" };
    std::env::var_os(variable).filter(|home| !home.is_empty()).map(PathBuf::from)
}

impl Config {
    /// The explicit path (`~` expanded against `home`), else `~/.desktop-mcp/config.toml` when it exists. With no
    /// home folder (`home` empty) nothing is looked for, rather than a path relative to the working directory.
    pub fn discover(explicit: Option<&str>, home: &Path) -> Option<PathBuf> {
        match explicit {
            Some(path) => Some(expand_tilde(path, home)),
            None if home.as_os_str().is_empty() => None,
            None => Some(home.join(".desktop-mcp/config.toml")).filter(|path| path.exists()),
        }
    }

    /// The defaults when `path` is `None`; otherwise the parsed and validated file.
    pub fn load(path: Option<&Path>) -> Result<Config, String> {
        let mut config = Config::default();
        let Some(path) = path else { return Ok(config) };
        let text = std::fs::read_to_string(path).map_err(|error| match error.kind() {
            std::io::ErrorKind::NotFound => format!("Config file not found: {}", path.display()),
            _ => format!("Cannot read {}: {error}", path.display()),
        })?;
        let table: toml::Table = text.parse().map_err(|error: toml::de::Error| format!("{}: {}", path.display(), error.message()))?;
        let section = |name: &str| table.get(name).and_then(toml::Value::as_table).cloned().unwrap_or_default();
        let (server, security, tools) = (section("server"), section("security"), section("tools"));
        let base = path.parent().unwrap_or(Path::new("/"));
        let home = home_directory().unwrap_or_default();

        if let Some(value) = server.get("transport") {
            let mut transport = text_of(value);
            if transport == "sse" {
                // Written by an older version; the server now only speaks streamable-http.
                transport = "streamable-http".into();
                config.warnings.push(LEGACY_TRANSPORT_WARNING.into());
            }
            if !TRANSPORTS.contains(&transport.as_str()) {
                return Err(format!("server.transport must be one of {TRANSPORTS:?}, got '{transport}'"));
            }
            config.server.transport = transport;
        }
        if let Some(value) = server.get("host") {
            config.server.host = text_of(value);
        }
        if let Some(value) = server.get("port") {
            let port = match value {
                toml::Value::Integer(n) => u16::try_from(*n).ok(),
                toml::Value::String(s) => s.trim().parse().ok(),
                _ => None,
            };
            config.server.port = port.ok_or("server.port must be an integer")?;
        }
        if let Some(value) = server.get("allow_insecure_remote") {
            config.server.allow_insecure_remote = value.as_bool().ok_or("server.allow_insecure_remote must be a TOML boolean")?;
        }
        if let Some(value) = server.get("allow_http") {
            config.server.allow_http = value.as_bool().ok_or("server.allow_http must be a TOML boolean")?;
        }
        config.server.auth_key = server.get("auth_key").map(text_of).filter(|s| !s.is_empty());
        // TLS paths are relative to the config file.
        let tls_path = |key: &str| server.get(key).map(text_of).filter(|s| !s.is_empty()).map(|p| normalize(&base.join(expand_tilde(&p, &home))));
        config.server.ssl_certfile = tls_path("ssl_certfile");
        config.server.ssl_keyfile = tls_path("ssl_keyfile");
        if let Some(value) = security.get("ip_allowlist") {
            config.security.ip_allowlist = strings(value, "security.ip_allowlist")?;
        }
        config.security.oauth_client_id = security.get("oauth_client_id").map(text_of).filter(|s| !s.is_empty());
        config.security.oauth_client_secret = security.get("oauth_client_secret").map(text_of).filter(|s| !s.is_empty());
        if let Some(value) = tools.get("exclude") {
            config.tools.exclude = strings(value, "tools.exclude")?;
        }
        if let Some(value) = tools.get("include") {
            config.tools.include = strings(value, "tools.include")?;
        }
        Ok(config)
    }
}

/// A value as Python's `str()` would show it, which is how the original reads text settings.
fn text_of(value: &toml::Value) -> String {
    match value {
        toml::Value::String(s) => s.clone(),
        toml::Value::Boolean(b) => if *b { "True" } else { "False" }.into(),
        other => other.to_string(),
    }
}

fn strings(value: &toml::Value, key: &str) -> Result<Vec<String>, String> {
    let error = || format!("{key} must be an array of strings");
    value.as_array().ok_or_else(error)?.iter().map(|item| item.as_str().map(str::to_string).ok_or_else(error)).collect()
}

/// `~` expanded against `home`.
fn expand_tilde(path: &str, home: &Path) -> PathBuf {
    match path.strip_prefix('~') {
        Some("") => home.to_path_buf(),
        Some(rest) if rest.starts_with('/') => home.join(&rest[1..]),
        _ => PathBuf::from(path),
    }
}

/// `.` and `..` removed without touching the file system.
fn normalize(path: &Path) -> String {
    let mut out = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                out.pop();
            }
            other => out.push(other),
        }
    }
    out.to_string_lossy().into_owned()
}
