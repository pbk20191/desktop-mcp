#!/bin/bash
# Builds a universal (arm64 + x86_64) release binary at macos/dist/desktop-mcp, signed ad hoc or
# with $CODESIGN_IDENTITY.
# The Swift package manifest lives at the repository root; this script runs from there.
#
# Toolchain: Xcode 16 (Swift 6.0/6.1) produces a single file that runs on macOS 13 and later.
# Swift 6.2+ toolchains (Xcode 26 and later) link against a Span back-deployment shim that
# macOS 13-15 do not have, so the shim dylib is shipped next to the binary in that case.
# Set DEVELOPER_DIR to choose an Xcode explicitly; otherwise an Xcode 16 under /Applications is
# preferred when one exists, and the default toolchain is used when there is none.
set -euo pipefail
cd "$(dirname "$0")/../.."
DIST=macos/dist

if [[ -z "${DEVELOPER_DIR:-}" ]]; then
    for app in /Applications/Xcode*.app; do
        [[ -d "$app" ]] || continue
        version="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$app/Contents/Info.plist" 2>/dev/null || true)"
        if [[ "$version" == 16.* ]]; then
            export DEVELOPER_DIR="$app/Contents/Developer"
            break
        fi
    done
fi
echo "Toolchain: $(xcrun swift --version 2>&1 | head -1)${DEVELOPER_DIR:+ ($DEVELOPER_DIR)}"

ARCHS=(--arch arm64 --arch x86_64)
swift build -c release "${ARCHS[@]}"
BIN_DIR="$(swift build -c release "${ARCHS[@]}" --show-bin-path)"
rm -rf "$DIST"
mkdir -p "$DIST"
cp "$BIN_DIR/desktop-mcp" "$DIST/desktop-mcp"
# Drop rpaths that point into this machine's toolchain; keep /usr/lib/swift and @loader_path.
otool -l "$DIST/desktop-mcp" | awk '/LC_RPATH/{getline; getline; print $2}' | sort -u | while read -r rpath; do
    case "$rpath" in
        /usr/lib/swift|@loader_path) ;;
        *) install_name_tool -delete_rpath "$rpath" "$DIST/desktop-mcp" 2>/dev/null || true ;;
    esac
done

# Swift 6.2+ toolchains link code that uses Span against a back-deployment shim. macOS 26 ships
# it in /usr/lib/swift (searched first); macOS 13–15 find it next to the binary via @loader_path.
SPAN_LIB="libswiftCompatibilitySpan.dylib"
if otool -L "$DIST/desktop-mcp" | grep -q "@rpath/$SPAN_LIB"; then
    echo "note: this toolchain needs $SPAN_LIB on macOS 13-15; shipping it next to the binary." \
         "Build with Xcode 16 for a single file."
    TOOLCHAIN="$(cd "$(dirname "$(xcrun -f swift)")/../.." && pwd)"
    SOURCE="$(find "$TOOLCHAIN/usr/lib" -path "*/macosx/$SPAN_LIB" | head -1)"
    if [[ -z "$SOURCE" ]]; then
        echo "error: the binary needs $SPAN_LIB but the toolchain does not ship it" >&2
        exit 1
    fi
    cp "$SOURCE" "$DIST/$SPAN_LIB"
    chmod 644 "$DIST/$SPAN_LIB"
    codesign --force --sign - "$DIST/$SPAN_LIB"
fi

# Signed ad hoc by default (required on Apple silicon). TCC remembers permissions by code
# signature, and an ad-hoc signature is just the binary's hash, so set CODESIGN_IDENTITY to a
# stable identity (a code signing certificate from Keychain Access, or a Developer ID) to keep
# Accessibility / Screen Recording grants across rebuilds.
codesign --force --sign "${CODESIGN_IDENTITY:--}" --identifier com.desktop-mcp.server "$DIST/desktop-mcp"
lipo -info "$DIST/desktop-mcp"
vtool -show-build "$DIST/desktop-mcp" | grep -E 'minos' | sort -u
ls -lh "$DIST/"
