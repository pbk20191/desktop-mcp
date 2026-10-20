# Windows port P0: foundations and probes, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the `windows/` solution so that a NativeAOT `desktop-mcp.exe` answers `tools/list` over stdio with the 21-tool contract, and settle the three unverified technology choices (DispatcherQueue + low-level hooks, Windows.Graphics.Capture, ImageSharp) with AOT probes.

**Architecture:** Four src projects (exe, Core, Desktop, Server) and three test projects under `windows/`. The tool contract lives in `spec/windows/tools.json`, is embedded into `DesktopMcp.Server`, and every tool's `ProtocolTool` is deserialised from it. Desktop-side probes are production classes in `DesktopMcp.Desktop` exercised by a small AOT-published `DesktopMcp.Probe` exe.

**Tech Stack:** .NET 10 SDK, NativeAOT, ModelContextProtocol 2.2.0, CsWinRT (via the Windows TFM), SixLabors ImageSharp, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-19-windows-port-design.md` (sections 2, 3, 4, 5, 6 "Capture and imaging", 11 P0, 12).

## Global Constraints

- Target framework everywhere: `net10.0-windows10.0.22621.0`, `SupportedOSPlatformVersion` `10.0.19041.0` (the 22621 projection is needed for `GraphicsCaptureSession.IsBorderRequired`; minimum OS stays Windows 10 2004).
- Runtime identifiers shipped: `win-x64`, `win-arm64`. Native interop structs in this plan assume 64-bit layouts.
- Every src project is NativeAOT-compatible (`IsAotCompatible`), and `TreatWarningsAsErrors` makes any IL2xxx/IL3xxx warning a build failure.
- Command/assembly name `desktop-mcp`; config directory `~/.desktop-mcp`; environment prefix `DESKTOP_MCP_`.
- Server name `desktop-mcp`; `initialize` instructions: `Windows MCP server provides tools to interact directly with the Windows desktop,\nthus enabling to operate the desktop on the user's behalf.` (upstream text without its leading and trailing newline).
- Tool contract: `spec/windows/tools.json`; descriptions verbatim from upstream commit `128f2960c79b74bd2630f4dbc9d4f664f4ce89b3`; no `_meta`, no `x-fastmcp-wrap-result`.
- Package versions must be at least two weeks old on the day they are added.
- Repo docs are written in English.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Work happens on branch `windows-port`.

## File map

```
spec/macos/tools.json                         (moved from spec/tools.json)
spec/windows/tools.json                       (new, the contract)
README.md                                     (contract paragraph, layout block)
macos/Tests/DesktopMCPCoreTests/ContractTests.swift   (spec path)
.gitignore                                    (bin/, obj/, .vs/)
windows/
  global.json
  Directory.Build.props
  Directory.Packages.props
  DesktopMcp.slnx
  src/DesktopMcp/DesktopMcp.csproj            (exe, Sdk.Web, AOT)
  src/DesktopMcp/Program.cs
  src/DesktopMcp/app.manifest
  src/DesktopMcp.Core/DesktopMcp.Core.csproj
  src/DesktopMcp.Core/ServerText.cs           (server name + instructions)
  src/DesktopMcp.Server/DesktopMcp.Server.csproj
  src/DesktopMcp.Server/Tools/ToolSpecCatalog.cs
  src/DesktopMcp.Server/Tools/SpecTool.cs
  src/DesktopMcp.Server/Tools/ToolResults.cs
  src/DesktopMcp.Server/Tools/PendingTool.cs
  src/DesktopMcp.Server/Tools/ToolRegistry.cs
  src/DesktopMcp.Desktop/DesktopMcp.Desktop.csproj
  src/DesktopMcp.Desktop/Interop/Native.cs
  src/DesktopMcp.Desktop/Dispatching/IDesktopDispatcher.cs
  src/DesktopMcp.Desktop/Dispatching/DispatcherQueueThread.cs
  src/DesktopMcp.Desktop/Imaging/CapturedImage.cs
  src/DesktopMcp.Desktop/Imaging/IImageRenderer.cs
  src/DesktopMcp.Desktop/Imaging/ImageSharpRenderer.cs
  src/DesktopMcp.Desktop/Capture/IScreenCapture.cs
  src/DesktopMcp.Desktop/Capture/GraphicsCaptureService.cs
  probes/DesktopMcp.Probe/DesktopMcp.Probe.csproj
  probes/DesktopMcp.Probe/Program.cs
  probes/DesktopMcp.Probe/HookProbe.cs
  tests/DesktopMcp.Core.Tests/DesktopMcp.Core.Tests.csproj
  tests/DesktopMcp.Core.Tests/ServerTextTests.cs
  tests/DesktopMcp.Server.Tests/DesktopMcp.Server.Tests.csproj
  tests/DesktopMcp.Server.Tests/RepoFile.cs
  tests/DesktopMcp.Server.Tests/ToolSpecCatalogTests.cs
  tests/DesktopMcp.Server.Tests/ContractTests.cs
  tests/DesktopMcp.Desktop.Tests/DesktopMcp.Desktop.Tests.csproj
  tests/DesktopMcp.Desktop.Tests/DispatcherQueueThreadTests.cs
  tests/DesktopMcp.Desktop.Tests/ImageSharpRendererTests.cs
  tests/DesktopMcp.Desktop.Tests/GraphicsCaptureServiceTests.cs
  scripts/build-release.ps1
  scripts/smoke-stdio.ps1
docs/superpowers/specs/2026-10-19-windows-port-design.md   (probe results, TFM, probes project)
```

All `dotnet` commands below run from `windows/` unless stated otherwise.

---

### Task 1: Toolchain prerequisites

No repository changes; this task makes the machine able to build the plan.

- [ ] **Step 1: Install the .NET 10 SDK machine-wide**

