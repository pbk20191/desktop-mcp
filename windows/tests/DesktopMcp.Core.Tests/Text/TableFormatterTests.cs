using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Tests;

public class TableFormatterTests
{
    [Fact]
    public void NumbersAlignRightTextAlignsLeftAndLinesAreRightStripped()
    {
        var table = TableFormatter.Simple(
            ["PID", "Name", "CPU%", "Memory"],
            [["4", "System", "0.0%", "0.1 MB"], ["1234", "chrome.exe", "12.5%", "300.5 MB"]]);
        Assert.Equal(
            "  PID  Name        CPU%    Memory\n" +
            "-----  ----------  ------  --------\n" +
            "    4  System      0.0%    0.1 MB\n" +
            " 1234  chrome.exe  12.5%   300.5 MB",
            table);
    }

    [Fact]
    public void FloatColumnsAlignOnTheDecimalPoint()
    {
        var table = TableFormatter.Simple(["x"], [["1.5"], ["10"], ["2.25"]]);
        Assert.Equal("    x\n-----\n 1.5\n10\n 2.25", table);
    }

    [Fact]
    public void IntegerCellsKeepTheirTextAndCellsAreTrimmed()
    {
        var table = TableFormatter.Simple(["n", "s"], [["007", " left "]]);
        Assert.Equal("  n  s\n---  ----\n007  left", table);
    }

    [Fact]
    public void SignedAndUnderscoredIntegersKeepTheirText()
    {
        Assert.Equal("  n\n---\n +5\n1_0", TableFormatter.Simple(["n"], [["+5"], ["1_0"]]));
    }

    [Fact]
    public void OnlyInfAndNanAreSpecialNumbers()
    {
        Assert.Equal("x\n----\n+inf\n1", TableFormatter.Simple(["x"], [["+inf"], ["1"]]));
    }

    [Fact]
    public void HeadersOnly()
    {
        Assert.Equal("Name\n------", TableFormatter.Simple(["Name"], []));
    }
}
