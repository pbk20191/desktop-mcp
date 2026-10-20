#Requires -Version 7
# Checks `serve --help`, then runs two stdio sessions of desktop-mcp.exe:
# 1. `serve --config <empty file>`: initialize, that tools/list equals spec/windows/tools.json (all tools, as JSON
#    values), Wait and the headless tools that leave the desktop alone (FileSystem, Process, Registry, PowerShell,
#    Scrape), and that this headless run did not load the (delay-loaded) graphics stack: d2d1, d3d11,
#    dwrite, d3dcompiler_47. The empty file keeps a developer's ~/.desktop-mcp/config.toml out of the check. A
#    PowerShell call starts a ping that must die with the server (the kill-on-close Job Object, spec §5).
#    The read-only desktop tools follow: DisplayInventory and Snapshot without vision before the graphics check (neither
#    may load the graphics stack), then Screenshot and Snapshot with vision, which need the Windows App SDK runtime and
#    an interactive session; -SkipCapture leaves those two out (Windows Sandbox has no runtime). The desktop calls
#    need an unlocked interactive desktop session: on a locked screen, a secure desktop or in session 0 they fail.
#    Snapshot must show a named virtual desktop (not the "Default Desktop" fallback) and at least one tree element.
#    Both sessions run with DESKTOP_MCP_EXCLUDE_PROCESSES set to -ExcludeProcesses (default Code.exe): Snapshot's UIA
#    walk hung VS Code, a Chromium app, twice on 2026-10-20 (see the P2 leftovers), and a developer runs this script from
#    VS Code. The exclusion keeps those processes out of the tree walk; the window list still probes their
#    top-level windows through UIA.
# 2. `serve --config <file excluding Registry>`: tools/list equals the spec less Registry, which proves the NativeAOT
#    exe parsed the TOML file.
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Spec = (Join-Path $PSScriptRoot '../../spec/windows/tools.json'),
    [switch]$SkipCapture,
    [string]$ExcludeProcesses = 'Code.exe'
)
$ErrorActionPreference = 'Stop'

function Send($message) {
    $script:process.StandardInput.WriteLine(($message | ConvertTo-Json -Compress -Depth 20))
    $script:process.StandardInput.Flush()
}
function Receive([int]$ExpectedId, [int]$TimeoutSeconds = 15) {
    while ($true) {
        $line = $script:process.StandardOutput.ReadLineAsync()
        if (-not $line.Wait($TimeoutSeconds * 1000)) { throw "no response within $TimeoutSeconds s (expected id $ExpectedId)" }
        if ($null -eq $line.Result) {
            $script:process.WaitForExit(2000) | Out-Null
            $text = if ($script:stderrTask.Wait(2000)) { $script:stderrTask.Result } else { '(stderr still open)' }
            throw "server exited: $text"
        }
        $message = $line.Result | ConvertFrom-Json -Depth 50
        if ($null -eq $message.PSObject.Properties['id']) { continue } # notification
        if ($message.id -ne $ExpectedId) { throw "expected response id $ExpectedId, got $($message.id)" }
        $script:lastLine = $line.Result
        return $message
    }
}
# tools/list must equal the contract file, less the excluded tool if any, as JSON values; the MCP SDK owns the
# serialisation, so not as bytes.
function Assert-ToolsMatchSpec([string]$ResponseLine, [string]$Excluded = '') {
    $actual = @{}
    foreach ($tool in [System.Text.Json.Nodes.JsonNode]::Parse($ResponseLine)['result']['tools'].AsArray()) { $actual[$tool['name'].ToString()] = $tool }
    $expected = @([System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText((Resolve-Path $Spec).Path))['tools'].AsArray() |
        Where-Object { $_['name'].ToString() -cne $Excluded })
    if ($Excluded -and $actual.ContainsKey($Excluded)) { throw "tools/list has $Excluded, which the config file excludes" }
    if ($actual.Count -ne $expected.Count) { throw "tools/list has $($actual.Count) tools; spec/windows/tools.json$(if ($Excluded) { " less $Excluded" }) has $($expected.Count)" }
    foreach ($tool in $expected) {
        $name = $tool['name'].ToString()
        if (-not $actual.ContainsKey($name)) { throw "tools/list lacks $name" }
        if (-not [System.Text.Json.Nodes.JsonNode]::DeepEquals($tool, $actual[$name])) {
            throw "$name differs from spec/windows/tools.json`nspec:   $($tool.ToJsonString())`nserver: $($actual[$name].ToJsonString())"
        }
    }
}
# Snapshot and Screenshot walk every window and capture each monitor, so they get 60 s (TimeoutSeconds).
function Invoke-Tool([int]$Id, [string]$Name, [hashtable]$Arguments, [int]$TimeoutSeconds = 15) {
    Send @{ jsonrpc = '2.0'; id = $Id; method = 'tools/call'; params = @{ name = $Name; arguments = $Arguments } }
    $response = Receive $Id $TimeoutSeconds
    if ($null -eq $response.result) { throw "$Name failed: $($response | ConvertTo-Json -Depth 5)" }
    return $response.result
}

