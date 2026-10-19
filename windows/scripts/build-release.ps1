#Requires -Version 7
# Publishes desktop-mcp.exe with NativeAOT for each runtime and smoke-tests the x64 build.
# Each dist/<rid> folder must hold exactly desktop-mcp.exe and desktop-mcp.pdb.
param([string[]]$Runtimes = @('win-x64', 'win-arm64'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$allowed = @('desktop-mcp.exe', 'desktop-mcp.pdb')

foreach ($rid in $Runtimes) {
    $out = Join-Path $root "dist/$rid"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish (Join-Path $root 'src/DesktopMcp/DesktopMcp.csproj') -c Release -r $rid -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    $files = @(Get-ChildItem $out -Recurse -Force | ForEach-Object { [IO.Path]::GetRelativePath($out, $_.FullName) })
    $extra = @($files | Where-Object { $_ -notin $allowed })
    $missing = @($allowed | Where-Object { $_ -notin $files })
    if ($extra.Count -gt 0 -or $missing.Count -gt 0) {
        throw "dist/$rid must contain only $($allowed -join ' and '); extra: [$($extra -join ', ')], missing: [$($missing -join ', ')]"
    }
    $exe = Join-Path $out 'desktop-mcp.exe'
    '{0}: {1:N1} MB, folder OK ({2})' -f $rid, ((Get-Item $exe).Length / 1MB), ($allowed -join ', ') | Write-Host
}

if ($Runtimes -contains 'win-x64') {
    & (Join-Path $PSScriptRoot 'smoke-stdio.ps1') -Exe (Join-Path $root 'dist/win-x64/desktop-mcp.exe')
}
