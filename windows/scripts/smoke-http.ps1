#Requires -Version 7.4
# Runs two HTTP sessions of desktop-mcp.exe under NativeAOT (P5 plan, Task 10). Both bind 127.0.0.1 on port 0 (an
# ephemeral loopback port: no firewall rule, nothing reachable from the network) and run with `[control] enabled = false`
# from a temp config file and DESKTOP_MCP_DISABLE_FLASH=1, with every inherited DESKTOP_MCP_* variable dropped (they would
# override the config file). They never take control, show nothing and inject nothing: the only tools called are
# ControlStatus and Wait. The HTTP calls go through a small compiled helper (System.Net.Http): a PowerShell scriptblock
# cannot be a certificate callback (it runs off the runspace) and Invoke-WebRequest cannot reliably override Host.
# 1. `serve --transport streamable-http` with DESKTOP_MCP_AUTH_KEY (32 random bytes, base64url): /health without auth,
#    401 without the bearer, initialize and the session id, tools/list equals spec/windows/tools.json, ControlStatus
#    (disabled) and Wait, a foreign Origin (403) and Host (400), DELETE of the session (then 404), and that the server did
#    not load the graphics stack (d2d1, d3d11, dwrite, d3dcompiler_47). The server is stopped with Stop-Process; its
#    kill-on-close job closes with it.
# 2. `serve --transport sse` over TLS with a self-signed ECDSA P-256 certificate made here with CertificateRequest and
#    written as PEM into a temp folder (no certificate store): the https announcement, /health over HTTP/2 with the
#    certificate pinned by its SHA-256 thumbprint, the SSE endpoint event, and an initialize posted to /message whose
#    result arrives on the stream. The server loads the PEM pair through PKCS#12, which keeps a temporary key file under
#    %APPDATA%\Microsoft\Crypto until the certificate is disposed, so this session is stopped with Ctrl+C (sent from a
#    helper attached to the server's own hidden console) and must exit by itself; Stop-Process is the fallback.
# The temp folders are removed in finally.
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Spec = (Join-Path $PSScriptRoot '../../spec/windows/tools.json')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'smoke-common.ps1')

if (-not ('DesktopMcpSmoke.Http' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopMcpSmoke
{
    public sealed class Reply
    {
        public int Status;
        public Version Version;
        public string Body;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Header(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }
    }

    public sealed class SseEvent
    {
        public string Event;
        public string Data;
    }

    public static class Http
    {
        // A client for loopback calls: no proxy, no redirects, no cookies. With a pinned SHA-256 thumbprint the server
        // certificate must be exactly that one; nothing else changes certificate validation.
        public static HttpClient Create(string pinnedSha256, bool http2Only)
        {
            var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
            if (pinnedSha256 != null)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    certificate != null && string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), pinnedSha256, StringComparison.OrdinalIgnoreCase);
            }
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            if (http2Only)
            {
                client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            }
            return client;
        }

        private static HttpRequestMessage Request(HttpClient client, string method, string url, IDictionary headers, string body)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), url)
            {
                Version = client.DefaultRequestVersion,
                VersionPolicy = client.DefaultVersionPolicy,
            };
            if (body != null) request.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
            if (headers != null)
            {
                foreach (DictionaryEntry entry in headers)
                {
                    var name = (string)entry.Key;
                    var value = (string)entry.Value;
                    if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)) request.Headers.Host = value;
                    else if (!request.Headers.TryAddWithoutValidation(name, value)) throw new ArgumentException("cannot send header " + name);
                }
            }
            return request;
        }

        public static Reply Send(HttpClient client, string method, string url, IDictionary headers, string body, int timeoutSeconds)
        {
            return Task.Run(async () =>
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                using (var request = Request(client, method, url, headers, body))
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false))
                {
                    var reply = new Reply { Status = (int)response.StatusCode, Version = response.Version };
                    reply.Body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    foreach (var header in response.Headers) reply.Headers[header.Key] = string.Join(", ", header.Value);
                    foreach (var header in response.Content.Headers) reply.Headers[header.Key] = string.Join(", ", header.Value);
                    return reply;
                }
            }).GetAwaiter().GetResult();
        }

        public static SseStream OpenSse(HttpClient client, string url, IDictionary headers, int timeoutSeconds)
        {
            return Task.Run(async () =>
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    var request = Request(client, "GET", url, headers, null);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    return new SseStream(request, response, new StreamReader(stream, new UTF8Encoding(false)));
                }
            }).GetAwaiter().GetResult();
        }
    }

    public sealed class SseStream : IDisposable
    {
        private readonly HttpRequestMessage _request;
        private readonly HttpResponseMessage _response;
        private readonly StreamReader _reader;

        internal SseStream(HttpRequestMessage request, HttpResponseMessage response, StreamReader reader)
        {
            _request = request;
            _response = response;
            _reader = reader;
        }

        public int Status { get { return (int)_response.StatusCode; } }

        public Version Version { get { return _response.Version; } }

        public string ContentType { get { return _response.Content.Headers.ContentType == null ? null : _response.Content.Headers.ContentType.MediaType; } }

        // The next event (comment lines skipped), or null when the stream ends; throws when none arrives in time.
        public SseEvent Next(int timeoutSeconds)
        {
            return Task.Run(async () =>
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    string name = null;
                    var data = new List<string>();
                    while (true)
                    {
                        string line;
                        try { line = await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw new TimeoutException("no SSE event within " + timeoutSeconds + " s"); }
                        if (line == null) return null;
                        if (line.Length == 0)
                        {
                            if (name == null && data.Count == 0) continue;
                            return new SseEvent { Event = name ?? "message", Data = string.Join("\n", data) };
                        }
                        if (line.StartsWith(":", StringComparison.Ordinal)) continue;
                        var colon = line.IndexOf(':');
                        var field = colon < 0 ? line : line.Substring(0, colon);
                        var value = colon < 0 ? "" : line.Substring(colon + 1);
                        if (value.StartsWith(" ", StringComparison.Ordinal)) value = value.Substring(1);
                        if (field == "event") name = value;
                        else if (field == "data") data.Add(value);
                    }
                }
            }).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            _reader.Dispose();
            _response.Dispose();
            _request.Dispose();
        }
    }
}
'@
}

