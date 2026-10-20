using System.Text.Json;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Tests;

public class CoercionTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\" TRUE \"", true)]
    [InlineData("\"False\"", false)]
    public void StrictBoolAcceptsBooleansAndTheTwoLiterals(string json, bool expected) =>
        Assert.Equal(expected, Coercion.StrictBool(Json(json), "recursive"));

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void StrictBoolRejectsEverythingElse(string json)
    {
        var error = Assert.Throws<ArgumentException>(() => Coercion.StrictBool(Json(json), "recursive"));
        Assert.Equal("recursive must be true or false", error.Message);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("\"TRUE\"", true)]
    [InlineData("\"true\"", true)]
    [InlineData("\" true\"", false)]
    [InlineData("\"yes\"", false)]
    [InlineData("1", false)]
    [InlineData("false", false)]
    public void LooseBoolIsTrueOnlyForTrueOrTheStringTrue(string json, bool expected) =>
        Assert.Equal(expected, Coercion.LooseBool(Json(json)));

    [Theory]
    [InlineData("5", 5L)]
    [InlineData("-3", -3L)]
    [InlineData("5.0", 5L)]
    [InlineData("\" 42 \"", 42L)]
    [InlineData("\"+7\"", 7L)]
    public void IntegerAcceptsIntegralNumbersAndIntegerStrings(string json, long expected) =>
        Assert.Equal(expected, Coercion.Integer(Json(json), "duration"));

    [Theory]
    [InlineData("1.5")]
    [InlineData("\"1.5\"")]
    [InlineData("true")]
    [InlineData("\"abc\"")]
    [InlineData("[]")]
    public void IntegerRejectsTheRest(string json)
    {
        var error = Assert.Throws<ArgumentException>(() => Coercion.Integer(Json(json), "duration"));
        Assert.Equal("duration must be an integer", error.Message);
    }

    [Fact]
    public void StringRequiresAJsonString()
    {
        Assert.Equal("x", Coercion.String(Json("\"x\""), "command"));
        Assert.Equal("command must be a string", Assert.Throws<ArgumentException>(() => Coercion.String(Json("5"), "command")).Message);
    }

    [Fact]
    public void JsonListAcceptsAnArrayOrAJsonString()
    {
        Assert.Equal(JsonValueKind.Array, Coercion.JsonList(Json("[1,2]"), "loc").ValueKind);
        var parsed = Coercion.JsonList(Json("\"[100, 200]\""), "loc");
        Assert.Equal(2, parsed.GetArrayLength());
        Assert.Equal(200, parsed[1].GetInt32());
    }

    [Fact]
    public void JsonListReportsInvalidJson()
    {
        var error = Assert.Throws<ArgumentException>(() => Coercion.JsonList(Json("\"[1,\""), "loc"));
        Assert.StartsWith("loc is not valid JSON: ", error.Message);
    }

    [Fact]
    public void StringListAcceptsStringsOnly()
    {
        Assert.Equal(["a", "b"], Coercion.StringList(Json("\"[\\\"a\\\", \\\"b\\\"]\""), "args"));
        Assert.Equal("args must be a list of strings", Assert.Throws<ArgumentException>(() => Coercion.StringList(Json("[1]"), "args")).Message);
    }
}
