namespace DesktopMcp.Core.Input;

/// <summary>Key tokens to wire-level steps with upstream's pauses (uia/core.py:1702-1744).</summary>
public static class SendKeysPlan
{
    /// <summary>Upstream's pause after a key down and between key ups ("must sleep for a while, otherwise combined keys may not be caught").</summary>
    public static readonly TimeSpan HotkeyInterval = TimeSpan.FromMilliseconds(10);

    public static IReadOnlyList<KeyStep> Build(IReadOnlyList<KeyToken> tokens, TimeSpan interval, TimeSpan waitTime)
    {
        var steps = new List<KeyStep>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var last = i + 1 == tokens.Count;
            KeyInput[] inputs;
            TimeSpan pause;
            if (token.Character is { } character)
            {
                inputs = new KeyInput[character.Length * 2];
                for (var j = 0; j < character.Length; j++)
                {
                    inputs[j * 2] = new KeyInput(0, character[j], false);
                    inputs[j * 2 + 1] = new KeyInput(0, character[j], true);
                }

                pause = interval;
            }
            else
            {
                inputs = [new KeyInput(token.VirtualKey, '\0', token.Up)];
                if (last)
                {
                    pause = interval;
                }
                else if (token.Up)
                {
                    var next = tokens[i + 1];
                    pause = next.Character is not null || !next.Up ? interval : HotkeyInterval;
                }
                else
                {
                    pause = HotkeyInterval;
                }
            }

            steps.Add(new KeyStep(inputs, last ? pause + waitTime : pause));
        }

        return steps;
    }
}
