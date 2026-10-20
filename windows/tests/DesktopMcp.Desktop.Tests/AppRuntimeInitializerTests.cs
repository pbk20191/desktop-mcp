namespace DesktopMcp.Desktop.Tests;

public class AppRuntimeInitializerTests
{
    [Fact]
    public void ConcurrentFirstCallsAddTheDependencyOnce()
    {
        const string fullName = "Microsoft.WindowsAppRuntime.2_2.5.1.0_x64__8wekyb3d8bbwe";
        var calls = 0;
        using var entered = new CountdownEvent(8);
        using var release = new ManualResetEventSlim();
        var initializer = new AppRuntimeInitializer(() => true, () => { }, () =>
        {
            Interlocked.Increment(ref calls);
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return fullName;
        });
        var seen = new string?[8];
        var threads = Enumerable.Range(0, 8).Select(n => new Thread(() =>
        {
            entered.Signal();
            initializer.EnsureInitialized();
            seen[n] = initializer.PackageFullName;
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        release.Set();
        foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, calls);
        Assert.All(seen, name => Assert.Equal(fullName, name));
    }

    [Fact]
    public void AFailedAttemptIsRetriedByTheNextCall()
    {
        var attempts = 0;
        var initializer = new AppRuntimeInitializer(() => true, () => { }, () =>
            ++attempts == 1 ? throw new InvalidOperationException("missing") : "full name");
        Assert.Throws<InvalidOperationException>(initializer.EnsureInitialized);
        initializer.EnsureInitialized();
        Assert.Equal(("full name", 2), (initializer.PackageFullName, attempts));
    }

    [Fact]
    public void AnOldWindowsFailsBeforeAnyCall()
    {
        var called = false;
        var initializer = new AppRuntimeInitializer(() => false, () => called = true, () => { called = true; return ""; });
        var error = Assert.Throws<InvalidOperationException>(initializer.EnsureInitialized);
        Assert.Contains("Windows 11 22H2 (build 22621) or later", error.Message);
        Assert.False(called);
    }

    [Fact]
    public void TheInstallHintNamesTheFailingCallAndTheMinimumVersion()
    {
        var hint = AppRuntime.InstallHint("AddPackageDependency", unchecked((int)0x80670016));
        Assert.Contains(") or later from ", hint);
        Assert.Contains("(Microsoft.WindowsAppRuntime.", hint);
        Assert.Contains("AddPackageDependency failed with HRESULT 0x80670016", hint);
        Assert.EndsWith($"from {AppRuntime.DownloadsPage}.", hint);
    }
}
