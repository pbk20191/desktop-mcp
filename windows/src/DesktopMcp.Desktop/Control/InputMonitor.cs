using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Errors;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DesktopMcp.Desktop.Control;

/// <summary>The Win32 side of desktop control's input watching (spec §5, §7), as the coordinator sees it.</summary>
internal interface IInputMonitor : IAsyncDisposable
{
    PhysicalInputFilter Filter { get; }

    Task StartAsync(CancellationToken ct);

    Task StopAsync();

    /// <summary>The timestamp (<c>now()</c> units) of the last 0.1 s dispatcher tick.</summary>
    long LastBeat { get; }

    /// <summary>The timestamp (<c>now()</c> units) of the last successful hook install.</summary>
    long LastRehook { get; }

    /// <summary>Any thread: reinstall the hooks on the next 0.1 s tick (the coordinator, after a fault).</summary>
    void RequestRehook();
}

/// <summary>
/// The Win32 calls of <see cref="InputMonitor"/>, a seam so the default test run installs nothing. Every member except
/// <see cref="CallNextHook"/> (the hook threads) and <see cref="GetAsyncKeyState"/> runs on the dispatcher thread.
/// </summary>
internal interface IInputMonitorNative
{
    /// <summary>Registers the window class, creates the message-only window and registers it for mouse Raw Input
    /// (<c>RIDEV_INPUTSINK</c>). Throws on failure, having undone its own partial work.</summary>
    void CreateSink();

    /// <summary>Removes the Raw Input registration, destroys the window and unregisters the class. Best effort, never throws.</summary>
    void DestroySink();

    /// <summary>Installs <c>WH_KEYBOARD_LL</c> and <c>WH_MOUSE_LL</c>. Throws on failure, having left neither installed.</summary>
    (nint Keyboard, nint Mouse) InstallHooks();

    void Unhook(nint hook);

    nint CallNextHook(int code, nint wParam, nint lParam);

    short GetAsyncKeyState(int vk);
}

/// <summary>
/// The low-level keyboard and mouse hooks and the Raw Input sink window on the shared dispatcher thread (spec §5, §7;
/// <c>control_win32.py:255-339</c>, <c>control.py:561-574</c>), the 5 s hook reinstall (Decisions 11) and the 0.1 s
/// missed-key-up sample and beat (Decisions 13, 25). Every decision is <see cref="PhysicalInputFilter"/>'s; this class only
/// carries events to it and its verdicts back to Windows. The hook procedures are static and route to the one started
/// monitor (<c>s_current</c>, set before the hooks are installed and cleared after they are removed). They stay minimal and
/// never block (Windows removes a hook slower than <c>LowLevelHooksTimeout</c>): no lock, no allocation, no logging, and
/// every exception is caught and fails open.
/// </summary>
internal sealed class InputMonitor : IInputMonitor
{
    internal static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RehookInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private const uint WmKeyDown = 0x100;
    private const uint WmSysKeyDown = 0x104;

    private static InputMonitor? s_current;

    private readonly DispatcherHost _host;
    private readonly PhysicalInputFilter _filter;
    private readonly Func<long> _now;
    private readonly IInputMonitorNative _native;
    private readonly TimeSpan _startLimit;
    private readonly TimeSpan _stopLimit;
    private readonly Func<int, short> _keyState;
    private readonly Lock _gate = new();

    // Thread-pool side, under _gate: the dispatcher of the current run and its teardown.
    private IDesktopDispatcher? _dispatcher;
    private Task? _stopping;
    private long _generation; // every start and stop moves it on; a start proceeds only while its own is current
    private Task<IDisposable>? _rehookTimer;
    private Task<IDisposable>? _tickTimer;

    // Dispatcher thread only.
    private bool _running;
    private bool _sinkCreated;
    private nint _keyboardHook;
    private nint _mouseHook;

    // Any thread.
    private bool _rehookWanted;
    private long _lastBeat;
    private long _lastRehook;
    private long _rawMessages;

    public InputMonitor(DispatcherHost host, PhysicalInputFilter filter, Func<long> now)
        : this(host, filter, now, new Win32Native(), StartLimit, StopLimit)
    {
    }

    internal InputMonitor(DispatcherHost host, PhysicalInputFilter filter, Func<long> now, IInputMonitorNative native, TimeSpan startLimit,
        TimeSpan stopLimit)
    {
        _stopLimit = stopLimit;
        _host = host;
        _filter = filter;
        _now = now;
        _native = native;
        _startLimit = startLimit;
        _keyState = native.GetAsyncKeyState; // one delegate for the life of the monitor, not one per tick
    }

