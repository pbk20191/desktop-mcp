#!/bin/bash
# Runs the black-box parity suite (crates/parity) against the desktop-mcp binary. The suite was written against
# the Swift port (branch archive/swift); to compare with it, pass that binary in DESKTOP_MCP_BIN yourself.
# Usage: scripts/parity.sh [debug|release]   (default: release)
set -euo pipefail
cd "$(dirname "$0")/.."
profile="${1:-release}"
if [ "$profile" = release ]; then
  cargo build --release -p desktop-mcp-cli
else
  cargo build -p desktop-mcp-cli
fi
DESKTOP_MCP_BIN="$PWD/target/$profile/desktop-mcp" cargo test -q -p desktop-mcp-parity