Only SDKs 7.0.410 and 9.0.319 are installed. Run (UAC prompt; the user must approve):

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact --accept-package-agreements --accept-source-agreements
```

- [ ] **Step 2: Verify**

Run: `dotnet --list-sdks`
Expected: a line starting with `10.0.` (for example `10.0.402 [C:\Program Files\dotnet\sdk]`). Note the exact version; Task 5 pins its feature band.

- [ ] **Step 3: Verify the C++ linkers NativeAOT needs**

Run (PowerShell):
```powershell
Get-ChildItem "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\*\lib" -Directory | Select-Object -ExpandProperty Name
```
Expected: the list contains `x64` and `arm64`. (Checked on 2026-10-19: MSVC 14.44.35207 has both.)

---

### Task 2: Split the tool contract per port

**Files:**
- Move: `spec/tools.json` → `spec/macos/tools.json`
- Modify: `macos/Tests/DesktopMCPCoreTests/ContractTests.swift`
- Modify: `README.md` (layout block lines 18-28, "The tool contract" paragraph lines 32-38, port table row for `windows/`)

**Interfaces:** Produces `spec/macos/tools.json` (unchanged content) and the convention `spec/<port>/tools.json`.

- [ ] **Step 1: Move the file with history**

```bash
git mv spec/tools.json spec/macos/tools.json
```

- [ ] **Step 2: Update the macOS contract test**

In `macos/Tests/DesktopMCPCoreTests/ContractTests.swift` replace the doc comment and path:

```swift
/// `spec/macos/tools.json` at the repository root is this port's tool contract. This port
/// generated it; this test fails when `tools/list` drifts from it, so a change to a tool's name,
/// parameters or description is a deliberate change to the contract.
final class ContractTests: XCTestCase {
    static let specURL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()  // DesktopMCPCoreTests
        .deletingLastPathComponent()  // Tests
        .deletingLastPathComponent()  // macos
        .deletingLastPathComponent()  // repository root
        .appendingPathComponent("spec/macos/tools.json")
```

and in the two assertion messages replace `spec/tools.json` with `spec/macos/tools.json`. The `$comment` inside `spec/macos/tools.json` says "shared by every desktop-mcp port"; change it to:

```json
"$comment": "Tool contract of the macOS port: names, parameters and descriptions must match this file. Generated from the macOS port (tools/list) and checked by its conformance test.",
```

The macOS port cannot be built on Windows (it links AppKit and Accessibility); the change is a path edit only. Verify with:

Run: `git grep -n "spec/tools.json"`
Expected: no output.

- [ ] **Step 3: Update the root README**

Replace the layout block's spec lines with:

```
  spec/
    macos/tools.json    # the macOS port's tool contract (names, parameters, descriptions)
    windows/tools.json  # the Windows port's tool contract
```

and the `windows/` layout line with `windows/          # .NET 10 NativeAOT solution, scripts/build-release.ps1 (output: windows/dist/<rid>/)`.

Replace the port table row for `windows/` with:

```
| [`windows/`](windows/) | [CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP) | in progress | C# (.NET 10 NativeAOT) |
```

Replace the first paragraph under "## The tool contract" with:

```markdown
Each port's contract is the tool list of its upstream server, kept in `spec/<port>/tools.json`
(`spec/macos/tools.json`, `spec/windows/tools.json`): what `tools/list` must return, compared as
JSON values. Each port has a test that fails when its own `tools/list` drifts from its file, so a
change to a tool's name, parameters or description is a deliberate change to the contract. The
upstream servers expose different tool sets, so the files differ; platform-specific behaviour
belongs in each port's README.
```

- [ ] **Step 4: Commit**

```bash
git add -A spec README.md macos/Tests/DesktopMCPCoreTests/ContractTests.swift
git commit -F - <<'EOF'
Split the tool contract into per-port files

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 3: Write `spec/windows/tools.json`

**Files:**
- Create: `spec/windows/tools.json`

**Interfaces:** Produces the contract file: `{"$comment": string, "upstream": {...}, "tools": [Tool...]}` where each tool has `name`, `title`, `description`, `inputSchema`, optional `outputSchema`, `annotations`, in upstream registration order.

The source is the upstream inventory extracted during design:
`C:\Users\pbk\AppData\Local\Temp\claude\d--Workspace-desktop-mcp\21666705-aa44-4812-b255-a6f8978ca7a9\scratchpad\upstream-windows-tools.json`
(21 tools; descriptions and annotations verbatim from upstream source; schemas derived by hand from the Python signatures).

- [ ] **Step 1: Generate the contract file**

Run from the repository root (save the script in the scratchpad, not the repo):

```python
# C:\Users\pbk\AppData\Local\Temp\claude\d--Workspace-desktop-mcp\21666705-aa44-4812-b255-a6f8978ca7a9\scratchpad\make_windows_spec.py
import json, sys

src_path, dst_path = sys.argv[1], sys.argv[2]
src = json.load(open(src_path, encoding="utf-8"))

def clean(node):
    if isinstance(node, dict):
        return {k: clean(v) for k, v in node.items() if k != "x-fastmcp-wrap-result"}
    if isinstance(node, list):
        return [clean(v) for v in node]
    return node

tools = []
for tool in src["tools"]:
    out = {k: tool[k] for k in ("name", "title", "description", "inputSchema", "outputSchema", "annotations") if k in tool}
    tools.append(clean(out))

doc = {
    "$comment": "Tool contract of the Windows port: names, parameters and descriptions must match this file. "
                "Taken from CursorTouch/Windows-MCP (FastMCP artefacts _meta and x-fastmcp-wrap-result removed) "
                "and checked by the port's ContractTests.",
    "upstream": {"repository": src["upstream"], "commit": src["commit"], "package_version": src["package_version"]},
    "tools": tools,
}
with open(dst_path, "w", encoding="utf-8", newline="\n") as f:
    json.dump(doc, f, indent=2, ensure_ascii=False)
    f.write("\n")
print(len(tools), "tools written")
```

```bash
mkdir -p spec/windows
python "C:\Users\pbk\AppData\Local\Temp\claude\d--Workspace-desktop-mcp\21666705-aa44-4812-b255-a6f8978ca7a9\scratchpad\make_windows_spec.py" "C:\Users\pbk\AppData\Local\Temp\claude\d--Workspace-desktop-mcp\21666705-aa44-4812-b255-a6f8978ca7a9\scratchpad\upstream-windows-tools.json" spec/windows/tools.json
```

Expected: `21 tools written`.

- [ ] **Step 2: Check the result**

```bash
python -c "import json;d=json.load(open('spec/windows/tools.json',encoding='utf-8'));t=d['tools'];print(len(t),[x['name'] for x in t]);s=json.dumps(d);print('_meta' in s,'x-fastmcp' in s,'x-source' in s)"
```

Expected: `21 ['ControlStatus', 'App', 'DisplayInventory', 'PowerShell', 'FileSystem', 'Snapshot', 'Screenshot', 'Click', 'Type', 'Scroll', 'Move', 'Shortcut', 'Wait', 'WaitFor', 'Scrape', 'MultiSelect', 'MultiEdit', 'Clipboard', 'Process', 'Notification', 'Registry']` then `False False False`.

- [ ] **Step 3: Commit**

```bash
git add spec/windows/tools.json
git commit -F - <<'EOF'
Add the Windows tool contract from upstream Windows-MCP

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 4: Check the contract against a live upstream server (needs user approval)

The schemas in Task 3 were derived by hand. This task compares them with a real upstream `tools/list`. It installs `uv` and runs the third-party `windows-mcp` package, so **ask the user before starting**. If the user declines, skip the task and add the sentence "Input and output schemas are hand-derived and were not checked against a running upstream server." to the end of the `$comment` in `spec/windows/tools.json`, then commit that.

**Files:**
- Modify (only if differences are found): `spec/windows/tools.json`

- [ ] **Step 1: Install uv**

```powershell
winget install --id astral-sh.uv --exact --accept-package-agreements --accept-source-agreements
```

- [ ] **Step 2: Dump upstream `tools/list`**

Save as `C:\Users\pbk\AppData\Local\Temp\claude\d--Workspace-desktop-mcp\21666705-aa44-4812-b255-a6f8978ca7a9\scratchpad\dump_upstream.py` and run it with `python -I`:

```python
import json, os, subprocess, sys
env = dict(os.environ, ANONYMIZED_TELEMETRY="false", WINDOWS_MCP_CONTROL="false")
p = subprocess.Popen(["uvx", "--python", "3.14", "--from", "windows-mcp==0.8.8", "windows-mcp", "serve"],
                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, env=env)
def send(o): p.stdin.write((json.dumps(o) + "\n").encode()); p.stdin.flush()
def recv(): return json.loads(p.stdout.readline())
send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "dump", "version": "1"}}})
recv()
send({"jsonrpc": "2.0", "method": "notifications/initialized"})
send({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
tools = recv()["result"]["tools"]
p.stdin.close(); p.wait(10)
json.dump(tools, open(sys.argv[1], "w", encoding="utf-8"), indent=2, ensure_ascii=False)
print(len(tools))
```

Run: `python -I <scratchpad>\dump_upstream.py <scratchpad>\upstream-live-tools.json`
Expected: `21`.

- [ ] **Step 3: Diff**

```python
import json, sys
def clean(n):
    if isinstance(n, dict): return {k: clean(v) for k, v in n.items() if k not in ("_meta", "x-fastmcp-wrap-result")}
    if isinstance(n, list): return [clean(v) for v in n]
    return n
live = {t["name"]: clean(t) for t in json.load(open(sys.argv[1], encoding="utf-8"))}
spec = {t["name"]: t for t in json.load(open("spec/windows/tools.json", encoding="utf-8"))["tools"]}
for name in sorted(set(live) | set(spec)):
    if live.get(name) != spec.get(name):
        print("DIFF", name); print(" live:", json.dumps(live.get(name), sort_keys=True)); print(" spec:", json.dumps(spec.get(name), sort_keys=True))
```

Expected: no `DIFF` lines. For every `DIFF`, the live value wins: copy the live tool (minus `_meta` and `x-fastmcp-wrap-result`) into `spec/windows/tools.json`, keeping upstream order, and rerun until clean. Then append " Checked against a live upstream tools/list (windows-mcp 0.8.8)." to the `$comment`.

- [ ] **Step 4: Commit**

```bash
git add spec/windows/tools.json
git commit -F - <<'EOF'
Check the Windows tool contract against live upstream

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 5: Solution skeleton and build settings

**Files:**
- Modify: `.gitignore`
- Create: `windows/global.json`, `windows/Directory.Build.props`, `windows/Directory.Packages.props`, `windows/DesktopMcp.slnx`
- Create: the four src `.csproj` files, `windows/src/DesktopMcp/app.manifest`, `windows/src/DesktopMcp.Core/ServerText.cs`
- Create: the three test `.csproj` files, `windows/tests/DesktopMcp.Core.Tests/ServerTextTests.cs`

**Interfaces:**
- Produces `DesktopMcp.Core.ServerText` with `public const string Name = "desktop-mcp";` and `public const string Instructions` (exact text in Global Constraints) and `public static string Version` (assembly informational version without the `+commit` suffix).

- [ ] **Step 1: Ignore build output**

Append to `.gitignore`:

```
bin/
obj/
.vs/
*.user
```

- [ ] **Step 2: Pin the SDK**

`windows/global.json` (use the feature band printed in Task 1 Step 2; `10.0.400` covers 10.0.4xx):

```json
{
  "sdk": {
    "version": "10.0.400",
    "rollForward": "latestPatch"
  }
}
```

- [ ] **Step 3: Shared build properties**

`windows/Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <InvariantGlobalization>true</InvariantGlobalization>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Version>0.1.0</Version>
    <Authors>desktop-mcp contributors</Authors>
  </PropertyGroup>

