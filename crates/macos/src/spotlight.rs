//! Spotlight through `MDQuery`: the query language, and a synchronous search on a libdispatch worker that a
//! watchdog stops at the deadline.
use desktop_mcp_core::clipboard::expand_tilde;
use desktop_mcp_core::spotlight::{preview, Found, Item, Kind, Query, Search};
use dispatch2::{DispatchQueue, DispatchQoS, DispatchTime, GlobalQueueIdentifier};
use objc2_core_foundation::{CFArray, CFDate, CFNumber, CFRetained, CFString, CFType};
use objc2_core_services::{
    kMDItemContentModificationDate, kMDItemContentTypeTree, kMDItemFSContentChangeDate, kMDItemFSSize, kMDItemKind, kMDItemPath, MDItem, MDQuery,
    MDQueryOptionFlags, MDQuerySortOptionFlags,
};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

/// Seconds from 1970 to 2001, CoreFoundation's epoch.
const CF_EPOCH: f64 = 978_307_200.0;

/// Escapes a word for use inside a quoted Spotlight string, where `*` would otherwise be a wildcard: the caller's
/// words are matched literally.
fn escape(word: &str) -> String {
    let mut escaped = String::with_capacity(word.len());
    for c in word.chars() {
        if matches!(c, '\\' | '"' | '*') {
            escaped.push('\\');
        }
        escaped.push(c);
    }
    escaped
}

