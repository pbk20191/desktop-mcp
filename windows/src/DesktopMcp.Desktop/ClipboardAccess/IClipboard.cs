namespace DesktopMcp.Desktop.ClipboardAccess;

/// <summary>The user's clipboard text.</summary>
public interface IClipboard
{
    /// <summary>The clipboard's Unicode text, or null when it holds no text.</summary>
    Task<string?> GetTextAsync(CancellationToken cancellationToken);

    Task SetTextAsync(string text, CancellationToken cancellationToken);
}

public enum ClipboardPath { None, WinRt, Win32 }