  <PropertyGroup Condition="!$(MSBuildProjectName.EndsWith('.Tests'))">
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>

  <PropertyGroup Condition="$(MSBuildProjectName.EndsWith('.Tests'))">
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
</Project>
```

`windows/Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="ModelContextProtocol" Version="2.2.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Projects**

`windows/src/DesktopMcp.Core/DesktopMcp.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>DesktopMcp.Core</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="DesktopMcp.Core.Tests" />
  </ItemGroup>
</Project>
```

`windows/src/DesktopMcp.Desktop/DesktopMcp.Desktop.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>DesktopMcp.Desktop</RootNamespace>
    <DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\DesktopMcp.Core\DesktopMcp.Core.csproj" />
    <InternalsVisibleTo Include="DesktopMcp.Desktop.Tests" />
  </ItemGroup>
</Project>
```

`windows/src/DesktopMcp.Server/DesktopMcp.Server.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>DesktopMcp.Server</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="ModelContextProtocol" />
    <ProjectReference Include="..\DesktopMcp.Core\DesktopMcp.Core.csproj" />
    <ProjectReference Include="..\DesktopMcp.Desktop\DesktopMcp.Desktop.csproj" />
    <InternalsVisibleTo Include="DesktopMcp.Server.Tests" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include="$(MSBuildThisFileDirectory)..\..\..\spec\windows\tools.json" LogicalName="DesktopMcp.Server.tools.json" />
  </ItemGroup>
</Project>
```

`windows/src/DesktopMcp/DesktopMcp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>desktop-mcp</AssemblyName>
    <RootNamespace>DesktopMcp</RootNamespace>
    <PublishAot>true</PublishAot>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <OptimizationPreference>Size</OptimizationPreference>
    <EventSourceSupport>false</EventSourceSupport>
    <UseSystemResourceKeys>true</UseSystemResourceKeys>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\DesktopMcp.Server\DesktopMcp.Server.csproj" />
  </ItemGroup>
</Project>
```

`windows/src/DesktopMcp/app.manifest` (verified by the design probes to give physical coordinates):

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="0.1.0.0" name="desktop-mcp"/>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>
    </application>
  </compatibility>
</assembly>
```

Test projects. `windows/tests/DesktopMcp.Core.Tests/DesktopMcp.Core.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <ProjectReference Include="..\..\src\DesktopMcp.Core\DesktopMcp.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
</Project>
```

`windows/tests/DesktopMcp.Server.Tests/DesktopMcp.Server.Tests.csproj` and `windows/tests/DesktopMcp.Desktop.Tests/DesktopMcp.Desktop.Tests.csproj` are identical except the `ProjectReference` points to `..\..\src\DesktopMcp.Server\DesktopMcp.Server.csproj` and `..\..\src\DesktopMcp.Desktop\DesktopMcp.Desktop.csproj` respectively.

`windows/DesktopMcp.slnx`:

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/DesktopMcp/DesktopMcp.csproj" />
    <Project Path="src/DesktopMcp.Core/DesktopMcp.Core.csproj" />
    <Project Path="src/DesktopMcp.Desktop/DesktopMcp.Desktop.csproj" />
    <Project Path="src/DesktopMcp.Server/DesktopMcp.Server.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/DesktopMcp.Core.Tests/DesktopMcp.Core.Tests.csproj" />
    <Project Path="tests/DesktopMcp.Desktop.Tests/DesktopMcp.Desktop.Tests.csproj" />
    <Project Path="tests/DesktopMcp.Server.Tests/DesktopMcp.Server.Tests.csproj" />
  </Folder>
</Solution>
```

- [ ] **Step 5: Write the failing test for ServerText**

`windows/tests/DesktopMcp.Core.Tests/ServerTextTests.cs`:

```csharp
using DesktopMcp.Core;

namespace DesktopMcp.Core.Tests;

public class ServerTextTests
{
    [Fact]
    public void NameIsDesktopMcp() => Assert.Equal("desktop-mcp", ServerText.Name);

    [Fact]
    public void InstructionsAreUpstreamTextWithoutOuterNewlines()
    {
        Assert.Equal(
            "Windows MCP server provides tools to interact directly with the Windows desktop,\n" +
            "thus enabling to operate the desktop on the user's behalf.",
            ServerText.Instructions);
    }

    [Fact]
    public void VersionHasNoBuildMetadata()
    {
        Assert.Equal("0.1.0", ServerText.Version);
    }
}
```

- [ ] **Step 6: Run it to see it fail**

Run: `dotnet test tests/DesktopMcp.Core.Tests`
Expected: build error `CS0103: The name 'ServerText' does not exist`.

- [ ] **Step 7: Implement ServerText**

`windows/src/DesktopMcp.Core/ServerText.cs`:

```csharp
using System.Reflection;

namespace DesktopMcp.Core;

/// <summary>Identity the server reports in <c>initialize</c>.</summary>
public static class ServerText
{
    public const string Name = "desktop-mcp";

    // Upstream's instructions without the leading and trailing newline (see spec §10).
    public const string Instructions =
        "Windows MCP server provides tools to interact directly with the Windows desktop,\n" +
        "thus enabling to operate the desktop on the user's behalf.";

    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(ServerText).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
```

- [ ] **Step 8: Build everything and run the tests**

Run: `dotnet build DesktopMcp.slnx` then `dotnet test DesktopMcp.slnx`
Expected: build succeeds with 0 warnings; 3 tests pass (the other two test projects report no tests).

- [ ] **Step 9: Commit**

```bash
git add .gitignore windows
git commit -F - <<'EOF'
Add the Windows solution skeleton

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 6: Spec-driven tools and the contract test

**Files:**
- Create: `windows/src/DesktopMcp.Server/Tools/ToolSpecCatalog.cs`, `SpecTool.cs`, `ToolResults.cs`, `PendingTool.cs`, `ToolRegistry.cs`
- Test: `windows/tests/DesktopMcp.Server.Tests/RepoFile.cs`, `ToolSpecCatalogTests.cs`, `ContractTests.cs`

**Interfaces:**
- Consumes: the embedded resource `DesktopMcp.Server.tools.json` (Task 5).
- Produces:
  - `public sealed class ToolSpecCatalog` with `static ToolSpecCatalog LoadEmbedded()`, `static ToolSpecCatalog Parse(Stream utf8Json)`, `IReadOnlyList<string> Names` (spec order), `Tool Get(string name)` (throws `KeyNotFoundException`). `Tool` is `ModelContextProtocol.Protocol.Tool`.
  - `public abstract class SpecTool : McpServerTool` with ctor `protected SpecTool(Tool protocolTool)`, `sealed override InvokeAsync` that calls `protected abstract ValueTask<CallToolResult> InvokeCoreAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)` and turns any exception (except cancellation) into `ToolResults.Error(name, message)`.
  - `public static class ToolResults` with `CallToolResult Text(string text)` (one `TextContentBlock` and `StructuredContent = {"result": text}`) and `CallToolResult Error(string toolName, string message)` (`IsError = true`, text `Error calling tool '<toolName>': <message>`).
  - `public sealed class PendingTool : SpecTool` whose invocation throws `NotSupportedException("not available in this build yet")`.
  - `public static class ToolRegistry` with `IReadOnlyList<McpServerTool> CreateAll(ToolSpecCatalog catalog)`; in P0 every tool is a `PendingTool`; later phases replace entries by name.

- [ ] **Step 1: Test helper to find repository files**

`windows/tests/DesktopMcp.Server.Tests/RepoFile.cs`:

```csharp
namespace DesktopMcp.Server.Tests;

internal static class RepoFile
{
    /// <summary>Walks up from the test binaries to the repository root and returns the file's full path.</summary>
    public static string Find(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{relativePath} not found above {AppContext.BaseDirectory}");
    }
}
```

- [ ] **Step 2: Write the failing catalog tests**

`windows/tests/DesktopMcp.Server.Tests/ToolSpecCatalogTests.cs`:

```csharp
using System.Text;
using DesktopMcp.Server.Tools;

namespace DesktopMcp.Server.Tests;

public class ToolSpecCatalogTests
{
    private const string Sample = """
        {"tools":[
          {"name":"Wait","title":"Wait","description":"Waits.","inputSchema":{"type":"object","properties":{"duration":{"type":"integer"}},"required":["duration"]},
           "annotations":{"title":"Wait","readOnlyHint":true}},
          {"name":"Click","title":"Click","description":"Clicks.","inputSchema":{"type":"object","properties":{}}}
        ]}
        """;

