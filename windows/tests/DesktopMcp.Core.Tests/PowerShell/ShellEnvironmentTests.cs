using DesktopMcp.Core.PowerShell;

namespace DesktopMcp.Core.Tests;

public class ShellEnvironmentTests
{
    private static Dictionary<string, string> Variables(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void DedupPathKeepsTheFirstSpellingAndDropsEmptyEntries() =>
        Assert.Equal(@"C:\A;C:\B\;D:\c", ShellEnvironment.DedupPath(@"C:\A;C:\B\", @"c:\a\;;C:\b", @"D:\c"));

    [Fact]
    public void BuildFillsMissingVariablesAndAppendsRegistryPaths()
    {
        var inherited = Variables(("PATH", @"C:\inherited"), ("TEMP", @"C:\keep"), ("EMPTY", ""), ("PATHEXT", ".COM"));
        var machine = new RegistryEnvironment(Variables(("TEMP", @"C:\machine"), ("EMPTY", "machine"), ("ONLYMACHINE", "m"), ("SHARED", "machine")),
            @"C:\Windows;C:\inherited", ".COM;.EXE;.BAT");
        var user = new RegistryEnvironment(Variables(("SHARED", "user")), @"C:\Users\me\bin", "");

        var environment = ShellEnvironment.Build(inherited, machine, user, "PC1", "me", @"C:\Users\me");

        Assert.Equal(@"C:\inherited;C:\Windows;C:\Users\me\bin", environment["PATH"]);
        Assert.Equal(@"C:\keep", environment["TEMP"]);
        Assert.Equal("machine", environment["EMPTY"]);
        Assert.Equal("m", environment["ONLYMACHINE"]);
        Assert.Equal("user", environment["SHARED"]);
        Assert.Equal(".COM;.EXE;.BAT", environment["PATHEXT"]);
        Assert.Equal("PC1", environment["COMPUTERNAME"]);
        Assert.Equal("me", environment["USERNAME"]);
        Assert.Equal(@"C:\Users\me", environment["USERPROFILE"]);
        Assert.Equal("C:", environment["HOMEDRIVE"]);
        Assert.Equal(@"\Users\me", environment["HOMEPATH"]);
        Assert.Equal("PC1", environment["USERDOMAIN"]);
    }

    [Fact]
    public void BuildNeverOverwritesInheritedValues()
    {
        var inherited = Variables(("USERPROFILE", @"X:\p"), ("COMPUTERNAME", "A"), ("PATHEXT", ".EXE"));
        var machine = new RegistryEnvironment(Variables(), "", ".BAT");
        var environment = ShellEnvironment.Build(inherited, machine, new RegistryEnvironment(Variables(), "", ""), "PC1", "me", @"C:\Users\me");
        Assert.Equal(@"X:\p", environment["USERPROFILE"]);
        Assert.Equal("A", environment["COMPUTERNAME"]);
        Assert.Equal(".EXE", environment["PATHEXT"]);
        Assert.Equal("A", environment["USERDOMAIN"]);
    }

    [Fact]
    public void BuildWithoutTheRegistryFallsBackForPathExt()
    {
        var environment = ShellEnvironment.Build(Variables(), null, null, "PC", "me", @"C:\Users\me");
        Assert.Equal(ShellEnvironment.FallbackPathExt, environment["PATHEXT"]);
    }

    [Fact]
    public void BuildReadsTheRealEnvironment()
    {
        var environment = ShellEnvironment.Build();
        Assert.False(string.IsNullOrEmpty(environment["PATH"]));
        Assert.Contains(".EXE", environment["PATHEXT"], StringComparison.OrdinalIgnoreCase);
    }
}
