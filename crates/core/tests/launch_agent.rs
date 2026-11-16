//! The Launch Agent's plist, and how `connect` finds the agent from it. Ported from the Swift port's
//! ConnectPlanTests and StdioBridgeTests.
use desktop_mcp_core::config::Config;
use desktop_mcp_core::launch_agent::{find_free_port, plist_xml, program_arguments, validate_transport, ConnectPlan, HttpTarget, LABEL};
use std::path::Path;

fn args(transport: &str, host: &str, port: u16, socket: Option<&str>) -> Vec<String> {
    program_arguments("/x", transport, host, port, socket)
}

#[test]
fn program_arguments_say_everything_explicitly() {
    assert_eq!(args("streamable-http", "127.0.0.1", 8123, Some("/s.sock")), ["/x", "serve", "--transport", "streamable-http", "--host", "127.0.0.1", "--port", "8123", "--socket", "/s.sock"]);
    assert_eq!(args("streamable-http", "127.0.0.1", 8000, None).len(), 8);
}

#[test]
fn socket_when_the_agent_offers_one_with_the_http_target_as_fallback() {
    let plan = ConnectPlan::installed(&args("streamable-http", "127.0.0.1", 8123, Some("/s.sock")), &Config::default()).unwrap();
    assert_eq!(plan, ConnectPlan::Socket { path: "/s.sock".into(), fallback: HttpTarget { url: "http://127.0.0.1:8123/mcp".into(), auth_key: None } });
}

#[test]
fn http_when_the_agent_predates_the_socket() {
    let plan = ConnectPlan::installed(&args("streamable-http", "127.0.0.1", 8000, None), &Config::default()).unwrap();
    assert_eq!(plan, ConnectPlan::Http(HttpTarget { url: "http://127.0.0.1:8000/mcp".into(), auth_key: None }));
}

#[test]
fn the_target_takes_its_key_and_scheme_from_the_config() {
    let mut config = Config::default();
    config.server.auth_key = Some("abc".into());
    let target = HttpTarget::installed(&args("streamable-http", "127.0.0.1", 8123, None), &config).unwrap();
    assert_eq!(target, HttpTarget { url: "http://127.0.0.1:8123/mcp".into(), auth_key: Some("abc".into()) });
    config.server.ssl_certfile = Some("/c.pem".into());
    config.server.ssl_keyfile = Some("/k.pem".into());
    assert!(HttpTarget::installed(&args("streamable-http", "127.0.0.1", 8123, None), &config).unwrap().url.starts_with("https://"));
    // An agent installed by an older version still says sse; it serves /mcp anyway.
    assert_eq!(HttpTarget::installed(&args("sse", "::1", 1, None), &config).unwrap().url, "https://[::1]:1/mcp");
    assert!(HttpTarget::installed(&args("stdio", "127.0.0.1", 1, None), &config).is_err());
    assert!(HttpTarget::installed(&[], &config).is_err(), "no ProgramArguments");
}

#[test]
fn ipv6_hosts_are_bracketed() {
    assert_eq!(HttpTarget::installed(&args("streamable-http", "::1", 8000, None), &Config::default()).unwrap().url, "http://[::1]:8000/mcp");
}

// The Launch Agent is macOS-only; its paths are POSIX.
#[cfg(unix)]
#[test]
fn the_plist_has_the_originals_keys_and_escapes_its_text() {
    let xml = plist_xml(&args("streamable-http", "127.0.0.1", 8000, Some("/s.sock")), Path::new("/logs & more"), Some("/s.sock"));
    for key in ["Label", "ProgramArguments", "RunAtLoad", "KeepAlive", "StandardOutPath", "StandardErrorPath", "Sockets", "Listener", "SockPathName", "SockPathMode"] {
        assert!(xml.contains(&format!("<key>{key}</key>")), "{key} missing");
    }
    assert!(xml.contains(&format!("<string>{LABEL}</string>")));
    assert!(xml.contains("<string>/logs &amp; more/server.log</string>"));
    assert!(xml.contains("<integer>384</integer>"), "SockPathMode 0600");
    assert!(!plist_xml(&args("streamable-http", "127.0.0.1", 8000, None), Path::new("/l"), None).contains("Sockets"));
}

#[test]
fn only_streamable_http_runs_as_a_service() {
    assert!(validate_transport("streamable-http").is_ok());
    assert_eq!(validate_transport("stdio").unwrap_err(), "stdio is not supported as a service; use --transport streamable-http.");
    assert_eq!(validate_transport("sse").unwrap_err(), "Invalid transport: sse (sse was removed; use streamable-http)");
}

#[test]
fn a_taken_port_is_skipped() {
    let taken = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let port = taken.local_addr().unwrap().port();
    let found = find_free_port("127.0.0.1", port).unwrap();
    assert_ne!(found, port);
    assert!(found > port);
    assert_eq!(find_free_port("localhost", 0).unwrap(), 0, "port 0 is always free");
}
