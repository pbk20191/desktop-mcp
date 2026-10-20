namespace DesktopMcp.Server.Tests;

public class ScrollToolTests
{
    [Fact]
    public async Task WithoutALocationTheCursorIsReported()
    {
        var desktop = new FakeDesktop { Cursor = new(1, 2) };
        Assert.Equal("Scrolled vertical down by 1 wheel times at current cursor position (1,2).",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Scroll", desktop.Services()), "{}")));
        Assert.Equal(["Scroll cursor vertical down 1"], desktop.Input.Calls);
    }

    [Fact]
    public async Task WithALocation() =>
        Assert.Equal("Scrolled horizontal left by 3 wheel times at (5,6).",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Scroll", new FakeDesktop().Services()), """{"loc": [5, 6], "type": "horizontal", "direction": "left", "wheel_times": 3}""")));

    [Fact]
    public async Task WithALabel()
    {
        var desktop = new FakeDesktop { Labels = { [2] = new(7, 8) } };
        Assert.Equal("Scrolled vertical up by 2 wheel times at (7,8).",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Scroll", desktop.Services()), """{"label": 2, "direction": "up", "wheel_times": "2"}""")));
        Assert.Equal(["Scroll (7,8) vertical up 2"], desktop.Input.Calls);
    }

    [Fact]
    public async Task AnInvalidCombinationIsANormalResult() =>
        Assert.Equal("Invalid direction. Use \"up\" or \"down\".",
            ToolCalls.OkText(await ToolCalls.CallAsync(ToolCalls.Create("Scroll", new FakeDesktop { ScrollResult = "Invalid direction. Use \"up\" or \"down\"." }.Services()), """{"direction": "left"}""")));

    [Theory]
    [InlineData("""{"loc": [1]}""", "Location must be a list of exactly 2 integers [x, y]")]
    [InlineData("""{"type": "diagonal"}""", "type must be one of: horizontal, vertical")]
    [InlineData("""{"direction": "in"}""", "direction must be one of: up, down, left, right")]
    public async Task ScrollErrors(string json, string message) =>
        Assert.Equal($"Error calling tool 'Scroll': {message}", ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("Scroll", new FakeDesktop().Services()), json)));
}
