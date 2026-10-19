using System.Text.Json;
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Snapshots;

public class SnapshotSelectionTests
{
    private static readonly ScreenRect Screen = new(-1920, 0, 2560, 1600);

    private static readonly DisplayInfo[] Displays =
    [
        new(0, @"\\.\DISPLAY1", new ScreenRect(0, 0, 2560, 1600), true, null, 144, 1.5, "landscape"),
        new(1, @"\\.\DISPLAY2", new ScreenRect(-1920, 0, 0, 1080), false, null, 96, 1.0, "landscape"),
    ];

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void DisplayArgumentAcceptsNullAndIntegerLists()
    {
        Assert.Null(SnapshotSelection.ReadDisplayArgument(null));
        Assert.Equal([0L, 1L], SnapshotSelection.ReadDisplayArgument(Json("[0, \"1\"]")));
        Assert.Equal("display must be a list of integers",
            Assert.Throws<ArgumentException>(() => SnapshotSelection.ReadDisplayArgument(Json("0"))).Message);
        Assert.Equal("display must be a list of integers",
            Assert.Throws<ArgumentException>(() => SnapshotSelection.ReadDisplayArgument(Json("[true]"))).Message);
    }

    [Fact]
    public void ParseDisplaysDropsDuplicatesAndRejectsNegatives()
    {
        Assert.Equal([1L, 0L], SnapshotSelection.ParseDisplays([1, 0, 1]));
        Assert.Null(SnapshotSelection.ParseDisplays([]));
        Assert.Equal(SnapshotSelection.DisplayIndicesMessage,
            Assert.Throws<ArgumentException>(() => SnapshotSelection.ParseDisplays([0, -1])).Message);
    }

    [Fact]
    public void DisplayUnionCoversTheSelectedDisplays()
    {
        Assert.Equal(new ScreenRect(-1920, 0, 2560, 1600), SnapshotSelection.DisplayUnion([0, 1], Displays));
        Assert.Equal("Invalid display index 4. Available displays: 0,1",
            Assert.Throws<ArgumentException>(() => SnapshotSelection.DisplayUnion([0, 4], Displays)).Message);
        Assert.Equal("No displays detected", Assert.Throws<ArgumentException>(() => SnapshotSelection.DisplayUnion([0], [])).Message);
    }

    [Fact]
    public void RegionFromAListOrAJsonString()
    {
        Assert.Equal(new ScreenRect(10, 20, 110, 220), SnapshotSelection.ParseRegion(new RegionArgument([10, 20, 110, 220], null), Screen));
        Assert.Equal(new ScreenRect(10, 20, 110, 220), SnapshotSelection.ParseRegion(new RegionArgument(null, "[10, 20, 110, 220]"), Screen));
        Assert.Null(SnapshotSelection.ParseRegion(null, Screen));
    }

    [Theory]
    [InlineData("[1, 2, 3]", SnapshotSelection.RegionShapeMessage)]
    [InlineData("{\"left\": 1}", SnapshotSelection.RegionShapeMessage)]
    [InlineData("[1, 2, 3.5, 4]", SnapshotSelection.RegionIntegersMessage)]
    [InlineData("[1, 2, true, 4]", SnapshotSelection.RegionIntegersMessage)]
    [InlineData("[10, 10, 5, 20]", SnapshotSelection.RegionOrderMessage)]
    [InlineData("[5000, 0, 6000, 100]", "region [5000, 0, 6000, 100] does not overlap the virtual screen bounds (-1920,0,2560,1600)")]
    public void RegionErrorsUseUpstreamMessages(string json, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => SnapshotSelection.ParseRegion(new RegionArgument(null, json), Screen)).Message);

    [Fact]
    public void InvalidJsonReportsTheParserMessage() =>
        Assert.NotEmpty(Assert.Throws<ArgumentException>(() => SnapshotSelection.ParseRegion(new RegionArgument(null, "nope"), Screen)).Message);

    [Fact]
    public void ARegionPastTheScreenIsClippedToIt() =>
        Assert.Equal(new ScreenRect(2000, 1000, 2560, 1600),
            SnapshotSelection.ParseRegion(new RegionArgument([2000, 1000, 9_000_000_000, 1700], null), Screen));

    [Fact]
    public void RegionArgumentRejectsOtherTypes() =>
        Assert.Equal("region must be a list of integers or a string",
            Assert.Throws<ArgumentException>(() => SnapshotSelection.ReadRegionArgument(Json("5"))).Message);
}
