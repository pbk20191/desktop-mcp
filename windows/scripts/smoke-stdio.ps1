#Requires -Version 7
# Starts desktop-mcp.exe over stdio and checks initialize, tools/list and one tool call.
param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference = 'Stop'

$utf8 = [System.Text.UTF8Encoding]::new($false)
$psi = [System.Diagnostics.ProcessStartInfo]::new((Resolve-Path $Exe).Path)
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardInputEncoding = $utf8
$psi.StandardOutputEncoding = $utf8
$psi.UseShellExecute = $false
$psi.Environment['ANONYMIZED_TELEMETRY'] = 'false'
$process = [System.Diagnostics.Process]::Start($psi)
# Drain stderr from the start so a chatty server never blocks on a full pipe.
$stderrTask = $process.StandardError.ReadToEndAsync()

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
        return $message
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
    if ($list.result.tools.Count -ne 21) { throw "expected 21 tools, got $($list.result.tools.Count)" }

    Send @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'Wait'; arguments = @{ duration = 0 } } }
    $call = Receive 3
    if ($null -eq $call.result) { throw "tools/call failed: $($call | ConvertTo-Json -Depth 5)" }

    Write-Host "smoke OK: $($init.result.serverInfo.name) $($init.result.serverInfo.version), $($list.result.tools.Count) tools"
}
finally {
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(5000)) { $process.Kill($true); throw 'server did not exit after stdin closed' }
}
