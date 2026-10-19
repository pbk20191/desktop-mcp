using System.Text;
using DesktopMcp.Core.FileSystem;
using Windows.Win32;

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
    [InlineData("ks_c_5601-1987", "가", new byte[] { 0xB0, 0xA1 })]
    [InlineData("iso8859-2", "ł", new byte[] { 0xB3 })]
    [InlineData("iso8859_15", "€", new byte[] { 0xA4 })]
    [InlineData("ISO-8859-15", "€", new byte[] { 0xA4 })]
    [InlineData("latin", "é", new byte[] { 0xE9 })]
    [InlineData("utf-7", "Hi ☺!", new byte[] { 0x48, 0x69, 0x20, 0x2B, 0x4A, 0x6A, 0x6F, 0x2D, 0x21 })]
    public void PythonCodecNamesMapToEncodings(string name, string text, byte[] bytes)
    {
        var path = _temp.Combine("enc.txt");
        File.WriteAllBytes(path, bytes);
        Assert.Equal(text, TextFiles.Read(path, name));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("unicode")]
    public void UnknownEncodingUsesPythonsText(string name) =>
        Assert.Equal($"unknown encoding: {name}", Assert.Throws<ArgumentException>(() => TextEncodings.Get(name)).Message);

    [Theory]
    [InlineData("mbcs")]
    [InlineData("ansi")]
    [InlineData("dbcs")]
    public void MbcsIsTheAnsiCodePage(string name) => Assert.Equal((int)PInvoke.GetACP(), TextEncodings.Get(name).Encoding.CodePage);

    [Fact]
    public void OemIsTheOemCodePage() => Assert.Equal((int)PInvoke.GetOEMCP(), TextEncodings.Get("oem").Encoding.CodePage);

    [Fact]
    public void Utf7EncodesLikePython()
    {
        var path = _temp.Combine("u7.txt");
        TextFiles.Write(path, "Hi ☺!", append: false, "utf-7");
        Assert.Equal("Hi +Jjo!"u8.ToArray(), File.ReadAllBytes(path));
    }

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
