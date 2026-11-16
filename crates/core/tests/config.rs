//! `config.toml`, option merging, and the security helpers: ported from the Swift port's ConfigTests.
use desktop_mcp_core::config::{Config, LEGACY_TRANSPORT_WARNING};
use desktop_mcp_core::plan::{CommandLine, ServePlan, ENV_AUTH_KEY, ENV_EXCLUDE_TOOLS, ENV_IP_ALLOWLIST, HTTP_NEEDS_AUTH, SOCKET_DISABLED};
use desktop_mcp_core::security::{constant_time_eq, is_loopback_host, token_url_safe, IpNetwork};
use std::collections::HashMap;
use std::path::PathBuf;
use std::time::Duration;

fn file(directory: &tempfile::TempDir, text: &str) -> PathBuf {
    let path = directory.path().join("config.toml");
    std::fs::write(&path, text).unwrap();
    path
}
fn none() -> HashMap<String, String> {
    HashMap::new()
}
fn resolve(cli: &CommandLine) -> Result<ServePlan, String> {
    ServePlan::resolve(cli, &none(), &Config::default())
}

#[test]
fn load_validates_and_resolves_relative_tls_paths() {
    let directory = tempfile::tempdir().unwrap();
    let path = file(&directory, "[server]\ntransport = \"streamable-http\"\nport = 9000\nssl_certfile = \"certs/cert.pem\"\nssl_keyfile = \"/abs/key.pem\"\n[tools]\nexclude = [\"Shell\"]\n");
    let config = Config::load(Some(&path)).unwrap();
    assert_eq!(config.server.transport, "streamable-http");
    assert_eq!(config.server.port, 9000);
    assert_eq!(config.server.host, "localhost");
    assert_eq!(config.server.ssl_certfile.as_deref(), Some(directory.path().join("certs").join("cert.pem").to_str().unwrap()));
    // Rooted but driveless on Windows: it takes the config file's drive.
    assert_eq!(config.server.ssl_keyfile.as_deref().map(std::path::Path::new), Some(directory.path().join("/abs/key.pem").as_path()));
    assert_eq!(config.tools.exclude, ["Shell"]);
}

#[test]
fn the_whole_file() {
    let directory = tempfile::tempdir().unwrap();
    let path = file(&directory, "# comment\n[server]\nhost = \"127.0.0.1\"\nauth_key = \"k\"\nallow_insecure_remote = true\n\n[security]\nip_allowlist = [\"127.0.0.1\", \"10.0.0.0/8\"]\noauth_client_id = \"id\"\noauth_client_secret = \"secret\"\n\n[tools]\ninclude = ['OCR']\n");
    let config = Config::load(Some(&path)).unwrap();
    assert_eq!(config.server.host, "127.0.0.1");
    assert_eq!(config.server.auth_key.as_deref(), Some("k"));
    assert!(config.server.allow_insecure_remote);
    assert_eq!(config.security.ip_allowlist, ["127.0.0.1", "10.0.0.0/8"]);
    assert_eq!(config.security.oauth_client_id.as_deref(), Some("id"));
    assert_eq!(config.security.oauth_client_secret.as_deref(), Some("secret"));
    assert_eq!(config.tools.include, ["OCR"]);
}

#[test]
fn legacy_sse_transport_is_mapped_with_a_warning() {
    let directory = tempfile::tempdir().unwrap();
    let config = Config::load(Some(&file(&directory, "[server]\ntransport = \"sse\"\n"))).unwrap();
    assert_eq!(config.server.transport, "streamable-http");
    assert_eq!(config.warnings, [LEGACY_TRANSPORT_WARNING]);
}

#[test]
fn plan_maps_legacy_sse_from_the_command_line() {
    let plan = resolve(&CommandLine { transport: Some("sse".into()), allow_http: true, auth_key: Some("k".into()), ..CommandLine::default() }).unwrap();
    assert_eq!(plan.transport, "streamable-http");
    assert!(plan.warnings.iter().any(|w| w == LEGACY_TRANSPORT_WARNING));
}

