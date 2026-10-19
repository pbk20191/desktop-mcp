import XCTest
@testable import DesktopMCPCore

final class ConfigTests: XCTestCase {
    var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("desktop-mcp-config-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: directory)
    }

    func file(_ text: String) throws -> URL {
        let url = directory.appendingPathComponent("config.toml")
        try text.write(to: url, atomically: true, encoding: .utf8)
        return url
    }

    // MARK: TOML subset

    func testTOMLParsesTheConfigSubset() throws {
        let tables = try TOML.parse("""
        # comment
        top = 1
        [server]
        transport = "sse"   # trailing comment
        host = 'literal # not a comment'
        port = 8_080
        allow_insecure_remote = true
        auth_key = "a\\"b\\\\c\\u00e9"
        [security]
        ip_allowlist = [
          "10.0.0.0/8",  # office
          "::1",
        ]
        """)
        XCTAssertEqual(tables[""]?["top"], .integer(1))
        XCTAssertEqual(tables["server"]?["transport"], .string("sse"))
        XCTAssertEqual(tables["server"]?["host"], .string("literal # not a comment"))
        XCTAssertEqual(tables["server"]?["port"], .integer(8080))
        XCTAssertEqual(tables["server"]?["allow_insecure_remote"], .bool(true))
        XCTAssertEqual(tables["server"]?["auth_key"], .string("a\"b\\cé"))
        XCTAssertEqual(tables["security"]?["ip_allowlist"], .array([.string("10.0.0.0/8"), .string("::1")]))
    }

    func testTOMLErrorsNameTheLine() {
        XCTAssertThrowsError(try TOML.parse("[server]\nport = \n")) { error in
            XCTAssertTrue("\(error)".contains("line 2"), "\(error)")
        }
        XCTAssertThrowsError(try TOML.parse("x = \"unterminated\n"))
    }

    // MARK: Loading

    func testLoadValidatesAndResolvesRelativeTLSPaths() throws {
        let url = try file("""
        [server]
        transport = "streamable-http"
        port = 9000
        ssl_certfile = "certs/cert.pem"
        ssl_keyfile = "/abs/key.pem"
        [tools]
        exclude = ["Shell"]
        """)
        let config = try DesktopMCPConfig.load(url)
        XCTAssertEqual(config.server.transport, "streamable-http")
        XCTAssertEqual(config.server.port, 9000)
        XCTAssertEqual(config.server.host, "localhost")
        XCTAssertEqual(config.server.sslCertfile, directory.appendingPathComponent("certs/cert.pem").standardizedFileURL.path)
        XCTAssertEqual(config.server.sslKeyfile, "/abs/key.pem")
        XCTAssertEqual(config.tools.exclude, ["Shell"])
        XCTAssertEqual(config.sourcePath, url.path)
    }

    func testLoadRejectsBadValues() throws {
        XCTAssertThrowsError(try DesktopMCPConfig.load(try file("[server]\ntransport = \"websocket\"\n")))
        XCTAssertThrowsError(try DesktopMCPConfig.load(try file("[server]\nallow_insecure_remote = \"yes\"\n")))
        XCTAssertThrowsError(try DesktopMCPConfig.load(try file("[tools]\nexclude = \"Shell\"\n")))
        XCTAssertThrowsError(try DesktopMCPConfig.load(directory.appendingPathComponent("missing.toml")))
        XCTAssertEqual(try DesktopMCPConfig.load(nil), DesktopMCPConfig())
    }

