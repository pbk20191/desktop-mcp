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
        if (!_windows.IsWindow(window))
        {
            var name = windowName.Length > 0 ? windowName : window.ToString(System.Globalization.CultureInfo.InvariantCulture);
            throw new DesktopException($"Could not bring {name} to the foreground: the window no longer exists; nothing was typed.");
        }
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
    /// upstream service.py:616-658 on a dedicated thread, abandoned after 2 s: a call into a hung thread costs only that
    /// thread. BringWindowToTop is left out (a synchronous cross-thread SetWindowPos, the one unbounded call inside the
    /// attached region); the SetWindowPos(HWND_TOP, SWP_ASYNCWINDOWPOS) that follows sets the z-order. Each successful
    /// attach is recorded, so when the caller abandons the thread (timeout or cancellation) it detaches every recorded
    /// thread itself (AttachThreadInput takes the attaching thread's id, so any thread can undo it) and the worker makes
    /// no further call once it recovers.
    /// </summary>
    private async Task RunAttachSequenceAsync(long window, CancellationToken ct)
    {
        var state = new AttachState();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                AttachSequence(window, state);
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
            // Abandoned; the poll that follows decides the outcome.
            Abandon(state);
        }
        catch (OperationCanceledException)
        {
            Abandon(state);
            throw;
        }
        catch (Exception)
        {
            // The worker failed (it detached in its finally); the poll that follows gives the clear failure text.
        }
    }

    private void Abandon(AttachState state)
    {
        var (attachingThread, attached) = state.Cancel();
        for (var i = attached.Count - 1; i >= 0; i--) _calls.AttachThreadInput(attachingThread, attached[i], false);
    }

    private void AttachSequence(long window, AttachState state)
    {
        // This thread's first user32 call (GetForegroundWindow below) creates its message queue, which AttachThreadInput needs.
        var current = _calls.GetCurrentThreadId();
        var foregroundThread = _windows.GetThreadId(_windows.GetForegroundWindow());
        var targetThread = _windows.GetThreadId(window);
        if (!state.Start(current)) return;
        try
        {
            _calls.AllowSetForegroundWindow(PInvoke.ASFW_ANY);
            Span<uint> tried = [0, 0];
            var count = 0;
            foreach (var thread in (ReadOnlySpan<uint>)[foregroundThread, targetThread])
            {
                // Equal threads are attached once; a failed attach (an elevated window's thread) is skipped (service.py:629-641).
                if (thread == 0 || thread == current || tried[..count].Contains(thread)) continue;
                tried[count++] = thread;
                if (state.Cancelled) return;
                if (!_calls.AttachThreadInput(current, thread, true)) continue;
                if (!state.Record(thread))
                {
                    // The caller gave up while this attach ran, so it did not see it: undo it here.
                    _calls.AttachThreadInput(current, thread, false);
                    return;
                }
            }
            if (state.Cancelled) return;
            _calls.SetForegroundWindow(window);
            if (state.Cancelled) return;
            _calls.SetWindowPos(window, 0, 0, 0, 0, 0, TopPlacement, out _); // HWND_TOP
        }
        finally
        {
            var attached = state.TakeAttached();
            for (var i = attached.Count - 1; i >= 0; i--) _calls.AttachThreadInput(current, attached[i], false);
        }
    }

    /// <summary>
    /// The attach thread's id and its outstanding attaches, shared by the worker and the caller. Whoever takes the list
    /// (the worker when it finishes, the caller when it abandons the worker) detaches those threads, so each is detached once.
    /// </summary>
    private sealed class AttachState
    {
        private readonly Lock _lock = new();
        private readonly List<uint> _attached = new(2);
        private uint _thread;
        private bool _cancelled;

        public bool Cancelled
        {
            get { lock (_lock) return _cancelled; }
        }

        public bool Start(uint thread)
        {
            lock (_lock)
            {
                _thread = thread;
                return !_cancelled;
            }
        }

        /// <summary>Records a successful attach; false when the caller has already given up.</summary>
        public bool Record(uint thread)
        {
            lock (_lock)
            {
                if (_cancelled) return false;
                _attached.Add(thread);
                return true;
            }
        }

        public List<uint> TakeAttached()
        {
            lock (_lock)
            {
                var attached = new List<uint>(_attached);
                _attached.Clear();
                return attached;
            }
        }

        public (uint Thread, List<uint> Attached) Cancel()
        {
            lock (_lock)
            {
                _cancelled = true;
                var attached = new List<uint>(_attached);
                _attached.Clear();
                return (_thread, attached);
            }
        }
    }
}

internal sealed class Win32ForegroundCalls : IForegroundCalls
{
    public bool ShowWindowAsync(long window, int command) => PInvoke.ShowWindowAsync(Hwnd(window), (SHOW_WINDOW_CMD)command);
    public bool SetForegroundWindow(long window) => PInvoke.SetForegroundWindow(Hwnd(window));
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
