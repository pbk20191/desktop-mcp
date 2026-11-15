//! Name resolution off the main thread.
mod support;
use desktop_mcp_core::mcp::Desktop;
use desktop_mcp_macos::desktop::MacDesktop;
use desktop_mcp_macos::main_loop::run;
use std::net::IpAddr;

fn resolve(host: &str) -> Result<Vec<IpAddr>, String> {
    let host = host.to_string();
    run(move |_| async move { MacDesktop::new().resolve(&host).await })
}

fn numeric_addresses_resolve_to_themselves() {
    assert_eq!(resolve("1.1.1.1"), Ok(vec!["1.1.1.1".parse().unwrap()]));
    assert_eq!(resolve("::1"), Ok(vec!["::1".parse().unwrap()]));
}

fn localhost_is_loopback() {
    let addresses = resolve("localhost").unwrap();
    assert!(!addresses.is_empty() && addresses.iter().all(IpAddr::is_loopback), "{addresses:?}");
}

fn unknown_names_fail_with_the_resolvers_reason() {
    assert_eq!(resolve("nowhere.invalid"), Err("nodename nor servname provided, or not known".to_string()));
}

fn main() {
    support::run(&[
        ("numeric_addresses_resolve_to_themselves", numeric_addresses_resolve_to_themselves),
        ("localhost_is_loopback", localhost_is_loopback),
        ("unknown_names_fail_with_the_resolvers_reason", unknown_names_fail_with_the_resolvers_reason),
    ])
}