    func testWriteOnlyNonDefaultsAndRoundTrip() throws {
        var config = DesktopMCPConfig()
        XCTAssertEqual(config.serialized(), "")
        config.server.transport = "sse"
        config.server.host = "0.0.0.0"
        config.server.authKey = "k\"ey"
        config.security.ipAllowlist = ["10.0.0.0/8"]
        config.tools.exclude = ["Shell", "Scrape"]
        XCTAssertEqual(config.serialized(), """
        [server]
        transport = "sse"
        host = "0.0.0.0"
        auth_key = "k\\"ey"

        [security]
        ip_allowlist = ["10.0.0.0/8"]

        [tools]
        exclude = ["Shell", "Scrape"]

        """)
        let url = directory.appendingPathComponent("nested/config.toml")
        try config.write(to: url)
        var loaded = try DesktopMCPConfig.load(url)
        loaded.sourcePath = nil
        XCTAssertEqual(loaded, config)
        let mode = try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? Int
        XCTAssertEqual(mode, 0o600)
    }

    // MARK: Merging

    func testPlanPrecedenceCommandLineThenEnvironmentThenConfig() throws {
        var config = DesktopMCPConfig()
        config.server.transport = "sse"
        config.server.port = 9000
        config.server.authKey = "from-config"
        config.security.ipAllowlist = ["10.0.0.0/8"]
        config.tools.exclude = ["Shell"]

        var cli = ServePlan.CommandLineValues()
        cli.port = 7000
        let env = [ServePlan.environmentKeys.authKey: "from-env", ServePlan.environmentKeys.excludeTools: "Scrape,Shell"]
        let plan = try ServePlan.resolve(cli, environment: env, config: config)
        XCTAssertEqual(plan.transport, "sse")
        XCTAssertEqual(plan.port, 7000)
        XCTAssertEqual(plan.host, "localhost")
        XCTAssertEqual(plan.authKey, "from-env")
        XCTAssertEqual(plan.ipAllowlist.map(\.description), ["10.0.0.0/8"])
        XCTAssertEqual(plan.excludedTools, ["Shell", "Scrape"])

        cli.authKey = "from-cli"
        cli.ipAllowlist = "192.168.1.5, ::1"
        let overridden = try ServePlan.resolve(cli, environment: env, config: config)
        XCTAssertEqual(overridden.authKey, "from-cli")
        XCTAssertEqual(overridden.ipAllowlist.map(\.description), ["192.168.1.5/32", "::1/128"])

        // Empty values count as unset at every level, so `--auth-key ""` neither disables auth
        // nor satisfies the remote-bind check with an empty token.
        cli.authKey = ""
        cli.ipAllowlist = ""
        let blank = try ServePlan.resolve(cli, environment: [ServePlan.environmentKeys.ipAllowlist: ""], config: config)
        XCTAssertEqual(blank.authKey, "from-config")
        XCTAssertEqual(blank.ipAllowlist.map(\.description), ["10.0.0.0/8"])
        cli.transport = "sse"
        cli.host = "0.0.0.0"
        XCTAssertThrowsError(try ServePlan.resolve(cli, environment: [:], config: DesktopMCPConfig()))
    }

    func testPlanRefusesUnauthenticatedRemoteBind() throws {
        var cli = ServePlan.CommandLineValues()
        cli.transport = "streamable-http"
        cli.host = "0.0.0.0"
        XCTAssertThrowsError(try ServePlan.resolve(cli, environment: [:], config: DesktopMCPConfig())) { error in
            XCTAssertTrue("\(error)".contains("Refusing to bind"))
        }
        cli.allowInsecureRemote = true
        XCTAssertNoThrow(try ServePlan.resolve(cli, environment: [:], config: DesktopMCPConfig()))
        cli.allowInsecureRemote = false
        cli.authKey = "secret"
        XCTAssertNoThrow(try ServePlan.resolve(cli, environment: [:], config: DesktopMCPConfig()))
        cli.host = "127.0.0.1"
        cli.authKey = nil
        XCTAssertNoThrow(try ServePlan.resolve(cli, environment: [:], config: DesktopMCPConfig()))
    }

