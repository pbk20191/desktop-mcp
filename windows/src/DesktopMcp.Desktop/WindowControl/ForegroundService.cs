using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tree;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DesktopMcp.Desktop.WindowControl;

/// <summary>The outcome of a switch: whether the window became the foreground window, and whether it had been minimised.</summary>
public sealed record ForegroundResult(bool Succeeded, bool WasMinimized);

/// <summary>Brings a top-level window to the foreground (Decisions 7).</summary>
public interface IForeground
{
    /// <summary>App switch: succeeds when the foreground handle is the window (upstream's handle comparison).</summary>
    Task<ForegroundResult> SwitchToAsync(long window, CancellationToken ct);

    /// <summary>
    /// Before keys: succeeds when the foreground window belongs to the window's GUI thread (a popup of the same thread
    /// counts); otherwise throws <see cref="DesktopException"/>, and nothing should be typed.
    /// </summary>
    Task EnsureKeyboardTargetAsync(long window, string windowName, CancellationToken ct);
}

/// <summary>The Win32 calls the foreground sequence and the window placement make; a seam for the tests.</summary>
internal interface IForegroundCalls
{
    bool ShowWindowAsync(long window, int command);
    bool SetForegroundWindow(long window);
    bool BringWindowToTop(long window);
    bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    bool AllowSetForegroundWindow(uint processId);
    bool SetWindowPos(long window, long insertAfter, int x, int y, int width, int height, uint flags, out int lastError);
    uint GetCurrentThreadId();
    bool IsIconic(long window);
}

/// <summary>
/// upstream desktop/service.py:559-661 (switch_app, bring_window_to_top) made bounded (Decisions 7): the WM_NULL check
/// first, so a hung window gets no call; a minimised window is restored with ShowWindowAsync; then SetForegroundWindow;
/// if that does not take, upstream's AttachThreadInput sequence runs on its own thread, abandoned after 2 s (it can block
/// on a hung thread); then the foreground is polled every 50 ms for 1 s.
/// </summary>
public sealed class ForegroundService : IForeground
{
    internal const int SwRestore = (int)SHOW_WINDOW_CMD.SW_RESTORE;

