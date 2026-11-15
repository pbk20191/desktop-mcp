//! The settings `serve` runs with, merged as in the original: a value given on the command line (or through
//! its environment variable) wins, then `config.toml`, then the default. An empty value, wherever it comes
//! from, means "not set".
use crate::config::{Config, LEGACY_TRANSPORT_WARNING, TRANSPORTS};
use crate::security::{is_loopback_host, IpNetwork};
use std::collections::HashMap;
use std::time::Duration;

pub const ENV_AUTH_KEY: &str = "DESKTOP_MCP_AUTH_KEY";
pub const ENV_IP_ALLOWLIST: &str = "DESKTOP_MCP_IP_ALLOWLIST";
pub const ENV_SSL_CERTFILE: &str = "DESKTOP_MCP_SSL_CERTFILE";
pub const ENV_SSL_KEYFILE: &str = "DESKTOP_MCP_SSL_KEYFILE";
pub const ENV_OAUTH_CLIENT_ID: &str = "DESKTOP_MCP_OAUTH_CLIENT_ID";
pub const ENV_OAUTH_CLIENT_SECRET: &str = "DESKTOP_MCP_OAUTH_CLIENT_SECRET";
pub const ENV_EXCLUDE_TOOLS: &str = "DESKTOP_MCP_EXCLUDE_TOOLS";
pub const ENV_INCLUDE_TOOLS: &str = "DESKTOP_MCP_INCLUDE_TOOLS";

