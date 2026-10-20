using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Overlay;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop;

/// <summary>
/// The stateful desktop services that Snapshot and the act tools share (spec §5: UIA jobs one at a time): one UIA worker,
/// one hang block list, one window catalog, one virtual desktop service. The server creates one for its lifetime.
/// </summary>
public sealed class DesktopMachine
{
    private DesktopMachine(UiaWorker<UiaSession> uia, HangBlockList blocks, IWindowSystem windows, VirtualDesktopService desktops,
        IDisplayProvider displays, ICaptureFlash flash)
    {
        Uia = uia;
        Blocks = blocks;
        Windows = windows;
        Desktops = desktops;
        Displays = displays;
        Flash = flash;
        Catalog = new WindowCatalog(windows, new UiaWindowProbe(uia), desktops, blocks, Environment.ProcessId, Task.Delay);
        Tree = new TreeService(windows, new UiaWindowWalker(uia), blocks, Environment.GetEnvironmentVariable, Task.Delay);
    }

    internal UiaWorker<UiaSession> Uia { get; }
    internal HangBlockList Blocks { get; }
    internal IWindowSystem Windows { get; }
    internal VirtualDesktopService Desktops { get; }
    internal IDisplayProvider Displays { get; }
    internal WindowCatalog Catalog { get; }
    internal TreeService Tree { get; }

    /// <summary>The flash shown after every screenshot (Decisions 27); the snapshot service shows it.</summary>
    public ICaptureFlash Flash { get; }

    /// <summary>The dispatcher the real capture flash runs on (null when the machine was built with a supplied flash).</summary>
    public DispatcherHost? Dispatcher { get; private init; }

    /// <summary>
    /// The real machine. Cheap: it starts no thread and touches no COM, UIA, desktop or Windows App SDK (the UIA worker
    /// starts its thread on the first job). <paramref name="log"/> receives the capture flash's failures.
    /// </summary>
    public static DesktopMachine CreateDefault(DesktopLog? log = null) => CreateDefault(DispatcherHost.Shared, log);

    /// <summary>
    /// The real machine whose capture flash runs on <paramref name="dispatcher"/> in place of <see cref="DispatcherHost.Shared"/>
    /// (a server's private host, so a test server never starts the shared one).
    /// </summary>
    public static DesktopMachine CreateDefault(DispatcherHost dispatcher, DesktopLog? log)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return Create(displays => CaptureFlash.CreateDefault(dispatcher, displays.GetVirtualScreen, log), dispatcher);
    }

    /// <summary>The real machine with <paramref name="flash"/> in place of the real capture flash (tests).</summary>
    internal static DesktopMachine CreateDefault(ICaptureFlash flash)
    {
        ArgumentNullException.ThrowIfNull(flash);
        return Create(_ => flash, dispatcher: null);
    }

    private static DesktopMachine Create(Func<IDisplayProvider, ICaptureFlash> flash, DispatcherHost? dispatcher)
    {
        var displays = new Win32Displays();
        return new(
            new UiaWorker<UiaSession>(UiaSession.Create),
            new HangBlockList(TimeProvider.System, HangBlockList.DefaultDuration),
            new Win32WindowSystem(),
            new VirtualDesktopService(),
            displays,
            flash(displays))
        {
            Dispatcher = dispatcher,
        };
    }
}
