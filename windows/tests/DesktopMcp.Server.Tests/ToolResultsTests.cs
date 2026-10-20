using System.Text.Json;
using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tests;

public class ToolResultsTests
{
    [Fact]
    public void TextCarriesContentAndStructuredResult()
    {
        var result = ToolResults.Text("hello");
        Assert.Equal("hello", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("hello", structured.GetProperty("result").GetString());
        Assert.Single(structured.EnumerateObject());
    }

    [Fact]
    public void ARejectionIsAnErrorWhoseWholeTextIsTheRejectionJson()
    {
        var rejection = new ControlRejectedException(ControlCodes.ControlPreempted, new ControlStatus(ControlState.User, 4, 2, true, "relaxed", 10), 3);
        var result = ToolResults.Rejection(rejection);
        Assert.True(result.IsError);
        Assert.Equal(ControlJson.Rejection(rejection), Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Null(result.StructuredContent);
    }

    [Fact]
    public void AnObjectIsItsTextAndItsParsedObject()
    {
        var result = ToolResults.Object("""{"a":1,"b":"x"}""");
        Assert.Equal("""{"a":1,"b":"x"}""", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(1, structured.GetProperty("a").GetInt32());
        Assert.Equal("x", structured.GetProperty("b").GetString());
        Assert.Null(result.IsError);
    }
}

public class SpecToolTests
{
    public static TheoryData<Exception> Interruptions() =>
    [
        new ControlRejectedException(ControlCodes.ControlPreempted, new ControlStatus(ControlState.User, 1, 0, true, "relaxed", 10), 2),
        new InputUnavailableException("blocked"),
    ];

    [Theory]
    [MemberData(nameof(Interruptions))]
    public async Task InterruptionsAreRethrown(Exception interruption)
    {
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => ToolCalls.CallAsync(new ThrowingTool(interruption), "{}"));
        Assert.Same(interruption, thrown);
    }

    [Fact]
    public async Task OtherExceptionsBecomeErrorResults()
    {
        var result = await ToolCalls.CallAsync(new ThrowingTool(new InvalidOperationException("broken")), "{}");
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Throwing': broken", ToolCalls.Text(result));
    }

    private sealed class ThrowingTool(Exception error) : SpecTool(new Tool { Name = "Throwing", InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone() })
    {
        protected override ValueTask<CallToolResult> InvokeCoreAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken) =>
            ValueTask.FromException<CallToolResult>(error);
    }
}
