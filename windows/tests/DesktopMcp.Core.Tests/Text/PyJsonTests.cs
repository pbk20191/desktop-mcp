using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Tests;

/// <summary>Python's <c>json.dumps(value, indent=2)</c> (ensure_ascii on) for launch_executable's result (P3 Decisions 34).</summary>
public class PyJsonTests
{
    [Fact]
    public void MatchesPythonsIndentedDump() =>
        Assert.Equal("{\n  \"pid\": 1234,\n  \"executable\": \"C:\\\\Tools\\\\caf\\u00e9.exe\",\n  \"args\": [\n    \"a b\"\n  ],\n  \"cwd\": null\n}",
            PyJson.Dumps([new("pid", 1234L), new("executable", @"C:\Tools\café.exe"), new("args", (IReadOnlyList<string>)["a b"]), new("cwd", null)]));

    [Fact]
    public void AnEmptyListIsBrackets() =>
        Assert.Equal("{\n  \"args\": []\n}", PyJson.Dumps([new("args", (IReadOnlyList<string>)[])]));

    [Fact]
    public void AnEmptyObjectIsBraces() => Assert.Equal("{}", PyJson.Dumps([]));

    // json.dumps('"\\\b\f\n\r\t\x01\x7f\U0001F600/') with ensure_ascii: short escapes, \u00XX for the other controls and
    // DEL, a surrogate pair for an astral character; '/' stays as is.
    [Fact]
    public void StringsAreEscapedAsPythonsEnsureAscii() =>
        Assert.Equal(@"{
  ""s"": ""\""\\\b\f\n\r\t\u0001\u007f\ud83d\ude00/""
}".ReplaceLineEndings("\n"),
            PyJson.Dumps([new("s", "\"\\\b\f\n\r\t\u0001\u007f\U0001F600/")]));

    [Fact]
    public void IntegersAndNegativeIntegers() =>
        Assert.Equal("{\n  \"a\": -5,\n  \"b\": 7\n}", PyJson.Dumps([new("a", -5L), new("b", 7)]));

    [Fact]
    public void OtherValueTypesAreRejected() =>
        Assert.Throws<ArgumentException>(() => PyJson.Dumps([new("a", 1.5)]));
}
