using DesktopMcp.Core.Text;

namespace DesktopMcp.Core.Input;

/// <summary>Shortcut text to key tokens (desktop/service.py:991-999 feeding uia/core.py:1541-1641).</summary>
public static class ShortcutParser
{
    public static IReadOnlyList<KeyToken> Parse(string shortcut)
    {
        var tokens = new List<KeyToken>();
        var hold = false;
        var insertIndex = 0;
        ushort? lastKey = null;

        foreach (var raw in shortcut.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                throw Invalid(shortcut, "empty key name");
            }

            if (PyText.CodePointLength(part) == 1)
            {
                if (hold && part.Length == 1 && KeyTables.CharacterCodes.TryGetValue(part[0], out var code))
                {
                    Insert(tokens, ref insertIndex, ref lastKey, code);
                }
                else
                {
                    tokens.Add(new KeyToken(0, false, part));
                    lastKey = null;
                }

                hold = false;
                continue;
            }

            var lower = part.ToLowerInvariant();
            var name = (KeyTables.Aliases.TryGetValue(lower, out var alias) ? alias : lower).ToUpperInvariant();
            if (!KeyTables.SpecialKeyNames.TryGetValue(name, out var vk))
            {
                throw Invalid(shortcut, $"unknown key {PyText.Repr(part)}");
            }

            var isHold = KeyTables.HoldKeys.Contains(name);
            if (hold)
            {
                Insert(tokens, ref insertIndex, ref lastKey, vk);
                if (isHold)
                {
                    insertIndex++;
                }
                else
                {
                    hold = false;
                }
            }
            else
            {
                tokens.Add(new KeyToken(vk, false));
                tokens.Add(new KeyToken(vk, true));
                lastKey = vk;
                if (isHold)
                {
                    hold = true;
                    insertIndex = tokens.Count - 1;
                }
            }
        }

        return tokens;
    }

    private static void Insert(List<KeyToken> tokens, ref int insertIndex, ref ushort? lastKey, ushort vk)
    {
        if (lastKey == vk)
        {
            insertIndex++;
        }

        tokens.Insert(insertIndex, new KeyToken(vk, false));
        tokens.Insert(insertIndex + 1, new KeyToken(vk, true));
        lastKey = vk;
    }

    private static ArgumentException Invalid(string shortcut, string reason) =>
        new($"Invalid shortcut {PyText.Repr(shortcut)}: {reason}");
}
