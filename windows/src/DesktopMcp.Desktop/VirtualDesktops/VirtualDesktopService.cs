using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using ComServiceProvider = Windows.Win32.System.Com.IServiceProvider;

namespace DesktopMcp.Desktop.VirtualDesktops;

/// <summary>Virtual desktops: which windows are on the current one, and the desktop names the Snapshot text lists.</summary>
public interface IVirtualDesktops
{
    /// <summary>The windows on the current virtual desktop, in the given order; a window that cannot be checked is kept.</summary>
    IReadOnlyList<long> OnCurrentDesktop(IReadOnlyList<long> windows);

    /// <summary>The current desktop and all desktops; <see cref="VirtualDesktopInfo.Default"/> when they cannot be read.</summary>
    (VirtualDesktopInfo Active, IReadOnlyList<VirtualDesktopInfo> All) GetDesktops();
}

/// <summary>The shell calls behind <see cref="VirtualDesktopService"/>; each may throw.</summary>
internal interface IVirtualDesktopSource
{
    bool IsOnCurrentDesktop(long window);

    /// <summary>The desktop ids in order; null where a desktop's id could not be read.</summary>
    IReadOnlyList<Guid?> DesktopIds();

    Guid CurrentDesktopId();
}

/// <summary>
/// upstream vdm/core.py:370-689. The documented IVirtualDesktopManager filters windows; the undocumented
/// IVirtualDesktopManagerInternal lists desktops, named from the registry or "Desktop N" (spec §6). Every call goes to
/// Explorer, so each is bounded by <see cref="CallTimeout"/> and fails open (spec §12: names only affect the report).
/// <para>
/// At most one shell call is in flight per service. A call waits for the previous one within its own deadline, so
/// concurrent healthy callers (spec §5 runs ungated tools such as Snapshot concurrently) all get real results. Only while
/// a call is abandoned (its caller gave up at the deadline and it is still blocked in Explorer) does a new call return its
/// fallback at once, without calling the shell or blocking another thread-pool thread.
/// </para>
/// <para>
/// If Explorer hangs for good rather than dying, the service stays degraded: it returns the fallbacks, holding the one
/// pool thread of the abandoned call, until Explorer answers or restarts. The gate is per instance, so the service must
/// be a singleton (one instance shared by every caller) for the one-call bound to hold process-wide.
/// </para>
/// </summary>
public sealed class VirtualDesktopService : IVirtualDesktops
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    private const string DesktopsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops";

    private readonly IVirtualDesktopSource _source;
    private readonly Func<Guid, string?> _registryName;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _abandoned;

    public VirtualDesktopService() : this(new ShellVirtualDesktops(), ReadRegistryName, CallTimeout)
    {
    }

    internal VirtualDesktopService(IVirtualDesktopSource source, Func<Guid, string?> registryName, TimeSpan timeout)
    {
        _source = source;
        _registryName = registryName;
        _timeout = timeout;
    }

    /// <summary>True while a shell call runs, including one abandoned after its deadline.</summary>
    internal bool CallInFlight => _gate.CurrentCount == 0 || Volatile.Read(ref _abandoned) is not null;

    /// <summary>True while a call whose caller gave up is still running; new calls fall back at once.</summary>
    internal bool CallAbandoned => Volatile.Read(ref _abandoned) is not null;

    public IReadOnlyList<long> OnCurrentDesktop(IReadOnlyList<long> windows) =>
        Bounded(() => windows.Where(window =>
        {
            try { return _source.IsOnCurrentDesktop(window); }
            catch (Exception) { return true; } // upstream vdm/core.py:415-420: fail open
        }).ToArray(), windows);

    public (VirtualDesktopInfo Active, IReadOnlyList<VirtualDesktopInfo> All) GetDesktops() =>
        Bounded(ReadDesktops, (VirtualDesktopInfo.Default, [VirtualDesktopInfo.Default]));

    /// <summary>The id as upstream prints it: str() of a comtypes GUID, "{...}" upper case.</summary>
    internal static string Format(Guid id) => id.ToString("B").ToUpperInvariant();

    // upstream vdm/core.py:621-689.
    private (VirtualDesktopInfo, IReadOnlyList<VirtualDesktopInfo>) ReadDesktops()
    {
        var ids = _source.DesktopIds();
        var all = new List<VirtualDesktopInfo>();
        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i] is not { } id) continue;
            var name = RegistryName(id);
            all.Add(new VirtualDesktopInfo(Format(id), string.IsNullOrEmpty(name) ? $"Desktop {i + 1}" : name));
        }
        var current = Format(_source.CurrentDesktopId());
        var active = all.FirstOrDefault(desktop => desktop.Id == current) ?? new VirtualDesktopInfo(current, "Unknown");
        return (active, all);
    }

    // upstream vdm/core.py:434-447: an unreadable name (SecurityException, IOException, ...) falls back to "Desktop N"
    // for that desktop only.
    private string? RegistryName(Guid id)
    {
        try { return _registryName(id); }
        catch (Exception) { return null; }
    }

    private static string? ReadRegistryName(Guid id)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{DesktopsKey}\{Format(id)}");
        return key?.GetValue("Name") as string;
    }

    // Preflight F3 as ruled in the Task 9 fix round: one call in flight at most; wait for the gate within the deadline;
    // fall back at once only while a call is abandoned.
    private T Bounded<T>(Func<T> read, T fallback)
    {
        if (CallAbandoned) return fallback;
        var start = Stopwatch.GetTimestamp();
        if (!_gate.Wait(_timeout)) return fallback;
        Task<T> task;
        try
        {
            task = Task.Run(read);
        }
        catch
        {
            _gate.Release();
            throw;
        }
        // Runs when the call finishes, however late: clear this call's abandonment, then let the next call in.
        task.ContinueWith(
            finished =>
            {
                Interlocked.CompareExchange(ref _abandoned, null, finished);
                _gate.Release();
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        var remaining = _timeout - Stopwatch.GetElapsedTime(start);
        try
        {
            if (task.Wait(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)) return task.Result;
        }
        catch (AggregateException)
        {
            return fallback;
        }

        // Give up on the still-running call. If it finished after the wait but before the flag was set, its continuation
        // may already have run; IsCompleted is true by then, so clear the flag here instead.
        Interlocked.CompareExchange(ref _abandoned, task, null);
        if (task.IsCompleted) Interlocked.CompareExchange(ref _abandoned, null, task);
        return fallback;
    }
}

/// <summary>The real shell calls (spec §6). COM objects are created on the calling thread-pool (MTA) thread.</summary>
internal sealed unsafe class ShellVirtualDesktops : IVirtualDesktopSource
{
    private readonly int _build;

    // Not a Lazy: a failed creation is retried on the next call, and a manager whose Explorer died is dropped.
    private IVirtualDesktopManager? _manager;

    public ShellVirtualDesktops() : this(Environment.OSVersion.Version.Build)
    {
    }

    /// <summary>Picks the internal manager declaration for <paramref name="build"/> (a test passes another build's).</summary>
    internal ShellVirtualDesktops(int build) => _build = build;

    public bool IsOnCurrentDesktop(long window)
    {
        var manager = _manager ??= VirtualDesktopManager.CreateInstance<IVirtualDesktopManager>();
        try
        {
            manager.IsWindowOnCurrentVirtualDesktop((HWND)(nint)window, out var onCurrent);
            return onCurrent;
        }
        catch (COMException error) when (ComErrors.IsDeadElement(error))
        {
            _manager = null; // Explorer restarted; the next call connects to the new one
            throw;
        }
    }

    public IReadOnlyList<Guid?> DesktopIds() => WithInternalManager((current, desktops) =>
    {
        Marshal.ThrowExceptionForHR(desktops(out var raw));
        var array = Wrap<IObjectArray>(raw, "GetDesktops returned no array.");
        array.GetCount(out var count);
        var iid = typeof(IVirtualDesktop).GUID;
        var ids = new List<Guid?>();
        for (uint i = 0; i < count; i++)
        {
            try
            {
                array.GetAt(i, &iid, out var item);
                ids.Add(ReadId(item));
            }
            catch (Exception)
            {
                ids.Add(null); // upstream vdm/core.py:659-661: a desktop that cannot be read is skipped
            }
        }
        return ids;
    });

    public Guid CurrentDesktopId() => WithInternalManager((current, _) =>
    {
        Marshal.ThrowExceptionForHR(current(out var raw));
        return ReadId(raw);
    });

    private delegate int GetObject(out void* result);

    private static Guid ReadId(void* raw)
    {
        var desktop = Wrap<IVirtualDesktop>(raw, "The virtual desktop is null.");
        Marshal.ThrowExceptionForHR(desktop.GetID(out var id));
        return id;
    }

    // upstream vdm/core.py:394-406: ImmersiveShell's IServiceProvider hands out the internal manager. On a build whose
    // IID differs, QueryService throws, or (as on 26220 with the 22621 IID) returns a pointer whose QueryInterface in
    // Wrap fails with E_NOINTERFACE; either way no slot is called and the service falls back.
    private T WithInternalManager<T>(Func<GetObject, GetObject, T> use)
    {
        var shell = VirtualDesktopIds.ImmersiveShell;
        PInvoke.CoCreateInstance<ComServiceProvider>(shell, null, CLSCTX.CLSCTX_LOCAL_SERVER, out var provider).ThrowOnFailure();
        var service = VirtualDesktopIds.VirtualDesktopManagerInternalService;
        var iid = VirtualDesktopIds.ManagerInternal(_build);
        provider.QueryService(&service, &iid, out var raw);
        const string NoManager = "QueryService returned no virtual desktop manager.";
        if (_build >= 26100)
        {
            var manager = Wrap<IVirtualDesktopManagerInternal26100>(raw, NoManager);
            return use(manager.GetCurrentDesktop, manager.GetDesktops);
        }
        var older = Wrap<IVirtualDesktopManagerInternal22621>(raw, NoManager);
        return use(older.GetCurrentDesktop, older.GetDesktops);
    }

    /// <summary>
    /// Wraps an out pointer and releases the caller's reference to it, also when the wrapper's QueryInterface for
    /// <typeparamref name="T"/> fails (InvalidCastException on a build whose IID differs).
    /// </summary>
    private static T Wrap<T>(void* raw, string whenNull) where T : class
    {
        if (raw is null) throw new InvalidOperationException(whenNull);
        try
        {
            return ComInterfaceMarshaller<T>.ConvertToManaged(raw) ?? throw new InvalidOperationException(whenNull);
        }
        finally
        {
            ComInterfaceMarshaller<T>.Free(raw);
        }
    }
}