#[test]
fn plan_still_rejects_unknown_transports() {
    assert!(resolve(&CommandLine { transport: Some("carrier-pigeon".into()), ..CommandLine::default() }).is_err());
}

#[test]
fn load_rejects_bad_values() {
    let directory = tempfile::tempdir().unwrap();
    for text in ["[server]\ntransport = \"websocket\"\n", "[server]\nallow_insecure_remote = \"yes\"\n", "[tools]\nexclude = \"Shell\"\n", "[server]\nport = \"many\"\n", "[server\n"] {
        assert!(Config::load(Some(&file(&directory, text))).is_err(), "{text:?}");
    }
    assert!(Config::load(Some(&directory.path().join("missing.toml"))).is_err());
    assert_eq!(Config::load(None).unwrap(), Config::default());
}

#[test]
fn discovery_finds_the_default_file_only_when_it_exists() {
    let home = tempfile::tempdir().unwrap();
    assert_eq!(Config::discover(None, home.path()), None);
    std::fs::create_dir_all(home.path().join(".desktop-mcp")).unwrap();
    std::fs::write(home.path().join(".desktop-mcp/config.toml"), "").unwrap();
    assert_eq!(Config::discover(None, home.path()), Some(home.path().join(".desktop-mcp/config.toml")));
    assert_eq!(Config::discover(Some("~/other.toml"), home.path()), Some(home.path().join("other.toml")));
    assert_eq!(Config::discover(Some("/x/y.toml"), home.path()), Some(PathBuf::from("/x/y.toml")));
}

#[test]
fn plan_precedence_command_line_then_environment_then_config() {
    let mut config = Config::default();
    config.server.transport = "streamable-http".into();
    config.server.port = 9000;
    config.server.auth_key = Some("from-config".into());
    config.security.ip_allowlist = vec!["10.0.0.0/8".into()];
    config.tools.exclude = vec!["Shell".into()];
    let env: HashMap<String, String> = [(ENV_AUTH_KEY.to_string(), "from-env".to_string()), (ENV_EXCLUDE_TOOLS.to_string(), "Scrape,Shell".to_string())].into();
    let mut cli = CommandLine { port: Some(7000), allow_http: true, ..CommandLine::default() };
    let plan = ServePlan::resolve(&cli, &env, &config).unwrap();
    assert_eq!(plan.transport, "streamable-http");
    assert_eq!(plan.port, 7000);
    assert_eq!(plan.host, "localhost");
    assert_eq!(plan.auth_key.as_deref(), Some("from-env"));
    assert_eq!(plan.ip_allowlist.iter().map(ToString::to_string).collect::<Vec<_>>(), ["10.0.0.0/8"]);
    assert_eq!(plan.excluded_tools, ["Shell", "Scrape"]);

    cli.auth_key = Some("from-cli".into());
    cli.ip_allowlist = Some("192.168.1.5, ::1".into());
    let overridden = ServePlan::resolve(&cli, &env, &config).unwrap();
    assert_eq!(overridden.auth_key.as_deref(), Some("from-cli"));
    assert_eq!(overridden.ip_allowlist.iter().map(ToString::to_string).collect::<Vec<_>>(), ["192.168.1.5/32", "::1/128"]);

    // Empty values count as unset at every level.
    cli.auth_key = Some(String::new());
    cli.ip_allowlist = Some(String::new());
    let blank = ServePlan::resolve(&cli, &[(ENV_IP_ALLOWLIST.to_string(), String::new())].into(), &config).unwrap();
    assert_eq!(blank.auth_key.as_deref(), Some("from-config"));
    assert_eq!(blank.ip_allowlist.iter().map(ToString::to_string).collect::<Vec<_>>(), ["10.0.0.0/8"]);
    cli.transport = Some("streamable-http".into());
    cli.host = Some("0.0.0.0".into());
    assert!(ServePlan::resolve(&cli, &none(), &Config::default()).is_err());
}