    [Fact]
    public void ParseKeepsSpecOrder()
    {
        var catalog = ToolSpecCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Sample)));
        Assert.Equal(["Wait", "Click"], catalog.Names);
    }

    [Fact]
    public void GetReturnsTheProtocolTool()
    {
        var catalog = ToolSpecCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Sample)));
        var wait = catalog.Get("Wait");
        Assert.Equal("Wait", wait.Name);
        Assert.Equal("Waits.", wait.Description);
        Assert.True(wait.Annotations?.ReadOnlyHint);
        Assert.Equal("integer", wait.InputSchema.GetProperty("properties").GetProperty("duration").GetProperty("type").GetString());
    }

    [Fact]
    public void GetUnknownToolThrows()
    {
        var catalog = ToolSpecCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Sample)));
        Assert.Throws<KeyNotFoundException>(() => catalog.Get("Nope"));
    }

    [Fact]
    public void EmbeddedCatalogHasTheTwentyOneTools()
    {
        Assert.Equal(21, ToolSpecCatalog.LoadEmbedded().Names.Count);
    }
}
```

- [ ] **Step 3: Write the failing contract test**

`windows/tests/DesktopMcp.Server.Tests/ContractTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopMcp.Server.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DesktopMcp.Server.Tests;

/// <summary>
/// spec/windows/tools.json is this port's tool contract. The tools the server registers must
/// serialise to exactly those JSON values, so any change to a tool's name, parameters or
/// description is a deliberate change to the contract file.
/// </summary>
public class ContractTests
{
    [Fact]
    public void RegisteredToolsMatchTheContract()
    {
        var spec = JsonNode.Parse(File.ReadAllText(RepoFile.Find("spec/windows/tools.json")))!["tools"]!.AsArray();
        var expected = spec.ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);

        var tools = ToolRegistry.CreateAll(ToolSpecCatalog.LoadEmbedded());
        var typeInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool));
        var actual = tools.ToDictionary(t => t.ProtocolTool.Name, t => JsonSerializer.SerializeToNode(t.ProtocolTool, typeInfo)!);

        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (name, tool) in expected)
        {
            Assert.True(JsonNode.DeepEquals(tool, actual[name]),
                $"{name} differs from spec/windows/tools.json\nspec:   {tool.ToJsonString()}\nserver: {actual[name].ToJsonString()}");
        }
    }

    [Fact]
    public async Task PendingToolReturnsAnErrorResult()
    {
        var tool = ToolRegistry.CreateAll(ToolSpecCatalog.LoadEmbedded()).Single(t => t.ProtocolTool.Name == "Wait");
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(null!, new JsonRpcRequest { Method = "tools/call" }) { Params = new CallToolRequestParams { Name = "Wait" } });
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Wait': not available in this build yet", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }
}
```

`RequestContext<T>`'s constructor signature can differ between SDK minor versions; if this line does not compile, build the context the way the SDK's own tests do for 2.2.0 (`new RequestContext<CallToolRequestParams>(server, request)` with a server from `McpServer.Create` over an in-memory transport) and keep the two assertions unchanged.

- [ ] **Step 4: Run to see them fail**

Run: `dotnet test tests/DesktopMcp.Server.Tests`
Expected: build errors for missing `DesktopMcp.Server.Tools` types.

- [ ] **Step 5: Implement the catalog**

`windows/src/DesktopMcp.Server/Tools/ToolSpecCatalog.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DesktopMcp.Server.Tools;

/// <summary>The tool contract (spec/windows/tools.json) as protocol <see cref="Tool"/> objects.</summary>
public sealed class ToolSpecCatalog
{
    private const string ResourceName = "DesktopMcp.Server.tools.json";
    private readonly Dictionary<string, Tool> _tools;

    private ToolSpecCatalog(List<Tool> tools)
    {
        Names = tools.Select(t => t.Name).ToArray();
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Names { get; }

    public Tool Get(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool : throw new KeyNotFoundException($"Tool '{name}' is not in the contract.");

    public static ToolSpecCatalog LoadEmbedded()
    {
        using var stream = typeof(ToolSpecCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        return Parse(stream);
    }

    public static ToolSpecCatalog Parse(Stream utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var typeInfo = (JsonTypeInfo<Tool>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool));
        var tools = new List<Tool>();
        foreach (var element in document.RootElement.GetProperty("tools").EnumerateArray())
        {
            tools.Add(element.Deserialize(typeInfo) ?? throw new JsonException("null tool entry"));
        }
        return new ToolSpecCatalog(tools);
    }
}
```

- [ ] **Step 6: Implement results, the base class, the pending tool and the registry**

`windows/src/DesktopMcp.Server/Tools/ToolResults.cs`:

```csharp
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace DesktopMcp.Server.Tools;

public static class ToolResults
{
    /// <summary>A text result shaped like upstream's: the text, plus structuredContent {"result": text}.</summary>
    public static CallToolResult Text(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        StructuredContent = JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { ["result"] = text }, ToolJsonContext.Default.DictionaryStringString),
    };

    public static CallToolResult Error(string toolName, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"Error calling tool '{toolName}': {message}" }],
    };
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ToolJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
```

If the SDK's `StructuredContent` is typed `JsonNode?` rather than `JsonElement?` in 2.2.0, assign `JsonSerializer.SerializeToNode(...)` with the same type info instead.

`windows/src/DesktopMcp.Server/Tools/SpecTool.cs`:

```csharp
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// Base class for every tool. The protocol definition comes from the contract file; subclasses
/// only implement the call. Exceptions become upstream-style error results.
/// </summary>
public abstract class SpecTool(Tool protocolTool) : McpServerTool
{
    public override Tool ProtocolTool { get; } = protocolTool;

    public override IReadOnlyList<object> Metadata => [];

    public sealed override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await InvokeCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return ToolResults.Error(ProtocolTool.Name, e.Message);
        }
    }

    protected abstract ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken);
}
```

`windows/src/DesktopMcp.Server/Tools/PendingTool.cs`:

```csharp
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>A contract entry whose implementation lands in a later phase.</summary>
public sealed class PendingTool(Tool protocolTool) : SpecTool(protocolTool)
{
    protected override ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("not available in this build yet");
}
```

`windows/src/DesktopMcp.Server/Tools/ToolRegistry.cs`:

```csharp
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

public static class ToolRegistry
{
    /// <summary>One tool per contract entry, in contract order.</summary>
    public static IReadOnlyList<McpServerTool> CreateAll(ToolSpecCatalog catalog) =>
        catalog.Names.Select(name => (McpServerTool)new PendingTool(catalog.Get(name))).ToArray();
}
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test tests/DesktopMcp.Server.Tests`
Expected: 6 tests pass. If `RegisteredToolsMatchTheContract` fails, the message prints both JSON values. A difference caused by SDK defaults (for example a property the SDK always writes) must be fixed in `spec/windows/tools.json` only if upstream also emits it; otherwise report the difference to the reviewer before changing anything.

- [ ] **Step 8: Commit**

```bash
git add windows
git commit -F - <<'EOF'
Load tool definitions from the contract file and test them

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 7: stdio host, AOT publish and smoke test

**Files:**
- Create: `windows/src/DesktopMcp/Program.cs`
- Create: `windows/scripts/smoke-stdio.ps1`, `windows/scripts/build-release.ps1`

**Interfaces:**
- Consumes: `ServerText` (Task 5), `ToolSpecCatalog`, `ToolRegistry` (Task 6).
- Produces: `windows/dist/<rid>/desktop-mcp.exe`; `scripts/smoke-stdio.ps1 -Exe <path>` exits 0 on success, non-zero with a message on failure.

- [ ] **Step 1: Write the smoke test first**

`windows/scripts/smoke-stdio.ps1`:

```powershell
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

function Send($message) {
    $process.StandardInput.WriteLine(($message | ConvertTo-Json -Compress -Depth 20))
    $process.StandardInput.Flush()
}
function Receive {
    $line = $process.StandardOutput.ReadLineAsync()
    if (-not $line.Wait(15000)) { throw 'no response within 15 s' }
    if ($null -eq $line.Result) { throw "server exited: $($process.StandardError.ReadToEnd())" }
    $line.Result | ConvertFrom-Json -Depth 50
}

try {
    Send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'smoke'; version = '1' } } }
    $init = Receive
    if ($init.result.serverInfo.name -ne 'desktop-mcp') { throw "unexpected serverInfo: $($init | ConvertTo-Json -Depth 5)" }
    Send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    Send @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
    $list = Receive
    if ($list.result.tools.Count -ne 21) { throw "expected 21 tools, got $($list.result.tools.Count)" }

    Send @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'Wait'; arguments = @{ duration = 0 } } }
    $call = Receive
    if ($null -eq $call.result) { throw "tools/call failed: $($call | ConvertTo-Json -Depth 5)" }

    Write-Host "smoke OK: $($init.result.serverInfo.name) $($init.result.serverInfo.version), $($list.result.tools.Count) tools"
}
finally {
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(5000)) { $process.Kill($true); throw 'server did not exit after stdin closed' }
}
```

