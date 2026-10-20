using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Tests;

public class PyTextTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(999L, "999")]
    [InlineData(1234567L, "1,234,567")]
    [InlineData(-1234L, "-1,234")]
    public void ThousandsMatchesPythonCommaFormat(long value, string expected) => Assert.Equal(expected, PyText.Thousands(value));

    [Theory]
    [InlineData(0.25, 1, "0.2")]
    [InlineData(0.35, 1, "0.3")]
    [InlineData(0.45, 1, "0.5")]
    [InlineData(2.675, 2, "2.67")]
    [InlineData(1.05, 1, "1.1")]
    [InlineData(300.45, 1, "300.4")]
    [InlineData(0.15, 1, "0.1")]
    [InlineData(12.25, 1, "12.2")]
    [InlineData(12.25, 2, "12.25")]
    [InlineData(2.5, 0, "2")]
    [InlineData(3.5, 0, "4")]
    [InlineData(-0.25, 1, "-0.2")]
    [InlineData(1024.0, 1, "1024.0")]
    public void FixedRoundsTheExactBinaryValueHalfToEven(double value, int decimals, string expected) =>
        Assert.Equal(expected, PyText.Fixed(value, decimals));

    [Theory]
    [InlineData(1.5, "1.5")]
    [InlineData(10.0, "10")]
    [InlineData(2.25, "2.25")]
    [InlineData(1e6, "1e+06")]
    [InlineData(123456.0, "123456")]
    [InlineData(1234567.0, "1.23457e+06")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(-0.0, "-0")]
    [InlineData(3.14159265, "3.14159")]
    public void FormatGMatchesPythonGeneralFormat(double value, string expected) => Assert.Equal(expected, PyText.FormatG(value));

    [Theory]
    [InlineData("it's", "\"it's\"")]
    [InlineData("a\"b", "'a\"b'")]
    [InlineData(@"x\y", @"'x\\y'")]
    [InlineData("tab\t", @"'tab\t'")]
    [InlineData("plain", "'plain'")]
    public void ReprMatchesPython(string value, string expected) => Assert.Equal(expected, PyText.Repr(value));

    [Fact]
    public void ShellJoinMatchesShlex() =>
        Assert.Equal("desktop-mcp serve --port 9000 'a b' 'it'\"'\"'s' ''",
            PyText.ShellJoin(["desktop-mcp", "serve", "--port", "9000", "a b", "it's", ""]));

    [Fact]
    public void PrefixCountsCodePoints()
    {
        var text = "a\U0001F437b";   // the pig is a surrogate pair
        Assert.Equal(3, PyText.CodePointLength(text));
        Assert.Equal("a\U0001F437", PyText.Prefix(text, 2));
        Assert.Equal("a", PyText.Prefix(text, 1));
        Assert.Equal(text, PyText.Prefix(text, 10));
    }
}
