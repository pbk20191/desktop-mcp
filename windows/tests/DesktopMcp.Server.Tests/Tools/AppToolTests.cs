using DesktopMcp.Desktop.Apps;

namespace DesktopMcp.Server.Tests;

public class AppToolTests
{
    [Theory]
    [InlineData("""{"mode": "switch", "executable": "a.exe"}""", "executable, args, and cwd require mode=\"launch_executable\"")]
    [InlineData("""{"mode": "resize", "args": []}""", "executable, args, and cwd require mode=\"launch_executable\"")]
    [InlineData("""{"cwd": "C:\\"}""", "executable, args, and cwd require mode=\"launch_executable\"")]
    [InlineData("""{"mode": "launch_executable"}""", "executable is required for mode=\"launch_executable\"")]
    [InlineData("""{"mode": "launch_executable", "executable": "a.exe", "name": "x"}""", "name, window_loc, and window_size are not supported for mode=\"launch_executable\"")]
    [InlineData("""{"mode": "launch_executable", "executable": "a.exe", "window_size": [1, 2]}""", "name, window_loc, and window_size are not supported for mode=\"launch_executable\"")]
    [InlineData("""{"mode": "launch_executable", "executable": "a.exe", "args": [1]}""", "args must be a list of strings")]
    [InlineData("""{"mode": "switch"}""", "name is required for mode=\"switch\"")]
    [InlineData("""{}""", "name is required for mode=\"launch\"")]
    [InlineData("""{"mode": "resize", "window_size": [1]}""", "window_size must be a list of exactly 2 integers")]
    [InlineData("""{"mode": "resize", "window_loc": "[1, true]"}""", "window_loc must be a list of exactly 2 integers")]
    [InlineData("""{"mode": "move"}""", "mode must be one of: launch, launch_executable, resize, switch")]
    public async Task ArgumentErrors(string json, string message)
    {
        var desktop = new FakeDesktop();
        Assert.Equal($"Error calling tool 'App': {message}", ToolCalls.Text(await ToolCalls.CallAsync(ToolCalls.Create("App", desktop.Services()), json)));
        Assert.Empty(desktop.AppCalls);
    }

    [Fact]
    public async Task ResultsHaveNoStructuredContentAndSwitchFailuresAreErrors()
    {
        var desktop = new FakeDesktop { AppResult = new AppResult("Application X not found.", true) };
        var result = await ToolCalls.CallAsync(ToolCalls.Create("App", desktop.Services()), """{"mode": "switch", "name": "x"}""");
        Assert.Equal("Error calling tool 'App': Application X not found.", ToolCalls.Text(result));
        desktop.AppResult = new AppResult("Notepad is minimized", false);
        var resize = await ToolCalls.CallAsync(ToolCalls.Create("App", desktop.Services()), """{"mode": "resize"}""");
        Assert.Equal("Notepad is minimized", ToolCalls.OkText(resize));
        Assert.Null(resize.StructuredContent);
    }

    [Fact]
    public async Task EachModeCallsTheService()
    {
        var desktop = new FakeDesktop { AppResult = new AppResult("ok", false) };
        var services = desktop.Services();
        await ToolCalls.CallAsync(ToolCalls.Create("App", services), """{"name": "notepad"}""");
        await ToolCalls.CallAsync(ToolCalls.Create("App", services), """{"mode": "switch", "name": "notepad"}""");
        await ToolCalls.CallAsync(ToolCalls.Create("App", services), """{"mode": "resize", "name": "notepad", "window_loc": [1, 2], "window_size": "[3, 4]"}""");
        await ToolCalls.CallAsync(ToolCalls.Create("App", services), """{"mode": "resize"}""");
        await ToolCalls.CallAsync(ToolCalls.Create("App", services), """{"mode": "launch_executable", "executable": "a.exe", "args": "[\"x y\"]", "cwd": "C:\\"}""");
        await ToolCalls.CallAsync(ToolCalls.Create("App", services), """{"mode": "launch_executable", "executable": "a.exe"}""");
        Assert.Equal(
        [
            "Launch notepad", "Switch notepad", "Resize notepad (1, 2) (3, 4)", "Resize  none none",
            "LaunchExecutable a.exe [x y] C:\\", "LaunchExecutable a.exe [] ",
        ], desktop.AppCalls);
    }
}