- [ ] **Step 2: Write the release script**

`windows/scripts/build-release.ps1`:

```powershell
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
```

- [ ] **Step 3: Run the release script to see it fail**

Run: `pwsh scripts/build-release.ps1 -Runtimes win-x64`
Expected: FAIL. The exe has no `Main` (`CS5001: Program does not contain a static 'Main' method`).

- [ ] **Step 4: Implement Program.cs**

`windows/src/DesktopMcp/Program.cs`:

```csharp
using DesktopMcp.Core;
using DesktopMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// P0: stdio only. The CLI (serve/install/uninstall/auth) arrives in P1, HTTP in P5.
var builder = Host.CreateEmptyApplicationBuilder(settings: null);
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = ServerText.Name, Version = ServerText.Version };
        options.ServerInstructions = ServerText.Instructions;
    })
    .WithStdioServerTransport()
    .WithTools(ToolRegistry.CreateAll(ToolSpecCatalog.LoadEmbedded()));

await builder.Build().RunAsync();
```

- [ ] **Step 5: Publish and smoke-test both architectures**

Run: `pwsh scripts/build-release.ps1`
Expected: both publishes succeed with no warnings; sizes are printed (the MCP SDK probe was 14 MB with the same size settings); last line `smoke OK: desktop-mcp 0.1.0, 21 tools`.

- [ ] **Step 6: Commit**

```bash
git add windows/src/DesktopMcp/Program.cs windows/scripts
git commit -F - <<'EOF'
Serve the tool contract over stdio from a NativeAOT exe

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 8: DispatcherQueue thread and the low-level hook probe

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Interop/Native.cs`
- Create: `windows/src/DesktopMcp.Desktop/Dispatching/IDesktopDispatcher.cs`, `DispatcherQueueThread.cs`
- Create: `windows/probes/DesktopMcp.Probe/DesktopMcp.Probe.csproj`, `Program.cs`, `HookProbe.cs`
- Modify: `windows/DesktopMcp.slnx` (add `/probes/` folder)
- Test: `windows/tests/DesktopMcp.Desktop.Tests/DispatcherQueueThreadTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface IDesktopDispatcher : IAsyncDisposable
  {
      bool HasThreadAccess { get; }
      int ManagedThreadId { get; }
      Task InvokeAsync(Action action);
      Task<T> InvokeAsync<T>(Func<T> func);
      Task<IDisposable> StartTimerAsync(TimeSpan interval, Action tick);
  }
  public sealed class DispatcherQueueThread : IDesktopDispatcher { public static DispatcherQueueThread Start(); }
  ```
  Work passed to the dispatcher must be short and non-blocking (spec §5).
- `Native` (internal) gains the P/Invokes used here and in Task 10.

- [ ] **Step 1: Write the failing tests**

`windows/tests/DesktopMcp.Desktop.Tests/DispatcherQueueThreadTests.cs`:

```csharp
using DesktopMcp.Desktop.Dispatching;

namespace DesktopMcp.Desktop.Tests;

public class DispatcherQueueThreadTests
{
    [Fact]
    public async Task InvokeRunsOnTheDedicatedThread()
    {
        await using var dispatcher = DispatcherQueueThread.Start();
        var (threadId, hasAccess) = await dispatcher.InvokeAsync(() => (Environment.CurrentManagedThreadId, dispatcher.HasThreadAccess));
        Assert.Equal(dispatcher.ManagedThreadId, threadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, threadId);
        Assert.True(hasAccess);
        Assert.False(dispatcher.HasThreadAccess);
    }

    [Fact]
    public async Task InvokePropagatesExceptions()
    {
        await using var dispatcher = DispatcherQueueThread.Start();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(() => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public async Task TimerTicksOnTheDispatcherUntilDisposed()
    {
        await using var dispatcher = DispatcherQueueThread.Start();
        var ticks = 0;
        var wrongThread = false;
        var timer = await dispatcher.StartTimerAsync(TimeSpan.FromMilliseconds(20), () =>
        {
            Interlocked.Increment(ref ticks);
            if (!dispatcher.HasThreadAccess) wrongThread = true;
        });
        await Task.Delay(300);
        timer.Dispose();
        var afterDispose = Volatile.Read(ref ticks);
        await Task.Delay(100);
        Assert.True(afterDispose >= 3, $"only {afterDispose} ticks");
        Assert.InRange(Volatile.Read(ref ticks), afterDispose, afterDispose + 1);
        Assert.False(wrongThread);
    }

    [Fact]
    public async Task InvokeAfterDisposeThrows()
    {
        var dispatcher = DispatcherQueueThread.Start();
        await dispatcher.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.InvokeAsync(() => { }));
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests`
Expected: build error, `DesktopMcp.Desktop.Dispatching` does not exist.

- [ ] **Step 3: Native declarations**

`windows/src/DesktopMcp.Desktop/Interop/Native.cs`:

```csharp
using System.Runtime.InteropServices;

namespace DesktopMcp.Desktop.Interop;

/// <summary>
/// Hand-declared Win32 entry points used before CsWin32 is introduced (P2). Layouts assume a
/// 64-bit process (win-x64, win-arm64).
/// </summary>
internal static unsafe partial class Native
{
    // CoreMessaging: DispatcherQueue
    [StructLayout(LayoutKind.Sequential)]
    internal struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;     // DQTYPE_THREAD_DEDICATED = 1
        public int ApartmentType;  // DQTAT_COM_NONE = 0
    }

    [LibraryImport("CoreMessaging.dll")]
    internal static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);

    // user32: monitors
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    internal const uint MonitorDefaultToPrimary = 1;

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(Point point, uint flags);

    // combase: WinRT activation
    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int WindowsCreateString(string sourceString, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    internal static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    internal static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    // d3d11
    internal const int D3DDriverTypeHardware = 1;
    internal const uint D3D11CreateDeviceBgraSupport = 0x20;
    internal const uint D3D11SdkVersion = 7;

    [LibraryImport("d3d11.dll")]
    internal static partial int D3D11CreateDevice(
        nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount,
        uint sdkVersion, out nint device, out int featureLevel, out nint immediateContext);

    [LibraryImport("d3d11.dll")]
    internal static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
```

- [ ] **Step 4: Implement the dispatcher**

`windows/src/DesktopMcp.Desktop/Dispatching/IDesktopDispatcher.cs`:

```csharp
namespace DesktopMcp.Desktop.Dispatching;

/// <summary>
/// The one dedicated thread with a message loop (hooks, Raw Input, overlay windows, timers).
/// Work posted here must be short and must never block: a slow low-level hook callback delays
/// all input on the machine. UIA, capture, file, process and network work never run here.
/// </summary>
public interface IDesktopDispatcher : IAsyncDisposable
{
    bool HasThreadAccess { get; }
    int ManagedThreadId { get; }
    Task InvokeAsync(Action action);
    Task<T> InvokeAsync<T>(Func<T> func);
    /// <summary>Starts a repeating timer whose ticks run on the dispatcher thread. Dispose to stop.</summary>
    Task<IDisposable> StartTimerAsync(TimeSpan interval, Action tick);
}
```

`windows/src/DesktopMcp.Desktop/Dispatching/DispatcherQueueThread.cs`:

```csharp
using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Interop;
using Windows.System;

namespace DesktopMcp.Desktop.Dispatching;

/// <summary><see cref="IDesktopDispatcher"/> on a system-owned DispatcherQueue thread.</summary>
public sealed class DispatcherQueueThread : IDesktopDispatcher
{
    private readonly DispatcherQueueController _controller;
    private readonly DispatcherQueue _queue;
    private int _disposed;

    private DispatcherQueueThread(DispatcherQueueController controller, int threadId)
    {
        _controller = controller;
        _queue = controller.DispatcherQueue;
        ManagedThreadId = threadId;
    }

    public static DispatcherQueueThread Start()
    {
        var options = new Native.DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<Native.DispatcherQueueOptions>(),
            ThreadType = 1,     // DQTYPE_THREAD_DEDICATED
            ApartmentType = 0,  // DQTAT_COM_NONE
        };
        Marshal.ThrowExceptionForHR(Native.CreateDispatcherQueueController(options, out var abi));
        DispatcherQueueController controller;
        try
        {
            controller = DispatcherQueueController.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }

        var threadId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!controller.DispatcherQueue.TryEnqueue(() => threadId.SetResult(Environment.CurrentManagedThreadId)))
            throw new InvalidOperationException("The new DispatcherQueue rejected work.");
        return new DispatcherQueueThread(controller, threadId.Task.GetAwaiter().GetResult());
    }

    public int ManagedThreadId { get; }

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _queue.TryEnqueue(() =>
        {
            try { completion.SetResult(func()); }
            catch (Exception e) { completion.SetException(e); }
        });
        if (!queued) throw new ObjectDisposedException(nameof(DispatcherQueueThread));
        return completion.Task;
    }

    public Task<IDisposable> StartTimerAsync(TimeSpan interval, Action tick) => InvokeAsync<IDisposable>(() =>
    {
        var timer = _queue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = true;
        timer.Tick += (_, _) => tick();
        timer.Start();
        return new TimerHandle(this, timer);
    });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _controller.ShutdownQueueAsync();
    }

    private sealed class TimerHandle(DispatcherQueueThread owner, DispatcherQueueTimer timer) : IDisposable
    {
        public void Dispose()
        {
            if (owner.HasThreadAccess) { timer.Stop(); return; }
            if (Volatile.Read(ref owner._disposed) == 0) owner._queue.TryEnqueue(timer.Stop);
        }
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests`
Expected: 4 tests pass.

