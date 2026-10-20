using System.Text.Json;
using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Input;

public class ActArgumentsTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private const string Shape = "Location must be a list of exactly 2 integers [x, y]";

    [Theory]
    [InlineData("[10, 20]")]
    [InlineData("\"[10, 20]\"")]
    [InlineData("[\"10\", 20.0]")]
    public void PointsAcceptListsJsonStringsAndIntegralValues(string json) =>
        Assert.Equal(new ScreenPoint(10, 20), ActArguments.Point(J(json), "loc", Shape));

    [Fact]
    public void AbsentPointsAreNull() => Assert.Null(ActArguments.Point(null, "loc", Shape));

    [Theory]
    [InlineData("[1]")]
    [InlineData("\"5\"")]          // JsonList used to return the number 5 (the follow-up)
    [InlineData("\"[1\"")]         // JSON parse failure of a string
    [InlineData("[1, 2, 3]")]
    [InlineData("[true, 2]")]
    [InlineData("[1.5, 2]")]
    public void BadPointsGiveTheCallersShapeMessage(string json) =>
        Assert.Equal(Shape, Assert.Throws<ArgumentException>(() => ActArguments.Point(J(json), "loc", Shape)).Message);

    [Fact]
    public void PointListsAndLabelListsParseOrNameTheirShape()
    {
        Assert.Equal([new ScreenPoint(1, 2), new ScreenPoint(3, 4)], ActArguments.PointList(J("[[1,2],\"[3,4]\"]"), "loc"));
        Assert.Equal("Each loc must be a list of exactly 2 integers [x, y]", Assert.Throws<ArgumentException>(() => ActArguments.PointList(J("[[1]]"), "loc")).Message);
        Assert.Equal([1L, 2L], ActArguments.LabelList(J("[1,\"2\"]"), "labels"));
        Assert.Equal("labels must be a list of integers", Assert.Throws<ArgumentException>(() => ActArguments.LabelList(J("[1,\"x\"]"), "labels")).Message);
        Assert.Equal("labels must be a list", Assert.Throws<ArgumentException>(() => ActArguments.LabelList(J("7"), "labels")).Message);
    }

    [Fact]
    public void EditItemsParse()
    {
        Assert.Equal([(new ScreenPoint(1, 2), "a")], ActArguments.EditLocs(J("[[1,2,\"a\"]]")));
        Assert.Equal([(7L, "b")], ActArguments.EditLabels(J("[[7,\"b\"]]")));
    }

    [Fact]
    public void EditItemsMustBeTriples() =>
        Assert.Equal("Each loc item must be [x, y, text]. Invalid: [1,2]",
            Assert.Throws<ArgumentException>(() => ActArguments.EditLocs(J("[[1,2]]"))).Message);

    [Fact]
    public void EditLabelsUseUpstreamsTexts()
    {
        Assert.Equal("Each label item must be [label, text]. Invalid: [1]", Assert.Throws<ArgumentException>(() => ActArguments.EditLabels(J("[[1]]"))).Message);
        Assert.Equal("Invalid label id in item: [\"x\",\"a\"]", Assert.Throws<ArgumentException>(() => ActArguments.EditLabels(J("[[\"x\",\"a\"]]"))).Message);
    }

    [Theory]
    [InlineData("\"abc\"", "duration must be a finite number of seconds")]
    [InlineData("true", "duration must be a finite number of seconds")]
    [InlineData("\"NaN\"", "duration must be a finite number of seconds")]
    [InlineData("11", "duration must be between 0 and 10 seconds")]
    [InlineData("-0.5", "duration must be between 0 and 10 seconds")]
    public void DurationFollowsUpstream(string json, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => ActArguments.Duration(J(json))).Message);

    [Fact]
    public void DurationAcceptsNumbersAndNumericStrings()
    {
        Assert.Null(ActArguments.Duration(null));
        Assert.Equal(0.5, ActArguments.Duration(J("0.5")));
        Assert.Equal(2.0, ActArguments.Duration(J("\"2\"")));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"x\"")]
    [InlineData("null")]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    public void FiniteNumberRejectsBooleansNonFiniteAndText(string json) =>
        Assert.Equal("timeout must be a finite number", Assert.Throws<ArgumentException>(() => ActArguments.FiniteNumber(J(json), "timeout")).Message);

    [Fact]
    public void PairsAreTwoIntegers()
    {
        Assert.Null(ActArguments.Pair(null, "window_size"));
        Assert.Equal((800, 600), ActArguments.Pair(J("[800,\"600\"]"), "window_size"));
        Assert.Equal("window_size must be a list of exactly 2 integers", Assert.Throws<ArgumentException>(() => ActArguments.Pair(J("[1]"), "window_size")).Message);
    }

    [Fact]
    public void ChoicesNameTheirValues()
    {
        Assert.Equal("button must be one of: left, right, middle",
            Assert.Throws<ArgumentException>(() => ActArguments.Choice(J("\"top\""), "button", "left", "left", "right", "middle")).Message);
        Assert.Equal("left", ActArguments.Choice(null, "button", "left", "left", "right", "middle"));
        Assert.Equal("right", ActArguments.Choice(J("\"right\""), "button", "left", "left", "right", "middle"));
        foreach (var bad in new[] { "\"LEFT\"", "\" left\"" })
            Assert.Equal("button must be one of: left, right, middle",
                Assert.Throws<ArgumentException>(() => ActArguments.Choice(J(bad), "button", "left", "left", "right", "middle")).Message);
    }
}
