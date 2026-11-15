//! What runs before routing: Origin and CORS, the IP allowlist, bearer and OAuth authentication, and the OAuth
//! endpoints. Ported from the Swift port's HTTPAppTests.
mod support;
use desktop_mcp_core::http::{handle, PeerAddress, Security};
use desktop_mcp_core::oauth::OAuth;
use desktop_mcp_core::security::{base64_url, IpNetwork};
use http::{Request, StatusCode};
use sha2::{Digest, Sha256};
use std::cell::Cell;
use std::rc::Rc;
use std::time::{Duration, Instant};
use support::*;

const PING: &str = r#"{"jsonrpc":"2.0","id":1,"method":"ping"}"#;

fn post(headers: &[(&str, &str)], remote: Option<&str>) -> Request<http_body_util::Full<bytes::Bytes>> {
    let mut builder = Request::builder().method("POST").uri("/mcp").header("Content-Type", "application/json").header("Accept", "application/json");
    for (name, value) in headers {
        builder = builder.header(*name, *value);
    }
    let mut request = builder.body(body(PING)).unwrap();
    request.extensions_mut().insert(PeerAddress(remote.map(|address| address.parse().unwrap())));
    request
}

fn get(target: &str, headers: &[(&str, &str)]) -> Request<http_body_util::Full<bytes::Bytes>> {
    let mut builder = Request::builder().method("GET").uri(target);
    for (name, value) in headers {
        builder = builder.header(*name, *value);
    }
    builder.body(body("")).unwrap()
}

fn with_key(key: &str) -> Security {
    Security { auth_key: Some(key.into()), ..Security::default() }
}

#[test]
fn bearer_auth() {
    block_on(async {
        let fixture = fixture_secured(with_key("s3cret"));
        let none = handle(&fixture.app, post(&[], Some("127.0.0.1"))).await;
        assert_eq!(none.status(), StatusCode::UNAUTHORIZED);
        assert_eq!(none.headers()["www-authenticate"], "Bearer");
        let wrong = handle(&fixture.app, post(&[("Authorization", "Bearer nope")], Some("127.0.0.1"))).await;
        assert_eq!(wrong.status(), StatusCode::UNAUTHORIZED);
        assert_eq!(json_of(wrong).await["error"], "Invalid authentication token");
        let right = handle(&fixture.app, post(&[("Authorization", "Bearer s3cret")], Some("127.0.0.1"))).await;
        assert_eq!(right.status(), StatusCode::OK);
    })
}

#[test]
fn health_stays_public() {
    block_on(async {
        let fixture = fixture_secured(Security { auth_key: Some("s3cret".into()), ip_allowlist: vec!["10.0.0.0/8".parse::<IpNetwork>().unwrap()], ..Security::default() });
        let health = handle(&fixture.app, get("/health", &[])).await;
        assert_eq!(health.status(), StatusCode::NOT_FOUND, "no route answers it, but neither auth nor the allowlist stops it");
    })
}

#[test]
fn options_and_cors() {
    block_on(async {
        let fixture = fixture();
        let preflight = handle(&fixture.app, Request::builder().method("OPTIONS").uri("/mcp").body(body("")).unwrap()).await;
        assert_eq!(preflight.status(), StatusCode::OK);
        assert_eq!(preflight.headers()["access-control-allow-origin"], "*");
        // A page served from this machine may call the server; its origin is echoed back.
        let local = handle(&fixture.app, post(&[("Origin", "http://localhost:3000")], Some("127.0.0.1"))).await;
        assert_eq!(local.status(), StatusCode::OK);
        assert_eq!(local.headers()["access-control-allow-origin"], "http://localhost:3000");
        for origin in ["http://[::1]:8080", "https://127.0.0.1"] {
            assert_eq!(handle(&fixture.app, post(&[("Origin", origin)], Some("127.0.0.1"))).await.status(), StatusCode::OK, "{origin}");
        }
        // Any other website is refused before auth or routing, preflight included.
        let foreign = handle(&fixture.app, post(&[("Origin", "https://x.example")], Some("127.0.0.1"))).await;
        assert_eq!(foreign.status(), StatusCode::FORBIDDEN);
        assert!(foreign.headers().get("access-control-allow-origin").is_none());
        let foreign_preflight = handle(&fixture.app, Request::builder().method("OPTIONS").uri("/mcp").header("Origin", "https://x.example").body(body("")).unwrap()).await;
        assert_eq!(foreign_preflight.status(), StatusCode::FORBIDDEN);
        for origin in ["null", "file:///x", "http://localhost.evil.example"] {
            assert_eq!(handle(&fixture.app, post(&[("Origin", origin)], Some("127.0.0.1"))).await.status(), StatusCode::FORBIDDEN, "{origin}");
        }
    })
}

