#Requires -Version 7
# Checks `serve --help`, then starts `desktop-mcp.exe serve --config <file>` over stdio and checks initialize, that
# tools/list equals spec/windows/tools.json less the tool the config file excludes, one tool call, and that this
# headless run did not load the (delay-loaded) graphics stack: d2d1, d3d11, dwrite, d3dcompiler_47.
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Spec = (Join-Path $PSScriptRoot '../../spec/windows/tools.json')
)
$ErrorActionPreference = 'Stop'

$exePath = (Resolve-Path $Exe).Path
# The CLI and Tomlyn only run under NativeAOT here (xUnit runs them under the JIT).
$help = (& $exePath serve --help) -join "`n"
if ($LASTEXITCODE -ne 0 -or -not $help.Contains('--exclude-tools')) { throw "serve --help failed ($LASTEXITCODE): $help" }
# A non-default value with a visible effect: tools/list must lack the excluded tool, which proves the exe parsed the file.
$excluded = 'Registry'
$config = Join-Path ([IO.Path]::GetTempPath()) "desktop-mcp-smoke-$PID.toml"
[IO.File]::WriteAllText($config, "[server]`ntransport = `"stdio`"`n`n[tools]`nexclude = [`"$excluded`"]`n`n[control]`npolicy = `"relaxed`"`n")

$utf8 = [System.Text.UTF8Encoding]::new($false)
$psi = [System.Diagnostics.ProcessStartInfo]::new($exePath)
foreach ($argument in 'serve', '--config', $config) { $psi.ArgumentList.Add($argument) }
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardInputEncoding = $utf8
$psi.StandardOutputEncoding = $utf8
$psi.UseShellExecute = $false
$psi.Environment['ANONYMIZED_TELEMETRY'] = 'false'
try {
    $process = [System.Diagnostics.Process]::Start($psi)
}
catch {
    Remove-Item $config -ErrorAction SilentlyContinue
    throw
}
# Drain stderr from the start so a chatty server never blocks on a full pipe.
$stderrTask = $process.StandardError.ReadToEndAsync()
$script:lastLine = $null

function Send($message) {
    $process.StandardInput.WriteLine(($message | ConvertTo-Json -Compress -Depth 20))
    $process.StandardInput.Flush()
}
function Receive([int]$ExpectedId) {
    while ($true) {
        $line = $process.StandardOutput.ReadLineAsync()
        if (-not $line.Wait(15000)) { throw "no response within 15 s (expected id $ExpectedId)" }
        if ($null -eq $line.Result) {
            $process.WaitForExit(2000) | Out-Null
            $text = if ($stderrTask.Wait(2000)) { $stderrTask.Result } else { '(stderr still open)' }
            throw "server exited: $text"
        }
        $message = $line.Result | ConvertFrom-Json -Depth 50
        if ($null -eq $message.PSObject.Properties['id']) { continue } # notification
        if ($message.id -ne $ExpectedId) { throw "expected response id $ExpectedId, got $($message.id)" }
        $script:lastLine = $line.Result
        return $message
    }
}
# tools/list must equal the contract file, less the excluded tool, as JSON values; the MCP SDK owns the serialisation,
# so not as bytes.
function Assert-ToolsMatchSpec([string]$ResponseLine, [string]$Excluded) {
    $actual = @{}
    foreach ($tool in [System.Text.Json.Nodes.JsonNode]::Parse($ResponseLine)['result']['tools'].AsArray()) { $actual[$tool['name'].ToString()] = $tool }
    $expected = @([System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText((Resolve-Path $Spec).Path))['tools'].AsArray() |
        Where-Object { $_['name'].ToString() -cne $Excluded })
    if ($actual.ContainsKey($Excluded)) { throw "tools/list has $Excluded, which the config file excludes" }
    if ($actual.Count -ne $expected.Count) { throw "tools/list has $($actual.Count) tools; spec/windows/tools.json less $Excluded has $($expected.Count)" }
    foreach ($tool in $expected) {
        $name = $tool['name'].ToString()
        if (-not $actual.ContainsKey($name)) { throw "tools/list lacks $name" }
        if (-not [System.Text.Json.Nodes.JsonNode]::DeepEquals($tool, $actual[$name])) {
            throw "$name differs from spec/windows/tools.json`nspec:   $($tool.ToJsonString())`nserver: $($actual[$name].ToJsonString())"
        }
    }
}

try {
    Send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'smoke'; version = '1' } } }
    $init = Receive 1
    if ($init.result.serverInfo.name -ne 'desktop-mcp') { throw "unexpected serverInfo: $($init | ConvertTo-Json -Depth 5)" }
    $expectedInstructions = "Windows MCP server provides tools to interact directly with the Windows desktop,`nthus enabling to operate the desktop on the user's behalf."
    if ($init.result.instructions -cne $expectedInstructions) { throw "unexpected instructions: $($init.result.instructions)" }
    Send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    Send @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
    $list = Receive 2
    Assert-ToolsMatchSpec $script:lastLine $excluded

    Send @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'Wait'; arguments = @{ duration = 0 } } }
    $call = Receive 3
    if ($null -eq $call.result -or $call.result.isError) { throw "Wait failed: $($call | ConvertTo-Json -Depth 5)" }
    if ($call.result.content[0].text -cne 'Waited for 0 seconds.') { throw "unexpected Wait text: $($call.result.content[0].text)" }

    # The graphics stack is delay-loaded (Win2DStatic.targets): a run that never renders must not load it.
    $process.Refresh()
    $graphics = @($process.Modules | Where-Object { $_.ModuleName -in 'd2d1.dll', 'd3d11.dll', 'dwrite.dll', 'd3dcompiler_47.dll' } | ForEach-Object ModuleName)
    if ($graphics.Count -gt 0) { throw "headless run loaded the graphics stack: $($graphics -join ', ')" }

    Write-Host "smoke OK: $($init.result.serverInfo.name) $($init.result.serverInfo.version), $($list.result.tools.Count) tools match spec/windows/tools.json less $excluded (excluded by --config), graphics stack not loaded ($($process.Modules.Count) modules)"
}
finally {
    $process.StandardInput.Close()
    $exited = $process.WaitForExit(5000)
    if (-not $exited) { $process.Kill($true) }
    Remove-Item $config -ErrorAction SilentlyContinue
    if (-not $exited) { throw 'server did not exit after stdin closed' }
}
