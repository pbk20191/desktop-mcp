// swift-tools-version:6.1
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
        // Official MCP Swift SDK. 0.12.1 transitively pins swift-docc-plugin to branch: "main"; accepted.
        .package(url: "https://github.com/modelcontextprotocol/swift-sdk.git", from: "0.12.1"),
        // Transitive (via swift-nio), capped to keep the graph loadable by Xcode 16.4's SwiftPM
        // (1.3+ manifests need a newer toolchain); macos/scripts/build-release.sh picks that Xcode.
        .package(url: "https://github.com/apple/swift-collections", "1.1.0"..<"1.3.0"),
    ],
    targets: [
        .target(
            name: "DesktopMCPCore",
            dependencies: [
                .product(name: "MCP", package: "swift-sdk"),
            ],
            path: "macos/Sources/DesktopMCPCore",
            linkerSettings: [
                .linkedFramework("ApplicationServices"),
                .linkedFramework("AppKit"),
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("Carbon"),
            ]
        ),
        // Socket layer for the HTTP transports: Network.framework via NIOTransportServices,
        // with NIOSSL for TLS from PEM files and NIOHTTP2 for HTTP/2 negotiated through ALPN.
        .target(
            name: "DesktopMCPHTTP",
            dependencies: [
                "DesktopMCPCore",
                .product(name: "MCP", package: "swift-sdk"),
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
        .testTarget(name: "DesktopMCPCoreTests", dependencies: ["DesktopMCPCore", .product(name: "MCP", package: "swift-sdk")], path: "macos/Tests/DesktopMCPCoreTests"),
        .testTarget(name: "DesktopMCPHTTPTests", dependencies: ["DesktopMCPHTTP", "DesktopMCPCore", .product(name: "MCP", package: "swift-sdk")], path: "macos/Tests/DesktopMCPHTTPTests"),
    ],
    swiftLanguageModes: [.v6]
)
