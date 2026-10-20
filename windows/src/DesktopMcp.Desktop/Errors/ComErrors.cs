using System.Runtime.InteropServices;

namespace DesktopMcp.Desktop.Errors;

/// <summary>
/// Classifies COM and WinRT failures and turns them into <see cref="DesktopException"/>s at the Desktop boundary
/// (P0 follow-up "COMException translation"). CsWinRT maps some HRESULTs to .NET types (E_ACCESSDENIED to
/// UnauthorizedAccessException, E_NOTIMPL to NotImplementedException, E_NOINTERFACE to InvalidCastException); those are
/// translated too, but only when their HRESULT is in a COM or WinRT facility (see <see cref="ShouldTranslate"/>).
/// Argument, cancellation and our own exceptions, and plain .NET exceptions with the CLR's own default HRESULT, pass through.
/// Translate is meant to wrap COM and WinRT calls only; do not apply it to file, parsing or other managed code.
/// The message uses fixed English text for known HRESULTs; the OS message (localised) is only the fallback.
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

    private const int RegdbClassNotReg = unchecked((int)0x80040154);

    private static readonly Dictionary<int, string> KnownText = BuildKnownText();

    private static Dictionary<int, string> BuildKnownText()
    {
        var map = new Dictionary<int, string>();
        foreach (var code in DeadElement) map[code] = "the window or control is no longer available";
        foreach (var code in Timeout) map[code] = "the application did not respond in time";
        map[unchecked((int)0x80131505)] = "the application did not respond in time";
        map[unchecked((int)0x80070005)] = "access was denied; the target may be running elevated";
        foreach (var code in new[] { 0x887A0005u, 0x887A0006u, 0x887A0007u, 0x887A0020u })
            map[unchecked((int)code)] = "the graphics device was lost";
        return map;
    }

    /// <summary>The element no longer exists (upstream UIADeadElementError).</summary>
    public static bool IsDeadElement(Exception error) => error is COMException && DeadElement.Contains(error.HResult);

    /// <summary>
    /// UIA's own timeout (UIA_E_TIMEOUT, RPC_E_TIMEOUT), the hang breaker's deadline, or a walked window that stopped
    /// answering (<see cref="UiaNotRespondingException"/>).
    /// </summary>
    public static bool IsTimeout(Exception error) =>
        error is UiaTimeoutException or UiaNotRespondingException || (error is COMException && Timeout.Contains(error.HResult));

    /// <summary>
    /// True for the COM/WinRT failures <see cref="Translate"/> wraps: every <see cref="COMException"/>, and the projected
    /// UnauthorizedAccessException, NotImplementedException and InvalidCastException only when their HRESULT is a failure in
    /// FACILITY_RPC (1), FACILITY_ITF (4), FACILITY_WIN32 (7) or FACILITY_DXGI (0x87A). A plain exception carrying the CLR's
    /// default HRESULT (E_NOTIMPL 0x80004001 and E_NOINTERFACE 0x80004002, facility 0) passes through. A managed
    /// UnauthorizedAccessException (default 0x80070005, WIN32) is indistinguishable from a projected E_ACCESSDENIED, which is why
    /// Translate must only wrap COM and WinRT calls.
    /// </summary>
    public static bool ShouldTranslate(Exception error)
    {
        if (error is COMException) return true;
        if (error is not (UnauthorizedAccessException or NotImplementedException or InvalidCastException) || error.HResult >= 0) return false;
        return ((error.HResult >> 16) & 0x7FF) is 1 or 4 or 7 or 0x87A;
    }

    /// <summary>
    /// "&lt;operation&gt; failed: &lt;message&gt; (HRESULT 0x...)", or "&lt;operation&gt; failed (HRESULT 0x...)" when the
    /// exception has no message; other exceptions are returned unchanged.
    /// </summary>
    public static Exception Translate(string operation, Exception error)
    {
        if (!ShouldTranslate(error)) return error;
        var hresult = $"HRESULT 0x{error.HResult:X8}";
        var message = error.HResult == RegdbClassNotReg
            ? $"{AppRuntime.InstallHint()} if it is missing, or the class is not activated"
            : KnownText.TryGetValue(error.HResult, out var known) ? known : error.Message.Trim();
        return new DesktopException(message.Length == 0 ? $"{operation} failed ({hresult})" : $"{operation} failed: {message} ({hresult})", error);
    }
}