# Starts `serve --config $ConfigPath`, initializes and lists the tools, then runs $Body with the initialize response,
# the tools/list response and its raw line before closing stdin and waiting up to $ExitSeconds for the server to exit.
function Invoke-Session([string]$ConfigPath, [scriptblock]$Body, [int]$ExitSeconds = 5) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($exePath)
    foreach ($argument in 'serve', '--config', $ConfigPath) { $psi.ArgumentList.Add($argument) }
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardInputEncoding = $utf8
    $psi.StandardOutputEncoding = $utf8
    $psi.UseShellExecute = $false
    $psi.Environment['ANONYMIZED_TELEMETRY'] = 'false'
    $psi.Environment['DESKTOP_MCP_EXCLUDE_PROCESSES'] = $ExcludeProcesses
    $script:process = [System.Diagnostics.Process]::Start($psi)
    # Drain stderr from the start so a chatty server never blocks on a full pipe.
    $script:stderrTask = $script:process.StandardError.ReadToEndAsync()
    try {
        Send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'smoke'; version = '1' } } }
        $init = Receive 1
        if ($init.result.serverInfo.name -ne 'desktop-mcp') { throw "unexpected serverInfo: $($init | ConvertTo-Json -Depth 5)" }
        Send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
        Send @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
        $list = Receive 2
        & $Body $init $list $script:lastLine
    }
    finally {
        # Close() can throw when the server already died; it must not keep the process alive or hide the body's error.
        try { $script:process.StandardInput.Close() } catch { }
        $exitClock = [Diagnostics.Stopwatch]::StartNew()
        if (-not $script:process.WaitForExit($ExitSeconds * 1000)) {
            $script:process.Kill($true)
            Write-Warning "server did not exit within $ExitSeconds s after stdin closed; killed it"
            $script:exitFailure = "server did not exit within $ExitSeconds s after stdin closed"
        }
        $script:exitMilliseconds = $exitClock.ElapsedMilliseconds
        # The capture session allows 30 s; a slow exit must still be visible so the allowance cannot hide a regression.
        if ($script:exitMilliseconds -gt 5000) { Write-Warning "server took $($script:exitMilliseconds) ms to exit after stdin closed" }
    }
    if ($script:exitFailure) { $failure = $script:exitFailure; $script:exitFailure = $null; throw $failure }
}
# A PNG image block: the content type, the MIME type, the PNG signature and a non-empty IHDR size of the decoded data.
function Assert-PngImage($Result, [string]$Name) {
    $image = @($Result.content | Where-Object { $_.type -eq 'image' })
    if ($image.Count -ne 1 -or $image[0].mimeType -ne 'image/png') { throw "$Name returned no PNG image: $($Result | ConvertTo-Json -Depth 4)" }
    $bytes = [Convert]::FromBase64String($image[0].data)
    $signature = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    if ($bytes.Length -lt 24 -or -not [Linq.Enumerable]::SequenceEqual([byte[]]$bytes[0..7], $signature) -or
        [Text.Encoding]::ASCII.GetString($bytes, 12, 4) -cne 'IHDR') { throw "$Name image is not a PNG" }
    $width = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
    $height = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
    if ($width -le 0 -or $height -le 0) { throw "$Name image is $width x $height" }
    return "$($bytes.Length) bytes PNG, ${width}x$height"
}
# The Snapshot text's "Active Desktop:" table (a header, a dash line, then the one name) and the UI tree's element lines
# (every tree line below "desktop" starts with a connector).
function Read-SnapshotText([string]$Text, [string]$Name) {
    $lines = $Text -split "`n"
    $header = [Array]::IndexOf($lines, 'Active Desktop:')
    if ($header -lt 0 -or $lines.Count -lt $header + 4) { throw "$Name has no Active Desktop table: $Text" }
    $tree = [Array]::IndexOf($lines, 'UI Tree:')
    if ($tree -lt 0 -or $lines[$tree + 1] -cne 'desktop') { throw "$Name has no UI tree: $Text" }
    $elements = @(if ($lines.Count -gt $tree + 2) { $lines[($tree + 2)..($lines.Count - 1)] | Where-Object { $_ -match '^[ │]*(├── |└── )' } })
    return @{ ActiveDesktop = $lines[$header + 3].Trim(); Elements = $elements.Count; Labelled = @($elements | Where-Object { $_ -match '(├── |└── )\[\d+\] ' }).Count }
}

