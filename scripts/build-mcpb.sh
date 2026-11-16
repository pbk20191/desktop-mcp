#!/bin/bash
# Packs the macOS desktop-mcp binary into a Claude Desktop extension (.mcpb) at dist/desktop-mcp.mcpb.
# Claude Desktop runs the bundled binary as a stdio server (`serve --transport stdio`) through its `disclaimer`
# helper, which makes desktop-mcp the responsible process: the binary itself must be granted Accessibility and
# Screen Recording (the Permissions tool with request=true asks for them). The tools listed in the manifest are
# the binary's own tools/list, opt-in OCR included.
#
# Usage: scripts/build-mcpb.sh [path/to/desktop-mcp]   (default: target/release/desktop-mcp)
set -euo pipefail
# A given path is relative to the caller's directory, the default one to the repository.
BIN="${1:-}"
[[ -z "$BIN" || "$BIN" = /* ]] || BIN="$PWD/$BIN"
cd "$(dirname "$0")/.."
BIN="${BIN:-$PWD/target/release/desktop-mcp}"
DIST=dist
STAGE="$DIST/mcpb"
OUT="$DIST/desktop-mcp.mcpb"

[[ -x "$BIN" ]] || { echo "error: $BIN is not an executable; build it with: cargo build --release -p desktop-mcp-cli" >&2; exit 1; }
VERSION="$("$BIN" --version | awk '{print $NF}')"

rm -rf "$STAGE" "$OUT"
mkdir -p "$STAGE/server"
cp "$BIN" "$STAGE/server/desktop-mcp"
chmod 755 "$STAGE/server/desktop-mcp"

python3 -I - "$VERSION" "$BIN" "$STAGE/manifest.json" <<'PY'
import json, os, subprocess, sys, tempfile
version, binary, out = sys.argv[1], sys.argv[2], sys.argv[3]
messages = [
    {"jsonrpc": "2.0", "id": 1, "method": "initialize",
     "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "build-mcpb", "version": "1"}}},
    {"jsonrpc": "2.0", "method": "notifications/initialized"},
    {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
]
# A scratch HOME, so no config.toml of the user's changes the list.
with tempfile.TemporaryDirectory() as home:
    env = dict(os.environ, HOME=home, DESKTOP_MCP_INCLUDE_TOOLS="OCR")
    run = subprocess.run([binary, "serve", "--transport", "stdio"], input="".join(json.dumps(m) + "\n" for m in messages),
                         capture_output=True, text=True, env=env, timeout=30)
replies = [json.loads(line) for line in run.stdout.splitlines() if line.strip()]
tools = next(reply["result"]["tools"] for reply in replies if reply.get("id") == 2)
manifest = {
    "manifest_version": "0.3",
    "name": "desktop-mcp",
    "display_name": "Desktop MCP",
    "version": version,
    "description": "Control the macOS desktop: apps, windows, menus, mouse and keyboard, "
                   "accessibility tree, clipboard, Spotlight and Shortcuts.",
    "long_description": "Runs desktop-mcp as a local stdio server. Grant the bundled binary "
                        "Accessibility and Screen Recording in System Settings > Privacy & "
                        "Security (the Permissions tool with request=true asks for them).",
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
              for t in sorted(tools, key=lambda t: t["name"])],
    "keywords": ["macos", "desktop", "automation", "accessibility"],
    "compatibility": {"platforms": ["darwin"]},
}
json.dump(manifest, open(out, "w"), indent=2, ensure_ascii=False)
PY

for f in LICENSE NOTICE; do [[ -f "$f" ]] && cp "$f" "$STAGE/"; done
(cd "$STAGE" && zip -qrX "../$(basename "$OUT")" .)
echo "Wrote $OUT ($VERSION, $(lipo -archs "$STAGE/server/desktop-mcp"), $(python3 -I -c "import json,sys; print(len(json.load(open(sys.argv[1]))['tools']))" "$STAGE/manifest.json") tools)"
