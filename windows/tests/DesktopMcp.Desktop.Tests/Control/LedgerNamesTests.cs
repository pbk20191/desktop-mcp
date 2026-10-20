using DesktopMcp.Desktop.Control;

namespace DesktopMcp.Desktop.Tests.Control;

public class LedgerNamesTests
{
    [Theory]
    [InlineData("key:ctrl", new[] { 0x11, 0xA2, 0xA3 })]
    [InlineData("key:vk:11", new[] { 0x11, 0xA2, 0xA3 })]
    [InlineData("key:shift", new[] { 0x10, 0xA0, 0xA1 })]
    [InlineData("key:vk:10", new[] { 0x10, 0xA0, 0xA1 })]
    [InlineData("key:vk:12", new[] { 0x12, 0xA4, 0xA5 })]
    [InlineData("key:vk:a3", new[] { 0x11, 0xA2, 0xA3 })]
    [InlineData("key:vk:41", new[] { 0x41 })]
    [InlineData("mouse:left", new int[0])]
    [InlineData("nonsense", new int[0])]
    public void BothNamesOfAKeyMapToTheSamePhysicalKeys(string name, int[] keys) => Assert.Equal(keys, LedgerNames.Keys(name));

    [Theory]
    [InlineData("mouse:left", 1)]
    [InlineData("mouse:right", 2)]
    [InlineData("mouse:middle", 3)]
    [InlineData("key:ctrl", null)]
    public void Buttons(string name, int? button) => Assert.Equal(button, LedgerNames.Button(name));
}
