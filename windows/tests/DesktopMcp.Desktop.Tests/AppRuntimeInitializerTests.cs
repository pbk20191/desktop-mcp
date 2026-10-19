namespace DesktopMcp.Desktop.Tests;

public class AppRuntimeInitializerTests
{
    [Fact]
    public void ConcurrentFirstCallsAddTheDependencyOnce()
    {
        var calls = 0;
        var initializer = new AppRuntimeInitializer(() => true, () => { }, () =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(50);
            return "Microsoft.WindowsAppRuntime.2_2.5.1.0_x64__8wekyb3d8bbwe";
        });
        using var start = new Barrier(8);
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            start.SignalAndWait();
            initializer.EnsureInitialized();
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, calls);
        Assert.Equal("Microsoft.WindowsAppRuntime.2_2.5.1.0_x64__8wekyb3d8bbwe", initializer.PackageFullName);
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
        Assert.Contains(" or later (Microsoft.WindowsAppRuntime.", hint);
        Assert.Contains("AddPackageDependency failed with HRESULT 0x80670016", hint);
        Assert.EndsWith($"from {AppRuntime.DownloadsPage}.", hint);
    }
}
