using System.Text;
using DesktopMcp.Core.FileSystem;

namespace DesktopMcp.Core.Tests;

public sealed class TextFilesTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ReadTranslatesNewlinesAndReplacesBadBytes()
    {
        var path = _temp.Combine("a.txt");
        File.WriteAllBytes(path, [(byte)'a', 0x0D, 0x0A, (byte)'b', 0x0D, (byte)'c', 0xFF]);
        Assert.Equal("a\nb\nc\uFFFD", TextFiles.Read(path, "utf-8"));
    }

    [Fact]
    public void Utf8KeepsTheBomAndUtf8SigStripsIt()
    {
        var path = _temp.Combine("bom.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'x']);
        Assert.Equal("\uFEFFx", TextFiles.Read(path, "utf-8"));
        Assert.Equal("x", TextFiles.Read(path, "UTF_8_SIG"));
    }

    [Fact]
    public void Utf16HonoursABigEndianBom()
    {
        var path = _temp.Combine("be.txt");
        File.WriteAllBytes(path, [0xFE, 0xFF, 0x00, (byte)'y']);
        Assert.Equal("y", TextFiles.Read(path, "utf-16"));
    }

    [Theory]
    [InlineData("latin-1", "é", new byte[] { 0xE9 })]
    [InlineData("cp1252", "€", new byte[] { 0x80 })]
    [InlineData("cp949", "가", new byte[] { 0xB0, 0xA1 })]
    public void PythonCodecNamesMapToEncodings(string name, string text, byte[] bytes)
    {
        var path = _temp.Combine("enc.txt");
        File.WriteAllBytes(path, bytes);
        Assert.Equal(text, TextFiles.Read(path, name));
    }

    [Fact]
    public void UnknownEncodingUsesPythonsText() =>
        Assert.Equal("unknown encoding: nope", Assert.Throws<ArgumentException>(() => TextEncodings.Get("nope")).Message);

    [Fact]
    public void WriteTranslatesNewlinesAndWritesABomOnlyIntoAnEmptyFile()
    {
        var path = _temp.Combine("w.txt");
        TextFiles.Write(path, "a\nb", append: false, "utf-16");
        TextFiles.Write(path, "\nc", append: true, "utf-16");
        Assert.Equal([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("a\r\nb\r\nc")], File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteRejectsUnencodableText() =>
        Assert.Throws<EncoderFallbackException>(() => TextFiles.Write(_temp.Combine("x.txt"), "é", append: false, "ascii"));

    [Fact]
    public void SplitLinesKeepsLineEnds() => Assert.Equal(["1\n", "2\n", "3"], TextFiles.SplitLines("1\n2\n3"));
}
