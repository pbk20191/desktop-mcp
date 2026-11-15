//! The launchd Launch Agent that keeps the HTTP server running from login, and how `connect` finds it. Only
//! the pure parts live here (what the plist says, which port to use, where to connect); the command writes the
//! file and talks to `launchctl`.
//!
//! Running as an agent is what lets desktop-mcp hold Accessibility and Screen Recording access itself: launchd
//! makes it the responsible process, so System Settings lists it under its own name.
use crate::config::Config;
use std::path::{Path, PathBuf};

/// Also the code signing identifier, so TCC and launchd agree on who this is.
pub const LABEL: &str = "com.desktop-mcp.server";
/// The plist's `Sockets` entry; the server asks launchd for its socket by this name.
pub const SOCKET_NAME: &str = "Listener";

/// `~/Library/LaunchAgents/com.desktop-mcp.server.plist`.
pub fn plist_path(home: &Path) -> PathBuf {
    home.join(format!("Library/LaunchAgents/{LABEL}.plist"))
}

/// `~/.desktop-mcp/mcp.sock`.
pub fn socket_path(home: &Path) -> PathBuf {
    home.join(".desktop-mcp/mcp.sock")
}

/// Where the agent's stdout and stderr go.
pub fn log_paths(directory: &Path) -> (PathBuf, PathBuf) {
    (directory.join("server.log"), directory.join("server.error.log"))
}

/// Transports a background service can offer; stdio needs a parent process to talk to.
pub fn validate_transport(transport: &str) -> Result<(), String> {
    match transport {
        "streamable-http" => Ok(()),
        "stdio" => Err("stdio is not supported as a service; use --transport streamable-http.".into()),
        "sse" => Err("Invalid transport: sse (sse was removed; use streamable-http)".into()),
        other => Err(format!("Invalid transport: {other}")),
    }
}

/// Everything explicit, so the agent does not depend on `config.toml` for where it listens.
pub fn program_arguments(executable: &str, transport: &str, host: &str, port: u16, socket: Option<&str>) -> Vec<String> {
    let mut arguments: Vec<String> = [executable, "serve", "--transport", transport, "--host", host, "--port"].iter().map(|s| s.to_string()).collect();
    arguments.push(port.to_string());
    if let Some(socket) = socket {
        arguments.extend(["--socket".to_string(), socket.to_string()]);
    }
    arguments
}

fn escape(text: &str) -> String {
    text.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;")
}

/// An XML plist with the original's keys (Label, ProgramArguments, RunAtLoad, KeepAlive, StandardOutPath,
/// StandardErrorPath). With a `socket`, also a `Sockets` entry, so launchd owns the Unix socket (mode 0600) and
/// starts the agent on the first connection.
pub fn plist_xml(program_arguments: &[String], log_directory: &Path, socket: Option<&str>) -> String {
    let (out, error) = log_paths(log_directory);
    let string = |value: &str| format!("<string>{}</string>", escape(value));
    let mut xml = String::from("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n<dict>\n");
    xml += &format!("\t<key>KeepAlive</key>\n\t<true/>\n\t<key>Label</key>\n\t{}\n\t<key>ProgramArguments</key>\n\t<array>\n", string(LABEL));
    for argument in program_arguments {
        xml += &format!("\t\t{}\n", string(argument));
    }
    xml += "\t</array>\n\t<key>RunAtLoad</key>\n\t<true/>\n";
    if let Some(socket) = socket {
        xml += &format!(
            "\t<key>Sockets</key>\n\t<dict>\n\t\t<key>{SOCKET_NAME}</key>\n\t\t<dict>\n\t\t\t<key>SockFamily</key>\n\t\t\t<string>Unix</string>\n\t\t\t<key>SockPathMode</key>\n\t\t\t<integer>{}</integer>\n\t\t\t<key>SockPathName</key>\n\t\t\t{}\n\t\t\t<key>SockType</key>\n\t\t\t<string>stream</string>\n\t\t</dict>\n\t</dict>\n",
            0o600,
            string(socket)
        );
    }
    xml += &format!("\t<key>StandardErrorPath</key>\n\t{}\n\t<key>StandardOutPath</key>\n\t{}\n</dict>\n</plist>\n", string(&error.to_string_lossy()), string(&out.to_string_lossy()));
    xml
}

/// The first port at or above `preferred` that can be bound on `host`. Port 0 means "any", always free.
pub fn find_free_port(host: &str, preferred: u16) -> Result<u16, String> {
    if preferred == 0 {
        return Ok(0);
    }
    let host = if host.eq_ignore_ascii_case("localhost") { "127.0.0.1" } else { host };
    let last = preferred.saturating_add(99);
    (preferred..=last)
        .find(|port| std::net::TcpListener::bind((host, *port)).is_ok())
        .ok_or_else(|| format!("No free port found in range {preferred}–{last} on {host}."))
}

/// The argument that follows `flag`.
fn value_after<'a>(flag: &str, arguments: &'a [String]) -> Option<&'a str> {
    let index = arguments.iter().position(|argument| argument == flag)?;
    arguments.get(index + 1).map(String::as_str)
}

/// A streamable-http endpoint and the key for it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HttpTarget {
    pub url: String,
    pub auth_key: Option<String>,
}

impl HttpTarget {
    /// Where the installed agent listens (its `ProgramArguments`) and how to authenticate (`config.toml`, which
    /// the agent reads as well).
    pub fn installed(arguments: &[String], config: &Config) -> Result<HttpTarget, String> {
        if arguments.is_empty() {
            return Err("Could not read the Launch Agent plist.".into());
        }
        let transport = value_after("--transport", arguments).unwrap_or("streamable-http");
        // An agent installed with `--transport sse` maps it to streamable-http itself.
        if transport != "streamable-http" && transport != "sse" {
            return Err(format!("connect supports streamable-http only; the installed agent runs {transport}."));
        }
        let host = value_after("--host", arguments).unwrap_or("127.0.0.1");
        let port = value_after("--port", arguments).and_then(|port| port.parse::<u16>().ok()).unwrap_or(8000);
        let scheme = if config.server.ssl_certfile.is_some() { "https" } else { "http" };
        let authority = if host.contains(':') { format!("[{host}]") } else { host.to_string() };
        Ok(HttpTarget { url: format!("{scheme}://{authority}:{port}/mcp"), auth_key: config.server.auth_key.clone() })
    }
}

/// How `connect` reaches the installed agent.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ConnectPlan {
    /// Over its Unix socket, with HTTP as the fallback when the socket refuses the connection.
    Socket { path: String, fallback: HttpTarget },
    /// Over HTTP, for an agent that predates the socket.
    Http(HttpTarget),
}

impl ConnectPlan {
    pub fn installed(arguments: &[String], config: &Config) -> Result<ConnectPlan, String> {
        let target = HttpTarget::installed(arguments, config)?;
        Ok(match value_after("--socket", arguments) {
            Some(path) => ConnectPlan::Socket { path: path.to_string(), fallback: target },
            None => ConnectPlan::Http(target),
        })
    }
}