    public PhysicalInputFilter Filter => _filter;

    public long LastBeat => Volatile.Read(ref _lastBeat);

    public long LastRehook => Volatile.Read(ref _lastRehook);

    /// <summary>WM_INPUT messages the sink window received, every kind counted before filtering (test-only).</summary>
    internal long RawMessages => Interlocked.Read(ref _rawMessages);

    public void RequestRehook() => Volatile.Write(ref _rehookWanted, true);

    /// <summary>
    /// Starts the sink window, the hooks and the timers on the shared dispatcher, bounded by 3 s (a slower start throws
    /// <see cref="TimeoutException"/>). One monitor per process: another running monitor makes this throw
    /// <see cref="InvalidOperationException"/>. A failed start leaves nothing installed (a set-up still queued on a stalled
    /// dispatcher finds its start stale and installs nothing; one already done is undone by the teardown queued behind it),
    /// so the same instance may be started again once that teardown has run.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref s_current, this, null) is { } running)
        {
            if (!ReferenceEquals(running, this)) throw new InvalidOperationException("Another input monitor is running.");
            lock (_gate)
            {
                throw new InvalidOperationException(_stopping is null
                    ? "This input monitor is already running."
                    : "The previous run of this input monitor is still being torn down on the dispatcher; try again later.");
            }
        }
        long run;
        lock (_gate)
        {
            run = ++_generation;
            _stopping = null;
            _dispatcher = null;
            _rehookTimer = _tickTimer = null;
        }
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(_startLimit);
        try
        {
            var dispatcher = await _host.GetAsync(bound.Token).ConfigureAwait(false);
            lock (_gate)
            {
                // A StopAsync while the dispatcher was starting has already freed the slot (and a newer start of this instance
                // may own it now): install nothing.
                ThrowIfStale(run);
                _dispatcher = dispatcher;
            }
            await dispatcher.InvokeAsync(() => SetUp(run)).WaitAsync(bound.Token).ConfigureAwait(false);
            Task<IDisposable> rehook, tick;
            lock (_gate)
            {
                // A StopAsync during the start has already queued the teardown; timers started now would outlive it.
                ThrowIfStale(run);
                rehook = _rehookTimer = dispatcher.StartTimerAsync(RehookInterval, RehookTick);
                tick = _tickTimer = dispatcher.StartTimerAsync(TickInterval, Tick);
            }
            await Task.WhenAll(rehook, tick).WaitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Queued behind the start on the dispatcher, so it undoes whatever the start did; nobody awaits it, so a failure is
            // reported to the host's sink.
            // A stale start (stopped, or superseded by a newer start of this instance) installed nothing and owns nothing.
            bool stale;
            lock (_gate) stale = run != _generation;
            if (!stale) ReportFailure(StopCoreAsync());
            if (e is OperationCanceledException && bound.IsCancellationRequested && !ct.IsCancellationRequested)
                throw new TimeoutException($"The input monitor did not start within {_startLimit.TotalSeconds:0.#} s.", e);
            throw;
        }
    }

    /// <summary>
    /// Turns suppression off at once (a fail-safe), then on the dispatcher stops the timers, removes the hooks and the sink
    /// window and frees the process's monitor slot. Idempotent; a monitor that never started does nothing. The wait is bounded
    /// at 2 s: on a stalled dispatcher it gives up, reports a <see cref="TimeoutException"/> to the host's
    /// <see cref="DispatcherHost.Report"/> sink and returns; the teardown still runs (and frees the slot) when the dispatcher
    /// wakes, and a failure of it is reported then.
    /// </summary>
    public async Task StopAsync()
    {
        _filter.SetSuppression(false, 0);
        var stopping = StopCoreAsync();
        try
        {
            await stopping.WaitAsync(_stopLimit).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            // A stalled dispatcher: give up waiting (suppression is already off, and Windows bypasses hooks that do not
            // answer). The teardown still runs when the dispatcher wakes; a later failure of it is reported then.
            Report(new TimeoutException($"The input monitor did not stop within {_stopLimit.TotalSeconds:0.#} s; its teardown is still queued.", e));
            ReportFailure(stopping);
        }
    }

    private void ReportFailure(Task task) =>
        task.ContinueWith(static (t, state) => ((InputMonitor)state!).Report(t.Exception!.GetBaseException()), this,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private void Report(Exception error)
    {
        if (_host.Report is { } report) TimerTicks.Report(report, error);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private Task StopCoreAsync()
    {
        lock (_gate)
        {
            _generation++; // any start still in progress is stale from now on
            if (_stopping is not null) return _stopping;
            if (_dispatcher is not { } dispatcher)
            {
                // No dispatcher yet (still starting, failed, or this monitor never started): nothing was installed. The stop
                // is recorded so a start still acquiring the dispatcher installs nothing.
                Interlocked.CompareExchange(ref s_current, null, this);
                return _stopping = Task.CompletedTask;
            }
            var rehook = _rehookTimer;
            var tick = _tickTimer;
            Task stopping;
            try
            {
                stopping = dispatcher.InvokeAsync(() => TearDown(rehook, tick));
            }
            catch (Exception e)
            {
                // The dispatcher has shut down, and its thread with the hooks and the window.
                Interlocked.CompareExchange(ref s_current, null, this);
                stopping = Task.FromException(e);
            }
            _stopping = stopping;
            return stopping;
        }
    }

    // Under _gate.
    private void ThrowIfStale(long run)
    {
        if (_stopping is not null || run != _generation)
            throw new OperationCanceledException("The input monitor was stopped while starting.");
    }

    private void SetUp(long run)
    {
        lock (_gate) ThrowIfStale(run); // checked on the dispatcher too: a stop may have come while this was queued
        _native.CreateSink();
        _sinkCreated = true;
        (_keyboardHook, _mouseHook) = _native.InstallHooks(); // a failure is undone by the teardown queued behind this
        var now = _now();
        Volatile.Write(ref _lastRehook, now);
        Volatile.Write(ref _lastBeat, now);
        _running = true;
    }

    private void TearDown(Task<IDisposable>? rehook, Task<IDisposable>? tick)
    {
        _running = false;
        _filter.SetSuppression(false, 0);
        StopTimer(rehook);
        StopTimer(tick);
        Unhook(ref _keyboardHook);
        Unhook(ref _mouseHook);
        try
        {
            if (_sinkCreated) _native.DestroySink();
        }
        finally
        {
            _sinkCreated = false;
            Interlocked.CompareExchange(ref s_current, null, this);
        }
    }

    // A timer whose start has not finished yet is stopped when it does; its ticks do nothing meanwhile (_running).
    private static void StopTimer(Task<IDisposable>? timer)
    {
        if (timer is null) return;
        if (timer.IsCompletedSuccessfully) timer.Result.Dispose();
        else
            timer.ContinueWith(static t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Unhook(ref nint hook)
    {
        if (hook == 0) return;
        var old = hook;
        hook = 0;
        try
        {
            _native.Unhook(old);
        }
        catch (Exception)
        {
            // A hook that cannot be removed goes with the thread; the rest of the teardown still runs.
        }
    }

    private void RehookTick()
    {
        if (_running) Reinstall();
    }

    /// <summary>The 0.1 s tick: a requested reinstall, the missed-key-up sample (Decisions 25), then the beat.</summary>
    private void Tick()
    {
        if (!_running) return;
        try
        {
            if (Volatile.Read(ref _rehookWanted)) Reinstall();
            _filter.SampleMissedReleases(_keyState);
        }
        catch (Exception)
        {
            // GetAsyncKeyState (or the sampler) failed: input passes until the coordinator recovers.
            _filter.RequestFailOpen();
        }
        finally
        {
            Volatile.Write(ref _lastBeat, _now());
        }
    }

    /// <summary>
    /// Installs both new hooks, then removes the old ones, so input is never unwatched (Decisions 11). A failed install
    /// keeps the old hooks, requests fail-open and asks again on the next tick.
    /// </summary>
    private void Reinstall()
    {
        Volatile.Write(ref _rehookWanted, false); // cleared first, so a request made during the install asks again
        (nint Keyboard, nint Mouse) fresh;
        try
        {
            fresh = _native.InstallHooks();
        }
        catch (Exception)
        {
            Volatile.Write(ref _rehookWanted, true);
            _filter.RequestFailOpen();
            return;
        }
        var oldKeyboard = _keyboardHook;
        var oldMouse = _mouseHook;
        (_keyboardHook, _mouseHook) = fresh;
        Unhook(ref oldKeyboard);
        Unhook(ref oldMouse);
        Volatile.Write(ref _lastRehook, _now());
    }

    /// <summary>The keyboard hook's work for a decoded <c>KBDLLHOOKSTRUCT</c>. Never throws.</summary>
    internal nint Keyboard(int code, nint wParam, nint lParam, uint vk, uint flags, nuint extraInfo)
    {
        nint result = 0;
        var called = false; // the next hook's verdict, once it has one, is what Windows gets
        try
        {
            if (code < 0) return _native.CallNextHook(code, wParam, lParam);
            var down = (uint)wParam is WmKeyDown or WmSysKeyDown;
            if (_filter.OnKey(vk, down, flags, extraInfo) == HookVerdict.Swallow) return 1;
            result = _native.CallNextHook(code, wParam, lParam);
            called = true;
            _filter.AfterKeyForwarded(vk, down, result);
            return result;
        }
        catch (Exception)
        {
            _filter.RequestFailOpen();
            return called ? result : CallNextGuarded(code, wParam, lParam);
        }
    }

    /// <summary>The mouse hook's work for a decoded <c>MSLLHOOKSTRUCT</c>. Never throws.</summary>
    internal nint Mouse(int code, nint wParam, nint lParam, int x, int y, uint mouseData, uint flags, nuint extraInfo)
    {
        nint result = 0;
        var called = false;
        try
        {
            if (code < 0) return _native.CallNextHook(code, wParam, lParam);
            var message = (uint)wParam;
            if (_filter.OnMouse(message, mouseData, x, y, flags, extraInfo) == HookVerdict.Swallow) return 1;
            result = _native.CallNextHook(code, wParam, lParam);
            called = true;
            _filter.AfterMouseForwarded(message, mouseData, result);
            return result;
        }
        catch (Exception)
        {
            _filter.RequestFailOpen();
            return called ? result : CallNextGuarded(code, wParam, lParam);
        }
    }

    // The chain must still run when our own work failed: one more, guarded, call to the next hook (0 if that fails too).
    private nint CallNextGuarded(int code, nint wParam, nint lParam)
    {
        try
        {
            return _native.CallNextHook(code, wParam, lParam);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>A WM_INPUT report: counted, then relative motion from a real device goes to the filter. Never throws.</summary>
    internal void OnRawInput(uint type, long device, ushort flags, int lastX, int lastY)
    {
        Interlocked.Increment(ref _rawMessages);
        if (RawMouse.Relative(type, device, flags, lastX, lastY) is { } motion)
            _filter.OnRawMouse(motion.Device, flags, motion.Dx, motion.Dy);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT KeyboardProc(int code, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            var monitor = Volatile.Read(ref s_current);
            if (monitor is null || code < 0) return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
            var info = (KBDLLHOOKSTRUCT*)lParam.Value;
            return (LRESULT)monitor.Keyboard(code, (nint)wParam.Value, lParam.Value, info->vkCode, (uint)info->flags, info->dwExtraInfo);
        }
        catch (Exception)
        {
            return PassOn(code, wParam, lParam);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT MouseProc(int code, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            var monitor = Volatile.Read(ref s_current);
            if (monitor is null || code < 0) return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
            var info = (MSLLHOOKSTRUCT*)lParam.Value;
            return (LRESULT)monitor.Mouse(code, (nint)wParam.Value, lParam.Value, info->pt.X, info->pt.Y, info->mouseData, info->flags, info->dwExtraInfo);
        }
        catch (Exception)
        {
            return PassOn(code, wParam, lParam);
        }
    }

    // The last resort of a hook procedure: pass the event on; nothing may escape an [UnmanagedCallersOnly] method.
    private static LRESULT PassOn(int code, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            Volatile.Read(ref s_current)?._filter.RequestFailOpen();
            return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
        }
        catch (Exception)
        {
            return (LRESULT)0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT WindowProc(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (message == PInvoke.WM_INPUT && Volatile.Read(ref s_current) is { } monitor)
            {
                RAWINPUT raw;
                var size = (uint)sizeof(RAWINPUT);
                if (PInvoke.GetRawInputData(new HRAWINPUT(lParam.Value), RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, &raw, &size,
                        (uint)sizeof(RAWINPUTHEADER)) != uint.MaxValue)
                {
                    var mouse = raw.data.mouse;
                    monitor.OnRawInput(raw.header.dwType, (long)(nint)raw.header.hDevice.Value, (ushort)mouse.usFlags, mouse.lLastX, mouse.lLastY);
                }
            }
            // WM_INPUT must reach DefWindowProc too, which frees the report.
            return PInvoke.DefWindowProc(window, message, wParam, lParam);
        }
        catch (Exception)
        {
            try
            {
                Volatile.Read(ref s_current)?._filter.RequestFailOpen();
                return PInvoke.DefWindowProc(window, message, wParam, lParam);
            }
            catch (Exception)
            {
                return (LRESULT)0;
            }
        }
    }

    /// <summary>The real Win32 calls, on the dispatcher thread (the hooks' and the window's thread).</summary>
    private sealed unsafe class Win32Native : IInputMonitorNative
    {
        private const string ClassName = "DesktopMcp.InputMonitor";
        private const int ErrorClassAlreadyExists = 1410;
        private const ushort GenericDesktopPage = 1;
        private const ushort MouseUsage = 2;

        private HWND _window;
        private bool _classRegistered;
        private bool _rawRegistered;

        private static HINSTANCE Instance => new(PInvoke.GetModuleHandle((PCWSTR)null).Value);

        public void CreateSink()
        {
            try
            {
                fixed (char* name = ClassName)
                {
                    var windowClass = new WNDCLASSEXW
                    {
                        cbSize = (uint)sizeof(WNDCLASSEXW),
                        lpfnWndProc = &WindowProc,
                        hInstance = Instance,
                        lpszClassName = name,
                    };
                    // A class left registered by an earlier run in this process has the same window procedure: reuse it.
                    if (PInvoke.RegisterClassEx(&windowClass) == 0 && Marshal.GetLastPInvokeError() != ErrorClassAlreadyExists)
                        throw Failure("RegisterClassEx");
                    _classRegistered = true;
                    _window = PInvoke.CreateWindowEx(0, name, name, 0, 0, 0, 0, 0, HWND.HWND_MESSAGE, default, Instance, null);
                }
                if (_window.IsNull) throw Failure("CreateWindowEx");
                var device = new RAWINPUTDEVICE
                {
                    usUsagePage = GenericDesktopPage,
                    usUsage = MouseUsage,
                    dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK,
                    hwndTarget = _window,
                };
                if (!PInvoke.RegisterRawInputDevices(&device, 1, (uint)sizeof(RAWINPUTDEVICE))) throw Failure("RegisterRawInputDevices");
                _rawRegistered = true;
            }
            catch
            {
                DestroySink();
                throw;
            }
        }

        /// <remarks>
        /// <c>RIDEV_REMOVE</c> removes the process-wide registration for usage page 1, usage 2 (the mouse), whichever window
        /// registered it: nothing else in this process may register mouse Raw Input (a note for Task 9's windows).
        /// </remarks>
        public void DestroySink()
        {
            if (_rawRegistered)
            {
                var removed = new RAWINPUTDEVICE
                {
                    usUsagePage = GenericDesktopPage,
                    usUsage = MouseUsage,
                    dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE,
                    hwndTarget = HWND.Null,
                };
                PInvoke.RegisterRawInputDevices(&removed, 1, (uint)sizeof(RAWINPUTDEVICE));
                _rawRegistered = false;
            }
            if (!_window.IsNull)
            {
                PInvoke.DestroyWindow(_window);
                _window = HWND.Null;
            }
            if (_classRegistered)
            {
                fixed (char* name = ClassName)
                    PInvoke.UnregisterClass(name, Instance);
                _classRegistered = false;
            }
        }

        public (nint Keyboard, nint Mouse) InstallHooks()
        {
            var instance = Instance;
            var mouse = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &MouseProc, instance, 0);
            if (mouse.IsNull) throw Failure("SetWindowsHookEx(WH_MOUSE_LL)");
            var keyboard = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &KeyboardProc, instance, 0);
            if (keyboard.IsNull)
            {
                var error = Failure("SetWindowsHookEx(WH_KEYBOARD_LL)");
                PInvoke.UnhookWindowsHookEx(mouse);
                throw error;
            }
            return ((nint)keyboard.Value, (nint)mouse.Value);
        }

        public void Unhook(nint hook) => PInvoke.UnhookWindowsHookEx(new HHOOK(hook));

        public nint CallNextHook(int code, nint wParam, nint lParam) =>
            PInvoke.CallNextHookEx(HHOOK.Null, code, new WPARAM((nuint)wParam), new LPARAM(lParam)).Value;

        public short GetAsyncKeyState(int vk) => PInvoke.GetAsyncKeyState(vk);

        private static DesktopException Failure(string call) =>
            new($"The input monitor could not start: {call} failed (Win32 error {Marshal.GetLastPInvokeError()}).");
    }
}
