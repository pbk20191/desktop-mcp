//! What a client meets before any MCP: Origin and CORS, the IP allowlist, bearer keys, OAuth, the settings'
//! sources, and the refusal of an unauthenticated remote bind. Every test holds for both ports.
use base64::Engine;
use desktop_mcp_parity::{run_to_exit, Launch, ServerUnderTest};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};

const PING: &str = r#"{"jsonrpc":"2.0","id":1,"method":"ping"}"#;
const JSON: [(&str, &str); 2] = [("Accept", "application/json"), ("Content-Type", "application/json")];

fn ping(server: &ServerUnderTest, extra: &[(&str, &str)]) -> desktop_mcp_parity::Reply {
    let mut headers = JSON.to_vec();
    headers.extend_from_slice(extra);
    server.request("POST", "/mcp", &headers, PING.as_bytes())
}

#[test]
fn a_bearer_key_guards_every_request() {
    let server = ServerUnderTest::start_with(&["--auth-key", "s3cret"]);
    let none = ping(&server, &[]);
    assert_eq!(none.status, 401);
    assert_eq!(none.header("www-authenticate"), Some("Bearer"));
    let wrong = ping(&server, &[("Authorization", "Bearer nope")]);
    assert_eq!(wrong.status, 401);
    assert_eq!(wrong.json()["error"], "Invalid authentication token");
    assert_eq!(ping(&server, &[("Authorization", "Bearer s3cret")]).status, 200);
    assert_ne!(server.request("GET", "/health", &[], b"").status, 401, "/health needs no key");
}

#[test]
fn the_key_can_come_from_the_environment_or_the_config_file() {
    let from_env = ServerUnderTest::launch(&Launch { env: &[("DESKTOP_MCP_AUTH_KEY", "from-env")], ..Launch::default() });
    assert_eq!(ping(&from_env, &[]).status, 401);
    assert_eq!(ping(&from_env, &[("Authorization", "Bearer from-env")]).status, 200);
    let from_config = ServerUnderTest::launch(&Launch { config: Some("[server]\nauth_key = \"from-config\"\n"), ..Launch::default() });
    assert_eq!(ping(&from_config, &[("Authorization", "Bearer from-config")]).status, 200);
    let overridden = ServerUnderTest::launch(&Launch { options: &["--auth-key", "from-cli"], env: &[("DESKTOP_MCP_AUTH_KEY", "from-env")], config: Some("[server]\nauth_key = \"from-config\"\n") });
    assert_eq!(ping(&overridden, &[("Authorization", "Bearer from-cli")]).status, 200);
    assert_eq!(ping(&overridden, &[("Authorization", "Bearer from-env")]).status, 401);
}

#[test]
fn the_ip_allowlist_refuses_other_clients() {
    let elsewhere = ServerUnderTest::start_with(&["--ip-allowlist", "10.0.0.0/8"]);
    let refused = ping(&elsewhere, &[]);
    assert_eq!(refused.status, 403);
    assert_eq!(refused.json()["error"], "Forbidden: 127.0.0.1 is not in the IP allowlist");
    let here = ServerUnderTest::start_with(&["--ip-allowlist", "10.0.0.0/8, 127.0.0.1"]);
    assert_eq!(ping(&here, &[]).status, 200);
}

#[test]
fn only_local_pages_may_call() {
    let server = ServerUnderTest::start();
    let local = ping(&server, &[("Origin", "http://localhost:3000")]);
    assert_eq!(local.status, 200);
    assert_eq!(local.header("access-control-allow-origin"), Some("http://localhost:3000"));
    let foreign = ping(&server, &[("Origin", "https://x.example")]);
    assert_eq!(foreign.status, 403);
    assert_eq!(foreign.header("access-control-allow-origin"), None);
    let preflight = server.request("OPTIONS", "/mcp", &[], b"");
    assert_eq!(preflight.status, 200);
    assert_eq!(preflight.header("access-control-allow-origin"), Some("*"));
}

#[test]
fn an_unauthenticated_remote_bind_is_refused() {
    let (status, stderr) = run_to_exit(&["serve", "--transport", "streamable-http", "--host", "0.0.0.0", "--port", "0"]);
    assert!(!status.success());
    // Swift: "Refusing to bind HTTP transport to '0.0.0.0' without authentication."; Rust refuses HTTP without
    // authentication wherever it is bound.
    assert!(stderr.contains("authentication"), "{stderr}");
}

#[test]
fn a_bad_allowlist_is_refused_naming_the_entry() {
    let (status, stderr) = run_to_exit(&["serve", "--transport", "streamable-http", "--port", "0", "--ip-allowlist", "10.0.0.0/33,nope"]);
    assert!(!status.success());
    assert!(stderr.contains("nope"), "{stderr}");
}

fn query_value(location: &str, name: &str) -> Option<String> {
    let query = location.split_once('?')?.1;
    form_urlencoded::parse(query.as_bytes()).find(|(key, _)| key == name).map(|(_, value)| value.into_owned())
}

#[test]
fn oauth_authorization_code_flow() {
    let server = ServerUnderTest::start_with(&["--oauth-client-id", "cid", "--oauth-client-secret", "csecret"]);
    let metadata = server.request("GET", "/.well-known/oauth-authorization-server", &[], b"").json();
    assert!(metadata["token_endpoint"].as_str().unwrap().ends_with("/oauth/token"), "{metadata}");
    assert_eq!(metadata["code_challenge_methods_supported"], json!(["S256"]));
    assert_eq!(server.request("POST", "/oauth/register", &[], b"").status, 403);
    assert_eq!(ping(&server, &[]).status, 401, "OAuth guards /mcp");

    let verifier = "verifier-0123456789-abcdefghijklmnopqrstuvwxyz";
    let challenge = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(Sha256::digest(verifier.as_bytes()));
    let redirect = "http://localhost:3333/callback";
    let authorize = format!("/oauth/authorize?response_type=code&client_id=cid&redirect_uri={redirect}&code_challenge={challenge}&code_challenge_method=S256&state=xyz");
    let redirected = server.request("GET", &authorize, &[], b"");
    assert_eq!(redirected.status, 302);
    let location = redirected.header("location").unwrap().to_string();
    assert!(location.starts_with(&format!("{redirect}?code=")) && location.ends_with("&state=xyz"), "{location}");
    let code = query_value(&location, "code").unwrap();

    let form = format!("grant_type=authorization_code&code={code}&client_id=cid&redirect_uri={redirect}&code_verifier={verifier}&client_secret=csecret");
    let token = server.request("POST", "/oauth/token", &[("Content-Type", "application/x-www-form-urlencoded")], form.as_bytes());
    assert_eq!(token.status, 200);
    let token: Value = token.json();
    assert_eq!(token["token_type"], "Bearer");
    let access_token = token["access_token"].as_str().unwrap();
    assert_eq!(server.request("POST", "/oauth/token", &[("Content-Type", "application/x-www-form-urlencoded")], form.as_bytes()).status, 400, "codes are single-use");
    assert_eq!(ping(&server, &[("Authorization", &format!("Bearer {access_token}"))]).status, 200);
    let remote = format!("/oauth/authorize?response_type=code&client_id=cid&redirect_uri=https://evil.example/cb&code_challenge={challenge}");
    assert_eq!(server.request("GET", &remote, &[], b"").status, 400, "only loopback redirects");
}