$exePath = (Resolve-Path $Exe).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)
$script:lastLine = $null
$script:exitFailure = $null
# The CLI and Tomlyn only run under NativeAOT here (xUnit runs them under the JIT).
$help = (& $exePath serve --help) -join "`n"
if ($LASTEXITCODE -ne 0 -or -not $help.Contains('--exclude-tools')) { throw "serve --help failed ($LASTEXITCODE): $help" }

$emptyConfig = Join-Path ([IO.Path]::GetTempPath()) "desktop-mcp-smoke-$PID-empty.toml"
$excludingConfig = Join-Path ([IO.Path]::GetTempPath()) "desktop-mcp-smoke-$PID.toml"
# A non-default value with a visible effect: tools/list must lack the excluded tool.
$excluded = 'Registry'
try {
    [IO.File]::WriteAllText($emptyConfig, '')
    [IO.File]::WriteAllText($excludingConfig, "[server]`ntransport = `"stdio`"`n`n[tools]`nexclude = [`"$excluded`"]`n`n[control]`npolicy = `"relaxed`"`n")

    # After a capture the exe has taken seconds to exit, its last thread in process teardown without using CPU (P2 Task
    # 19 measured 0.03-7.5 s in the attempt during which VS Code hung, then 23-25 ms on five runs with Code.exe
    # excluded; cause unknown, see the P2 leftovers). A run without capture keeps 5 s.
    Invoke-Session $emptyConfig -ExitSeconds $(if ($SkipCapture) { 5 } else { 30 }) -Body {
        param($init, $list, $listLine)
        $expectedInstructions = "Windows MCP server provides tools to interact directly with the Windows desktop,`nthus enabling to operate the desktop on the user's behalf."
        if ($init.result.instructions -cne $expectedInstructions) { throw "unexpected instructions: $($init.result.instructions)" }
        Assert-ToolsMatchSpec $listLine

        Send @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'Wait'; arguments = @{ duration = 0 } } }
        $call = Receive 3
        if ($null -eq $call.result -or $call.result.isError) { throw "Wait failed: $($call | ConvertTo-Json -Depth 5)" }
        if ($call.result.content[0].text -cne 'Waited for 0 seconds.') { throw "unexpected Wait text: $($call.result.content[0].text)" }

        # Headless tools under NativeAOT. Clipboard and Notification are left out: they change the user's desktop.
        $cases = @(
            @{ Name = 'FileSystem'; Arguments = @{ mode = 'info'; path = $exePath }; Prefix = 'Path: '; IsError = $false },
            @{ Name = 'Process'; Arguments = @{ mode = 'list'; limit = 1 }; Prefix = 'Processes (1 shown):'; IsError = $false },
            @{ Name = 'Registry'; Arguments = @{ mode = 'list'; path = 'HKCU:\Environment' }; Prefix = 'Registry key [HKCU:\Environment]:'; IsError = $false },
            @{ Name = 'PowerShell'; Arguments = @{ command = 'Write-Output smoke'; timeout = 30 }; Prefix = 'Response: smoke'; IsError = $false },
            @{ Name = 'Scrape'; Arguments = @{ url = 'http://127.0.0.1:9/'; use_sampling = $false }; Prefix = "Error calling tool 'Scrape': Private, loopback"; IsError = $true }
        )
        $id = 10
        foreach ($case in $cases) {
            $result = Invoke-Tool ($id++) $case.Name $case.Arguments
            $text = $result.content[0].text
            if ([bool]$result.isError -ne $case.IsError -or -not $text.StartsWith($case.Prefix, [StringComparison]::Ordinal)) {
                throw "$($case.Name): unexpected result (isError=$($result.isError)): $text"
            }
        }

        # Read-only desktop tools that must not load the graphics stack: DisplayInventory and Snapshot without vision.
        # Snapshot runs the real UIA walk and the virtual-desktop calls under NativeAOT, so check what they found.
        $displays = Invoke-Tool 30 'DisplayInventory' @{}
        if ($displays.isError -or $displays.structuredContent.result.Count -lt 1) { throw "DisplayInventory failed: $($displays | ConvertTo-Json -Depth 6)" }
        $snapshot = Invoke-Tool 31 'Snapshot' @{ use_vision = $false } 60
        $snapshotText = $snapshot.content[0].text
        if ($snapshot.isError -or $snapshot.content.Count -ne 1 -or -not $snapshotText.StartsWith('Cursor Position: ') -or -not $snapshotText.Contains("`nUI Tree:`ndesktop")) {
            throw "Snapshot failed: $snapshotText"
        }
        $read = Read-SnapshotText $snapshotText 'Snapshot'
        # "Default Desktop" is the fallback when the virtual-desktop manager fails (P2 plan, Decisions 19).
        if ($read.ActiveDesktop -in '', 'Default Desktop') { throw "Snapshot read no virtual desktop (Active Desktop: '$($read.ActiveDesktop)'): $snapshotText" }
        if ($read.Elements -lt 1) { throw "Snapshot has no UI tree elements: $snapshotText" }
        $script:desktopRead = "DisplayInventory ($($displays.structuredContent.result.Count) displays) and Snapshot without vision OK (active desktop '$($read.ActiveDesktop)', $($read.Elements) tree elements, $($read.Labelled) labelled)"

        # A grandchild of the server: the kill-on-close job must stop it when the server exits.
        Send @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{ name = 'PowerShell'; arguments = @{ command = "(Start-Process -FilePath ping.exe -ArgumentList '-n','120','127.0.0.1' -WindowStyle Hidden -PassThru).Id"; timeout = 20 } } }
        $call = Receive 4
        if ($null -eq $call.result -or $call.result.isError -or $call.result.content[0].text -notmatch '^Response: (\d+)\r?\n\nStatus Code: 0$') { throw "PowerShell failed: $($call | ConvertTo-Json -Depth 5)" }
        $script:pingId = [int]$Matches[1]

        # The graphics stack is delay-loaded (Win2DStatic.targets): a run that never renders must not load it.
        $script:process.Refresh()
        $graphics = @($script:process.Modules | Where-Object { $_.ModuleName -in 'd2d1.dll', 'd3d11.dll', 'dwrite.dll', 'd3dcompiler_47.dll' } | ForEach-Object ModuleName)
        if ($graphics.Count -gt 0) { throw "headless run loaded the graphics stack: $($graphics -join ', ')" }
        $script:full = "$($init.result.serverInfo.name) $($init.result.serverInfo.version), $($list.result.tools.Count) tools match spec/windows/tools.json, Wait and $($cases.Count) headless tools OK, $script:desktopRead, graphics stack not loaded ($($script:process.Modules.Count) modules)"

        # Capture under NativeAOT: Win2D and Windows.Graphics.Capture through the static activation. From here on the
        # graphics stack is expected to load.
        if (-not $SkipCapture) {
            $screenshot = Invoke-Tool 40 'Screenshot' @{} 60
            if ($screenshot.isError -or -not $screenshot.content[0].text.StartsWith('Cursor Position: ')) { throw "Screenshot failed: $($screenshot.content[0].text)" }
            $screenshotImage = Assert-PngImage $screenshot 'Screenshot'
            $vision = Invoke-Tool 41 'Snapshot' @{ use_vision = $true; region = @(0, 0, 200, 120) } 60
            if ($vision.isError -or -not $vision.content[0].text.Contains('Screenshot Region: (')) { throw "Snapshot with a region failed: $($vision.content[0].text)" }
            $visionImage = Assert-PngImage $vision 'Snapshot'
            $script:full += ", Screenshot ($screenshotImage) and Snapshot with vision ($visionImage) OK"
        }
    }
    $ping = Get-Process -Id $script:pingId -ErrorAction SilentlyContinue
    if ($ping -and -not $ping.WaitForExit(5000)) { $ping.Kill(); throw "ping $($script:pingId) outlived the server: no kill-on-close job" }
    $script:full += ", PowerShell OK, its child died with the server (server exit $($script:exitMilliseconds) ms)"

    Invoke-Session $excludingConfig {
        param($init, $list, $listLine)
        Assert-ToolsMatchSpec $listLine $excluded
        $script:filtered = "--config excluded $excluded ($($list.result.tools.Count) tools match the rest)"
    }

    Write-Host "smoke OK: $script:full; $script:filtered"
}
finally {
    Remove-Item $emptyConfig, $excludingConfig -ErrorAction SilentlyContinue
}
