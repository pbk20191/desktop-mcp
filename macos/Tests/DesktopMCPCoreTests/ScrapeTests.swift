import XCTest
@testable import DesktopMCPCore

final class ScrapeTests: XCTestCase {
    func testBlockedAddresses() {
        for address in ["10.1.2.3", "127.0.0.1", "169.254.169.254", "172.16.0.1", "192.168.1.1", "0.0.0.0",
                        "224.0.0.1", "255.255.255.255", "::1", "::", "fe80::1", "fd00::1", "ff02::1",
                        "::ffff:127.0.0.1", "2001:db8::1", "not-an-ip",
                        // Reserved ranges the Python ipaddress module also treats as non-global.
                        "::10.0.0.1", "::8.8.8.8", "64:ff9b::808:808", "2001::1", "2001:10::1"] {
            XCTAssertTrue(Scrape.isBlocked(address), address)
        }
        for address in ["8.8.8.8", "1.1.1.1", "172.32.0.1", "2606:4700:4700::1111"] {
            XCTAssertFalse(Scrape.isBlocked(address), address)
        }
    }

    func testValidateRejectsUnsafeURLs() async {
        for (url, expected) in [
            ("ftp://example.com", "scheme"),
            ("file:///etc/passwd", "scheme"),
            ("http://user:pass@example.com", "credentials"),
            ("http://127.0.0.1:8080/", "blocked"),
            ("http://localhost/", "blocked"),
            ("http://[::1]/", "blocked"),
        ] {
            do {
                try await Scrape.validate(url)
                XCTFail("\(url) should be rejected")
            } catch {
                XCTAssertTrue("\(error)".contains(expected), "\(url): \(error)")
            }
        }
    }

    func testHTMLToMarkdown() {
        let html = """
        <!doctype html><html><head><title>Ignored &amp; title</title><style>body{color:red}</style></head>
        <body><script>var x = "<p>no</p>";</script>
        <h1>Hello&nbsp;World</h1>
        <p>Some <strong>bold</strong>, <em>italic</em> and <a href="https://example.com/a?b=1&amp;c=2">a link</a>.<br>Next line</p>
        <ul><li>One</li><li>Two<ol><li>Nested</li></ol></li></ul>
        <pre><code>let x = 1
        let y = 2</code></pre>
        <p>Inline <code>code</code> &lt;tag&gt; &#169; &#x263A;</p>
        <img src="/logo.png" alt="Logo">
        </body></html>
        """
        let markdown = HTMLToMarkdown.convert(html)
        XCTAssertTrue(markdown.hasPrefix("# Hello World"), markdown)
        XCTAssertTrue(markdown.contains("Some **bold**, *italic* and [a link](https://example.com/a?b=1&c=2).  \nNext line"), markdown)
        XCTAssertTrue(markdown.contains("- One\n- Two\n  1. Nested"), markdown)
        XCTAssertTrue(markdown.contains("```\nlet x = 1\nlet y = 2\n```"), markdown)
        XCTAssertTrue(markdown.contains("Inline `code` <tag> © ☺"), markdown)
        XCTAssertTrue(markdown.contains("![Logo](/logo.png)"), markdown)
        XCTAssertFalse(markdown.contains("color:red"))
        XCTAssertFalse(markdown.contains("var x"))
        XCTAssertFalse(markdown.contains("Ignored"))
    }

    func testTitleIsUsedWhenThereIsNoHeading() {
        let markdown = HTMLToMarkdown.convert("<html><head><title>Page</title></head><body><p>Body</p></body></html>")
        XCTAssertEqual(markdown, "Page\n\nBody")
    }

    func testMalformedHTMLIsRepairedByTheParser() {
        // Unclosed tags, unquoted attributes, uppercase names, a stray close tag.
        let markdown = HTMLToMarkdown.convert("""
        <H1>Title<P>First para<p>Second <B>bold <i>both</i></B> tail</div>
        <UL><LI>one<LI>two <A HREF=/rel>link</A></UL><table><tr><td>a</td><td>b<tr><td>c</td><td>d</table>
        """)
        XCTAssertTrue(markdown.hasPrefix("# Title\n\nFirst para\n\nSecond **bold *both*** tail"), markdown)
        XCTAssertTrue(markdown.contains("- one\n- two [link](/rel)"), markdown)
        XCTAssertTrue(markdown.contains("| a | b |\n| c | d |"), markdown)
        XCTAssertEqual(HTMLToMarkdown.convert(""), "")
        XCTAssertEqual(HTMLToMarkdown.convert("plain text < 3"), "plain text < 3")
    }

    func testDeclaredCharsetsDoNotRedecodeAlreadyDecodedText() {
        // The body was decoded from the HTTP charset before conversion; a page's own
        // declaration (meta, http-equiv or XML prolog) must not make the parser re-decode it.
        for prefix in [
            "<html><head><meta charset=\"euc-kr\"></head>",
            "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=Shift_JIS\"></head>",
            "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><html>",
        ] {
            let markdown = HTMLToMarkdown.convert(prefix + "<body><p>한글 日本語 café</p></body></html>")
            XCTAssertEqual(markdown, "한글 日本語 café", prefix)
        }
    }
}
