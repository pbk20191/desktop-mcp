using DesktopMcp.Core.PowerShell;

namespace DesktopMcp.Core.Tests;

/// <summary>The PowerShellRunner tests that change the process working directory.</summary>
[Collection(CwdSensitiveCollection.Name)]
public class PowerShellRunnerCwdTests
{
    [Fact]
    public void ResolveShellIgnoresRelativePathEntries()
    {
        // shutil.which never searches the current directory: a relative entry such as "." must not find a pwsh.exe there.
        using var temp = new TempDirectory();
        temp.WriteFile("pwsh.exe", "");
        temp.WriteFile(@"sub\pwsh.exe", "");
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @".;sub",
            ["PATHEXT"] = ".EXE",
            ["SystemRoot"] = @"X:\Win",
        };
        var saved = Environment.CurrentDirectory;
        Environment.CurrentDirectory = temp.Path;
        try
        {
            Assert.Equal(@"X:\Win\System32\WindowsPowerShell\v1.0\powershell.exe", PowerShellRunner.ResolveShell(null, variables));
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }
}
