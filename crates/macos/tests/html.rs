//! HTML parsing by NSXMLDocument, rendered by the core: the cases of the Swift port's ScrapeTests.
use desktop_mcp_core::html::to_markdown;

fn convert(html: &str) -> String {
    convert_bytes(html.as_bytes(), None)
}
fn convert_bytes(body: &[u8], charset: Option<&str>) -> String {
    desktop_mcp_macos::html::parse(body, charset).map(|root| to_markdown(&root)).unwrap_or_default()
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

