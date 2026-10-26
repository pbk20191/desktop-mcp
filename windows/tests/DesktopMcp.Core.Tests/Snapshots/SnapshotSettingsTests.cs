using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class SnapshotSettingsTests
{
    [Theory]
    [InlineData(null, 500)]
    [InlineData("  ", 500)]
    [InlineData("abc", 500)]
    [InlineData("0", 500)]
    [InlineData("-5", 500)]
    [InlineData(" 1_000 ", 1000)]
    [InlineData("+25", 25)]
    [InlineData("99999999999999999999999", int.MaxValue)]
    public void MaxTreeElementsFollowsUpstream(string? raw, int expected) => Assert.Equal(expected, SnapshotSettings.MaxTreeElements(raw));

    [Fact]
    public void ExcludedProcessesAreTrimmedAndCaseFolded() =>
        Assert.Equal(new HashSet<string> { "chrome.exe", "code.exe" }, SnapshotSettings.ExcludedProcesses(" Chrome.EXE, ,code.exe "));

    [Theory]
    [InlineData(null, 1.0)]
    [InlineData("0.5", 0.5)]
    [InlineData("5", 1.0)]
    [InlineData("0.01", 0.1)]
    [InlineData("bad", 1.0)]
    [InlineData("nan", 1.0)]
    [InlineData("-inf", 0.1)]
    public void ScreenshotScaleIsClamped(string? raw, double expected) => Assert.Equal(expected, SnapshotSettings.ScreenshotScale(raw));

    [Theory]
    [InlineData(null, 2560, 1600, 0.675)]
    [InlineData(null, 1920, 1080, 1.0)]
    [InlineData("0.5", 1280, 720, 0.5)]
    [InlineData(null, 3840, 1080, 0.5)]
    public void ImageScaleFitsInto1920By1080(string? raw, int width, int height, double expected) =>
        Assert.Equal(expected, SnapshotSettings.ImageScale(raw, width, height));

    [Theory]
    [InlineData("1", true)]
    [InlineData(" Yes ", true)]
    [InlineData("0", false)]
    [InlineData(null, false)]
    public void ProfileFlag(string? raw, bool expected) => Assert.Equal(expected, SnapshotSettings.ProfileEnabled(raw));
}
