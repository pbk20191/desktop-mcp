using System.Globalization;
using System.Text.RegularExpressions;

namespace DesktopMcp.Core.Text;

/// <summary>
/// tabulate 0.10's "simple" format with headers (spec §6: a deterministic implementation). A column whose non-empty
/// cells all parse as integers is an int column (cells kept as written, right-aligned); one whose cells all
/// parse as numbers is a float column (cells printed with Python's "g" format, aligned on the decimal point);
/// anything else is text (left-aligned). Each column is at least two characters wider than its header, columns are
/// separated by two spaces, widths count code points, and every line is right-stripped.
/// </summary>
public static partial class TableFormatter
{
    private const int MinPadding = 2;

    public static string Simple(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var columns = new List<(string[] Cells, bool Numeric, int Width)>();
        for (var c = 0; c < headers.Count; c++)
        {
            var cells = rows.Select(r => c < r.Count ? r[c].Trim() : "").ToArray();
            var kind = ColumnKind(cells);
            if (kind != Kind.Text) cells = AlignDecimal(cells.Select(s => Format(s, kind)).ToArray());
            var width = Math.Max(PyText.CodePointLength(headers[c]) + MinPadding, cells.Length == 0 ? 0 : cells.Max(PyText.CodePointLength));
            columns.Add((cells, kind != Kind.Text, width));
        }

        var lines = new List<string>
        {
            Join(headers.Select((h, c) => Pad(h, columns[c].Width, columns[c].Numeric))),
            Join(columns.Select(col => new string('-', col.Width))),
        };
        for (var r = 0; r < rows.Count; r++)
            lines.Add(Join(columns.Select(col => Pad(col.Cells[r], col.Width, col.Numeric))));
        return string.Join("\n", lines);
    }

    private enum Kind { Text, Int, Float }

    private static Kind ColumnKind(string[] cells)
    {
        var present = cells.Where(s => s.Length > 0).ToArray();
        if (present.Length == 0) return Kind.Text;
        if (present.All(IsInt)) return Kind.Int;
        return present.All(IsNumber) ? Kind.Float : Kind.Text;
    }

    private static bool IsInt(string s) => IntPattern().IsMatch(s);

    private static bool IsNumber(string s) =>
        IsInt(s) || s.ToLowerInvariant() is "inf" or "-inf" or "nan"
        || (FloatPattern().IsMatch(s) && double.TryParse(s.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out _));

    private static string Format(string cell, Kind kind)
    {
        if (cell.Length == 0) return cell;
        if (kind == Kind.Int) return cell;   // tabulate's int format is a no-op on strings: "007" stays "007"
        var value = cell.ToLowerInvariant() switch
        {
            "inf" => double.PositiveInfinity,
            "-inf" => double.NegativeInfinity,
            "nan" => double.NaN,
            _ => double.Parse(cell.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture),
        };
        return PyText.FormatG(value);
    }

    // tabulate's _afterpoint: digits after the decimal point (or after the exponent marker), -1 for integers.
    private static string[] AlignDecimal(string[] cells)
    {
        int AfterPoint(string s)
        {
            if (s.Length == 0 || IsInt(s)) return -1;
            var point = s.LastIndexOf('.');
            if (point < 0) point = s.ToLowerInvariant().LastIndexOf('e');
            return point < 0 ? -1 : s.Length - point - 1;
        }
        var after = cells.Select(AfterPoint).ToArray();
        var max = after.Length == 0 ? -1 : after.Max();
        return cells.Select((s, i) => s.Length == 0 ? s : s + new string(' ', max - after[i])).ToArray();
    }

    private static string Pad(string text, int width, bool right)
    {
        var fill = new string(' ', Math.Max(0, width - PyText.CodePointLength(text)));
        return right ? fill + text : text + fill;
    }

    private static string Join(IEnumerable<string> cells) => string.Join("  ", cells).TrimEnd();

    [GeneratedRegex(@"^[+-]?[0-9]+(_[0-9]+)*$")]
    private static partial Regex IntPattern();

    [GeneratedRegex(@"^[+-]?([0-9]+(_[0-9]+)*)?(\.[0-9]*)?([eE][+-]?[0-9]+)?$")]
    private static partial Regex FloatPattern();
}
