namespace DesktopMcp.Core.Text;

/// <summary>
/// thefuzz 0.22's <c>fuzz.partial_ratio</c> (rapidfuzz 3.14 underneath): the best <c>ratio</c> between the shorter
/// string and every window of the longer one, including the partial windows at both ends, rounded half to even.
/// <c>ratio</c> is the normalised Indel similarity, 200 * LCS / (len1 + len2).
/// </summary>
public static class Fuzzy
{
    public static int PartialRatio(string s1, string s2)
    {
        if (s1.Length == 0 || s2.Length == 0) return s1.Length == s2.Length ? 100 : 0;
        var score = s1.Length <= s2.Length ? Best(s1, s2) : Best(s2, s1);
        if (s1.Length == s2.Length) score = Math.Max(score, Best(s2, s1));
        return (int)Math.Round(score, MidpointRounding.ToEven);
    }

    private static double Best(string needle, string haystack)
    {
        int n = needle.Length, m = haystack.Length;
        var best = 0.0;
        for (var i = 1; i < n; i++) best = Math.Max(best, Ratio(needle, haystack.AsSpan(0, i)));
        for (var i = 0; i <= m - n; i++) best = Math.Max(best, Ratio(needle, haystack.AsSpan(i, n)));
        for (var i = m - n + 1; i < m; i++) best = Math.Max(best, Ratio(needle, haystack.AsSpan(i)));
        return best;
    }

    private static double Ratio(ReadOnlySpan<char> a, ReadOnlySpan<char> b) =>
        200.0 * LongestCommonSubsequence(a, b) / (a.Length + b.Length);

    private static int LongestCommonSubsequence(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
                current[j] = a[i - 1] == b[j - 1] ? previous[j - 1] + 1 : Math.Max(previous[j], current[j - 1]);
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
