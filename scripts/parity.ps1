# The parity suite against the Windows binary: builds desktop-mcp, then runs crates/parity with DESKTOP_MCP_BIN set.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    cargo build -p desktop-mcp-cli
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $env:DESKTOP_MCP_BIN = Join-Path $root 'target\debug\desktop-mcp.exe'
    cargo test -p desktop-mcp-parity -- @args
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
