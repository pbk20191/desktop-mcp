//! A minimal OAuth 2.0 authorization server for MCP clients, as in the original: RFC 8414 metadata,
//! authorization code with PKCE (S256 only), one pre-provisioned confidential client (dynamic registration is
//! refused), loopback redirect URIs only, state in memory.
use crate::security::{base64_url, constant_time_eq, is_loopback_host, token_url_safe};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::cell::RefCell;
use std::collections::HashMap;
use std::time::{Duration, Instant};

pub const TOKEN_LIFETIME: Duration = Duration::from_secs(3600);
pub const CODE_LIFETIME: Duration = Duration::from_secs(300);
/// `/oauth/authorize` is unauthenticated, so pending codes are bounded: beyond this the oldest are dropped.
const MAX_PENDING_CODES: usize = 1000;

struct AuthorizationCode {
    client_id: String,
    redirect_uri: String,
    challenge: String,
    expires_at: Instant,
}

#[derive(Default)]
struct State {
    codes: HashMap<String, AuthorizationCode>,
    tokens: HashMap<String, Instant>,
}

/// What an OAuth endpoint answers.
pub struct Reply {
    pub status: u16,
    /// A redirect's target.
    pub location: Option<String>,
    pub body: Option<Value>,
}

impl Reply {
    fn json(status: u16, body: Value) -> Reply {
        Reply { status, location: None, body: Some(body) }
    }

    /// An RFC 6749 error response.
    fn failure(error: &str, description: Option<&str>, status: u16) -> Reply {
        let mut body = json!({ "error": error });
        if let Some(description) = description {
            body["error_description"] = json!(description);
        }
        Reply::json(status, body)
    }
}

pub struct OAuth {
    issuer: String,
    client_id: String,
    client_secret: String,
    state: RefCell<State>,
    now: Box<dyn Fn() -> Instant>,
}

impl OAuth {
    pub fn new(issuer: &str, client_id: &str, client_secret: &str) -> OAuth {
        OAuth { issuer: issuer.into(), client_id: client_id.into(), client_secret: client_secret.into(), state: RefCell::default(), now: Box::new(Instant::now) }
    }

    /// Replaces the clock (tests).
    pub fn with_clock(mut self, now: impl Fn() -> Instant + 'static) -> OAuth {
        self.now = Box::new(now);
        self
    }

    /// Whether `token` is a live access token. Expired tokens are forgotten.
    pub fn validate(&self, token: &str) -> bool {
        let now = (self.now)();
        let mut state = self.state.borrow_mut();
        match state.tokens.get(token) {
            Some(expiry) if now <= *expiry => true,
            Some(_) => {
                state.tokens.remove(token);
                false
            }
            None => false,
        }
    }

    /// The configured issuer, except that a wildcard bind address (0.0.0.0, ::) is useless to a client, so then
    /// the address the client actually used (its `Host` header) is advertised.
    fn issuer_for(&self, host: Option<&str>) -> String {
        let Some((scheme, configured)) = self.issuer.split_once("://") else { return self.issuer.clone() };
        let wildcard = ["0.0.0.0", "[::]", "::"].iter().any(|w| configured == *w || configured.starts_with(&format!("{w}:")));
        match host.filter(|h| !h.is_empty()) {
            Some(host) if wildcard => format!("{scheme}://{host}"),
            _ => self.issuer.clone(),
        }
    }

    pub fn metadata(&self, host: Option<&str>) -> Value {
        let issuer = self.issuer_for(host);
        json!({
            "issuer": issuer,
            "authorization_endpoint": format!("{issuer}/oauth/authorize"),
            "token_endpoint": format!("{issuer}/oauth/token"),
            "response_types_supported": ["code"],
            "grant_types_supported": ["authorization_code"],
            "code_challenge_methods_supported": ["S256"],
            "token_endpoint_auth_methods_supported": ["client_secret_post"],
        })
    }

    pub fn register(&self) -> Reply {
        Reply::failure("registration_disabled", Some("Dynamic OAuth client registration is disabled. Configure --oauth-client-id and --oauth-client-secret on the server."), 403)
    }

    /// `GET /oauth/authorize` with its query string.
    pub fn authorize(&self, query: &str) -> Reply {
        let params = first_values(query.as_bytes());
        let param = |name: &str| params.get(name).cloned().unwrap_or_default();
        let (client_id, redirect_uri, state, challenge) = (param("client_id"), param("redirect_uri"), param("state"), param("code_challenge"));
        let method = params.get("code_challenge_method").cloned().unwrap_or_else(|| "S256".into());
        if param("response_type") != "code" {
            return Reply::failure("unsupported_response_type", None, 400);
        }
        if challenge.is_empty() {
            return Reply::failure("invalid_request", Some("code_challenge required (PKCE)"), 400);
        }
        if method != "S256" {
            return Reply::failure("invalid_request", Some("code_challenge_method must be S256"), 400);
        }
        if !is_loopback_redirect(&redirect_uri) {
            return Reply::failure("invalid_request", Some("redirect_uri must be a loopback http(s) URI"), 400);
        }
        if client_id != self.client_id {
            return Reply::failure("invalid_client", None, 400);
        }
        self.sweep();
        let code = token_url_safe(32);
        let expires_at = (self.now)() + CODE_LIFETIME;
        self.state.borrow_mut().codes.insert(code.clone(), AuthorizationCode { client_id, redirect_uri: redirect_uri.clone(), challenge, expires_at });
        let mut location = format!("{redirect_uri}{}code={}", if redirect_uri.contains('?') { '&' } else { '?' }, quote(&code));
        if !state.is_empty() {
            location += &format!("&state={}", quote(&state));
        }
        Reply { status: 302, location: Some(location), body: None }
    }

