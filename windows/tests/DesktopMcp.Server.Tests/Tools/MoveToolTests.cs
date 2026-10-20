namespace DesktopMcp.Server.Tests;

public class MoveToolTests
{
    [Theory]
    [InlineData("""{"loc": [5, 6]}""", "Moved the mouse pointer to (5,6).")]
    [InlineData("""{"loc": [5, 6], "drag": "true"}""", "Dragged from (1,2) to (5,6).")]
    [InlineData("""{"loc": [5, 6], "drag": true, "from_loc": [0, 0], "duration": "0.25"}""", "Dragged from (0,0) to (5,6) over 0.250 seconds.")]
    public async Task MoveTexts(string json, string text) =>
        Assert.Equal(text, ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Move", new FakeDesktop { Cursor = new(1, 2) }.Services()), json)));

    [Theory]
    [InlineData("""{"loc": [5, 6], "duration": 1}""", "from_loc and duration require drag=True")]
    [InlineData("""{"loc": [5, 6], "drag": true, "from_loc": [1]}""", "from_loc must be a list of exactly 2 integers [x, y]")]
    [InlineData("""{"loc": [5, 6], "drag": "yes"}""", "drag must be true or false")]
    [InlineData("""{"loc": [5, 6], "drag": true, "duration": 11}""", "duration must be between 0 and 10 seconds")]
    [InlineData("""{"loc": [5]}""", "loc must be a list of exactly 2 integers [x, y]")]
    [InlineData("""{"drag": true}""", "Either loc or label must be provided.")]
    public async Task MoveErrors(string json, string message) =>
        Assert.Equal($"Error calling tool 'Move': {message}", ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("Move", new FakeDesktop().Services()), json)));

    [Fact]
    public async Task ADragFromTheCursorToALabel()
    {
        var desktop = new FakeDesktop { Cursor = new(1, 2), Labels = { [3] = new(30, 40) } };
        Assert.Equal("Dragged from (1,2) to (30,40).",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Move", desktop.Services()), """{"label": 3, "drag": true}""")));
        Assert.Equal(["Drag (1,2) (30,40) none"], desktop.Input.Calls);
    }

    [Fact]
    public async Task ARejectedDragSendsNothing()
    {
        var desktop = new FakeDesktop { PointError = "outside" };
        var result = await ToolCalls.CallAsync(ToolCalls.Create("Move", desktop.Services()), """{"loc": [5, 6], "drag": true, "from_loc": [0, 0]}""");
        Assert.Equal("Error calling tool 'Move': outside", ToolCalls.Text(result));
        Assert.Empty(desktop.Input.Calls);
    }
}