# Sends Ctrl+C to the console of $ProcessId from a helper pwsh that detaches from its own console, attaches to the
# server's, ignores Ctrl+C itself and raises the event for that console only.
$ctrlCHelper = @'
param([uint32]$Target)
Add-Type -Namespace DesktopMcpSmoke -Name Console -MemberDefinition @"
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(System.IntPtr handler, bool add);
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
"@
[void][DesktopMcpSmoke.Console]::FreeConsole()
if (-not [DesktopMcpSmoke.Console]::AttachConsole($Target)) { exit 2 }
[void][DesktopMcpSmoke.Console]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)
if (-not [DesktopMcpSmoke.Console]::GenerateConsoleCtrlEvent(0, 0)) { exit 3 }
exit 0
'@

# Starts the exe with $Arguments, the temp config file and $Environment, with stderr redirected, and waits up to 15 s
# for the "desktop-mcp: serving <transport> on <url>" line. The server gets a console of its own without a window
# (CreateNoWindow), so a Ctrl+C sent to it reaches nobody else.
function Start-Server([string[]]$Arguments, [hashtable]$Environment = @{}) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($exePath)
    foreach ($argument in $Arguments + @('--config', $configPath)) { $psi.ArgumentList.Add($argument) }
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardErrorEncoding = $utf8
    $psi.StandardOutputEncoding = $utf8
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    foreach ($name in @($psi.Environment.Keys | Where-Object { $_ -like 'DESKTOP_MCP_*' })) { $psi.Environment.Remove($name) | Out-Null }
    $psi.Environment['DESKTOP_MCP_DISABLE_FLASH'] = '1'
    foreach ($name in $Environment.Keys) { $psi.Environment[$name] = $Environment[$name] }
    $process = [System.Diagnostics.Process]::Start($psi)
    $script:servers.Add($process)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $seen = [System.Collections.Generic.List[string]]::new()
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $remaining = 15000 - $clock.ElapsedMilliseconds
        $line = $process.StandardError.ReadLineAsync()
        if ($remaining -le 0 -or -not $line.Wait([Math]::Max(0, $remaining))) { throw "no announcement within 15 s: $($seen -join ' | ')" }
        if ($null -eq $line.Result) { throw "server exited before announcing: $($seen -join ' | ')" }
        $seen.Add($line.Result)
        if ($line.Result -match '^desktop-mcp: serving (\S+) on (\S+)$') { break }
    }
    # Drain stderr from here on, so a chatty server never blocks on a full pipe.
    $stderr = $process.StandardError.ReadToEndAsync()
    return [pscustomobject]@{ Process = $process; Transport = $Matches[1]; Url = [Uri]$Matches[2]; Stderr = $stderr; Stdout = $stdout }
}

