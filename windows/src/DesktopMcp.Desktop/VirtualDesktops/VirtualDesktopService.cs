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
/// At most one shell call is in flight per service: while one is running, including one abandoned after its deadline
/// because Explorer hangs, a new call returns its fallback at once instead of blocking another thread-pool thread.
/// </summary>
public sealed class VirtualDesktopService : IVirtualDesktops
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    private const string DesktopsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops";

    private readonly IVirtualDesktopSource _source;
    private readonly Func<Guid, string?> _registryName;
    private readonly TimeSpan _timeout;
    private int _inFlight;

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
    internal bool CallInFlight => Volatile.Read(ref _inFlight) != 0;

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
            var name = _registryName(id);
            all.Add(new VirtualDesktopInfo(Format(id), string.IsNullOrEmpty(name) ? $"Desktop {i + 1}" : name));
        }
        var current = Format(_source.CurrentDesktopId());
        var active = all.FirstOrDefault(desktop => desktop.Id == current) ?? new VirtualDesktopInfo(current, "Unknown");
        return (active, all);
    }

    // upstream vdm/core.py:434-447.
    private static string? ReadRegistryName(Guid id)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{DesktopsKey}\{Format(id)}");
        return key?.GetValue("Name") as string;
    }

    // Preflight F3: one call in flight at most, so a hung Explorer holds one pool thread per service, not two per Snapshot.
    private T Bounded<T>(Func<T> read, T fallback)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return fallback;
        var task = Task.Run(() =>
        {
            try { return read(); }
            finally { Volatile.Write(ref _inFlight, 0); }
        });
        try
        {
            return task.Wait(_timeout) ? task.Result : fallback;
        }
        catch (AggregateException)
        {
            return fallback;
        }
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
