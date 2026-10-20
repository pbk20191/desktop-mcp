using DesktopMcp.Core.Input;

namespace DesktopMcp.Core.Tests.Input;

public class ShortcutParserTests
{
    private static KeyToken D(ushort vk) => new(vk, false);
    private static KeyToken U(ushort vk) => new(vk, true);
    private static KeyToken C(string ch) => new(0, false, ch);

    [Fact]
    public void CtrlCHoldsCtrlAroundTheLetter() =>
        Assert.Equal([D(0x11), D(0x43), U(0x43), U(0x11)], ShortcutParser.Parse("ctrl+c"));

    [Fact]
    public void HoldKeysNest() =>
        Assert.Equal([D(0x11), D(0x10), D(0x1B), U(0x1B), U(0x10), U(0x11)], ShortcutParser.Parse("ctrl+shift+esc"));

    [Fact]
    public void ANonHoldKeyEndsTheHold() =>
        Assert.Equal([D(0x11), D(0x41), U(0x41), U(0x11), C("b")], ShortcutParser.Parse("ctrl+a+b"));

    [Fact]
    public void AliasesAndCaseResolve()
    {
        Assert.Equal([D(0x5B), D(0x52), U(0x52), U(0x5B)], ShortcutParser.Parse("Windows+R"));
        Assert.Equal([D(0x08), U(0x08)], ShortcutParser.Parse(" backspace "));
        Assert.Equal([D(0x12), D(0x73), U(0x73), U(0x12)], ShortcutParser.Parse("alt+F4"));
    }

    // uia/core.py:1687-1689: a character without a virtual key is appended after the release, not inside the hold.
    [Fact]
    public void ACharacterWithoutAVirtualKeyIsTypedAfterTheHold() =>
        Assert.Equal([D(0x11), U(0x11), C("!")], ShortcutParser.Parse("ctrl+!"));

    [Fact]
    public void BracesAndParenthesesAreLiteral() =>
        Assert.Equal([C("(")], ShortcutParser.Parse("("));

    [Theory]
    [InlineData("ctrl+", "Invalid shortcut 'ctrl+': empty key name")]
    [InlineData("", "Invalid shortcut '': empty key name")]
    [InlineData("ctrl+foo", "Invalid shortcut 'ctrl+foo': unknown key 'foo'")]
    public void BadShortcutsFailBeforeAnythingIsSent(string shortcut, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => ShortcutParser.Parse(shortcut)).Message);
}
