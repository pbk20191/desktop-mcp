//! Spotlight queries and MDQuery: the cases of the Swift port's SpotlightToolTests. Live searches only read the
//! index, and are skipped when it answers nothing (indexing off).
mod support;
use desktop_mcp_core::mcp::Desktop;
use desktop_mcp_core::spotlight::{Found, Kind, Query, Search};
use desktop_mcp_macos::desktop::MacDesktop;
use desktop_mcp_macos::main_loop::run;
use desktop_mcp_macos::spotlight::{content_only_query, name_query, words_query};
use std::time::{Duration, Instant};

fn words(text: &str) -> Vec<String> {
    text.split_whitespace().map(str::to_string).collect()
}

fn queries_match_each_word_in_name_or_text() {
    assert_eq!(
        words_query(&words("tax 2025")),
        r#"(kMDItemDisplayName == "*tax*"cdw || kMDItemTextContent == "tax"cdw) && (kMDItemDisplayName == "*2025*"cdw || kMDItemTextContent == "2025"cdw)"#
    );
    assert_eq!(words_query(&words(r#"a"b\c*="#)), r#"(kMDItemDisplayName == "*a\"b\\c\*=*"cdw || kMDItemTextContent == "a\"b\\c\*="cdw)"#);
    assert_eq!(name_query(&words("tax 2025")), r#"kMDItemDisplayName == "*tax*"cdw && kMDItemDisplayName == "*2025*"cdw"#);
    assert_eq!(name_query(&words("a*b")), r#"kMDItemDisplayName == "*a\*b*"cdw"#);
    assert_eq!(content_only_query(&words("tax 2025")), words_query(&words("tax 2025")) + r#" && (kMDItemDisplayName != "*tax*"cdw || kMDItemDisplayName != "*2025*"cdw)"#);
    assert_eq!(content_only_query(&words(r#"a"b"#)), r#"(kMDItemDisplayName == "*a\"b*"cdw || kMDItemTextContent == "a\"b"cdw) && (kMDItemDisplayName != "*a\"b*"cdw)"#);
}

fn search(query: Query, shown: &str, scope: &[&str], limit: usize, timeout: Duration) -> Result<Found, String> {
    let search = Search { query, shown: shown.into(), kind: Kind::Any, scope: scope.iter().map(|s| s.to_string()).collect(), limit, timeout, content: false };
    run(move |_| async move { MacDesktop::new().search_files(search).await })
}

fn an_invalid_raw_query_quotes_what_was_written() {
    for kind in [Kind::Any, Kind::Pdf] {
        let search = Search { query: Query::Raw("kMDItem ==".into()), shown: "kMDItem ==".into(), kind, scope: vec![], limit: 5, timeout: Duration::from_secs(10), content: false };
        let outcome = run(move |_| async move { MacDesktop::new().search_files(search).await });
        assert_eq!(outcome, Err("Invalid Spotlight query: kMDItem ==".to_string()), "{kind:?}");
    }
}

fn a_live_query_finds_safari() {
    let found = search(Query::Raw(r#"kMDItemFSName == "Safari.app""#.into()), "", &["/Applications"], 20, Duration::from_secs(10)).unwrap();
    if found.items.is_empty() {
        return eprintln!("    skipped: Spotlight returned nothing (indexing disabled?)");
    }
    assert_eq!((found.items.len(), found.total, found.stopped), (1, 1, false));
    let safari = &found.items[0];
    assert!(safari.path.ends_with("/Safari.app"), "{}", safari.path);
    assert!(!safari.kind.is_empty());
    assert!(safari.modified.is_some());
}

fn a_live_search_stops_at_its_deadline() {
    let start = Instant::now();
    let home = std::env::var("HOME").unwrap();
    let found = search(Query::Names(words("a")), "a", &[&home], 5, Duration::from_millis(200)).unwrap();
    if !found.stopped && found.total == 0 {
        return eprintln!("    skipped: Spotlight returned nothing (indexing disabled?)");
    }
    assert!(found.stopped, "{found:?}");
    assert!(start.elapsed() < Duration::from_secs(3), "took {:?}", start.elapsed());
    assert!(found.items.len() <= 5);
}

fn main() {
    support::run(&[
        ("queries_match_each_word_in_name_or_text", queries_match_each_word_in_name_or_text),
        ("an_invalid_raw_query_quotes_what_was_written", an_invalid_raw_query_quotes_what_was_written),
        ("a_live_query_finds_safari", a_live_query_finds_safari),
        ("a_live_search_stops_at_its_deadline", a_live_search_stops_at_its_deadline),
    ])
}
