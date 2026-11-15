//! The Spotlight tool: words matched in names first, then in content only, under one deadline, and the result
//! lines. The platform owns the index and its query language.
use crate::mcp::Desktop;
use rmcp::schemars;
use std::io::Read;
use std::path::Path;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

/// The whole call's time, both searches included.
pub const DEADLINE: Duration = Duration::from_secs(10);
pub const STOPPED_LINE: &str = "(search stopped after 10 s; results may be incomplete)";
pub const MAX_LIMIT: i64 = 200;
const PREVIEW_CHARS: usize = 300;
const PREVIEW_BYTES: u64 = 4096;

// The type of item looked for. (A plain comment: a doc comment would reach the schema the model reads.)
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum Kind {
    #[default]
    Any,
    Document,
    Pdf,
    Image,
    Folder,
    Application,
}

/// What a search matches.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Query {
    /// The platform's own query language, as the caller wrote it.
    Raw(String),
    /// Every word appears in the display name.
    Names(Vec<String>),
    /// Every word appears in the name or the text, and at least one is missing from the name: never an item
    /// [`Query::Names`] returns.
    ContentOnly(Vec<String>),
}

/// One search of the index.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Search {
    pub query: Query,
    /// What the caller wrote, quoted when the platform rejects the query.
    pub shown: String,
    pub kind: Kind,
    /// Folders as given (`~` unexpanded); empty means the platform's default for `kind`.
    pub scope: Vec<String>,
    /// Items to read, newest first; 0 only counts.
    pub limit: usize,
    /// The search, reads included, stops at this.
    pub timeout: Duration,
    /// Whether text files get a [`preview`].
    pub content: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Item {
    pub path: String,
    pub kind: String,
    pub modified: Option<SystemTime>,
    /// None for folders.
    pub size: Option<u64>,
    pub text: Option<String>,
}

/// The items read, how many matched in all, and whether the deadline cut the search short.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Found {
    pub items: Vec<Item>,
    pub total: usize,
    pub stopped: bool,
}

/// The caller's whitespace-separated words.
pub fn words(query: &str) -> Result<Vec<String>, String> {
    let words: Vec<String> = query.split_whitespace().map(str::to_string).collect();
    if words.is_empty() { Err("query is required.".to_string()) } else { Ok(words) }
}

/// The first search's items, then the second's, up to `limit`; never re-sorted across the two. The total counts
/// both, so "N more not shown" covers rows the second search was not read for.
pub fn merge(first: Vec<Item>, first_total: usize, second: Vec<Item>, second_total: usize, limit: usize) -> (Vec<Item>, usize) {
    (first.into_iter().chain(second).take(limit).collect(), first_total + second_total)
}

/// One `<path>\t<kind>\t<ISO-8601>\t<size>` line per item, in the order given, then how many more matched and
/// whether the search was stopped.
pub fn format(items: &[Item], total: usize, content: bool, stopped: bool) -> String {
    let mut lines = Vec::new();
    for item in items {
        let modified = item.modified.map(iso8601).unwrap_or_else(|| "-".to_string());
        let size = item.size.map(|size| size.to_string()).unwrap_or_else(|| "-".to_string());
        lines.push(format!("{}\t{}\t{modified}\t{size}", item.path, item.kind));
        if let (true, Some(text)) = (content, &item.text) {
            lines.push(format!("  {text}"));
        }
    }
    if lines.is_empty() {
        lines.push("No matches.".to_string());
    }
    if total > items.len() {
        lines.push(format!("{} more not shown", total - items.len()));
    }
    if stopped {
        lines.push(STOPPED_LINE.to_string());
    }
    lines.join("\n")
}

/// `2023-11-14T22:13:20Z`: UTC, whole seconds.
fn iso8601(time: SystemTime) -> String {
    let seconds = match time.duration_since(UNIX_EPOCH) {
        Ok(after) => after.as_secs() as i64,
        Err(before) => -(before.duration().as_secs() as i64) - i64::from(before.duration().subsec_nanos() > 0),
    };
    let (days, second_of_day) = (seconds.div_euclid(86_400), seconds.rem_euclid(86_400));
    // Days to a civil date (Howard Hinnant's algorithm).
    let z = days + 719_468;
    let (era, day_of_era) = (z.div_euclid(146_097), z.rem_euclid(146_097));
    let year_of_era = (day_of_era - day_of_era / 1460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let shifted_month = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * shifted_month + 2) / 5 + 1;
    let month = if shifted_month < 10 { shifted_month + 3 } else { shifted_month - 9 };
    let year = year_of_era + era * 400 + i64::from(month <= 2);
    format!("{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}Z", second_of_day / 3600, second_of_day / 60 % 60, second_of_day % 60)
}

/// The first 300 characters of a text file, from at most its first 4 KiB, decoded as UTF-8 with replacement and
/// with line breaks turned into spaces; `None` when unreadable or blank.
pub fn preview(path: &Path) -> Option<String> {
    let mut data = Vec::new();
    std::fs::File::open(path).ok()?.take(PREVIEW_BYTES).read_to_end(&mut data).ok()?;
    let flat: String = String::from_utf8_lossy(&data).chars().map(|c| if matches!(c, '\n' | '\r' | '\u{0B}' | '\u{0C}' | '\u{85}' | '\u{2028}' | '\u{2029}') { ' ' } else { c }).collect();
    if flat.trim_matches(|c: char| c == ' ' || c == '\t').is_empty() {
        return None;
    }
    Some(flat.chars().take(PREVIEW_CHARS).collect())
}

/// The arguments the tool takes.
pub struct Request {
    pub query: String,
    pub raw: bool,
    pub kind: Kind,
    pub scope: Vec<String>,
    pub limit: i64,
    pub content: bool,
}

pub async fn run(desktop: &dyn Desktop, request: Request) -> Result<String, String> {
    let limit = request.limit.clamp(1, MAX_LIMIT) as usize;
    let search = |query: Query, shown: &str, limit: usize, timeout: Duration| Search {
        query,
        shown: shown.to_string(),
        kind: request.kind,
        scope: request.scope.clone(),
        limit,
        timeout,
        content: request.content,
    };
    if request.raw {
        let query = request.query.trim();
        if query.is_empty() {
            return Err("query is required.".to_string());
        }
        let found = desktop.search_files(search(Query::Raw(query.to_string()), query, limit, DEADLINE)).await?;
        return Ok(format(&found.items, found.total, request.content, found.stopped));
    }
    // Name matches first, then matches by content only; both share one deadline.
    let start = Instant::now();
    let words = words(&request.query)?;
    let first = desktop.search_files(search(Query::Names(words.clone()), &request.query, limit, DEADLINE)).await?;
    let remaining = DEADLINE.saturating_sub(start.elapsed());
    if first.stopped || remaining.is_zero() {
        return Ok(format(&first.items, first.total, request.content, true));
    }
    // When the name matches filled the limit, the second search only counts.
    let second = desktop.search_files(search(Query::ContentOnly(words), &request.query, limit - first.items.len().min(limit), remaining)).await?;
    let (items, total) = merge(first.items, first.total, second.items, second.total, limit);
    Ok(format(&items, total, request.content, second.stopped))
}
