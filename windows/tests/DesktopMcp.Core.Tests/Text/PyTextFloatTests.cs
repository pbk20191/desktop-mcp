using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Tests;

public class PyTextFloatTests
{
    // Expected values are Python 3.13's repr().
    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(100.0, "100.0")]
    [InlineData(1.5, "1.5")]
    [InlineData(33.33, "33.33")]
    [InlineData(0.1, "0.1")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(0.000123, "0.000123")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1.2345678901234568e+17, "1.2345678901234568e+17")]
    [InlineData(1.3333333333333333, "1.3333333333333333")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(double.PositiveInfinity, "inf")]
    [InlineData(double.NaN, "nan")]
    public void FloatReprMatchesPython(double value, string expected) => Assert.Equal(expected, PyText.FloatRepr(value));

    [Theory]
    [InlineData(2.675, 2, 2.67)]
    [InlineData(1.0 / 0.675, 6, 1.481481)]
    [InlineData(33.335, 2, 33.34)]
    [InlineData(-0.001, 2, -0.0)]
    public void RoundMatchesPython(double value, int digits, double expected) => Assert.Equal(expected, PyText.Round(value, digits));

    // Assert.Equal(-0.0, 0.0) passes, so the sign of zero is checked through the text Snapshot prints.
    [Fact]
    public void RoundKeepsTheSignOfZero() => Assert.Equal("-0.0", PyText.FloatRepr(PyText.Round(-0.001, 2)));

    [Theory]
    [InlineData(296.2962, 296L)]
    [InlineData(222.5, 222L)]
    [InlineData(223.5, 224L)]
    public void RoundToIntegerIsHalfToEven(double value, long expected) => Assert.Equal(expected, PyText.RoundToInteger(value));

    [Theory]
    [InlineData("list item", "List item")]
    [InlineData("PANE", "Pane")]
    [InlineData("", "")]
    public void CapitalizeMatchesPython(string value, string expected) => Assert.Equal(expected, PyText.Capitalize(value));
}
