namespace DesktopMcp.Desktop;

/// <summary>The levels of <see cref="DesktopLog"/>.</summary>
public enum DesktopLogLevel { Debug, Warning, Error }

/// <summary>
/// A log sink for the desktop services. This project references no logging package (Global Constraints: no new package
/// references), so the server's composition root adapts its <c>ILogger</c> to this delegate. It must not throw.
/// </summary>
public delegate void DesktopLog(DesktopLogLevel level, string message, Exception? error);
