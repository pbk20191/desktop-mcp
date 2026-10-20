namespace DesktopMcp.Desktop.Errors;

/// <summary>
/// A desktop operation failed. Its message is meant for the tool result; COM and WinRT details (HRESULTs) stay inside it
/// (spec §3 boundary rule).
/// </summary>
public class DesktopException : Exception
{
    public DesktopException(string message) : base(message)
    {
    }

    public DesktopException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>A UI Automation call did not finish within its deadline (the hang breaker, Decisions 2).</summary>
public sealed class UiaTimeoutException(string message) : DesktopException(message);

/// <summary>
/// The walked window did not answer WM_NULL in time, so its walk stopped before the next UI Automation call (Decisions 38).
/// Handled like UIA's own timeout: the window is left out and nothing is blocked.
/// </summary>
public sealed class UiaNotRespondingException(string message) : DesktopException(message);

/// <summary>Too many UI Automation calls are still blocked in other applications to start another one.</summary>
public sealed class UiaUnavailableException(string message) : DesktopException(message);
