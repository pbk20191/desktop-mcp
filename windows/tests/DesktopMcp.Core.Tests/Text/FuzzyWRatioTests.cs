using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Tests;

/// <summary>
/// fuzzywuzzy 0.18 <c>fuzz.WRatio</c> and <c>process.extractOne</c> with P3 Decisions 30 (non-ASCII kept, rapidfuzz's
/// optimal partial_ratio). The extra rows were checked against a Python port of fuzzywuzzy 0.18's fuzz.py with those
/// two changes.
/// </summary>
public class FuzzyWRatioTests
{
    [Theory]
    [InlineData("notepad", "Notepad", 100)]
    [InlineData("note", "notepad", 90)]                  // lenRatio 1.75: partial 100 * 0.9
    [InlineData("Microsoft Edge", "Edge Microsoft", 95)] // token sort 100 * 0.95
    [InlineData("메모장", "메모장", 100)]                  // non-ASCII is kept (Decisions 30)
    [InlineData("", "x", 0)]
    [InlineData("!!", "x", 0)]
    [InlineData("mets new york", "new york mets fan", 95)] // lenRatio < 1.5: token set 100 * 0.95
    [InlineData("york new", "new york city hall", 86)]   // partial token sort 100 * 0.95 * 0.9 = 85.5, half to even
    [InlineData("x", "abcdefghijklmnopqrstx", 60)]       // lenRatio > 8: partial scale 0.6
    [InlineData("calculator", "Paint", 40)]
    [InlineData("my_app", "My App", 83)]                 // '_' is a word character
    [InlineData("①②", "①②", 100)]                      // Unicode numbers are word characters, as Python's \w
    [InlineData("메모장", "제목 없음 - 메모장", 90)]
    public void Scores(string a, string b, int score) => Assert.Equal(score, Fuzzy.WRatio(a, b));

    [Fact]
    public void ExtractOneKeepsTheFirstOfEqualScores() =>
        Assert.Equal(("Google Chrome", 90), Fuzzy.ExtractOne("chrome", ["Google Chrome", "Chrome Remote Desktop"], 70));

    [Fact]
    public void ExtractOneReturnsTheBestChoice() =>
        Assert.Equal(("Notepad", 100), Fuzzy.ExtractOne("notepad", ["Paint", "*Untitled - Notepad", "Notepad"], 70));

    [Fact]
    public void ExtractOneHonoursTheCutoff() => Assert.Null(Fuzzy.ExtractOne("calculator", ["Paint"], 70));

    [Fact]
    public void ExtractOneOfNoChoicesIsNull() => Assert.Null(Fuzzy.ExtractOne("x", [], 0));

    // Python's (?ui)\W works on code points: an astral letter or number is kept, any other astral character (an emoji)
    // becomes one space, not two.
    [Theory]
    [InlineData("a\U0001D49Cb", "a\U0001D49Cb")] // MATHEMATICAL SCRIPT CAPITAL A (Lu), no lower case
    [InlineData("x\U0001D7D9", "x\U0001D7D9")]    // MATHEMATICAL DOUBLE-STRUCK DIGIT ONE (Nd)
    [InlineData("a\U0001F600b", "a b")]
    [InlineData("\U0001F600\U0001F600", "")]
    public void ProcessWorksOnRunes(string value, string processed) => Assert.Equal(processed, Fuzzy.Process(value));

    [Fact]
    public void AnEmojiIsOneSeparator() => Assert.Equal("x  y", Fuzzy.Process("x\U0001F600\U0001F600y"));

    // A lone surrogate is one code point of category Cs, so one space.
    [Fact]
    public void ALoneSurrogateIsOneSeparator() => Assert.Equal("a b", Fuzzy.Process("a\uD800b"));
}
