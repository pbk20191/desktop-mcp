using System.Runtime.InteropServices;

namespace DesktopMcp.Desktop.Errors;

/// <summary>
/// Classifies COM and WinRT failures and turns them into <see cref="DesktopException"/>s at the Desktop boundary
/// (P0 follow-up "COMException translation"). CsWinRT maps some HRESULTs to .NET types (E_ACCESSDENIED to
/// UnauthorizedAccessException, E_NOTIMPL to NotImplementedException, E_NOINTERFACE to InvalidCastException); those are
/// translated too. Argument, cancellation and our own exceptions pass through unchanged.
/// </summary>
public static class ComErrors
{
    // upstream uia/exceptions.py:57-65: the element or its process is gone; retrying will not help.
    private static readonly HashSet<int> DeadElement =
    [
        unchecked((int)0x80040201), // UIA_E_ELEMENTNOTAVAILABLE
        unchecked((int)0x80000013), // RO_E_CLOSED
        unchecked((int)0x80010108), // RPC_E_DISCONNECTED
        unchecked((int)0x80010007), // RPC_E_SERVER_DIED
        unchecked((int)0x80010012), // RPC_E_SERVER_DIED_DNE
        unchecked((int)0x80010006), // RPC_E_CONNECTION_TERMINATED
        unchecked((int)0x800401FD), // CO_E_OBJNOTCONNECTED
        unchecked((int)0x800401FF), // CO_E_RELEASED
    ];

    // upstream uia/exceptions.py:73-74.
    private static readonly HashSet<int> Timeout =
    [
        unchecked((int)0x80131505), // UIA_E_TIMEOUT
        unchecked((int)0x8001011F), // RPC_E_TIMEOUT
    ];

    /// <summary>The element no longer exists (upstream UIADeadElementError).</summary>
    public static bool IsDeadElement(Exception error) => error is COMException && DeadElement.Contains(error.HResult);

    /// <summary>UIA's own timeout (UIA_E_TIMEOUT, RPC_E_TIMEOUT) or the hang breaker's deadline.</summary>
    public static bool IsTimeout(Exception error) =>
        error is UiaTimeoutException || (error is COMException && Timeout.Contains(error.HResult));

    /// <summary>True for the COM/WinRT failures <see cref="Translate"/> wraps.</summary>
    public static bool ShouldTranslate(Exception error) =>
        error is COMException or UnauthorizedAccessException or NotImplementedException or InvalidCastException
        && error.HResult < 0;

    /// <summary>
    /// "&lt;operation&gt; failed: &lt;message&gt; (HRESULT 0x...)", or "&lt;operation&gt; failed (HRESULT 0x...)" when the
    /// exception has no message; other exceptions are returned unchanged.
    /// </summary>
    public static Exception Translate(string operation, Exception error)
    {
        if (!ShouldTranslate(error)) return error;
        var hresult = $"HRESULT 0x{error.HResult:X8}";
        var message = error.Message.Trim();
        return new DesktopException(message.Length == 0 ? $"{operation} failed ({hresult})" : $"{operation} failed: {message} ({hresult})", error);
    }
}