- [ ] **Step 6: Probe project with the hook check**

`windows/probes/DesktopMcp.Probe/DesktopMcp.Probe.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>desktop-mcp-probe</AssemblyName>
    <PublishAot>true</PublishAot>
    <ApplicationManifest>..\..\src\DesktopMcp\app.manifest</ApplicationManifest>
    <DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\DesktopMcp.Desktop\DesktopMcp.Desktop.csproj" />
  </ItemGroup>
</Project>
```

Add to `windows/DesktopMcp.slnx` before `</Solution>`:

```xml
  <Folder Name="/probes/">
    <Project Path="probes/DesktopMcp.Probe/DesktopMcp.Probe.csproj" />
  </Folder>
```

`windows/probes/DesktopMcp.Probe/HookProbe.cs`:

```csharp
using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Dispatching;

namespace DesktopMcp.Probe;

/// <summary>
/// Installs WH_MOUSE_LL on the dispatcher thread, injects two signed relative mouse moves
/// (+1 px, -1 px) and checks that the hook saw both, flagged as injected, on the dispatcher thread.
/// </summary>
internal static unsafe partial class HookProbe
{
    private const nuint Signature = 0x44_4D_43_50; // "DMCP"
    private const int WhMouseLl = 14;
    private const uint LlmhfInjected = 0x01;
    private static int s_hits, s_wrongThread, s_notInjected, s_dispatcherThreadId;

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public nuint ExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseInput Mouse; }

    [LibraryImport("user32.dll")] private static partial nint SetWindowsHookExW(int idHook, delegate* unmanaged<int, nint, nint, nint> proc, nint module, uint threadId);
    [LibraryImport("user32.dll")] private static partial int UnhookWindowsHookEx(nint hook);
    [LibraryImport("user32.dll")] private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial uint SendInput(uint count, Input* inputs, int size);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);

    [UnmanagedCallersOnly]
    private static nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var info = (MsllHookStruct*)lParam;
            if (info->ExtraInfo == Signature)
            {
                Interlocked.Increment(ref s_hits);
                if (Environment.CurrentManagedThreadId != s_dispatcherThreadId) Interlocked.Increment(ref s_wrongThread);
                if ((info->Flags & LlmhfInjected) == 0) Interlocked.Increment(ref s_notInjected);
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    public static async Task<bool> RunAsync(IDesktopDispatcher dispatcher)
    {
        s_dispatcherThreadId = dispatcher.ManagedThreadId;
        var hook = await dispatcher.InvokeAsync(() => SetWindowsHookExW(WhMouseLl, &MouseProc, GetModuleHandleW(null), 0));
        if (hook == 0) { Console.WriteLine($"hook: SetWindowsHookExW failed ({Marshal.GetLastPInvokeError()})"); return false; }
        try
        {
            var inputs = stackalloc Input[2];
            inputs[0] = new Input { Type = 0, Mouse = new MouseInput { Dx = 1, Flags = 0x0001, ExtraInfo = Signature } };  // MOUSEEVENTF_MOVE
            inputs[1] = new Input { Type = 0, Mouse = new MouseInput { Dx = -1, Flags = 0x0001, ExtraInfo = Signature } };
            var sent = SendInput(2, inputs, sizeof(Input));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (Volatile.Read(ref s_hits) < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
            var ok = sent == 2 && s_hits >= 2 && s_wrongThread == 0 && s_notInjected == 0;
            Console.WriteLine($"hook: sent={sent} hits={s_hits} wrongThread={s_wrongThread} notInjected={s_notInjected} -> {(ok ? "OK" : "FAIL")}");
            return ok;
        }
        finally
        {
            await dispatcher.InvokeAsync(() => UnhookWindowsHookEx(hook));
        }
    }
}
```

`windows/probes/DesktopMcp.Probe/Program.cs`:

```csharp
using System.Diagnostics;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Probe;

// AOT probes for P0. Usage: desktop-mcp-probe dispatcher
var command = args.Length > 0 ? args[0] : "dispatcher";
var ok = command switch
{
    "dispatcher" => await ProbeDispatcherAsync(),
    _ => Fail($"unknown probe '{command}'"),
};
return ok ? 0 : 1;

static async Task<bool> ProbeDispatcherAsync()
{
    var clock = Stopwatch.StartNew();
    await using var dispatcher = DispatcherQueueThread.Start();
    var onThread = await dispatcher.InvokeAsync(() => dispatcher.HasThreadAccess);
    var ticks = 0;
    using (await dispatcher.StartTimerAsync(TimeSpan.FromMilliseconds(20), () => ticks++))
        await Task.Delay(200);
    Console.WriteLine($"dispatcher: started+invoked in {clock.ElapsedMilliseconds} ms, onThread={onThread}, ticks={ticks}");
    var hookOk = await HookProbe.RunAsync(dispatcher);
    return onThread && ticks >= 3 && hookOk;
}

static bool Fail(string message) { Console.Error.WriteLine(message); return false; }
```

- [ ] **Step 7: Publish the probe with AOT and run it**

Run: `dotnet publish probes/DesktopMcp.Probe -c Release -r win-x64 -o probes/out` then `probes/out/desktop-mcp-probe.exe dispatcher`
Expected: publish has 0 warnings; output ends with `hook: sent=2 hits=2 wrongThread=0 notInjected=0 -> OK`; exit code 0. Add `windows/probes/out/` to `.gitignore`.

Decision rule: if `Start()` throws, `FromAbi` fails under AOT, or the hook line says FAIL, stop and report to the reviewer; the fallback (spec §5) is a `MsgWaitForMultipleObjectsEx` loop implementing the same `IDesktopDispatcher`, which becomes its own task.

- [ ] **Step 8: Commit**

```bash
git add .gitignore windows
git commit -F - <<'EOF'
Add the DispatcherQueue thread and an AOT hook probe

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 9: ImageSharp renderer

**Files:**
- Modify: `windows/Directory.Packages.props`, `windows/src/DesktopMcp.Desktop/DesktopMcp.Desktop.csproj`
- Create: `windows/src/DesktopMcp.Desktop/Imaging/CapturedImage.cs`, `IImageRenderer.cs`, `ImageSharpRenderer.cs`
- Modify: `windows/probes/DesktopMcp.Probe/Program.cs` (add `imaging`)
- Test: `windows/tests/DesktopMcp.Desktop.Tests/ImageSharpRendererTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record CapturedImage(int Width, int Height, byte[] Bgra); // 32-bit BGRA, stride = Width * 4
  public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom) { public int Width => Right - Left; public int Height => Bottom - Top; }
  public sealed record Annotation(PixelRect Box, string Label);
  public interface IImageRenderer { byte[] RenderPng(CapturedImage image, double scale, IReadOnlyList<Annotation> annotations); }
  public sealed class ImageSharpRenderer : IImageRenderer { public static IReadOnlyList<(byte R, byte G, byte B)> Palette { get; } }
  ```
  Annotation boxes are in source-image pixels; the renderer scales them with the image. The colour of annotation `i` is `Palette[i % Palette.Count]`.

- [ ] **Step 1: Add the packages**

Find the newest stable versions published at least two weeks ago:

```bash
dotnet package search SixLabors.ImageSharp --exact-match --format json
dotnet package search SixLabors.ImageSharp.Drawing --exact-match --format json
```

Add them to `Directory.Packages.props` (`<PackageVersion Include="SixLabors.ImageSharp" Version="..." />`, same for `SixLabors.ImageSharp.Drawing`; Drawing brings SixLabors.Fonts) and reference both from `DesktopMcp.Desktop.csproj`:

```xml
  <ItemGroup>
    <PackageReference Include="SixLabors.ImageSharp" />
    <PackageReference Include="SixLabors.ImageSharp.Drawing" />
  </ItemGroup>