# A JSON-RPC POST with the Streamable HTTP Accept header. Returns the reply and the JSON-RPC message for $Id, read from a
# JSON body or from the data: lines of an SSE body.
function Invoke-Mcp([string]$Url, [hashtable]$Message, [hashtable]$Headers = @{}, $Client = $script:plain) {
    $all = @{ Accept = 'application/json, text/event-stream' } + $Headers
    $reply = [DesktopMcpSmoke.Http]::Send($Client, 'POST', $Url, $all, ($Message | ConvertTo-Json -Compress -Depth 20), 30)
    $response = $null
    if ($reply.Status -eq 200 -and $Message.ContainsKey('id')) {
        $type = "$($reply.Header('Content-Type'))"
        $payloads = if ($type.StartsWith('text/event-stream')) {
            @($reply.Body -split "\r?\n" | Where-Object { $_.StartsWith('data:') } | ForEach-Object { $_.Substring(5).TrimStart() })
        }
        else { @($reply.Body) }
        foreach ($payload in $payloads) {
            $candidate = $payload | ConvertFrom-Json -Depth 50
            if ($null -ne $candidate.PSObject.Properties['id'] -and $candidate.id -eq $Message.id) { $response = $candidate; $script:lastPayload = $payload }
        }
        if ($null -eq $response) { throw "no JSON-RPC response with id $($Message.id) in: $($reply.Body)" }
    }
    return [pscustomobject]@{ Reply = $reply; Message = $response }
}

function New-Initialize([int]$Id) {
    return @{ jsonrpc = '2.0'; id = $Id; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'smoke-http'; version = '1' } } }
}

function Assert-Status($Reply, [int]$Expected, [string]$What) {
    if ($Reply.Status -ne $Expected) { throw "${What}: expected $Expected, got $($Reply.Status): $($Reply.Body)" }
}

