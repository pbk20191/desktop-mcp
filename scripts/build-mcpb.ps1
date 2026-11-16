# Packs the Windows desktop-mcp.exe into a Claude Desktop extension (.mcpb) at dist\desktop-mcp.mcpb.
# Claude Desktop runs the bundled binary as a stdio server (`serve --transport stdio`). The tools listed in the
# manifest come from the pinned Windows tools/list, crates\cli\tests\snapshots\windows-tools.json.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts\build-mcpb.ps1 [path\to\desktop-mcp.exe]
#        (default: target\release\desktop-mcp.exe; pwsh works too)
param([string]$Binary = 'target\release\desktop-mcp.exe')
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if (-not (Test-Path $Binary -PathType Leaf)) { throw "$Binary does not exist; build it with: cargo build --release -p desktop-mcp-cli" }
$version = ((& $Binary --version) -split '\s+')[-1]
$dist = 'dist'
$stage = Join-Path $dist 'mcpb'
$out = Join-Path $dist 'desktop-mcp.mcpb'

Remove-Item -Recurse -Force $stage, $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $stage 'server') | Out-Null
Copy-Item $Binary (Join-Path $stage 'server\desktop-mcp.exe')

$listed = Get-Content -Raw -Encoding UTF8 'crates\cli\tests\snapshots\windows-tools.json' | ConvertFrom-Json
$tools = foreach ($tool in $listed.tools) {
    [ordered]@{ name = $tool.name; description = ($tool.description -split '\. ')[0].TrimEnd('.') + '.' }
}
$manifest = [ordered]@{
    manifest_version = '0.3'
    name = 'desktop-mcp'
    display_name = 'Desktop MCP'
    version = $version
    description = 'Control the Windows desktop: apps, windows, mouse and keyboard, UI Automation tree, screenshots, OCR, clipboard, notifications and PowerShell.'
    long_description = 'Runs desktop-mcp as a local stdio server. Windows 11 22H2 or later. Input to elevated (administrator) windows is refused unless Claude Desktop itself runs elevated.'
    author = [ordered]@{ name = 'pbk'; url = 'https://github.com/pbk20191' }
    repository = [ordered]@{ type = 'git'; url = 'https://github.com/pbk20191/desktop-mcp' }
    homepage = 'https://github.com/pbk20191/desktop-mcp'
    license = 'MIT'
    server = [ordered]@{
        type = 'binary'
        entry_point = 'server/desktop-mcp.exe'
        mcp_config = [ordered]@{
            command = '${__dirname}/server/desktop-mcp.exe'
            args = @('serve', '--transport', 'stdio')
            # The host turns single tools off itself, so the bundle offers every tool, opt-in ones included.
            env = [ordered]@{ DESKTOP_MCP_INCLUDE_TOOLS = 'OCR' }
        }
    }
    tools = @($tools)
    keywords = @('windows', 'desktop', 'automation', 'ui-automation')
    compatibility = [ordered]@{ platforms = @('win32') }
}
$json = $manifest | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path (Resolve-Path $stage) 'manifest.json'), $json, [Text.UTF8Encoding]::new($false))

foreach ($file in 'LICENSE', 'NOTICE') { if (Test-Path $file) { Copy-Item $file $stage } }
# Entry by entry, so the names use '/' (Windows PowerShell's .NET Framework writes '\' with CreateFromDirectory).
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = (Resolve-Path $stage).Path
$archive = [IO.Compression.ZipFile]::Open((Join-Path (Resolve-Path $dist) 'desktop-mcp.mcpb'), 'Create')
try {
    foreach ($file in Get-ChildItem -Recurse -File $root) {
        $entry = $file.FullName.Substring($root.Length).TrimStart('\').Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry) | Out-Null
    }
} finally {
    $archive.Dispose()
}
Write-Output "Wrote $out ($version)"