#[test]
fn ip_allowlist() {
    block_on(async {
        let fixture = fixture_secured(Security { ip_allowlist: vec!["10.0.0.0/8".parse().unwrap()], ..Security::default() });
        let outside = handle(&fixture.app, post(&[], Some("192.168.0.2"))).await;
        assert_eq!(outside.status(), StatusCode::FORBIDDEN);
        assert_eq!(json_of(outside).await["error"], "Forbidden: 192.168.0.2 is not in the IP allowlist");
        assert_eq!(handle(&fixture.app, post(&[], None)).await.status(), StatusCode::FORBIDDEN, "no peer address");
        assert_eq!(handle(&fixture.app, post(&[], Some("10.9.9.9"))).await.status(), StatusCode::OK);
        assert_eq!(handle(&fixture.app, post(&[], Some("::ffff:10.1.1.1"))).await.status(), StatusCode::OK);
    })
}

#[test]
fn oauth_metadata_advertises_the_host_clients_used() {
    let wildcard = OAuth::new("https://0.0.0.0:8000", "c", "s");
    let seen = wildcard.metadata(Some("mac.local:8000"));
    assert_eq!(seen["token_endpoint"], "https://mac.local:8000/oauth/token");
    let fixed = OAuth::new("https://127.0.0.1:8000", "c", "s");
    assert_eq!(fixed.metadata(Some("mac.local:8000"))["issuer"], "https://127.0.0.1:8000");
}

fn challenge(verifier: &str) -> String {
    base64_url(&Sha256::digest(verifier.as_bytes()))
}

/// The `code` of a redirect's `Location`.
fn code_of(location: &str) -> String {
    let query = location.split_once('?').unwrap().1;
    form_urlencoded::parse(query.as_bytes()).find(|(name, _)| name == "code").unwrap().1.into_owned()
}

#[test]
fn oauth_authorization_code_flow_with_pkce() {
    block_on(async {
        let oauth = Rc::new(OAuth::new("http://127.0.0.1:8000", "cid", "csecret"));
        let fixture = fixture_secured(Security { auth_key: Some("static".into()), oauth: Some(oauth), ..Security::default() });
        let app = &fixture.app;
        let metadata = handle(app, get("/.well-known/oauth-authorization-server", &[])).await;
        assert_eq!(json_of(metadata).await["token_endpoint"], "http://127.0.0.1:8000/oauth/token");
        let register = handle(app, Request::builder().method("POST").uri("/oauth/register").body(body("")).unwrap()).await;
        assert_eq!(register.status(), StatusCode::FORBIDDEN);

        let verifier = "verifier-0123456789-abcdefghijklmnopqrstuvwxyz";
        let redirect = "http://localhost:3333/callback";
        let authorize = || {
            get(&format!("/oauth/authorize?response_type=code&client_id=cid&redirect_uri={redirect}&code_challenge={}&code_challenge_method=S256&state=xyz", challenge(verifier)), &[])
        };
        let redirected = handle(app, authorize()).await;
        assert_eq!(redirected.status(), StatusCode::FOUND);
        let location = redirected.headers()["location"].to_str().unwrap().to_string();
        assert!(location.starts_with(&format!("{redirect}?code=")), "{location}");
        assert!(location.ends_with("&state=xyz"), "{location}");

        let exchange = |code: &str, verifier: &str| {
            let form = format!("grant_type=authorization_code&code={code}&client_id=cid&redirect_uri={redirect}&code_verifier={verifier}&client_secret=csecret");
            Request::builder().method("POST").uri("/oauth/token").header("Content-Type", "application/x-www-form-urlencoded").body(body(&form)).unwrap()
        };
        let bad_verifier = handle(app, exchange(&code_of(&location), "wrong")).await;
        assert_eq!(json_of(bad_verifier).await["error_description"], "PKCE verification failed");

        let second = handle(app, authorize()).await;
        let code = code_of(second.headers()["location"].to_str().unwrap());
        let token = handle(app, exchange(&code, verifier)).await;
        assert_eq!(token.status(), StatusCode::OK);
        let token = json_of(token).await;
        assert_eq!(token["token_type"], "Bearer");
        assert_eq!(token["expires_in"], 3600);
        let access_token = token["access_token"].as_str().unwrap().to_string();
        assert_eq!(handle(app, exchange(&code, verifier)).await.status(), StatusCode::BAD_REQUEST, "codes are single-use");

        let call = handle(app, post(&[("Authorization", &format!("Bearer {access_token}"))], Some("127.0.0.1"))).await;
        assert_eq!(call.status(), StatusCode::OK);
    })
}

