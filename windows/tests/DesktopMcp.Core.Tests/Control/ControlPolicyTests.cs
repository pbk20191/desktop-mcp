using System.Text.Json;
using DesktopMcp.Core.Control;

namespace DesktopMcp.Core.Tests;

public class ControlPolicyTests
{
    [Theory]
    [InlineData("relaxed", "Click", null, true)]
    [InlineData("relaxed", "PowerShell", null, false)]
    [InlineData("balanced", "PowerShell", null, true)]
    [InlineData("balanced", "Snapshot", null, false)]
    [InlineData("strict", "Snapshot", null, true)]
    [InlineData("strict", "ControlStatus", null, false)]
    [InlineData("relaxed", "App", "launch", false)]
    [InlineData("balanced", "App", "launch_executable", true)]
    [InlineData("relaxed", "App", "switch", true)]
    [InlineData("relaxed", "Process", "kill", false)]
    [InlineData("balanced", "Process", "kill", true)]
    [InlineData("balanced", "Clipboard", "get", false)]
    [InlineData("relaxed", "Process", null, true)]
    [InlineData("relaxed", "App", "fly", true)]
    [InlineData("relaxed", "SomeNewTool", null, true)]
    public void UpstreamsPolicyTable(string policy, string tool, string? mode, bool gated) =>
        Assert.Equal(gated, ControlPolicy.RequiresControl(policy, tool, mode));

    // Upstream tools/control_policy.py:8-31. d = direct, p = possible, o = other, n = never gated.
    private static readonly Dictionary<string, char> Categories = new()
    {
        ["Click"] = 'd', ["Type"] = 'd', ["Scroll"] = 'd', ["Move"] = 'd', ["Shortcut"] = 'd', ["MultiSelect"] = 'd', ["MultiEdit"] = 'd',
        ["PowerShell"] = 'p', ["Notification"] = 'p',
        ["FileSystem"] = 'o', ["Registry"] = 'o', ["Snapshot"] = 'o', ["Screenshot"] = 'o', ["Scrape"] = 'o', ["Wait"] = 'o', ["WaitFor"] = 'o', ["DisplayInventory"] = 'o',
        ["ControlStatus"] = 'n',
        ["App:launch"] = 'p', ["App:launch_executable"] = 'p', ["App:resize"] = 'd', ["App:switch"] = 'd',
        ["Process:list"] = 'o', ["Process:kill"] = 'p',
        ["Clipboard:get"] = 'o', ["Clipboard:set"] = 'p',
    };

    private static readonly string[] ModedTools = ["App", "Process", "Clipboard"];

    private static string[] SpecToolNames()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "spec", "windows", "tools.json");
            if (!File.Exists(candidate)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(candidate));
            return doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
        }
        throw new FileNotFoundException("spec/windows/tools.json not found above " + AppContext.BaseDirectory);
    }

    [Theory]
    [InlineData("relaxed")]
    [InlineData("balanced")]
    [InlineData("strict")]
    public void EverySpecToolAndModeIsClassified(string policy)
    {
        var names = SpecToolNames();
        Assert.NotEmpty(names);
        foreach (var tool in names)
        {
            var calls = ModedTools.Contains(tool)
                ? Categories.Keys.Where(k => k.StartsWith(tool + ":")).Select(k => (Key: k, Mode: (string?)k[(tool.Length + 1)..]))
                : [(Key: tool, Mode: (string?)null)];
            foreach (var (key, mode) in calls)
            {
                Assert.True(Categories.TryGetValue(key, out var category), $"{key} is not in the expected table");
                var expected = category switch
                {
                    'n' => false,
                    _ when policy == "strict" => true,
                    'd' => true,
                    'p' => policy == "balanced",
                    _ => false,
                };
                Assert.True(expected == ControlPolicy.RequiresControl(policy, tool, mode), $"{policy} {key}");
            }
        }
        // every mode in the table is exercised for the moded tools
        Assert.Equal(8, Categories.Keys.Count(k => k.Contains(':')));
    }

    [Fact]
    public void ModeDefaultsFollowUpstream()
    {
        static IDictionary<string, JsonElement> Args(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.Equal("launch", ControlPolicy.ModeOf("App", null));
        Assert.Equal("launch", ControlPolicy.ModeOf("App", Args("{}")));
        Assert.Null(ControlPolicy.ModeOf("App", Args("""{"mode": null}""")));
        Assert.Null(ControlPolicy.ModeOf("Process", Args("{}")));
        Assert.Null(ControlPolicy.ModeOf("Process", Args("""{"mode": 3}""")));
        Assert.Equal("kill", ControlPolicy.ModeOf("Process", Args("""{"mode": "kill"}""")));
    }

    [Fact]
    public void AnUnknownPolicyIsAnError() =>
        Assert.Equal("Unknown control policy: loose", Assert.Throws<ArgumentException>(() => ControlPolicy.RequiresControl("loose", "Click", null)).Message);
}
