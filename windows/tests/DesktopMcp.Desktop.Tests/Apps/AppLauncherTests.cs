using System.ComponentModel;
using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Apps;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tests.Tree;
using DesktopMcp.Desktop.Tree;

namespace DesktopMcp.Desktop.Tests.Apps;

/// <summary>An empty folder under %TEMP% holding one empty file, deleted on dispose.</summary>
internal sealed class TempFile : IDisposable
{
    public TempFile(string name)
    {
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dmcp-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Path = System.IO.Path.Combine(Directory, name);
        File.WriteAllBytes(Path, []);
    }

    public string Directory { get; }
    public string Path { get; }

    /// <summary>Python's Path.resolve() of <see cref="Path"/> (the temp folder can contain 8.3 names).</summary>
    public string FinalPath => PathResolver.Resolve(Path);

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// An <see cref="AppService"/> for App launch: a fake Start menu (<paramref name="apps"/>), a fake launcher that records
/// its calls and reports <paramref name="pid"/>, a fake window system and a manual clock that the delay advances. A
/// visible window of the process appears once <paramref name="windowOfPidAfter"/> has passed, and one with the given
/// title once its time has passed.
/// </summary>
internal sealed class LaunchRig
{
    public const long PidWindow = 501;
    public const long TitleWindow = 502;

    private readonly DateTimeOffset _start;
    private readonly int? _pid;
    private readonly TimeSpan? _windowOfPidAfter;
    private readonly (string Title, TimeSpan After)? _titleAfter;

    public LaunchRig(IReadOnlyList<AppEntry> apps, int? pid = null, TimeSpan? windowOfPidAfter = null, (string Title, TimeSpan After)? titleAfter = null)
    {
        _start = Time.Now;
        _pid = pid;
        _windowOfPidAfter = windowOfPidAfter;
        _titleAfter = titleAfter;
        Catalog = new FakeCatalog(apps);
        Launcher = new FakeLauncher(pid);
        Service = new AppService(new AppRig.FakeScanner(WindowScan.Empty), new AppRig.FakeForeground(null!, true),
            (_, _, _, _) => throw new NotSupportedException(), Catalog, Launcher, Windows, Delay, Time);
        ShowDueWindows();
    }

    public AppService Service { get; }
    public FakeCatalog Catalog { get; }
    public FakeLauncher Launcher { get; }
    public FakeWindowSystem Windows { get; } = new();
    public ManualTime Time { get; } = new();
    public List<TimeSpan> Delays { get; } = [];

    /// <summary>The fake time spent waiting.</summary>
    public TimeSpan Waited => Time.Now - _start;

    private Task Delay(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Delays.Add(delay);
        Time.Now += delay;
        ShowDueWindows();
        return Task.CompletedTask;
    }

    private void ShowDueWindows()
    {
        if (_windowOfPidAfter is { } after && _pid is { } pid && Waited >= after && !Windows.TopLevel.Contains(PidWindow))
        {
            Windows.TopLevel.Add(PidWindow);
            Windows.Processes[PidWindow] = pid;
        }
        if (_titleAfter is { } title && Waited >= title.After && !Windows.TopLevel.Contains(TitleWindow))
        {
            Windows.TopLevel.Add(TitleWindow);
            Windows.Titles[TitleWindow] = title.Title;
        }
    }

    internal sealed class FakeCatalog(IReadOnlyList<AppEntry> apps) : IAppCatalog
    {
        public Task<IReadOnlyList<AppEntry>> ListAsync(CancellationToken ct) => Task.FromResult(apps);
    }

    internal sealed class FakeLauncher(int? pid) : IAppLauncher
    {
        public List<string> Calls { get; } = [];

        /// <summary>When set, ActivatePackaged and StartFromShell wait for it (a launch that hangs).</summary>
        public ManualResetEventSlim? Gate { get; set; }

        /// <summary>When set, every launch throws it.</summary>
        public Exception? Error { get; set; }

        public int ActivatePackaged(string appUserModelId)
        {
            Record($"ActivatePackaged {appUserModelId}");
            return pid ?? 0;
        }