    func testPlanRequiresPairs() {
        var cli = ServePlan.CommandLineValues()
        cli.sslCertfile = "/c.pem"
        XCTAssertThrowsError(try ServePlan.resolve(cli, environment: [:], config: DesktopMCPConfig()))
        cli = ServePlan.CommandLineValues()
        cli.oauthClientID = "id"
        XCTAssertThrowsError(try ServePlan.resolve(cli, environment: [:], config: DesktopMCPConfig()))
        cli = ServePlan.CommandLineValues()
        cli.ipAllowlist = "10.0.0.0/33,nope"
        XCTAssertThrowsError(try ServePlan.resolve(cli, environment: [:], config: DesktopMCPConfig())) { error in
            XCTAssertTrue("\(error)".contains("nope"))
        }
    }

    func testStdioWarnsAboutHTTPOnlySettings() throws {
        var cli = ServePlan.CommandLineValues()
        cli.authKey = "x"
        let plan = try ServePlan.resolve(cli, environment: [:], config: DesktopMCPConfig())
        XCTAssertEqual(plan.warnings, ["auth_key / ip_allowlist have no effect on stdio transport"])
    }

    // MARK: Security helpers

    func testIPNetworks() throws {
        let office = try IPNetwork("10.1.2.3/8")
        XCTAssertEqual(office.description, "10.0.0.0/8")
        XCTAssertTrue(office.contains("10.200.0.1"))
        XCTAssertTrue(office.contains("::ffff:10.0.0.9"))
        XCTAssertFalse(office.contains("11.0.0.1"))
        XCTAssertTrue(try IPNetwork("2001:db8::/32").contains("2001:db8:1::5"))
        XCTAssertFalse(try IPNetwork("::1").contains("::2"))
        XCTAssertThrowsError(try IPNetwork("10.0.0.0/40"))
        XCTAssertThrowsError(try IPNetwork("example.com"))
    }

    func testLoopbackAndTokens() {
        for host in ["localhost", "LOCALHOST", "127.0.0.1", "127.5.5.5", "::1"] {
            XCTAssertTrue(SecurityUtil.isLoopbackHost(host), host)
        }
        for host in ["0.0.0.0", "192.168.0.1", "::", "example.com"] {
            XCTAssertFalse(SecurityUtil.isLoopbackHost(host), host)
        }
        let token = SecurityUtil.tokenURLSafe(32)
        XCTAssertEqual(token.count, 43)
        XCTAssertTrue(token.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" })
        XCTAssertNotEqual(token, SecurityUtil.tokenURLSafe(32))
        XCTAssertTrue(SecurityUtil.constantTimeEquals("abc", "abc"))
        XCTAssertFalse(SecurityUtil.constantTimeEquals("abc", "abd"))
        XCTAssertFalse(SecurityUtil.constantTimeEquals("abc", "abcd"))
    }
}

extension ConfigTests {
    func testOsascriptDenyAppsRoundTrips() throws {
        let config = try DesktopMCPConfig.load(try file("[osascript]\ndeny_apps = [\"com.apple.systemevents\", \"com.apple.finder\"]\n"))
        XCTAssertEqual(config.osascript.denyApps, ["com.apple.systemevents", "com.apple.finder"])
        XCTAssertTrue(config.serialized().contains("[osascript]\ndeny_apps = [\"com.apple.systemevents\", \"com.apple.finder\"]"))
        XCTAssertThrowsError(try DesktopMCPConfig.load(try file("[osascript]\ndeny_apps = \"x\"\n")))
        XCTAssertFalse(DesktopMCPConfig().serialized().contains("osascript"))

        var plan = try ServePlan.resolve(.init(), environment: [:], config: config)
        XCTAssertEqual(plan.osaPolicy.deniedBundleIDs, ["com.apple.systemevents", "com.apple.finder"])
        plan = try ServePlan.resolve(.init(), environment: [:], config: DesktopMCPConfig())
        XCTAssertTrue(plan.osaPolicy.deniedBundleIDs.isEmpty)
    }
}