```

Add `<PackageReference Include="SixLabors.ImageSharp" />` to `DesktopMcp.Desktop.Tests.csproj` as well (the tests decode PNGs).

- [ ] **Step 2: Write the failing tests**

`windows/tests/DesktopMcp.Desktop.Tests/ImageSharpRendererTests.cs`:

```csharp
using DesktopMcp.Desktop.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DesktopMcp.Desktop.Tests;

public class ImageSharpRendererTests
{
    private static CapturedImage Gray(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4) { bgra[i] = 0x80; bgra[i + 1] = 0x80; bgra[i + 2] = 0x80; bgra[i + 3] = 0xFF; }
        return new CapturedImage(width, height, bgra);
    }

    [Fact]
    public void EncodesAPngOfTheSameSize()
    {
        var png = new ImageSharpRenderer().RenderPng(Gray(120, 90), 1.0, []);
        using var image = Image.Load<Rgba32>(png);
        Assert.Equal((120, 90), (image.Width, image.Height));
        Assert.Equal(new Rgba32(0x80, 0x80, 0x80, 0xFF), image[60, 45]);
    }

    [Fact]
    public void ScalesDown()
    {
        var png = new ImageSharpRenderer().RenderPng(Gray(200, 100), 0.5, []);
        using var image = Image.Load<Rgba32>(png);
        Assert.Equal((100, 50), (image.Width, image.Height));
    }

    [Fact]
    public void DrawsTheBoxInThePaletteColourAndLeavesTheInsideAlone()
    {
        var png = new ImageSharpRenderer().RenderPng(Gray(200, 150), 1.0, [new Annotation(new PixelRect(40, 50, 140, 120), "0")]);
        using var image = Image.Load<Rgba32>(png);
        var (r, g, b) = ImageSharpRenderer.Palette[0];
        Assert.Equal(new Rgba32(r, g, b, 0xFF), image[40, 90]);   // left edge, mid-height
        Assert.Equal(new Rgba32(0x80, 0x80, 0x80, 0xFF), image[90, 90]); // inside
    }

    [Fact]
    public void BoxesScaleWithTheImage()
    {
        var png = new ImageSharpRenderer().RenderPng(Gray(400, 300), 0.5, [new Annotation(new PixelRect(80, 100, 280, 240), "0")]);
        using var image = Image.Load<Rgba32>(png);
        var (r, g, b) = ImageSharpRenderer.Palette[0];
        Assert.Equal(new Rgba32(r, g, b, 0xFF), image[40, 85]);   // left edge at x = 80 * 0.5
    }
}
```

- [ ] **Step 3: Run to see them fail**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter FullyQualifiedName~ImageSharpRenderer`
Expected: build error, `DesktopMcp.Desktop.Imaging` does not exist.

- [ ] **Step 4: Implement**

`windows/src/DesktopMcp.Desktop/Imaging/CapturedImage.cs`:

```csharp
namespace DesktopMcp.Desktop.Imaging;

/// <summary>A captured frame: 32-bit BGRA pixels, rows packed (stride = Width * 4).</summary>
public sealed record CapturedImage(int Width, int Height, byte[] Bgra);

/// <summary>A rectangle in image pixels; Right and Bottom are exclusive.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>A numbered box drawn over a screenshot.</summary>
public sealed record Annotation(PixelRect Box, string Label);
```

`windows/src/DesktopMcp.Desktop/Imaging/IImageRenderer.cs`:

```csharp
namespace DesktopMcp.Desktop.Imaging;

public interface IImageRenderer
{
    /// <summary>Scales the image by <paramref name="scale"/> (0 &lt; scale ≤ 1), draws the annotations and encodes PNG.</summary>
    byte[] RenderPng(CapturedImage image, double scale, IReadOnlyList<Annotation> annotations);
}
```

`windows/src/DesktopMcp.Desktop/Imaging/ImageSharpRenderer.cs`:

```csharp
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DesktopMcp.Desktop.Imaging;

public sealed class ImageSharpRenderer : IImageRenderer
{
    // Fixed colours instead of upstream's random ones, so the same label keeps its colour.
    public static IReadOnlyList<(byte R, byte G, byte B)> Palette { get; } =
    [
        (230, 25, 75), (60, 180, 75), (0, 130, 200), (245, 130, 48), (145, 30, 180), (70, 240, 240),
        (240, 50, 230), (210, 245, 60), (0, 128, 128), (170, 110, 40), (128, 0, 0), (0, 0, 128),
    ];

    private const float BoxThickness = 2f;
    private readonly Lazy<Font> _font = new(() =>
        SystemFonts.TryGet("Arial", out var family) ? family.CreateFont(12) : SystemFonts.Families.First().CreateFont(12));

    public byte[] RenderPng(CapturedImage image, double scale, IReadOnlyList<Annotation> annotations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(scale, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, 1);

        using var canvas = Image.LoadPixelData<Bgra32>(image.Bgra, image.Width, image.Height);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        canvas.Mutate(context =>
        {
            if (width != image.Width || height != image.Height)
                context.Resize(width, height, KnownResamplers.Lanczos3);
            for (var i = 0; i < annotations.Count; i++)
                DrawAnnotation(context, annotations[i], i, scale);
        });

        using var output = new MemoryStream();
        canvas.SaveAsPng(output);
        return output.ToArray();
    }

    private void DrawAnnotation(IImageProcessingContext context, Annotation annotation, int index, double scale)
    {
        var (r, g, b) = Palette[index % Palette.Count];
        var color = Color.FromRgb(r, g, b);
        var box = annotation.Box;
        float left = (float)(box.Left * scale), top = (float)(box.Top * scale);
        float width = (float)(box.Width * scale), height = (float)(box.Height * scale);

        // Inset by half the pen so the stroke covers the box's first and last pixel columns.
        var half = BoxThickness / 2;
        context.Draw(color, BoxThickness, new RectangularPolygon(left + half, top + half, width - BoxThickness, height - BoxThickness));

        var font = _font.Value;
        var size = TextMeasurer.MeasureSize(annotation.Label, new TextOptions(font));
        var labelWidth = size.Width + 4;
        var labelHeight = size.Height + 2;
        var labelLeft = left + width - labelWidth;
        var labelTop = top - labelHeight >= 0 ? top - labelHeight : top + height;
        context.Fill(color, new RectangularPolygon(labelLeft, labelTop, labelWidth, labelHeight));
        context.DrawText(annotation.Label, font, Color.White, new PointF(labelLeft + 2, labelTop + 1));
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter FullyQualifiedName~ImageSharpRenderer`
Expected: 4 tests pass. If antialiasing makes the edge pixel differ from the exact palette colour, set `context.SetGraphicsOptions(o => o.Antialias = false)` before drawing boxes; do not loosen the assertions.

- [ ] **Step 6: Add the imaging probe**

In `windows/probes/DesktopMcp.Probe/Program.cs`, add `"imaging" => ProbeImaging(args.Length > 1 ? args[1] : "probe-imaging.png"),` to the switch and this function:

```csharp
static bool ProbeImaging(string path)
{
    const int width = 1920, height = 1080;
    var bgra = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var i = (y * width + x) * 4;
        bgra[i] = (byte)x; bgra[i + 1] = (byte)y; bgra[i + 2] = 0x40; bgra[i + 3] = 0xFF;
    }
    var annotations = Enumerable.Range(0, 40)
        .Select(n => new DesktopMcp.Desktop.Imaging.Annotation(new(40 + n % 8 * 230, 60 + n / 8 * 200, 200 + n % 8 * 230, 200 + n / 8 * 200), n.ToString()))
        .ToArray();
    var clock = Stopwatch.StartNew();
    var png = new DesktopMcp.Desktop.Imaging.ImageSharpRenderer().RenderPng(new(width, height, bgra), 0.75, annotations);
    File.WriteAllBytes(path, png);
    Console.WriteLine($"imaging: {png.Length / 1024} KB PNG in {clock.ElapsedMilliseconds} ms -> {Path.GetFullPath(path)}");
    return png.Length > 0;
}
```

- [ ] **Step 7: Publish with AOT, run, and record the size cost**

Run: `dotnet publish probes/DesktopMcp.Probe -c Release -r win-x64 -o probes/out` then `probes/out/desktop-mcp-probe.exe imaging probes/out/probe-imaging.png`
Expected: publish has 0 warnings (warnings are errors); output `imaging: ... KB PNG in ... ms`. Open the PNG and confirm 40 numbered boxes with white labels. Record the probe exe size before and after this task (`(Get-Item probes/out/desktop-mcp-probe.exe).Length`) for Task 11.

Decision rule: if publishing fails with IL2xxx/IL3xxx warnings from ImageSharp, or the probe crashes under AOT, stop and report; the fallback is a Direct2D/DirectWrite `IImageRenderer` (spec §6), which becomes its own task.

- [ ] **Step 8: Commit**