        public int? StartFromShell(string parsingName)
        {
            Record($"StartFromShell {parsingName}");
            return pid;
        }

        public int StartExecutable(string commandLine, string? cwd)
        {
            Record($"StartExecutable {commandLine} cwd={cwd}");
            return pid ?? 1;
        }

        private void Record(string call)
        {
            lock (Calls) Calls.Add(call);
            Gate?.Wait();
            if (Error is { } error) throw error;
        }
    }
}

public class AppLauncherTests
{
    [Fact]
    public async Task PackagedAppsWaitForAWindowOfTheirProcess()
    {
        var rig = new LaunchRig(apps: [new("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")], pid: 77, windowOfPidAfter: TimeSpan.FromSeconds(1));
        Assert.Equal(new AppResult("Calculator launched.", false), await rig.Service.LaunchAsync("calculator", default));
        Assert.Equal(["ActivatePackaged Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"], rig.Launcher.Calls);
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Waited);
        Assert.All(rig.Delays, delay => Assert.Equal(TimeSpan.FromMilliseconds(500), delay));
    }

    [Fact]
    public async Task Win32EntriesGoThroughTheShellAndWaitForATitle()
    {
        var rig = new LaunchRig(apps: [new("Notepad", @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\notepad.exe")], titleAfter: ("Untitled - Notepad", TimeSpan.FromSeconds(2)));
        Assert.Equal(new AppResult("Notepad launched.", false), await rig.Service.LaunchAsync("notepad", default));
        Assert.Equal([@"StartFromShell {1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\notepad.exe"], rig.Launcher.Calls);
        Assert.Equal(TimeSpan.FromSeconds(2), rig.Waited);
    }

    [Fact]
    public async Task AShellLaunchWithAProcessWaitsForItsWindow()
    {
        var rig = new LaunchRig(apps: [new("Notepad", "x")], pid: 90, windowOfPidAfter: TimeSpan.FromSeconds(3));
        Assert.Equal(new AppResult("Notepad launched.", false), await rig.Service.LaunchAsync("notepad", default));
        Assert.Equal(TimeSpan.FromSeconds(3), rig.Waited);
    }

    [Fact]
    public async Task WithoutAWindowOfTheProcessTheTitleIsTheFallback()
    {
        // 10 s for the process's window, then the title wait (upstream app(): the PID search, then the RegexName one).
        var rig = new LaunchRig(apps: [new("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")], pid: 77,
            titleAfter: ("Calculator", TimeSpan.FromSeconds(12)));
        Assert.Equal(new AppResult("Calculator launched.", false), await rig.Service.LaunchAsync("calculator", default));
        Assert.Equal(TimeSpan.FromSeconds(12), rig.Waited);
    }

    [Fact]
    public async Task NoWindowAfterTenSecondsIsUpstreamsText() =>
        Assert.Equal(new AppResult("Launching Notepad sent, but window not detected yet.", false),
            await new LaunchRig(apps: [new("Notepad", "x")]).Service.LaunchAsync("notepad", default));

    [Fact]
    public async Task TheTitleWaitLastsTenSeconds()
    {
        var rig = new LaunchRig(apps: [new("Notepad", "x")]);
        await rig.Service.LaunchAsync("notepad", default);
        Assert.Equal(TimeSpan.FromSeconds(10), rig.Waited);
    }

    [Fact]
    public async Task HiddenWindowsDoNotCount()
    {
        var rig = new LaunchRig(apps: [new("Notepad", "x")], titleAfter: ("Notepad", TimeSpan.Zero));
        rig.Windows.Hidden.Add(LaunchRig.TitleWindow);
        Assert.Equal(new AppResult("Launching Notepad sent, but window not detected yet.", false), await rig.Service.LaunchAsync("notepad", default));
    }

    [Fact]
    public async Task UnknownAppsAreNotFound() =>
        Assert.Equal(new AppResult("Qxqxqx not found in start menu.", false),
            await new LaunchRig(apps: [new("Notepad", "x")]).Service.LaunchAsync("qxqxqx", default));

    [Fact]
    public async Task TheFirstEntryOfANameWins()
    {
        var rig = new LaunchRig(apps: [new("Notepad", "first"), new("NOTEPAD", "second"), new("", "nameless"), new("Paint", "")]);
        await rig.Service.LaunchAsync("notepad", default);
        Assert.Equal(["StartFromShell first"], rig.Launcher.Calls);
    }

    [Fact]
    public async Task ALaunchFailureIsUpstreamsText()
    {
        var rig = new LaunchRig(apps: [new("Notepad", "x")]);
        rig.Launcher.Error = new DesktopException("boom");
        Assert.Equal(new AppResult("Failed to launch Notepad: boom", false), await rig.Service.LaunchAsync("notepad", default));
    }

    [Fact]
    public async Task AShellExecuteFailureIsUpstreamsText()
    {
        var rig = new LaunchRig(apps: [new("Notepad", "x")]);
        rig.Launcher.Error = new Win32Exception(2, "not found");
        Assert.Equal(new AppResult("Failed to launch Notepad: not found", false), await rig.Service.LaunchAsync("notepad", default));
    }

    [Fact]
    public async Task AnUnexpectedLaunchExceptionPropagates()
    {
        var rig = new LaunchRig(apps: [new("Notepad", "x")]);
        rig.Launcher.Error = new InvalidOperationException("bug");
        Assert.Equal("bug", (await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.LaunchAsync("notepad", default))).Message);
    }

    [Fact]
    public async Task AHangingLaunchIsAbandonedAfterTenSeconds()
    {
        var rig = new LaunchRig(apps: [new("Notepad", "x")]);
        using var gate = new ManualResetEventSlim(false);
        rig.Launcher.Gate = gate;
        var launch = rig.Service.LaunchAsync("notepad", default);
        while (!launch.IsCompleted)
        {
            rig.Time.Now += TimeSpan.FromSeconds(10);
            await Task.Delay(10);
        }
        gate.Set();
        Assert.Equal(new AppResult("Failed to launch Notepad: it did not start within 10 s.", false), await launch);
    }

    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true)]
    [InlineData(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\notepad.exe", false)]
    [InlineData("Microsoft.Office.WINWORD.EXE.15", false)]
    [InlineData(@"a!b\c", false)]
    [InlineData("a!b!c", false)]
    [InlineData("!App", false)]
    public void PackagedParsingNames(string parsingName, bool packaged) => Assert.Equal(packaged, AppLauncher.IsPackaged(parsingName));

    [Fact]
    public async Task LaunchExecutableReportsPythonsJson()
    {
        using var temp = new TempFile("tool.exe");
        var rig = new LaunchRig(apps: [], pid: 4321);
        var result = await rig.Service.LaunchExecutableAsync(temp.Path, ["a b"], null, default);
        Assert.Equal($"{{\n  \"pid\": 4321,\n  \"executable\": {JsonString(temp.FinalPath)},\n  \"args\": [\n    \"a b\"\n  ],\n  \"cwd\": null\n}}", result.Text);
        Assert.False(result.IsError);
        Assert.Equal([$"StartExecutable {CommandLine.Join([temp.FinalPath, "a b"])} cwd="], rig.Launcher.Calls);
    }

    [Fact]
    public async Task LaunchExecutableResolvesTheWorkingDirectory()
    {
        using var temp = new TempFile("tool.exe");
        var rig = new LaunchRig(apps: [], pid: 5);
        var cwd = PathResolver.Resolve(temp.Directory);
        var result = await rig.Service.LaunchExecutableAsync(temp.Path, [], temp.Directory + @"\.", default);
        Assert.Equal($"{{\n  \"pid\": 5,\n  \"executable\": {JsonString(temp.FinalPath)},\n  \"args\": [],\n  \"cwd\": {JsonString(cwd)}\n}}", result.Text);
        Assert.Equal([$"StartExecutable {CommandLine.Join([temp.FinalPath])} cwd={cwd}"], rig.Launcher.Calls);
    }

    [Fact]
    public async Task AMissingExecutableIsAnError() =>
        Assert.StartsWith("Executable does not exist: ", (await new LaunchRig(apps: []).Service.LaunchExecutableAsync(@"C:\nope\none.exe", [], null, default)).Text);

    [Fact]
    public async Task PathErrorsShowTheResolvedPathAndLaunchNothing()
    {
        using var temp = new TempFile("tool.exe");
        var rig = new LaunchRig(apps: []);
        Assert.Equal(new AppResult($"Executable does not exist: {PathResolver.Resolve(temp.Directory)}", true),
            await rig.Service.LaunchExecutableAsync(temp.Directory, [], null, default));
        var missing = Path.Combine(temp.Directory, "nope");
        Assert.Equal(new AppResult($"Working directory does not exist: {PathResolver.Resolve(missing)}", true),
            await rig.Service.LaunchExecutableAsync(temp.Path, [], missing, default));
        Assert.Equal(new AppResult($"Working directory does not exist: {temp.FinalPath}", true),
            await rig.Service.LaunchExecutableAsync(temp.Path, [], temp.Path, default));
        Assert.Empty(rig.Launcher.Calls);
    }

    [Fact]
    public async Task TheExecutablesTildeIsTheUserProfile()
    {
        var name = "dmcp-missing-" + Guid.NewGuid().ToString("N") + ".exe";
        var expected = PathResolver.Resolve(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), name));
        Assert.Equal(new AppResult($"Executable does not exist: {expected}", true),
            await new LaunchRig(apps: []).Service.LaunchExecutableAsync(@"~\" + name, [], null, default));
    }

    [Fact]
    public async Task AFailedCreateProcessIsPythonsOSErrorText()
    {
        using var temp = new TempFile("tool.exe");
        var rig = new LaunchRig(apps: []);
        rig.Launcher.Error = new Win32Exception(193);
        var result = await rig.Service.LaunchExecutableAsync(temp.Path, [], null, default);
        Assert.True(result.IsError);
        Assert.StartsWith("[WinError 193] ", result.Text);
        Assert.False(result.Text.EndsWith('.'), result.Text);
    }

    [Fact]
    public void TheRealLauncherReportsAMissingProgramWithoutStartingAnything()
    {
        // CreateProcess of a path that does not exist: the attribute list is built and the call fails; nothing runs.
        var missing = @"C:\dmcp-missing-" + Guid.NewGuid().ToString("N") + @"\none.exe";
        var error = Assert.Throws<Win32Exception>(() => new AppLauncher().StartExecutable(CommandLine.Join([missing]), null));
        Assert.Contains(error.NativeErrorCode, new[] { 2, 3 });
    }

    [Fact]
    public async Task AHangingStartMenuListIsAbandonedAfterTenSeconds()
    {
        var time = new ManualTime();
        using var gate = new ManualResetEventSlim(false);
        var list = new AppCatalog(() =>
        {
            gate.Wait();
            return [];
        }, time).ListAsync(default);
        while (!list.IsCompleted)
        {
            time.Now += TimeSpan.FromSeconds(10);
            await Task.Delay(10);
        }
        gate.Set();
        var error = await Assert.ThrowsAsync<DesktopException>(() => list);
        Assert.Equal("Listing the Start menu apps failed: it did not finish within 10 s.", error.Message);
    }

    [Fact]
    [Trait("Category", "Desktop")]
    public async Task AppsFolderListsKnownEntries()
    {
        // Reads the Start menu's AppsFolder only; nothing is launched or activated.
        var apps = await new AppCatalog().ListAsync(default);
        Assert.True(apps.Count > 10, $"{apps.Count} entries");
        Assert.Contains(apps, app => AppLauncher.IsPackaged(app.ParsingName));
        // Names relative to the AppsFolder (what upstream's FolderItem.Path gives), not absolute shell paths.
        Assert.DoesNotContain(apps, app => app.ParsingName.StartsWith("::", StringComparison.Ordinal)
            || app.ParsingName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase));
    }

    // Python's json.dumps of one string, taken from PyJson's own output.
    private static string JsonString(string value)
    {
        const string Prefix = "{\n  \"s\": ";
        var dump = PyJson.Dumps([new("s", value)]);
        return dump[Prefix.Length..^2];
    }
}