#[test]
fn session_timeout_defaults_to_half_an_hour_and_zero_disables_it() {
    assert_eq!(resolve(&CommandLine::default()).unwrap().session_timeout, Some(Duration::from_secs(1800)));
    assert_eq!(resolve(&CommandLine { session_timeout: Some(0), ..CommandLine::default() }).unwrap().session_timeout, None);
    assert_eq!(resolve(&CommandLine { session_timeout: Some(90), ..CommandLine::default() }).unwrap().session_timeout, Some(Duration::from_secs(90)));
}

#[test]
fn http_requires_authentication_on_loopback_too() {
    let mut cli = CommandLine { transport: Some("streamable-http".into()), host: Some("127.0.0.1".into()), allow_http: true, allow_insecure_remote: true, ..CommandLine::default() };
    assert_eq!(resolve(&cli).unwrap_err(), HTTP_NEEDS_AUTH);
    cli.auth_key = Some("secret".into());
    assert!(resolve(&cli).is_ok());
}

#[test]
fn plan_requires_pairs() {
    assert!(resolve(&CommandLine { ssl_certfile: Some("/c.pem".into()), ..CommandLine::default() }).is_err());
    assert!(resolve(&CommandLine { oauth_client_id: Some("id".into()), ..CommandLine::default() }).is_err());
    let bad = resolve(&CommandLine { ip_allowlist: Some("10.0.0.0/33,nope".into()), ..CommandLine::default() }).unwrap_err();
    assert!(bad.contains("nope"), "{bad}");
}

#[test]
fn stdio_warns_about_http_only_settings() {
    let plan = resolve(&CommandLine { auth_key: Some("x".into()), ..CommandLine::default() }).unwrap();
    assert_eq!(plan.warnings, ["auth_key / ip_allowlist have no effect on stdio transport"]);
}

#[test]
fn ip_networks() {
    let office: IpNetwork = "10.1.2.3/8".parse().unwrap();
    assert_eq!(office.to_string(), "10.0.0.0/8");
    assert!(office.contains("10.200.0.1".parse().unwrap()));
    assert!(office.contains("::ffff:10.0.0.9".parse().unwrap()));
    assert!(!office.contains("11.0.0.1".parse().unwrap()));
    assert!("2001:db8::/32".parse::<IpNetwork>().unwrap().contains("2001:db8:1::5".parse().unwrap()));
    assert!(!"::1".parse::<IpNetwork>().unwrap().contains("::2".parse().unwrap()));
    assert!("10.0.0.0/40".parse::<IpNetwork>().is_err());
    assert!("example.com".parse::<IpNetwork>().is_err());
}

#[test]
fn loopback_and_tokens() {
    for host in ["localhost", "LOCALHOST", "127.0.0.1", "127.5.5.5", "::1", "[::1]"] {
        assert!(is_loopback_host(host), "{host}");
    }
    for host in ["0.0.0.0", "192.168.0.1", "::", "example.com"] {
        assert!(!is_loopback_host(host), "{host}");
    }
    let token = token_url_safe(32);
    assert_eq!(token.len(), 43);
    assert!(token.chars().all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_'));
    assert_ne!(token, token_url_safe(32));
    assert!(constant_time_eq("abc", "abc"));
    assert!(!constant_time_eq("abc", "abd"));
    assert!(!constant_time_eq("abc", "abcd"));
}

#[test]
fn http_needs_an_explicit_opt_in() {
    let refused = resolve(&CommandLine { transport: Some("streamable-http".into()), ..CommandLine::default() }).unwrap_err();
    assert!(refused.starts_with("The HTTP transport is off by default"), "{refused}");
    assert!(resolve(&CommandLine { transport: Some("streamable-http".into()), allow_http: true, auth_key: Some("k".into()), ..CommandLine::default() }).is_ok());
}

#[test]
fn the_socket_transport_is_disabled() {
    assert_eq!(resolve(&CommandLine { transport: Some("socket".into()), socket_path: Some("/tmp/a.sock".into()), ..CommandLine::default() }).unwrap_err(), SOCKET_DISABLED);
    assert_eq!(resolve(&CommandLine { socket_path: Some("/tmp/a.sock".into()), ..CommandLine::default() }).unwrap_err(), SOCKET_DISABLED);
}