```bash
git add windows
git commit -F - <<'EOF'
Render and annotate screenshots with ImageSharp

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 10: Windows.Graphics.Capture

**Files:**
- Create: `windows/src/DesktopMcp.Desktop/Capture/IScreenCapture.cs`, `GraphicsCaptureService.cs`
- Modify: `windows/probes/DesktopMcp.Probe/Program.cs` (add `capture`)
- Test: `windows/tests/DesktopMcp.Desktop.Tests/GraphicsCaptureServiceTests.cs`

**Interfaces:**
- Consumes: `Native` (Task 8), `CapturedImage`, `IImageRenderer` (Task 9).
- Produces:
  ```csharp
  public interface IScreenCapture { Task<CapturedImage> CapturePrimaryMonitorAsync(CancellationToken cancellationToken); }
  public sealed class GraphicsCaptureService : IScreenCapture { public static bool IsSupported { get; } }
  ```
  P2 generalises this to any monitor and composite areas; P0 only proves the path.

- [ ] **Step 1: Write the failing desktop test**

`windows/tests/DesktopMcp.Desktop.Tests/GraphicsCaptureServiceTests.cs`:

```csharp
using DesktopMcp.Desktop.Capture;

namespace DesktopMcp.Desktop.Tests;

[Trait("Category", "Desktop")]
public class GraphicsCaptureServiceTests
{
    [Fact]
    public async Task CapturesThePrimaryMonitor()
    {
        Assert.True(GraphicsCaptureService.IsSupported);
        var image = await new GraphicsCaptureService().CapturePrimaryMonitorAsync(CancellationToken.None);

        Assert.True(image.Width > 0 && image.Height > 0);
        Assert.Equal(image.Width * image.Height * 4, image.Bgra.Length);
        var nonBlack = 0;
        for (var i = 0; i < image.Bgra.Length; i += 4 * 97)
            if (image.Bgra[i] != 0 || image.Bgra[i + 1] != 0 || image.Bgra[i + 2] != 0) nonBlack++;
        Assert.True(nonBlack > 100, "the capture is entirely black");
    }
}
```

- [ ] **Step 2: Run to see it fail**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter Category=Desktop`
Expected: build error, `DesktopMcp.Desktop.Capture` does not exist.

- [ ] **Step 3: Implement**

`windows/src/DesktopMcp.Desktop/Capture/IScreenCapture.cs`:

```csharp
using DesktopMcp.Desktop.Imaging;

namespace DesktopMcp.Desktop.Capture;

public interface IScreenCapture
{
    Task<CapturedImage> CapturePrimaryMonitorAsync(CancellationToken cancellationToken);
}
```

`windows/src/DesktopMcp.Desktop/Capture/GraphicsCaptureService.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DesktopMcp.Desktop.Imaging;
using DesktopMcp.Desktop.Interop;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace DesktopMcp.Desktop.Capture;

/// <summary>Screen capture through Windows.Graphics.Capture (one frame per call).</summary>
public sealed unsafe class GraphicsCaptureService : IScreenCapture
{
    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);

    public static bool IsSupported { get; } = GraphicsCaptureSession.IsSupported();

    public async Task<CapturedImage> CapturePrimaryMonitorAsync(CancellationToken cancellationToken)
    {
        var monitor = Native.MonitorFromPoint(default, Native.MonitorDefaultToPrimary);
        var item = CreateItemForMonitor(monitor);
        using var device = CreateDevice();

        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            session.IsBorderRequired = false;

        var firstFrame = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (sender, _) =>
        {
            var frame = sender.TryGetNextFrame();
            if (frame is not null && !firstFrame.TrySetResult(frame)) frame.Dispose();
        };
        session.StartCapture();

        using var frame = await firstFrame.Task.WaitAsync(FrameTimeout, cancellationToken).ConfigureAwait(false);
        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        return new CapturedImage(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    private static GraphicsCaptureItem CreateItemForMonitor(nint monitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(Native.WindowsCreateString(className, (uint)className.Length, out var hstring));
        nint interop = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Native.RoGetActivationFactory(hstring, IidGraphicsCaptureItemInterop, out interop));
            // IGraphicsCaptureItemInterop: IUnknown (3 slots), CreateForWindow (3), CreateForMonitor (4).
            var createForMonitor = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(void***)interop)[4];
            var iid = IidGraphicsCaptureItem;
            Marshal.ThrowExceptionForHR(createForMonitor(interop, monitor, &iid, &item));
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (interop != 0) Marshal.Release(interop);
            Native.WindowsDeleteString(hstring);
        }
    }

    private static IDirect3DDevice CreateDevice()
    {
        Marshal.ThrowExceptionForHR(Native.D3D11CreateDevice(
            0, Native.D3DDriverTypeHardware, 0, Native.D3D11CreateDeviceBgraSupport, 0, 0,
            Native.D3D11SdkVersion, out var d3dDevice, out _, out var context));
        nint dxgiDevice = 0, inspectable = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, IidDxgiDevice, out dxgiDevice));
            Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable));
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != 0) Marshal.Release(inspectable);
            if (dxgiDevice != 0) Marshal.Release(dxgiDevice);
            Marshal.Release(context);
            Marshal.Release(d3dDevice);
        }
    }
}
```

- [ ] **Step 4: Run the desktop test (interactive session required)**

Run: `dotnet test tests/DesktopMcp.Desktop.Tests --filter Category=Desktop`
Expected: `CapturesThePrimaryMonitor` passes.

- [ ] **Step 5: Add the capture probe**

In `windows/probes/DesktopMcp.Probe/Program.cs` add `"capture" => await ProbeCaptureAsync(args.Length > 1 ? args[1] : "probe-capture.png"),` to the switch and:

```csharp
static async Task<bool> ProbeCaptureAsync(string path)
{
    Console.WriteLine($"capture: supported={DesktopMcp.Desktop.Capture.GraphicsCaptureService.IsSupported}");
    var clock = Stopwatch.StartNew();
    var image = await new DesktopMcp.Desktop.Capture.GraphicsCaptureService().CapturePrimaryMonitorAsync(CancellationToken.None);
    var captureMs = clock.ElapsedMilliseconds;
    var png = new DesktopMcp.Desktop.Imaging.ImageSharpRenderer().RenderPng(image, 1.0, []);
    File.WriteAllBytes(path, png);
    Console.WriteLine($"capture: {image.Width}x{image.Height} in {captureMs} ms, png {png.Length / 1024} KB -> {Path.GetFullPath(path)}");
    return image.Width > 0;
}
```

- [ ] **Step 6: Publish with AOT and run**

Run: `dotnet publish probes/DesktopMcp.Probe -c Release -r win-x64 -o probes/out` then `probes/out/desktop-mcp-probe.exe capture probes/out/probe-capture.png`
Expected: 0 warnings; `capture: <W>x<H> in <ms> ms` with the primary monitor's physical resolution. Open the PNG and confirm it shows the desktop. Ask the user whether a yellow capture border flashed on screen during the run, and record the answer and the Windows build (`[Environment]::OSVersion.Version`) for Task 11.

Decision rule: if the service fails under AOT (exception from `FromAbi`, `MarshalInterface`, or `CreateCopyFromSurfaceAsync`), stop and report; the fallback is a GDI `IScreenCapture` (spec §6), which becomes its own task.

- [ ] **Step 7: Commit**

```bash
git add windows
git commit -F - <<'EOF'
Capture the screen with Windows.Graphics.Capture

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 11: Record the probe results in the spec (P0 checkpoint)

**Files:**
- Modify: `docs/superpowers/specs/2026-10-19-windows-port-design.md`

- [ ] **Step 1: Update the spec**

1. §2 "Minimum OS" row: say the TFM is `net10.0-windows10.0.22621.0` with `SupportedOSPlatformVersion` 10.0.19041.0, because `IsBorderRequired` only exists in the 22621 projection.
2. §3 layout: add `probes/DesktopMcp.Probe/   # AOT probe exe (dispatcher, imaging, capture); not shipped` under `windows/`, and note that P0 uses hand-declared `LibraryImport`s in `Desktop/Interop/Native.cs` until CsWin32 arrives with UIA in P2.
3. §12 Risks: for each of the three probes, replace the mitigation with the measured outcome: dispatcher start time and hook result; ImageSharp warnings (none) and the exe size added; capture resolution, time, and whether the border appeared on this Windows build.
4. Release sizes: add the `desktop-mcp.exe` sizes for win-x64 and win-arm64 from Task 7 to §2's NativeAOT row.

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/2026-10-19-windows-port-design.md
git commit -F - <<'EOF'
Record P0 probe results in the Windows design

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
EOF
```

- [ ] **Step 3: Checkpoint**

Report to the user: the three probe outcomes, the exe sizes, the contract status (Task 4 run or skipped), and any decision rule that fired. The P1 plan is written only after this checkpoint.
