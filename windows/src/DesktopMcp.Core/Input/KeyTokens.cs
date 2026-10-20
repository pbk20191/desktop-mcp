namespace DesktopMcp.Core.Input;

/// <summary>A virtual-key down or up, or (when <see cref="Character"/> is set) one character typed as Unicode (one or two UTF-16 units).</summary>
public readonly record struct KeyToken(ushort VirtualKey, bool Up, string? Character = null);

/// <summary>One wire event. <c>VirtualKey == 0</c> means a Unicode unit.</summary>
public readonly record struct KeyInput(ushort VirtualKey, char Unicode, bool Up);

/// <summary>The events sent together, and the pause that follows them.</summary>
public sealed record KeyStep(IReadOnlyList<KeyInput> Inputs, TimeSpan PauseAfter);
