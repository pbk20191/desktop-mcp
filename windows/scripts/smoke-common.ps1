# Shared by smoke-stdio.ps1 and smoke-http.ps1 (dot-sourced). The caller defines $Spec, the path of spec/windows/tools.json.

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

# The ControlStatus text of a session started with [control] enabled = false (P4's smoke).
$DisabledControlStatus = '{"state":"disabled","generation":0,"wait_seconds":0.0,"enabled":false,"policy":"relaxed","idle_timeout_seconds":10.0}'

# The modules of the graphics stack that $Process has loaded (a headless run must load none of them).
function Get-GraphicsModules([System.Diagnostics.Process]$Process) {
    $Process.Refresh()
    return @($Process.Modules | Where-Object { $_.ModuleName -in 'd2d1.dll', 'd3d11.dll', 'dwrite.dll', 'd3dcompiler_47.dll' } | ForEach-Object ModuleName)
}
