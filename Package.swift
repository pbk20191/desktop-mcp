// swift-tools-version:6.0
import PackageDescription

// The macOS port of desktop-mcp. Sources live under macos/; build and test from the repository root.
let package = Package(
    name: "DesktopMCP",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "desktop-mcp", targets: ["desktop-mcp"]),
    ],
    dependencies: [
        // Build-time only: compiled into the binary, nothing to install for users.
        .package(url: "https://github.com/apple/swift-argument-parser", from: "1.5.0"),
        .package(url: "https://github.com/apple/swift-nio", from: "2.65.0"),
        .package(url: "https://github.com/apple/swift-nio-transport-services", from: "1.21.0"),
        .package(url: "https://github.com/apple/swift-nio-ssl", from: "2.27.0"),
        .package(url: "https://github.com/apple/swift-nio-http2", from: "1.35.0"),
        // Transitive (via swift-nio), capped so Swift 6.0/6.1 toolchains (Xcode 16) can load the
        // graph: 1.7.x needs tools 6.2+, and the 6.0 manifests of 1.3-1.6 fail SwiftPM 6.1's
        // language-mode check. Release builds use Xcode 16 to ship a single file for macOS 13+
        // (see scripts/build-release.sh).
        .package(url: "https://github.com/apple/swift-collections", "1.1.0"..<"1.3.0"),
    ],
    targets: [
        .target(
            name: "DesktopMCPCore",
            path: "macos/Sources/DesktopMCPCore",
            linkerSettings: [
                .linkedFramework("ApplicationServices"),
                .linkedFramework("AppKit"),
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("Carbon"),
                .linkedFramework("OSAKit"),
            ]
        ),
        // Socket layer for the HTTP transports: Network.framework via NIOTransportServices,
        // with NIOSSL for TLS from PEM files and NIOHTTP2 for HTTP/2 negotiated through ALPN.
        .target(
            name: "DesktopMCPHTTP",
            dependencies: [
                "DesktopMCPCore",
                .product(name: "NIOCore", package: "swift-nio"),
                .product(name: "NIOHTTP1", package: "swift-nio"),
                .product(name: "NIOHTTP2", package: "swift-nio-http2"),
                .product(name: "NIOTransportServices", package: "swift-nio-transport-services"),
                .product(name: "NIOSSL", package: "swift-nio-ssl"),
                // Already linked through NIO; named here so the version cap above is a used dependency.
                .product(name: "DequeModule", package: "swift-collections"),
            ],
            path: "macos/Sources/DesktopMCPHTTP"
        ),
        .executableTarget(
            name: "desktop-mcp",
            dependencies: [
                "DesktopMCPCore",
                "DesktopMCPHTTP",
                .product(name: "ArgumentParser", package: "swift-argument-parser"),
            ],
            path: "macos/Sources/desktop-mcp"
        ),
        .testTarget(name: "DesktopMCPCoreTests", dependencies: ["DesktopMCPCore"], path: "macos/Tests/DesktopMCPCoreTests"),
        .testTarget(name: "DesktopMCPHTTPTests", dependencies: ["DesktopMCPHTTP", "DesktopMCPCore"], path: "macos/Tests/DesktopMCPHTTPTests"),
    ],
    swiftLanguageModes: [.v6]
)