/// Each word must appear in the display name or the text content.
pub fn words_query(words: &[String]) -> String {
    words.iter().map(|w| escape(w)).map(|w| format!(r#"(kMDItemDisplayName == "*{w}*"cdw || kMDItemTextContent == "{w}"cdw)"#)).collect::<Vec<_>>().join(" && ")
}

/// Each word must appear in the display name: the matches ranked first.
pub fn name_query(words: &[String]) -> String {
    words.iter().map(|w| format!(r#"kMDItemDisplayName == "*{}*"cdw"#, escape(w))).collect::<Vec<_>>().join(" && ")
}

/// The words query minus the name matches: at least one word is missing from the name, so this and
/// [`name_query`] never return the same item.
pub fn content_only_query(words: &[String]) -> String {
    let not_in_name: Vec<String> = words.iter().map(|w| format!(r#"kMDItemDisplayName != "*{}*"cdw"#, escape(w))).collect();
    format!("{} && ({})", words_query(words), not_in_name.join(" || "))
}

pub fn kind_clause(kind: Kind) -> Option<String> {
    let uti = match kind {
        Kind::Any => return None,
        Kind::Document => "public.content",
        Kind::Pdf => "com.adobe.pdf",
        Kind::Image => "public.image",
        Kind::Folder => "public.folder",
        Kind::Application => "com.apple.application",
    };
    Some(format!(r#"kMDItemContentTypeTree == "{uti}""#))
}

/// The caller's folders, or when there are none the application folders (searched recursively, so Utilities is
/// included) for applications and home otherwise, with `~` expanded.
pub fn scope(paths: &[String], kind: Kind) -> Vec<PathBuf> {
    let defaults: &[&str] = if kind == Kind::Application { &["/Applications", "/System/Applications", "~/Applications"] } else { &["~"] };
    let paths: Vec<&str> = if paths.is_empty() { defaults.to_vec() } else { paths.iter().map(String::as_str).collect() };
    paths.into_iter().map(expand_tilde).collect()
}

fn create(query: &str) -> Option<CFRetained<MDQuery>> {
    let sorting = CFArray::from_objects(&[unsafe { kMDItemContentModificationDate }.expect("a CoreServices constant")]);
    unsafe { MDQuery::new(None, Some(&CFString::from_str(query)), None, Some(sorting.as_opaque())) }
}

/// Runs `search` and reads its first `limit` results, newest first. Blocks until done or until the deadline: call
/// it off the main thread.
pub fn search(search: Search) -> Result<Found, String> {
    let deadline = Instant::now() + search.timeout;
    // The caller's part is checked alone, so the error quotes what they wrote rather than the kind clause.
    let query = match &search.query {
        Query::Raw(query) => query.clone(),
        Query::Names(words) => name_query(words),
        Query::ContentOnly(words) => content_only_query(words),
    };
    if create(&query).is_none() {
        return Err(format!("Invalid Spotlight query: {}", search.shown));
    }
    let narrowed = match kind_clause(search.kind) {
        Some(clause) => format!("({query}) && {clause}"),
        None => query,
    };
    let invalid = || format!("Invalid Spotlight query: {narrowed}");
    let md_query = create(&narrowed).ok_or_else(invalid)?;
    let paths: Vec<CFRetained<CFString>> = scope(&search.scope, search.kind).iter().map(|path| CFString::from_str(&path.to_string_lossy())).collect();
    unsafe {
        md_query.set_sort_option_flags_for_attribute(kMDItemContentModificationDate, MDQuerySortOptionFlags::ReverseSortOrderFlag.0);
        md_query.set_search_scope(Some(CFArray::from_retained_objects(&paths).as_opaque()), 0);
    }

    // Observed behaviour: MDQueryStop from another thread makes a synchronous MDQueryExecute return with what it
    // gathered so far. The watchdog only calls that, and the slot is emptied once the execute returns so it stops
    // pinning the query. The execute gets 80% of the time, so a stopped search still has time to read its first
    // results before the deadline.
    let running = Arc::new(Mutex::new(Some(Running(md_query.clone()))));
    let stopped_execute = Arc::new(AtomicBool::new(false));
    let budget = search.timeout.mul_f64(0.8);
    let watchdog = (running.clone(), stopped_execute.clone());
    let _ = DispatchQueue::global_queue(GlobalQueueIdentifier::QualityOfService(DispatchQoS::Default)).after(DispatchTime::try_from(budget).unwrap_or(DispatchTime::FOREVER), move || {
        if let Some(Running(query)) = watchdog.0.lock().unwrap().as_ref() {
            unsafe { query.stop() };
            watchdog.1.store(true, Ordering::SeqCst);
        }
    });
    let executed = unsafe { md_query.execute(MDQueryOptionFlags::Synchronous.0 as _) };
    running.lock().unwrap().take();
    if !executed {
        return Err(invalid());
    }
    unsafe { md_query.disable_updates() };

    let mut stopped = stopped_execute.load(Ordering::SeqCst);
    let total = unsafe { md_query.result_count() }.max(0) as usize;
    let mut items = Vec::new();
    for index in 0..total.min(search.limit) {
        if Instant::now() >= deadline {
            stopped = true;
            break;
        }
        let raw = unsafe { md_query.result_at_index(index as _) };
        let Some(md_item) = (unsafe { (raw as *const MDItem).as_ref() }) else { continue };
        if let Some(item) = read(md_item, search.content) {
            items.push(item);
        }
    }
    Ok(Found { items, total, stopped })
}

/// The query, movable to the watchdog's thread. MDQueryStop is the one call made there.
struct Running(CFRetained<MDQuery>);
unsafe impl Send for Running {}

fn attribute(item: &MDItem, name: Option<&'static CFString>) -> Option<CFRetained<CFType>> {
    unsafe { item.attribute(name) }
}

fn read(item: &MDItem, content: bool) -> Option<Item> {
    let path = attribute(item, unsafe { kMDItemPath })?.downcast_ref::<CFString>()?.to_string();
    let tree: Vec<String> = attribute(item, unsafe { kMDItemContentTypeTree })
        .and_then(|tree| {
            // An array of content types; its elements are read as CF objects and kept when they are strings.
            let array: &CFArray<CFType> = unsafe { &*(tree.downcast_ref::<CFArray>()? as *const CFArray as *const CFArray<CFType>) };
            Some((0..array.len()).filter_map(|i| unsafe { array.get_unchecked(i as _) }.downcast_ref::<CFString>().map(|s| s.to_string())).collect())
        })
        .unwrap_or_default();
    let date = |name| attribute(item, name).and_then(|value| value.downcast_ref::<CFDate>().map(|date| system_time(date.absolute_time())));
    let modified = date(unsafe { kMDItemContentModificationDate }).or_else(|| date(unsafe { kMDItemFSContentChangeDate }));
    let is_folder = tree.iter().any(|t| t == "public.folder");
    let size = if is_folder { None } else { attribute(item, unsafe { kMDItemFSSize }).and_then(|size| size.downcast_ref::<CFNumber>()?.as_i64()).map(|size| size.max(0) as u64) };
    let kind = attribute(item, unsafe { kMDItemKind }).and_then(|kind| kind.downcast_ref::<CFString>().map(|kind| kind.to_string())).unwrap_or_default();
    let text = if content && tree.iter().any(|t| t == "public.text") { preview(Path::new(&path)) } else { None };
    Some(Item { path, kind, modified, size, text })
}

fn system_time(absolute: f64) -> SystemTime {
    let since_1970 = absolute + CF_EPOCH;
    if since_1970 >= 0.0 { UNIX_EPOCH + Duration::from_secs_f64(since_1970) } else { UNIX_EPOCH - Duration::from_secs_f64(-since_1970) }
}
