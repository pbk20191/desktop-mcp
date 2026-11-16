#!/bin/bash
# Packs a desktop-mcp binary into a Claude Desktop extension (.mcpb) at macos/dist/desktop-mcp.mcpb.
# Claude Desktop runs the bundled binary as a stdio server (`serve --transport stdio`), so the
# binary itself must be granted Accessibility and Screen Recording (Claude launches it through
# its `disclaimer` helper, which makes desktop-mcp the responsible process).
#
# Usage: macos/scripts/build-mcpb.sh [path/to/desktop-mcp]   (default: macos/dist/desktop-mcp)
set -euo pipefail
cd "$(dirname "$0")/../.."
BIN="${1:-macos/dist/desktop-mcp}"
DIST=macos/dist
STAGE="$DIST/mcpb"
OUT="$DIST/desktop-mcp.mcpb"

[[ -x "$BIN" ]] || { echo "error: $BIN is not an executable" >&2; exit 1; }
VERSION="$("$BIN" --version | awk '{print $NF}')"

rm -rf "$STAGE" "$OUT"
mkdir -p "$STAGE/server"
cp "$BIN" "$STAGE/server/desktop-mcp"
# A Span shim built next to the binary (Swift 6.2+ toolchains) is found via @loader_path.
SPAN_LIB="$(dirname "$BIN")/libswiftCompatibilitySpan.dylib"
[[ -f "$SPAN_LIB" ]] && cp "$SPAN_LIB" "$STAGE/server/"
chmod 755 "$STAGE/server/desktop-mcp"

python3 - "$VERSION" "$STAGE/manifest.json" <<'PY'
import json, sys
version, out = sys.argv[1], sys.argv[2]
tools = json.load(open("spec/tools.json"))["tools"] + [{"name": "OCR", "description": "Reads the text shown on screen with on-device text recognition."}]
manifest = {
    "manifest_version": "0.3",
    "name": "desktop-mcp",
    "display_name": "Desktop MCP",
    "version": version,
    "description": "Control the macOS desktop: apps, windows, menus, mouse and keyboard, "
                   "accessibility tree, clipboard, Spotlight and Shortcuts.",
    "long_description": "Runs desktop-mcp as a local stdio server. Grant the bundled binary "
                        "Accessibility and Screen Recording in System Settings > Privacy & "
                        "Security (the Permissions tool reports and requests them).",
    "author": {"name": "pbk", "url": "https://github.com/pbk20191"},
    "repository": {"type": "git", "url": "https://github.com/pbk20191/desktop-mcp"},
    "homepage": "https://github.com/pbk20191/desktop-mcp",
    "license": "MIT",
    "server": {
        "type": "binary",
        "entry_point": "server/desktop-mcp",
        "mcp_config": {
            "command": "${__dirname}/server/desktop-mcp",
            "args": ["serve", "--transport", "stdio"],
            # The host turns single tools off itself, so the bundle offers every tool, opt-in ones included.
            "env": {"DESKTOP_MCP_INCLUDE_TOOLS": "OCR"},
        },
    },
    "tools": [{"name": t["name"], "description": t["description"].split(". ")[0].rstrip(".") + "."}
              for t in tools],
    "keywords": ["macos", "desktop", "automation", "accessibility"],
    "compatibility": {"platforms": ["darwin"]},
}
json.dump(manifest, open(out, "w"), indent=2, ensure_ascii=False)
PY

for f in LICENSE NOTICE; do [[ -f "$f" ]] && cp "$f" "$STAGE/"; done
(cd "$STAGE" && zip -qrX "../$(basename "$OUT")" .)
echo "Wrote $OUT ($VERSION, $(lipo -archs "$STAGE/server/desktop-mcp"))"