/// What was given on the command line; `None` means "not given".
#[derive(Clone, Debug, Default)]
pub struct CommandLine {
    pub transport: Option<String>,
    pub host: Option<String>,
    pub port: Option<u16>,
    pub auth_key: Option<String>,
    pub allow_insecure_remote: bool,
    pub ip_allowlist: Option<String>,
    pub ssl_certfile: Option<String>,
    pub ssl_keyfile: Option<String>,
    pub oauth_client_id: Option<String>,
    pub oauth_client_secret: Option<String>,
    pub exclude_tools: Option<String>,
    pub include_tools: Option<String>,
    /// Seconds; 0 keeps sessions until the client ends them.
    pub session_timeout: Option<u64>,
    /// A Unix socket to serve the stdio binding on as well (streamable-http only); command line only.
    pub socket_path: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Tls {
    pub certificate: String,
    pub private_key: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct OAuthClient {
    pub client_id: String,
    pub client_secret: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ServePlan {
    pub transport: String,
    pub host: String,
    pub port: u16,
    pub auth_key: Option<String>,
    pub allow_insecure_remote: bool,
    pub ip_allowlist: Vec<IpNetwork>,
    pub tls: Option<Tls>,
    pub oauth: Option<OAuthClient>,
    pub excluded_tools: Vec<String>,
    pub included_tools: Vec<String>,
    pub session_timeout: Option<Duration>,
    pub socket_path: Option<String>,
    /// Non-fatal notes for the log.
    pub warnings: Vec<String>,
}

fn non_empty(value: Option<&str>) -> Option<String> {
    value.filter(|v| !v.is_empty()).map(str::to_string)
}

fn list(text: &str) -> Vec<String> {
    text.split(',').map(str::trim).filter(|s| !s.is_empty()).map(str::to_string).collect()
}

impl ServePlan {
    pub fn resolve(cli: &CommandLine, env: &HashMap<String, String>, config: &Config) -> Result<ServePlan, String> {
        let from_env = |key: &str| non_empty(env.get(key).map(String::as_str));
        let pick = |flag: &Option<String>, key: &str, configured: &Option<String>| non_empty(flag.as_deref()).or_else(|| from_env(key)).or_else(|| configured.clone());

        let mut transport = cli.transport.clone().unwrap_or_else(|| config.server.transport.clone());
        let mut warnings = config.warnings.clone();
        if transport == "sse" {
            // A Launch Agent plist from an older version still passes `--transport sse`; rejecting it would
            // crash-loop the agent under KeepAlive.
            transport = "streamable-http".into();
            if !warnings.iter().any(|w| w == LEGACY_TRANSPORT_WARNING) {
                warnings.push(LEGACY_TRANSPORT_WARNING.into());
            }
        }
        if !TRANSPORTS.contains(&transport.as_str()) {
            return Err(format!("Invalid transport: {transport}"));
        }
        let socket_path = non_empty(cli.socket_path.as_deref());
        if let Some(path) = &socket_path {
            if transport != "streamable-http" {
                return Err("--socket requires --transport streamable-http.".into());
            }
            // What fits in `sockaddr_un.sun_path`.
            if path.len() > 103 {
                return Err(format!("Socket path too long (max 103 bytes): {path}"));
            }
        }
        let host = cli.host.clone().unwrap_or_else(|| config.server.host.clone());
        let port = cli.port.unwrap_or(config.server.port);
        let auth_key = pick(&cli.auth_key, ENV_AUTH_KEY, &config.server.auth_key);
        let allow_insecure_remote = cli.allow_insecure_remote || config.server.allow_insecure_remote;
        let certificate = pick(&cli.ssl_certfile, ENV_SSL_CERTFILE, &config.server.ssl_certfile);
        let private_key = pick(&cli.ssl_keyfile, ENV_SSL_KEYFILE, &config.server.ssl_keyfile);
        let client_id = pick(&cli.oauth_client_id, ENV_OAUTH_CLIENT_ID, &config.security.oauth_client_id);
        let client_secret = pick(&cli.oauth_client_secret, ENV_OAUTH_CLIENT_SECRET, &config.security.oauth_client_secret);
        let allowlist_entries = non_empty(cli.ip_allowlist.as_deref()).or_else(|| from_env(ENV_IP_ALLOWLIST)).map(|text| list(&text)).unwrap_or_else(|| config.security.ip_allowlist.clone());
        let mut excluded_tools = config.tools.exclude.clone();
        for name in cli.exclude_tools.clone().or_else(|| env.get(ENV_EXCLUDE_TOOLS).cloned()).map(|t| list(&t)).unwrap_or_default() {
            if !excluded_tools.contains(&name) {
                excluded_tools.push(name);
            }
        }
        let mut included_tools = config.tools.include.clone();
        for name in cli.include_tools.clone().or_else(|| env.get(ENV_INCLUDE_TOOLS).cloned()).map(|t| list(&t)).unwrap_or_default() {
            if !included_tools.contains(&name) {
                included_tools.push(name);
            }
        }

        let tls = match (certificate, private_key) {
            (Some(certificate), Some(private_key)) => Some(Tls { certificate, private_key }),
            (None, None) => None,
            _ => return Err("--ssl-certfile and --ssl-keyfile must be provided together.".into()),
        };
        let oauth = match (client_id, client_secret) {
            (Some(client_id), Some(client_secret)) => Some(OAuthClient { client_id, client_secret }),
            (None, None) => None,
            _ => return Err("OAuth requires both --oauth-client-id and --oauth-client-secret.".into()),
        };
        let ip_allowlist = IpNetwork::parse_list(&allowlist_entries).map_err(|error| format!("Invalid ip_allowlist: {error}"))?;

        if transport != "stdio" && !is_loopback_host(&host) && auth_key.is_none() && oauth.is_none() && !allow_insecure_remote {
            return Err(format!(
                "Refusing to bind HTTP transport to '{host}' without authentication.\n  Use --auth-key <token> or --oauth-client-id/--oauth-client-secret.\n  Or pass --allow-insecure-remote to explicitly allow unauthenticated access (not recommended)."
            ));
        }
        if transport == "stdio" && (auth_key.is_some() || !ip_allowlist.is_empty()) {
            warnings.push("auth_key / ip_allowlist have no effect on stdio transport".into());
        }
        let session_timeout = match cli.session_timeout {
            Some(0) => None,
            Some(seconds) => Some(Duration::from_secs(seconds)),
            None => Some(Duration::from_secs(1800)),
        };
        Ok(ServePlan { transport, host, port, auth_key, allow_insecure_remote, ip_allowlist, tls, oauth, excluded_tools, included_tools, session_timeout, socket_path, warnings })
    }

    pub fn scheme(&self) -> &'static str {
        if self.tls.is_some() { "https" } else { "http" }
    }
}
