using System.Diagnostics;
using System.Globalization;
using System.Text;
using DesktopMcp.Core.PowerShell;

namespace DesktopMcp.Core.Tests;

/// <summary>Runs the real pwsh (or Windows PowerShell) with -NoProfile and short timeouts.</summary>
public class PowerShellRunnerTests
{
    private static readonly PowerShellRunner Runner = new();

    private static Dictionary<string, string> Variables(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task ReturnsStdoutAsUtf8AndTheExitCode()
    {
        var result = await Runner.RunAsync("Write-Output 'héllo ✓'; exit 3", 30, CancellationToken.None);
        Assert.Equal(3, result.StatusCode);
        Assert.Equal("héllo ✓", result.Output.TrimEnd());
    }

    [Fact]
    public async Task FallsBackToStderrWhenStdoutIsEmpty()
    {
        var result = await Runner.RunAsync("[Console]::Error.WriteLine('only stderr'); exit 1", 30, CancellationToken.None);
        Assert.Equal(1, result.StatusCode);
        Assert.Equal("only stderr", result.Output.TrimEnd());
    }

    [Fact]
    public async Task TimeoutStopsTheWholeProcessTree()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"dmcp-ps-{Guid.NewGuid():N}.txt");
        var command = "$p = Start-Process -FilePath ping.exe -ArgumentList '-n','120','127.0.0.1' -WindowStyle Hidden -PassThru; " +
                      $"Set-Content -LiteralPath '{pidFile}' -Value $p.Id; Start-Sleep -Seconds 120";
        var clock = Stopwatch.StartNew();
        var result = await Runner.RunAsync(command, 8, CancellationToken.None);
        clock.Stop();
        try
        {
            Assert.Equal(new PowerShellResult("Command execution timed out", 1), result);
            Assert.InRange(clock.Elapsed.TotalSeconds, 8, 30);
            var childPid = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);
            Assert.True(HasExited(childPid), $"grandchild {childPid} survived the timeout");
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task CancellationStopsTheCommandAndThrows()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner.RunAsync("Start-Sleep -Seconds 60", 60, cancellation.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task AMissingShellIsReportedWithItsName()
    {
        var result = await Runner.RunAsync("Write-Output x", 10, CancellationToken.None, shell: @"C:\no\such\dir\shell.exe");
        Assert.Equal(1, result.StatusCode);
        Assert.StartsWith("Command execution failed: Win32Exception: ", result.Output);
        Assert.EndsWith(@"(shell: C:\no\such\dir\shell.exe)", result.Output);
    }

    [Fact]
    public async Task AccessDeniedGetsTheElevationHintWhenNotElevated()
    {
        var runner = new PowerShellRunner(ShellEnvironment.Build, () => false);
        var result = await runner.RunAsync("Write-Output 'Access is denied'; exit 5", 30, CancellationToken.None);
        Assert.Equal(5, result.StatusCode);
        Assert.EndsWith(PowerShellRunner.AccessDeniedHint, result.Output);
    }

    [Fact]
    public async Task NoHintWhenElevated()
    {
        var runner = new PowerShellRunner(ShellEnvironment.Build, () => true);
        var result = await runner.RunAsync("Write-Output 'Access is denied'; exit 5", 30, CancellationToken.None);
        Assert.DoesNotContain("HINT", result.Output);
    }

    [Fact]
    public void EncodeIsUtf16LeBase64OfThePreambleAndCommand() =>
        Assert.Equal(PowerShellRunner.Preamble + "Get-Date", Encoding.Unicode.GetString(Convert.FromBase64String(PowerShellRunner.Encode("Get-Date"))));

    [Fact]
    public void ResolveShellPrefersPwshOnTheChildPath()
    {
        using var temp = new TempDirectory();
        var pwsh = temp.WriteFile("pwsh.exe", "");
        // Like shutil.which, the result carries PATHEXT's spelling (pwsh.EXE).
        Assert.Equal(pwsh, PowerShellRunner.ResolveShell(null, Variables(("PATH", temp.Path), ("PATHEXT", ".COM;.EXE"))), ignoreCase: true);
    }

    [Fact]
    public void ResolveShellFallsBackToSystem32WindowsPowerShell() =>
        Assert.Equal(@"X:\Win\System32\WindowsPowerShell\v1.0\powershell.exe",
            PowerShellRunner.ResolveShell(null, Variables(("PATH", ""), ("SystemRoot", @"X:\Win"))));

    [Fact]
    public void ResolveShellKeepsAnUnknownExplicitShell() =>
        Assert.Equal("nushell", PowerShellRunner.ResolveShell("nushell", Variables(("PATH", ""))));

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
