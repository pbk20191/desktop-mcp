using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DesktopMcp.Core.Interop;

namespace DesktopMcp.Core.PowerShell;

public sealed record PowerShellResult(string Output, int StatusCode);

/// <summary>upstream PowerShellExecutor.execute_command and run_with_graceful_timeout (Decisions 7 and 8).</summary>
public sealed class PowerShellRunner
{
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);

    // $OutputEncoding: how Windows PowerShell encodes its own output; [Console]::OutputEncoding: how it decodes native
    // programs' output. Both UTF-8, so the pipe carries UTF-8 either way.
    internal const string Preamble =
        "$OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; ";

    internal const string AccessDeniedHint =
        "\n\nHINT: This command may require an elevated (Administrator) terminal. " +
        "The desktop-mcp server is currently running at a lower integrity level.";

    // A CancellationTokenSource delay is at most uint.MaxValue - 1 milliseconds; longer timeouts mean no timeout.
    private const long MaxTimeoutSeconds = (uint.MaxValue - 1L) / 1000;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<Dictionary<string, string>> _environment;
    private readonly Func<bool> _isElevated;

    public PowerShellRunner() : this(ShellEnvironment.Build, Elevation.IsElevated) { }

    internal PowerShellRunner(Func<Dictionary<string, string>> environment, Func<bool> isElevated)
    {
        _environment = environment;
        _isElevated = isElevated;
    }

    public async Task<PowerShellResult> RunAsync(string command, long timeoutSeconds, CancellationToken cancellationToken, string? shell = null)
    {
        var environment = _environment();
        // NO_COLOR suppresses ANSI escape sequences in pwsh 7.2+; Windows PowerShell ignores it.
        environment["NO_COLOR"] = "1";
        var resolved = ResolveShell(shell, environment);
        Process process;
        try
        {
            process = Start(resolved, command, environment);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            // "The system cannot find the file specified" does not say which file, so name the shell.
            return new PowerShellResult($"Command execution failed: {e.GetType().Name}: {e.Message} (shell: {resolved})", 1);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = timeoutSeconds > MaxTimeoutSeconds
                ? new CancellationTokenSource()
                : new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(0, timeoutSeconds)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            try
            {
                // Like Python's communicate(): the exit and the end of both streams.
                await Task.WhenAll(process.WaitForExitAsync(linked.Token), stdout.WaitAsync(linked.Token), stderr.WaitAsync(linked.Token))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await StopAsync(process).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new PowerShellResult("Command execution timed out", 1);
            }

            var output = await stdout.ConfigureAwait(false);
            if (output.Length == 0) output = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 && output.Contains("Access is denied", StringComparison.Ordinal) && !_isElevated())
                output += AccessDeniedHint;
            return new PowerShellResult(output, process.ExitCode);
        }
    }

    internal static string Encode(string command) => Convert.ToBase64String(Encoding.Unicode.GetBytes(Preamble + command));

    /// <summary>upstream _resolve_shell: the shell (or pwsh, then powershell) on the child's PATH, else Windows
    /// PowerShell's System32 path, else an explicit non-PowerShell name unchanged so the launch error names it.</summary>
    internal static string ResolveShell(string? shell, IReadOnlyDictionary<string, string> environment)
    {
        var path = environment.GetValueOrDefault("PATH");
        var pathExt = environment.GetValueOrDefault("PATHEXT");
        string[] names = shell is not null ? [shell] : ["pwsh", "powershell"];
        foreach (var name in names)
        {
            if (Which(name, path, pathExt) is { } found) return found;
        }
        if (shell is not null && !Path.GetFileNameWithoutExtension(shell).Equals("powershell", StringComparison.OrdinalIgnoreCase)) return shell;
        var systemRoot = environment.GetValueOrDefault("SystemRoot") ?? Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        return Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    // shutil.which over the child's PATH with PATHEXT (never the current directory).
    private static string? Which(string name, string? path, string? pathExt)
    {
        var extensions = (string.IsNullOrEmpty(pathExt) ? ShellEnvironment.FallbackPathExt : pathExt).Split(';', StringSplitOptions.RemoveEmptyEntries);
        string[] candidates = extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? [name] : extensions.Select(e => name + e).ToArray();
        if (!string.IsNullOrEmpty(Path.GetDirectoryName(name))) return candidates.FirstOrDefault(File.Exists);
        foreach (var entry in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            // A relative entry such as "." would resolve against the server's working directory.
            var directory = entry.Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (var candidate in candidates)
            {
                var full = Path.Combine(directory, candidate);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    private static Process Start(string shell, string command, Dictionary<string, string> environment)
    {
        var info = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,          // CREATE_NO_WINDOW: no console flashes up and steals focus
            CreateNewProcessGroup = true,   // CREATE_NEW_PROCESS_GROUP: the target of Ctrl+Break
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        info.ArgumentList.Add("-NoProfile");
        // Only Windows PowerShell 5.1 takes -OutputFormat Text here.
        if (Path.GetFileNameWithoutExtension(shell).Equals("powershell", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add("-OutputFormat");
            info.ArgumentList.Add("Text");
        }
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Encode(command));
        info.Environment.Clear();
        foreach (var (name, value) in environment) info.Environment[name] = value;
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {shell}");
        // The child must not read the MCP pipe.
        process.StandardInput.Close();
        return process;
    }

    private static async Task StopAsync(Process process)
    {
        // Ctrl+Break first. The child has its own hidden console (CREATE_NO_WINDOW), so this usually fails, as it does
        // upstream; the result is ignored.
        _ = CoreNative.GenerateConsoleCtrlEvent(CoreNative.CtrlBreakEvent, (uint)process.Id);
        if (await WaitForExitAsync(process, GracePeriod).ConfigureAwait(false)) return;
        try { process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
        await WaitForExitAsync(process, GracePeriod).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
