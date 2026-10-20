using DesktopMcp.Core.Input;

namespace DesktopMcp.Core.Tests.Input;

public class TextKeysTests
{
    // desktop/service.py:62-78: \n is Enter, \t is Tab, \r is skipped, braces are characters; one entry per code point.
    [Fact]
    public void TextKeysSendSurrogatePairsAndNewlines()
    {
        var keys = TextKeys.PerCharacter("a\r\n{😀\t").ToArray();
        Assert.Equal(6, keys.Length);
        Assert.Equal([new KeyToken(0, false, "a")], keys[0]);
        Assert.Empty(keys[1]);
        Assert.Equal([new KeyToken(0x0D, false), new KeyToken(0x0D, true)], keys[2]);
        Assert.Equal([new KeyToken(0, false, "{")], keys[3]);
        Assert.Equal([new KeyToken(0, false, "😀")], keys[4]);
        Assert.Equal([new KeyToken(0x09, false), new KeyToken(0x09, true)], keys[5]);
    }
}
