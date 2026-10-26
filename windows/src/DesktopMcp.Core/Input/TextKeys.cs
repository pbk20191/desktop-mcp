namespace DesktopMcp.Core.Input;

/// <summary>Type's text as key tokens, one entry per code point (desktop/service.py:62-78, 807-810).</summary>
public static class TextKeys
{
    public static IEnumerable<IReadOnlyList<KeyToken>> PerCharacter(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\r':
                    yield return [];
                    break;
                case '\n':
                    yield return [new KeyToken(KeyTables.VkReturn, false), new KeyToken(KeyTables.VkReturn, true)];
                    break;
                case '\t':
                    yield return [new KeyToken(KeyTables.VkTab, false), new KeyToken(KeyTables.VkTab, true)];
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        yield return [new KeyToken(0, false, text.Substring(i, 2))];
                        i++;
                    }
                    else
                    {
                        yield return [new KeyToken(0, false, c.ToString())];
                    }

                    break;
            }
        }
    }
}
