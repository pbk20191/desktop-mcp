#Requires -Version 7
# Checks `serve --help`, then runs two stdio sessions of desktop-mcp.exe:
# 1. `serve --config <empty file>`: initialize, that tools/list equals spec/windows/tools.json (all tools, as JSON
#    values), one tool call, and that this headless run did not load the (delay-loaded) graphics stack: d2d1, d3d11,
#    dwrite, d3dcompiler_47. The empty file keeps a developer's ~/.desktop-mcp/config.toml out of the check. A
#    PowerShell call starts a ping that must die with the server (the kill-on-close Job Object, spec §5).
# 2. `serve --config <file excluding Registry>`: tools/list equals the spec less Registry, which proves the NativeAOT
#    exe parsed the TOML file.
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Spec = (Join-Path $PSScriptRoot '../../spec/windows/tools.json')
)
$ErrorActionPreference = 'Stop'

function Send($message) {
    $script:process.StandardInput.WriteLine(($message | ConvertTo-Json -Compress -Depth 20))
    $script:process.StandardInput.Flush()
}
function Receive([int]$ExpectedId) {
    while ($true) {
        $line = $script:process.StandardOutput.ReadLineAsync()
        if (-not $line.Wait(15000)) { throw "no response within 15 s (expected id $ExpectedId)" }
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

# Starts `serve --config $ConfigPath`, initializes and lists the tools, then runs $Body with the initialize response,
# the tools/list response and its raw line before closing stdin and waiting for the server to exit.
function Invoke-Session([string]$ConfigPath, [scriptblock]$Body) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($exePath)
    foreach ($argument in 'serve', '--config', $ConfigPath) { $psi.ArgumentList.Add($argument) }
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardInputEncoding = $utf8
    $psi.StandardOutputEncoding = $utf8
    $psi.UseShellExecute = $false
    $psi.Environment['ANONYMIZED_TELEMETRY'] = 'false'
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
        $script:process.StandardInput.Close()
        if (-not $script:process.WaitForExit(5000)) { $script:process.Kill($true); throw 'server did not exit after stdin closed' }
    }
}

$exePath = (Resolve-Path $Exe).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)
$script:lastLine = $null
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

    Invoke-Session $emptyConfig {
        param($init, $list, $listLine)
        $expectedInstructions = "Windows MCP server provides tools to interact directly with the Windows desktop,`nthus enabling to operate the desktop on the user's behalf."
        if ($init.result.instructions -cne $expectedInstructions) { throw "unexpected instructions: $($init.result.instructions)" }
        Assert-ToolsMatchSpec $listLine

        Send @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'Wait'; arguments = @{ duration = 0 } } }
        $call = Receive 3
        if ($null -eq $call.result -or $call.result.isError) { throw "Wait failed: $($call | ConvertTo-Json -Depth 5)" }
        if ($call.result.content[0].text -cne 'Waited for 0 seconds.') { throw "unexpected Wait text: $($call.result.content[0].text)" }

        # A grandchild of the server: the kill-on-close job must stop it when the server exits.
        Send @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{ name = 'PowerShell'; arguments = @{ command = "(Start-Process -FilePath ping.exe -ArgumentList '-n','120','127.0.0.1' -WindowStyle Hidden -PassThru).Id"; timeout = 20 } } }
        $call = Receive 4
        if ($null -eq $call.result -or $call.result.isError -or $call.result.content[0].text -notmatch '^Response: (\d+)?

Status Code: 0$') { throw "PowerShell failed: $($call | ConvertTo-Json -Depth 5)" }
        $script:pingId = [int]$Matches[1]

        # The graphics stack is delay-loaded (Win2DStatic.targets): a run that never renders must not load it.
        $script:process.Refresh()
        $graphics = @($script:process.Modules | Where-Object { $_.ModuleName -in 'd2d1.dll', 'd3d11.dll', 'dwrite.dll', 'd3dcompiler_47.dll' } | ForEach-Object ModuleName)
        if ($graphics.Count -gt 0) { throw "headless run loaded the graphics stack: $($graphics -join ', ')" }
        $script:full = "$($init.result.serverInfo.name) $($init.result.serverInfo.version), $($list.result.tools.Count) tools match spec/windows/tools.json, Wait OK, graphics stack not loaded ($($script:process.Modules.Count) modules)"
    }
    $ping = Get-Process -Id $script:pingId -ErrorAction SilentlyContinue
    if ($ping -and -not $ping.WaitForExit(5000)) { $ping.Kill(); throw "ping $($script:pingId) outlived the server: no kill-on-close job" }
    $script:full += ", PowerShell OK, its child died with the server"

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