#[test]
fn oauth_rejections() {
    block_on(async {
        let oauth = Rc::new(OAuth::new("http://127.0.0.1:8000", "cid", "csecret"));
        let fixture = fixture_secured(Security { oauth: Some(oauth.clone()), ..Security::default() });
        let base = "response_type=code&client_id=cid&code_challenge=abc&code_challenge_method=S256";
        for query in [
            format!("{base}&redirect_uri=https://evil.example/cb"),
            "response_type=code&client_id=cid&code_challenge=abc&code_challenge_method=plain&redirect_uri=http://127.0.0.1/cb".into(),
            "response_type=code&client_id=other&code_challenge=abc&redirect_uri=http://127.0.0.1/cb".into(),
            "response_type=code&client_id=cid&redirect_uri=http://127.0.0.1/cb".into(),
            "response_type=token&client_id=cid&code_challenge=abc&redirect_uri=http://127.0.0.1/cb".into(),
        ] {
            assert_eq!(handle(&fixture.app, get(&format!("/oauth/authorize?{query}"), &[])).await.status(), StatusCode::BAD_REQUEST, "{query}");
        }
        let token = Request::builder().method("POST").uri("/oauth/token").header("Content-Type", "application/json").body(body(r#"{"grant_type":"password"}"#)).unwrap();
        assert_eq!(handle(&fixture.app, token).await.status(), StatusCode::BAD_REQUEST);
        assert!(!oauth.validate("made-up"));
        // Without the key, an OAuth-only server still refuses a call with no token.
        assert_eq!(handle(&fixture.app, post(&[], Some("127.0.0.1"))).await.status(), StatusCode::UNAUTHORIZED);
    })
}

#[test]
fn oauth_tokens_expire() {
    block_on(async {
        let offset = Rc::new(Cell::new(Duration::ZERO));
        let start = Instant::now();
        let clock = offset.clone();
        let oauth = Rc::new(OAuth::new("http://127.0.0.1:8000", "cid", "csecret").with_clock(move || start + clock.get()));
        let fixture = fixture_secured(Security { oauth: Some(oauth.clone()), ..Security::default() });
        let redirected = handle(&fixture.app, get(&format!("/oauth/authorize?response_type=code&client_id=cid&redirect_uri=http://127.0.0.1/cb&code_challenge={}", challenge("v")), &[])).await;
        let code = code_of(redirected.headers()["location"].to_str().unwrap());
        let body_text = format!(r#"{{"grant_type":"authorization_code","code":"{code}","client_id":"cid","redirect_uri":"http://127.0.0.1/cb","code_verifier":"v","client_secret":"csecret"}}"#);
        let token = handle(&fixture.app, Request::builder().method("POST").uri("/oauth/token").header("Content-Type", "application/json").body(body(&body_text)).unwrap()).await;
        let access_token = json_of(token).await["access_token"].as_str().unwrap().to_string();
        assert!(oauth.validate(&access_token));
        offset.set(Duration::from_secs(3601));
        assert!(!oauth.validate(&access_token));
    })
}

#[test]
fn a_wrong_client_secret_is_401() {
    block_on(async {
        let oauth = Rc::new(OAuth::new("http://127.0.0.1:8000", "cid", "csecret"));
        let fixture = fixture_secured(Security { oauth: Some(oauth), ..Security::default() });
        let redirected = handle(&fixture.app, get(&format!("/oauth/authorize?response_type=code&client_id=cid&redirect_uri=http://127.0.0.1/cb&code_challenge={}", challenge("v")), &[])).await;
        let code = code_of(redirected.headers()["location"].to_str().unwrap());
        let form = format!("grant_type=authorization_code&code={code}&client_id=cid&redirect_uri=http://127.0.0.1/cb&code_verifier=v&client_secret=nope");
        let token = handle(&fixture.app, Request::builder().method("POST").uri("/oauth/token").header("Content-Type", "application/x-www-form-urlencoded").body(body(&form)).unwrap()).await;
        assert_eq!(token.status(), StatusCode::UNAUTHORIZED);
        assert_eq!(json_of(token).await["error"], "invalid_client");
    })
}

#[test]
fn query_decodes_form_encoding() {
    block_on(async {
        let oauth = Rc::new(OAuth::new("http://127.0.0.1:8000", "cid", "csecret"));
        let fixture = fixture_secured(Security { oauth: Some(oauth), ..Security::default() });
        let redirected = handle(&fixture.app, get(&format!("/oauth/authorize?response_type=code&client_id=cid&redirect_uri=http://127.0.0.1/cb&code_challenge={}&state=a+b%2Bc", challenge("v")), &[])).await;
        let location = redirected.headers()["location"].to_str().unwrap().to_string();
        assert!(location.ends_with("&state=a%20b%2Bc"), "{location}");
    })
}
