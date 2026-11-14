#!/bin/bash
# Runs the black-box parity suite (crates/parity) against the Swift port and the Rust port.
# Usage: scripts/parity.sh [debug|release]   (default: release)
set -euo pipefail
cd "$(dirname "$0")/.."
profile="${1:-release}"
if [ "$profile" = release ]; then
  swift build -c release
  cargo build --release -p desktop-mcp-cli
else
  swift build
  cargo build -p desktop-mcp-cli
fi
status=0
for binary in ".build/$profile/desktop-mcp" "target/$profile/desktop-mcp"; do
  echo "=== parity suite against $binary"
  DESKTOP_MCP_BIN="$PWD/$binary" cargo test -q -p desktop-mcp-parity || status=1
done
exit $status
