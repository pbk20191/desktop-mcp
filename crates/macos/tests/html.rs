//! HTML parsing by NSXMLDocument, rendered by the core: the cases of the Swift port's ScrapeTests.
use desktop_mcp_core::html::to_markdown;
use desktop_mcp_core::mcp::Desktop;
use desktop_mcp_macos::desktop::MacDesktop;

fn convert(html: &str) -> String {
    convert_bytes(html.as_bytes(), None)
}
fn convert_bytes(body: &[u8], charset: Option<&str>) -> String {
    MacDesktop::new().parse_html(body, charset).map(|root| to_markdown(&root)).unwrap_or_default()
}

#[test]
fn html_to_markdown() {
    let markdown = convert(
        r#"<!doctype html><html><head><title>Ignored &amp; title</title><style>body{color:red}</style></head>
<body><script>var x = "<p>no</p>";</script>
<h1>Hello&nbsp;World</h1>
<p>Some <strong>bold</strong>, <em>italic</em> and <a href="https://example.com/a?b=1&amp;c=2">a link</a>.<br>Next line</p>
<ul><li>One</li><li>Two<ol><li>Nested</li></ol></li></ul>
<pre><code>let x = 1
let y = 2</code></pre>
<p>Inline <code>code</code> &lt;tag&gt; &#169; &#x263A;</p>
<img src="/logo.png" alt="Logo">
</body></html>"#,
    );
    assert!(markdown.starts_with("# Hello World"), "{markdown}");
    assert!(markdown.contains("Some **bold**, *italic* and [a link](https://example.com/a?b=1&c=2).  \nNext line"), "{markdown}");
    assert!(markdown.contains("- One\n- Two\n  1. Nested"), "{markdown}");
    assert!(markdown.contains("```\nlet x = 1\nlet y = 2\n```"), "{markdown}");
    assert!(markdown.contains("Inline `code` <tag> © ☺"), "{markdown}");
    assert!(markdown.contains("![Logo](/logo.png)"), "{markdown}");
    assert!(!markdown.contains("color:red"));
    assert!(!markdown.contains("var x"));
    assert!(!markdown.contains("Ignored"));
}

#[test]
fn title_is_used_when_there_is_no_heading() {
    assert_eq!(convert("<html><head><title>Page</title></head><body><p>Body</p></body></html>"), "Page\n\nBody");
}

#[test]
fn malformed_html_is_repaired_by_the_parser() {
    // Unclosed tags, unquoted attributes, uppercase names, a stray close tag.
    let markdown = convert(
        "<H1>Title<P>First para<p>Second <B>bold <i>both</i></B> tail</div>\n<UL><LI>one<LI>two <A HREF=/rel>link</A></UL><table><tr><td>a</td><td>b<tr><td>c</td><td>d</table>",
    );
    assert!(markdown.starts_with("# Title\n\nFirst para\n\nSecond **bold *both*** tail"), "{markdown}");
    assert!(markdown.contains("- one\n- two [link](/rel)"), "{markdown}");
    assert!(markdown.contains("| a | b |\n| c | d |"), "{markdown}");
    assert_eq!(convert(""), "");
    assert_eq!(convert("plain text < 3"), "plain text < 3");
}

#[test]
fn declared_charsets_do_not_redecode_already_decoded_text() {
    // The body is decoded from the HTTP charset; a page's own declaration (meta, http-equiv or XML prolog) must
    // not make the parser decode it again.
    for prefix in [
        "<html><head><meta charset=\"euc-kr\"></head>",
        "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=Shift_JIS\"></head>",
        "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><html>",
    ] {
        assert_eq!(convert(&format!("{prefix}<body><p>한글 日本語 café</p></body></html>")), "한글 日本語 café", "{prefix}");
    }
}

#[test]
fn the_http_charset_decodes_the_body() {
    // "한글" in EUC-KR, and "café" in Latin-1.
    assert_eq!(convert_bytes(b"<p>\xc7\xd1\xb1\xdb</p>", Some("EUC-KR")), "한글");
    assert_eq!(convert_bytes(b"<p>caf\xe9</p>", Some("iso-8859-1")), "café");
    // Unknown or absent: UTF-8, and invalid UTF-8 does not lose the page.
    assert_eq!(convert_bytes("<p>café</p>".as_bytes(), Some("no-such-charset")), "café");
    assert_eq!(convert_bytes(b"<p>ok \xff</p>", None), "ok \u{fffd}");
}