    internal const uint TopPlacement = (uint)(SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
        | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RestoreLimit = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SetForegroundLimit = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AttachLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VerifyLimit = TimeSpan.FromSeconds(1);

    private readonly IWindowSystem _windows;
    private readonly IForegroundCalls _calls;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _time;

    public ForegroundService()
        : this(new Win32WindowSystem(), new Win32ForegroundCalls(), (delay, ct) => Task.Delay(delay, ct), TimeProvider.System)
    {
    }

    internal ForegroundService(IWindowSystem windows, IForegroundCalls calls, Func<TimeSpan, CancellationToken, Task> delay, TimeProvider time)
    {
        _windows = windows;
        _calls = calls;
        _delay = delay;
        _time = time;
    }

    public async Task<ForegroundResult> SwitchToAsync(long window, CancellationToken ct)
    {
        // upstream bring_window_to_top raises "Invalid window handle" for a window that is gone.
        if (!_windows.IsWindow(window)) throw new DesktopException("Invalid window handle");
        if (!await RespondsAsync(window, ct)) throw new DesktopException($"{_windows.GetTitle(window)} is not responding");
        var wasMinimized = _calls.IsIconic(window);
        var succeeded = await BringToForegroundAsync(window, wasMinimized, () => _windows.GetForegroundWindow() == window, ct);
        return new ForegroundResult(succeeded, wasMinimized);
    }

    public async Task EnsureKeyboardTargetAsync(long window, string windowName, CancellationToken ct)
    {
        if (!await RespondsAsync(window, ct)) throw new DesktopException($"{windowName} is not responding; nothing was typed.");
        var thread = _windows.GetThreadId(window);
        bool HasKeyboard() => thread != 0 && _windows.GetThreadId(_windows.GetForegroundWindow()) == thread;
        if (!await BringToForegroundAsync(window, _calls.IsIconic(window), HasKeyboard, ct))
            throw new DesktopException($"Could not bring {windowName} to the foreground; nothing was typed.");
    }

    private async Task<bool> RespondsAsync(long window, CancellationToken ct)
    {
        // The WM_NULL check waits up to 250 ms; keep it off the caller's thread.
        var response = await Task.Run(() => _windows.CheckResponding(window), ct).ConfigureAwait(false);
        return response == WindowResponse.Responding;
    }

    private async Task<bool> BringToForegroundAsync(long window, bool minimized, Func<bool> succeeded, CancellationToken ct)
    {
        if (minimized)
        {
            _calls.ShowWindowAsync(window, SwRestore);
            await PollAsync(() => !_calls.IsIconic(window), RestoreLimit, ct).ConfigureAwait(false);
        }
        if (succeeded()) return true;

        _calls.SetForegroundWindow(window);
        if (await PollAsync(succeeded, SetForegroundLimit, ct).ConfigureAwait(false)) return true;

        await RunAttachSequenceAsync(window, ct).ConfigureAwait(false);
        return await PollAsync(succeeded, VerifyLimit, ct).ConfigureAwait(false);
    }

    /// <summary>Checks the condition now and every 50 ms until it holds or <paramref name="limit"/> has passed.</summary>
    private async Task<bool> PollAsync(Func<bool> condition, TimeSpan limit, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + limit;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (condition()) return true;
            if (_time.GetUtcNow() >= deadline) return false;
            await _delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// upstream service.py:616-658 on a dedicated thread: AttachThreadInput attaches the calling thread, so the attach and
    /// the detach happen on that thread, and a call blocked on a hung thread only costs the abandoned thread.
    /// </summary>
    private async Task RunAttachSequenceAsync(long window, CancellationToken ct)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                AttachSequence(window);
                finished.TrySetResult();
            }
            catch (Exception e)
            {
                finished.TrySetException(e);
            }
        })
        { IsBackground = true, Name = "desktop-mcp foreground" };
        thread.Start();
        try
        {
            await finished.Task.WaitAsync(AttachLimit, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Abandoned: the thread detaches itself if the blocked call ever returns. The poll below decides the outcome.
        }
    }

    private void AttachSequence(long window)
    {
        var current = _calls.GetCurrentThreadId();
        var foregroundThread = _windows.GetThreadId(_windows.GetForegroundWindow());
        var targetThread = _windows.GetThreadId(window);
        _calls.AllowSetForegroundWindow(PInvoke.ASFW_ANY);
        var attached = new List<uint>(2);
        try
        {
            foreach (var thread in (ReadOnlySpan<uint>)[foregroundThread, targetThread])
            {
                // Equal threads are attached once; a failed attach (an elevated window's thread) is skipped (service.py:629-641).
                if (thread == 0 || thread == current || attached.Contains(thread)) continue;
                if (_calls.AttachThreadInput(current, thread, true)) attached.Add(thread);
            }
            _calls.SetForegroundWindow(window);
            _calls.BringWindowToTop(window);
            _calls.SetWindowPos(window, 0, 0, 0, 0, 0, TopPlacement, out _); // HWND_TOP
        }
        finally
        {
            for (var i = attached.Count - 1; i >= 0; i--) _calls.AttachThreadInput(current, attached[i], false);
        }
    }
}

internal sealed class Win32ForegroundCalls : IForegroundCalls
{
    public bool ShowWindowAsync(long window, int command) => PInvoke.ShowWindowAsync(Hwnd(window), (SHOW_WINDOW_CMD)command);
    public bool SetForegroundWindow(long window) => PInvoke.SetForegroundWindow(Hwnd(window));
    public bool BringWindowToTop(long window) => PInvoke.BringWindowToTop(Hwnd(window));
    public bool AttachThreadInput(uint attach, uint attachTo, bool doAttach) => PInvoke.AttachThreadInput(attach, attachTo, doAttach);
    public bool AllowSetForegroundWindow(uint processId) => PInvoke.AllowSetForegroundWindow(processId);

    public bool SetWindowPos(long window, long insertAfter, int x, int y, int width, int height, uint flags, out int lastError)
    {
        if (PInvoke.SetWindowPos(Hwnd(window), Hwnd(insertAfter), x, y, width, height, (SET_WINDOW_POS_FLAGS)flags))
        {
            lastError = 0;
            return true;
        }
        lastError = Marshal.GetLastPInvokeError();
        return false;
    }

    public uint GetCurrentThreadId() => PInvoke.GetCurrentThreadId();
    public bool IsIconic(long window) => PInvoke.IsIconic(Hwnd(window));

    private static HWND Hwnd(long window) => (HWND)(nint)window;
}