    /// `POST /oauth/token`: the body as form fields, or a JSON object when `content_type` says so.
    pub fn token(&self, content_type: &str, body: &[u8]) -> Reply {
        let Some(fields) = form_or_json(content_type, body) else { return Reply::failure("invalid_request", None, 400) };
        let field = |name: &str| fields.get(name).cloned().unwrap_or_default();
        if field("grant_type") != "authorization_code" {
            return Reply::failure("unsupported_grant_type", None, 400);
        }
        let code_value = field("code");
        let invalid_grant = |description: &str| Reply::failure("invalid_grant", Some(description), 400);
        {
            let mut state = self.state.borrow_mut();
            let Some(code) = state.codes.get(&code_value) else { return invalid_grant("unknown code") };
            if code.client_id != field("client_id") {
                return invalid_grant("client_id mismatch");
            }
            if code.redirect_uri != field("redirect_uri") {
                return invalid_grant("redirect_uri mismatch");
            }
            if (self.now)() > code.expires_at {
                state.codes.remove(&code_value);
                return invalid_grant("code expired");
            }
            let verified = constant_time_eq(&base64_url(&Sha256::digest(field("code_verifier").as_bytes())), &code.challenge);
            // One-time use; RFC 7636 §4.6: a failed verification burns the code too.
            state.codes.remove(&code_value);
            if !verified {
                return invalid_grant("PKCE verification failed");
            }
        }
        self.sweep();
        let secret = field("client_secret");
        if secret.is_empty() || !constant_time_eq(&secret, &self.client_secret) {
            return Reply::failure("invalid_client", None, 401);
        }
        let access_token = token_url_safe(48);
        self.state.borrow_mut().tokens.insert(access_token.clone(), (self.now)() + TOKEN_LIFETIME);
        Reply::json(200, json!({ "access_token": access_token, "token_type": "Bearer", "expires_in": TOKEN_LIFETIME.as_secs() }))
    }

    /// Forgets expired codes and tokens, and caps the number of pending codes.
    fn sweep(&self) {
        let now = (self.now)();
        let mut state = self.state.borrow_mut();
        state.codes.retain(|_, code| code.expires_at > now);
        state.tokens.retain(|_, expiry| *expiry > now);
        if state.codes.len() > MAX_PENDING_CODES {
            let mut by_age: Vec<(String, Instant)> = state.codes.iter().map(|(code, entry)| (code.clone(), entry.expires_at)).collect();
            by_age.sort_by_key(|(_, expires_at)| *expires_at);
            let excess = state.codes.len() - MAX_PENDING_CODES;
            for (code, _) in by_age.into_iter().take(excess) {
                state.codes.remove(&code);
            }
        }
    }
}

/// The first value of each form field, `+` and percent escapes decoded.
fn first_values(form: &[u8]) -> HashMap<String, String> {
    let mut fields = HashMap::new();
    for (name, value) in form_urlencoded::parse(form) {
        fields.entry(name.into_owned()).or_insert_with(|| value.into_owned());
    }
    fields
}

fn form_or_json(content_type: &str, body: &[u8]) -> Option<HashMap<String, String>> {
    if !content_type.to_ascii_lowercase().contains("application/json") {
        return Some(first_values(body));
    }
    let object: serde_json::Map<String, Value> = serde_json::from_slice(body).ok()?;
    Some(
        object
            .into_iter()
            .filter_map(|(key, value)| match value {
                Value::String(s) => Some((key, s)),
                Value::Number(n) => Some((key, n.to_string())),
                Value::Bool(b) => Some((key, b.to_string())),
                _ => None,
            })
            .collect(),
    )
}

fn is_loopback_redirect(uri: &str) -> bool {
    let Some((scheme, rest)) = uri.split_once("://") else { return false };
    if !matches!(scheme.to_ascii_lowercase().as_str(), "http" | "https") || uri.contains('#') {
        return false;
    }
    let authority = rest.split(['/', '?']).next().unwrap_or_default();
    let authority = authority.rsplit_once('@').map_or(authority, |(_, host)| host);
    let host = if let Some(bracketed) = authority.strip_prefix('[') {
        bracketed.split(']').next().unwrap_or_default()
    } else {
        authority.split(':').next().unwrap_or_default()
    };
    !host.is_empty() && is_loopback_host(host)
}

/// Python's `urllib.parse.quote` with its default safe set (`/`).
fn quote(text: &str) -> String {
    text.bytes()
        .map(|byte| match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' | b'/' => (byte as char).to_string(),
            _ => format!("%{byte:02X}"),
        })
        .collect()
}
