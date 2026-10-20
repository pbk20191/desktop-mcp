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
        return RoundHalfEven(score);
    }

    /// <summary>
    /// fuzzywuzzy 0.18 <c>fuzz.WRatio</c> with P3 Decisions 30: both strings are processed (<see cref="Process"/>, which
    /// keeps non-ASCII characters where fuzzywuzzy's <c>force_ascii</c> strips them), an empty side scores 0, and
    /// <c>partial_ratio</c> is <see cref="PartialRatio"/>. As fuzzy.py, every component ratio is an integer (its
    /// <c>intr</c>) before it is scaled, and the result is rounded half to even.
    /// </summary>
    public static int WRatio(string s1, string s2)
    {
        var p1 = Process(s1);
        var p2 = Process(s2);
        return p1.Length == 0 || p2.Length == 0 ? 0 : ProcessedWRatio(p1, p2);
    }

    /// <summary>
    /// fuzzywuzzy 0.18 <c>process.extractOne(query, choices, scorer=WRatio, score_cutoff=cutoff)</c>: the query and every
    /// choice are processed, each choice scoring at least <paramref name="cutoff"/> is a candidate, and the first of the
    /// highest scores wins (Python's <c>max</c> keeps the first). Null when no choice reaches the cutoff.
    /// </summary>
    public static (string Choice, int Score)? ExtractOne(string query, IReadOnlyList<string> choices, int cutoff)
    {
        var processedQuery = Process(query);
        (string Choice, int Score)? best = null;
        foreach (var choice in choices)
        {
            var processed = Process(choice);
            var score = processedQuery.Length == 0 || processed.Length == 0 ? 0 : ProcessedWRatio(processedQuery, processed);
            if (score >= cutoff && (best is null || score > best.Value.Score)) best = (choice, score);
        }
        return best;
    }

    /// <summary>
    /// fuzzywuzzy utils.full_process without <c>force_ascii</c>: every character that is not a word character (Python's
    /// <c>\W</c>: letters, numbers and <c>_</c>) becomes a space, then lower case, then trimmed.
    /// </summary>
    internal static string Process(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (!(char.IsLetter(chars[i]) || char.IsNumber(chars[i]) || chars[i] == '_')) chars[i] = ' ';
        return new string(chars).ToLowerInvariant().Trim();
    }

    // fuzz.py WRatio on processed, non-empty strings.
    private static int ProcessedWRatio(string p1, string p2)
    {
        const double UnbaseScale = 0.95;
        var partialScale = 0.90;
        double baseRatio = IntRatio(p1, p2);
        var lenRatio = (double)Math.Max(p1.Length, p2.Length) / Math.Min(p1.Length, p2.Length);
        if (lenRatio > 8) partialScale = 0.6;
        if (lenRatio < 1.5)
        {
            var tokenSort = TokenSort(p1, p2, IntRatio) * UnbaseScale;
            var tokenSet = TokenSet(p1, p2, IntRatio) * UnbaseScale;
            return RoundHalfEven(Math.Max(baseRatio, Math.Max(tokenSort, tokenSet)));
        }
        var partial = PartialRatio(p1, p2) * partialScale;
        var partialTokenSort = TokenSort(p1, p2, PartialRatio) * UnbaseScale * partialScale;
        var partialTokenSet = TokenSet(p1, p2, PartialRatio) * UnbaseScale * partialScale;
        return RoundHalfEven(Math.Max(Math.Max(baseRatio, partial), Math.Max(partialTokenSort, partialTokenSet)));
    }

    // fuzz.ratio: equal strings score 100, an empty one 0 (its check_for_equivalence and check_empty_string decorators).
    private static int IntRatio(string a, string b) =>
        a == b ? 100 : a.Length == 0 || b.Length == 0 ? 0 : RoundHalfEven(Ratio(a, b));

    // fuzz._token_sort: the tokens sorted and joined with one space.
    private static int TokenSort(string p1, string p2, Func<string, string, int> scorer) =>
        scorer(string.Join(' ', Tokens(p1).Order(StringComparer.Ordinal)), string.Join(' ', Tokens(p2).Order(StringComparer.Ordinal)));

    // fuzz._token_set: the sorted intersection, and it followed by each side's sorted remainder.
    private static int TokenSet(string p1, string p2, Func<string, string, int> scorer)
    {
        var tokens1 = Tokens(p1).ToHashSet(StringComparer.Ordinal);
        var tokens2 = Tokens(p2).ToHashSet(StringComparer.Ordinal);
        var section = string.Join(' ', tokens1.Intersect(tokens2, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var combined1 = (section + " " + string.Join(' ', tokens1.Except(tokens2, StringComparer.Ordinal).Order(StringComparer.Ordinal))).Trim();
        var combined2 = (section + " " + string.Join(' ', tokens2.Except(tokens1, StringComparer.Ordinal).Order(StringComparer.Ordinal))).Trim();
        section = section.Trim();
        return Math.Max(Math.Max(scorer(section, combined1), scorer(section, combined2)), scorer(combined1, combined2));
    }

    private static string[] Tokens(string processed) => processed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static int RoundHalfEven(double value) => (int)Math.Round(value, MidpointRounding.ToEven);

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
