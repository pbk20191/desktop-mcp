using System.Globalization;

namespace DesktopMcp.Desktop.Control;

/// <summary>
/// Maps <see cref="Input.InputLedger"/> entry names to the physical keys and buttons the monitor tracks, so the ledger
/// leaves alone input the user physically holds (Decisions 19). <see cref="Input.DesktopInput"/> names Ctrl and Shift two
/// ways (<c>key:ctrl</c>/<c>key:shift</c> for its own holds, <c>key:vk:11</c>/<c>key:vk:10</c> for Shortcut keys), and the
/// low-level hook reports the left and right codes, so every name of a modifier maps to its generic, left and right codes.
/// </summary>
internal static class LedgerNames
{
    private const string KeyPrefix = "key:";
    private const string VkPrefix = "key:vk:";

    private static readonly int[] Shift = [0x10, 0xA0, 0xA1];
    private static readonly int[] Ctrl = [0x11, 0xA2, 0xA3];
    private static readonly int[] Alt = [0x12, 0xA4, 0xA5];

    /// <summary>The virtual keys whose delivered physical down means the user holds this entry's key; empty for a non-key.</summary>
    public static IReadOnlyList<int> Keys(string ledgerName)
    {
        if (ledgerName.StartsWith(VkPrefix, StringComparison.Ordinal))
        {
            return int.TryParse(ledgerName.AsSpan(VkPrefix.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var vk)
                && vk is >= 0 and < 256
                ? Expand(vk)
                : [];
        }
        if (!ledgerName.StartsWith(KeyPrefix, StringComparison.Ordinal)) return [];
        return ledgerName.AsSpan(KeyPrefix.Length) switch
        {
            "ctrl" => Ctrl,
            "shift" => Shift,
            "alt" => Alt,
            _ => [],
        };
    }

    /// <summary>The hook's button id (1 left, 2 right, 3 middle) for a mouse entry; null otherwise.</summary>
    public static int? Button(string ledgerName) => ledgerName switch
    {
        "mouse:left" => 1,
        "mouse:right" => 2,
        "mouse:middle" => 3,
        _ => null,
    };

    private static int[] Expand(int vk) => VirtualKeys.Normalize(vk) switch
    {
        0x10 => Shift,
        0x11 => Ctrl,
        0x12 => Alt,
        _ => [vk],
    };
}
