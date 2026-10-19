#Requires -Version 7
# Publishes desktop-mcp.exe with NativeAOT for each runtime and smoke-tests the x64 build.
param([string[]]$Runtimes = @('win-x64', 'win-arm64'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

foreach ($rid in $Runtimes) {
    $out = Join-Path $root "dist/$rid"
    dotnet publish (Join-Path $root 'src/DesktopMcp/DesktopMcp.csproj') -c Release -r $rid -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
    $exe = Join-Path $out 'desktop-mcp.exe'
    '{0}: {1:N1} MB' -f $rid, ((Get-Item $exe).Length / 1MB) | Write-Host
}

if ($Runtimes -contains 'win-x64') {
    & (Join-Path $PSScriptRoot 'smoke-stdio.ps1') -Exe (Join-Path $root 'dist/win-x64/desktop-mcp.exe')
    if ($LASTEXITCODE -ne 0) { throw 'smoke test failed' }
}
