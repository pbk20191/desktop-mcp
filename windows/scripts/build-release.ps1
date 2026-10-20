#Requires -Version 7.4
# Publishes desktop-mcp.exe with NativeAOT for each runtime and smoke-tests the x64 build over stdio and HTTP.
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
    # Streamable HTTP and SSE over TLS on 127.0.0.1 port 0, control disabled (P5 plan, Task 10).
    & (Join-Path $PSScriptRoot 'smoke-http.ps1') -Exe (Join-Path $root 'dist/win-x64/desktop-mcp.exe')
}

# NativeAOT activation check. xUnit runs under the JIT, so only an AOT run proves that the statically linked Win2D
# activates. The probe's imaging step renders through Win2D and fails if Microsoft.Graphics.Canvas.dll loads.
# It needs an x64 host and the Windows App SDK runtime (a developer machine).
if ($Runtimes -contains 'win-x64' -and [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'X64') {
    $probeOut = Join-Path $root 'probes/out'
    if (Test-Path $probeOut) { Remove-Item $probeOut -Recurse -Force }
    dotnet publish (Join-Path $root 'probes/DesktopMcp.Probe/DesktopMcp.Probe.csproj') -c Release -r win-x64 -o $probeOut
    if ($LASTEXITCODE -ne 0) { throw 'probe publish failed' }
    $probeFiles = @(Get-ChildItem $probeOut -Recurse -Force | ForEach-Object { [IO.Path]::GetRelativePath($probeOut, $_.FullName) })
    $probeExtra = @($probeFiles | Where-Object { $_ -notin 'desktop-mcp-probe.exe', 'desktop-mcp-probe.pdb' })
    if ($probeExtra.Count -gt 0) { throw "probes/out must contain only desktop-mcp-probe.exe and its pdb; extra: [$($probeExtra -join ', ')]" }
    $png = Join-Path ([IO.Path]::GetTempPath()) "desktop-mcp-activation-$PID.png"
    try {
        & (Join-Path $probeOut 'desktop-mcp-probe.exe') imaging $png
        if ($LASTEXITCODE -ne 0) { throw "AOT activation check failed: desktop-mcp-probe imaging exited with $LASTEXITCODE" }
        Write-Host 'AOT activation check OK: the NativeAOT probe activated the statically linked Win2D'
    }
    finally { Remove-Item $png -ErrorAction SilentlyContinue }
    # UI Automation through CsWin32's [GeneratedComInterface] interfaces, which only NativeAOT can prove (P2 Task 1).
    & (Join-Path $probeOut 'desktop-mcp-probe.exe') uia
    if ($LASTEXITCODE -ne 0) { throw "AOT UIA check failed: desktop-mcp-probe uia exited with $LASTEXITCODE" }
    Write-Host 'AOT UIA check OK: the NativeAOT probe read the desktop''s cached children through UI Automation'
    # The overlay path under NativeAOT (P4 Task 14): the product's OverlayArt and LayeredWindowFactory render the indicator for
    # the real monitors and one flash, create every layer, push its content and set an opacity, and read back the click-through
    # ex-styles and WDA_EXCLUDEFROMCAPTURE. No layer is ever shown.
    & (Join-Path $probeOut 'desktop-mcp-probe.exe') overlay
    if ($LASTEXITCODE -ne 0) { throw "AOT overlay check failed: desktop-mcp-probe overlay exited with $LASTEXITCODE" }
    Write-Host 'AOT overlay check OK: the NativeAOT probe rendered and uploaded the indicator and flash without showing them'
}
else {
    Write-Host 'AOT activation, UIA and overlay checks skipped: they need win-x64 in -Runtimes and an x64 host'
}