# Stops the server with Ctrl+C and waits for it to exit; on failure it is killed and the run fails.
function Stop-ServerGracefully($Server, [int]$Seconds = 10) {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("& { $ctrlCHelper } $($Server.Process.Id)"))
    # CreateNoWindow: the helper's own console has no window either (no terminal opens, not even briefly).
    $psi = [System.Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    foreach ($argument in '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded) { $psi.ArgumentList.Add($argument) }
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $helper = [System.Diagnostics.Process]::Start($psi)
    if (-not $helper.WaitForExit(30000)) { $helper.Kill(); throw 'the Ctrl+C helper did not finish within 30 s' }
    if ($Server.Process.WaitForExit($Seconds * 1000)) { return $helper.ExitCode }
    Stop-Process -Id $Server.Process.Id -Force -ErrorAction SilentlyContinue
    throw "the TLS server did not exit within $Seconds s of Ctrl+C (helper exit $($helper.ExitCode)); it was killed and may have left a temporary key file under %APPDATA%\Microsoft\Crypto"
}

$exePath = (Resolve-Path $Exe).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)
$script:servers = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$script:plain = [DesktopMcpSmoke.Http]::Create($null, $false)
$script:lastPayload = $null
$tempFolder = Join-Path ([IO.Path]::GetTempPath()) "desktop-mcp-smoke-http-$PID-$([Guid]::NewGuid().ToString('N'))"
$configPath = Join-Path $tempFolder 'config.toml'
$tlsClient = $null
$sse = $null
$script:tlsServer = $null
try {
    New-Item -ItemType Directory -Path $tempFolder | Out-Null
    [IO.File]::WriteAllText($configPath, "[control]`nenabled = false`n")

    # Session 1: Streamable HTTP with an auth key.
    $keyBytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($keyBytes)
    $authKey = [Convert]::ToBase64String($keyBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $server = Start-Server @('serve', '--transport', 'streamable-http', '--host', '127.0.0.1', '--port', '0') @{ DESKTOP_MCP_AUTH_KEY = $authKey }
    if ($server.Transport -cne 'streamable-http' -or $server.Url.Scheme -cne 'http' -or $server.Url.Host -cne '127.0.0.1' -or $server.Url.AbsolutePath -cne '/mcp') {
        throw "unexpected announcement: $($server.Transport) on $($server.Url)"
    }
    $mcp = $server.Url.AbsoluteUri
    $base = $server.Url.GetLeftPart([UriPartial]::Authority)
    $bearer = @{ Authorization = "Bearer $authKey" }

    # 1. /health is public.
    $health = [DesktopMcpSmoke.Http]::Send($script:plain, 'GET', "$base/health", $null, $null, 15)
    Assert-Status $health 200 'GET /health'
    if ($health.Body -cne '{"status":"ok"}') { throw "unexpected /health body: $($health.Body)" }

    # 2. No bearer: 401 with upstream's body and the scheme.
    $denied = (Invoke-Mcp $mcp (New-Initialize 1)).Reply
    Assert-Status $denied 401 'initialize without Authorization'
    if ($denied.Body -cne '{"error":"Missing or invalid Authorization header. Expected: Bearer <token>"}') { throw "unexpected 401 body: $($denied.Body)" }
    if ($denied.Header('WWW-Authenticate') -cne 'Bearer') { throw "401 without WWW-Authenticate: Bearer: '$($denied.Header('WWW-Authenticate'))'" }

    # 3. initialize with the bearer: a session.
    $init = Invoke-Mcp $mcp (New-Initialize 1) $bearer
    Assert-Status $init.Reply 200 'initialize'
    $sessionId = $init.Reply.Header('Mcp-Session-Id')
    if (-not $sessionId) { throw 'initialize returned no Mcp-Session-Id' }
    if ($init.Message.result.serverInfo.name -cne 'desktop-mcp') { throw "unexpected serverInfo: $($init.Message | ConvertTo-Json -Depth 5)" }
    $session = $bearer + @{ 'Mcp-Session-Id' = $sessionId; 'MCP-Protocol-Version' = '2025-06-18' }
    $initialized = (Invoke-Mcp $mcp @{ jsonrpc = '2.0'; method = 'notifications/initialized' } $session).Reply
    Assert-Status $initialized 202 'notifications/initialized'

    # 4. tools/list equals the contract.
    $list = Invoke-Mcp $mcp @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' } $session
    Assert-Status $list.Reply 200 'tools/list'
    Assert-ToolsMatchSpec $script:lastPayload
    $toolCount = @($list.Message.result.tools).Count

    # 5. Control is off: ControlStatus says so and Wait is not gated.
    $status = Invoke-Mcp $mcp @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'ControlStatus'; arguments = @{} } } $session
    $statusText = "$($status.Message.result.content[0].text)"
    if ($status.Message.result.isError -or $statusText -cne $DisabledControlStatus) { throw "unexpected ControlStatus with control disabled: $statusText" }
    $wait = Invoke-Mcp $mcp @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{ name = 'Wait'; arguments = @{ duration = 0 } } } $session
    if ($null -eq $wait.Message.result -or $wait.Message.result.isError) { throw "Wait failed: $($wait.Message | ConvertTo-Json -Depth 5)" }

    # 6. A foreign Origin and a foreign Host.
    $origin = (Invoke-Mcp $mcp (New-Initialize 5) ($bearer + @{ Origin = 'https://attacker.example' })).Reply
    Assert-Status $origin 403 'initialize with Origin https://attacker.example'
    $hostReply = (Invoke-Mcp $mcp (New-Initialize 6) ($bearer + @{ Host = 'attacker.example' })).Reply
    Assert-Status $hostReply 400 'initialize with Host attacker.example'
    if ($hostReply.Body -cne 'Invalid host header') { throw "unexpected bad-host body: $($hostReply.Body)" }

    # 8 (before the session ends). The headless server never loaded the graphics stack.
    $graphics = Get-GraphicsModules $server.Process
    if ($graphics.Count -gt 0) { throw "the HTTP server loaded the graphics stack: $($graphics -join ', ')" }
    $moduleCount = $server.Process.Modules.Count

    # 7. DELETE ends the session; the id is then unknown.
    $deleted = [DesktopMcpSmoke.Http]::Send($script:plain, 'DELETE', $mcp, $session, $null, 15)
    if ($deleted.Status -lt 200 -or $deleted.Status -gt 299) { throw "DELETE /mcp: expected 2xx, got $($deleted.Status): $($deleted.Body)" }
    $gone = (Invoke-Mcp $mcp @{ jsonrpc = '2.0'; id = 7; method = 'tools/list' } $session).Reply
    Assert-Status $gone 404 'tools/list after DELETE'

    Stop-Process -Id $server.Process.Id -Force
    $server.Process.WaitForExit(5000) | Out-Null

    # Session 2: legacy SSE over TLS with a certificate made here (no certificate store).
    $certPath = Join-Path $tempFolder 'cert.pem'
    $keyPath = Join-Path $tempFolder 'key.pem'
    $ecdsa = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $ecdsa, [Security.Cryptography.HashAlgorithmName]::SHA256)
        $names = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $names.AddDnsName('localhost')
        $names.AddIpAddress([Net.IPAddress]::Loopback)
        $names.AddIpAddress([Net.IPAddress]::IPv6Loopback)
        $request.CertificateExtensions.Add($names.Build())
        $now = [DateTimeOffset]::UtcNow
        $certificate = $request.CreateSelfSigned($now.AddDays(-1), $now.AddDays(1))
        try {
            [IO.File]::WriteAllText($certPath, $certificate.ExportCertificatePem())
            $pinned = $certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
        }
        finally { $certificate.Dispose() }
        [IO.File]::WriteAllText($keyPath, $ecdsa.ExportPkcs8PrivateKeyPem())
    }
    finally { $ecdsa.Dispose() }

    $server = Start-Server @('serve', '--transport', 'sse', '--host', '127.0.0.1', '--port', '0', '--ssl-certfile', $certPath, '--ssl-keyfile', $keyPath)
    $script:tlsServer = $server
    # 1. The announcement.
    if (-not $server.Url.OriginalString.StartsWith('https://') -or -not $server.Url.OriginalString.EndsWith('/sse')) {
        throw "unexpected TLS announcement: $($server.Transport) on $($server.Url)"
    }
    $base = $server.Url.GetLeftPart([UriPartial]::Authority)
    $tlsClient = [DesktopMcpSmoke.Http]::Create($pinned, $true)

    # 2. /health over HTTP/2, the certificate pinned.
    $health = [DesktopMcpSmoke.Http]::Send($tlsClient, 'GET', "$base/health", $null, $null, 15)
    Assert-Status $health 200 'GET /health over TLS'
    if ($health.Version -ne [Version]'2.0') { throw "GET /health over TLS used HTTP/$($health.Version), not HTTP/2" }

    # 3. The endpoint event, then initialize through /message with the result on the stream.
    $sse = [DesktopMcpSmoke.Http]::OpenSse($tlsClient, $server.Url.AbsoluteUri, $null, 15)
    if ($sse.Status -ne 200 -or $sse.ContentType -cne 'text/event-stream') { throw "GET /sse: $($sse.Status) $($sse.ContentType)" }
    $endpoint = $sse.Next(10)
    if ($null -eq $endpoint -or $endpoint.Event -cne 'endpoint' -or -not $endpoint.Data.StartsWith('/message?sessionId=')) {
        throw "the first SSE event is not the endpoint: $($endpoint.Event) $($endpoint.Data)"
    }
    $posted = (Invoke-Mcp "$base$($endpoint.Data)" (New-Initialize 1) @{} $tlsClient).Reply
    Assert-Status $posted 202 'POST initialize to /message'
    $result = $null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($null -eq $result) {
        $left = 10 - [int][Math]::Floor($clock.Elapsed.TotalSeconds)
        if ($left -le 0) { throw 'no initialize result on the SSE stream within 10 s' }
        $next = $sse.Next($left)
        if ($null -eq $next) { throw 'the SSE stream ended before the initialize result' }
        if ($next.Event -cne 'message') { continue }
        $candidate = $next.Data | ConvertFrom-Json -Depth 50
        if ($null -ne $candidate.PSObject.Properties['id'] -and $candidate.id -eq 1) { $result = $candidate }
    }
    if ($result.result.serverInfo.name -cne 'desktop-mcp') { throw "unexpected initialize result over SSE: $($result | ConvertTo-Json -Depth 5)" }
    $sse.Dispose()
    $sse = $null

    $tlsExit = Stop-ServerGracefully $server
    Write-Host "HTTP smoke details: streamable-http on $mcp ($toolCount tools, graphics stack not loaded, $moduleCount modules); sse on $($server.Url) (HTTP/$($health.Version), exit $($server.Process.ExitCode) after Ctrl+C, helper exit $tlsExit)"
    Write-Host "HTTP smoke OK: streamable-http (auth, session, $toolCount tools, origin and host checks, delete), sse over TLS (HTTP/2, endpoint event, initialize)"
}
finally {
    if ($sse) { $sse.Dispose() }
    if ($tlsClient) { $tlsClient.Dispose() }
    $script:plain.Dispose()
    # A TLS server still running after a failed check gets the graceful stop too, so it removes its temporary key file.
    if ($script:tlsServer -and -not $script:tlsServer.Process.HasExited) {
        try { Stop-ServerGracefully $script:tlsServer | Out-Null }
        catch { Write-Warning "$_" }
    }
    foreach ($process in $script:servers) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item $tempFolder -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $tempFolder) { Write-Warning "could not remove $tempFolder (it holds the test certificate's private key)" }
}
