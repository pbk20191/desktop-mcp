using DesktopMcp.Core.Config;
using DesktopMcp.Core.Control;
using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Core.Processes;
using DesktopMcp.Core.Scrape;
using DesktopMcp.Core.WindowsRegistry;
using DesktopMcp.Desktop;
using DesktopMcp.Desktop.Actions;
using DesktopMcp.Desktop.Apps;
using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Desktop.Control;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Desktop.Snapshots;
using DesktopMcp.Desktop.Toasts;
using DesktopMcp.Desktop.Waiting;
using DesktopMcp.Desktop.WindowControl;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// The services the tools call. Tests replace the ones with side effects (clipboard, toasts, input) with fakes through
/// <c>with</c>. Creating the defaults touches nothing: no Windows App SDK, no desktop, no UIA thread.
/// </summary>
public sealed record ToolServices
{
    public required PowerShellRunner PowerShell { get; init; }
    public required FileSystemService FileSystem { get; init; }
    public required RegistryService Registry { get; init; }
    public required ProcessService Processes { get; init; }
    public required IClipboard Clipboard { get; init; }
    public required IToastSender Toasts { get; init; }
    public required ScrapeClient Scrape { get; init; }
    public required IDisplayProvider Displays { get; init; }
    public required IDesktopSnapshots Snapshots { get; init; }

    /// <summary>The one desktop machine Snapshot and the act tools share (one UIA worker, one hang block list).</summary>
    public required DesktopMachine Machine { get; init; }
    public required IDesktopInput Input { get; init; }
    public required IActionTargets Targets { get; init; }
    public required IForeground Foreground { get; init; }

    /// <summary>Type's and MultiEdit's read-back of the focused element's value (P3 Decisions 4).</summary>
    public required ITypeReadBack ReadBack { get; init; }

    /// <summary>The App tool's modes (switch, resize, launch, launch_executable).</summary>
    public required IAppService Apps { get; init; }

    /// <summary>WaitFor's per-condition reads (P3 Decisions 6); they never replace the snapshot store (Decisions 24).</summary>
    public required IWaitConditions WaitConditions { get; init; }

    /// <summary>
    /// Desktop ownership (spec §7): the control gate's status, leases and checkpoints. <see cref="Create"/> and
    /// <see cref="CreateDefault"/> build <see cref="Input"/> and <see cref="Ledger"/> over it.
    /// </summary>
    public required IDesktopControl Control { get; init; }

    /// <summary>
    /// Every key or button the input holds; the server releases it on shutdown (Decisions 19). In the production wiring it
    /// is <see cref="IDesktopControl.Ledger"/> and the ledger <see cref="Input"/> presses through, or a takeover or the
    /// shutdown release misses what the input holds; tests may replace it on its own.
    /// </summary>
    public required InputLedger Ledger { get; init; }
    public required TimeProvider Time { get; init; }

    /// <summary>
    /// The dispatcher thread the input monitor, indicator and flash share, started on first use; the server logs its
    /// unhandled exceptions and shuts it down after the control stops. <see cref="Create"/> uses
    /// <see cref="DispatcherHost.Shared"/>; <see cref="CreateDefault"/> uses a host of its own (never started by its
    /// disabled control) so a test server's shutdown leaves the shared one alone.
    /// </summary>
    public DispatcherHost Dispatcher { get; init; } = DispatcherHost.Shared;

    /// <summary>
    /// The services with control disabled (default settings) and no process allowlist: what the tests and the contract
    /// checks build on. Nothing starts a monitor.
    /// </summary>
    public static ToolServices CreateDefault() =>
        Create(new DisabledControl(DefaultControlSettings()), ProcessAllowlist.Everything, log: null) with { Dispatcher = new DispatcherHost() };

    /// <summary>
    /// The server's services for <paramref name="plan"/>: the desktop control its <c>[control]</c> settings describe
    /// (<see cref="ControlCoordinator.Create"/>), the process allowlist, and the input over the control's ledger and
    /// checkpoint. <paramref name="log"/> receives the desktop services' own log lines (control, indicator, flash).
    /// Cheap: nothing starts until the control is started.
    /// </summary>
    public static ToolServices Create(ServePlan plan, DesktopLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Create(ControlCoordinator.Create(plan.Control, log), new ProcessAllowlist(plan.Control.ProcessAllowlist), log);
    }

    /// <summary>The <c>[control]</c> defaults with control turned off.</summary>
    private static ControlSettings DefaultControlSettings()
    {
        var defaults = new ControlConfig();
        return new ControlSettings(false, defaults.Policy, defaults.IdleTimeoutSeconds, defaults.MouseTakeoverUnits, defaults.MouseTakeoverPixels);
    }

    private static ToolServices Create(IDesktopControl control, ProcessAllowlist allowlist, DesktopLog? log)
    {
        var machine = DesktopMachine.CreateDefault(log);
        var snapshots = new SnapshotService(machine);
        var clipboard = new SystemClipboard();
        var ledger = control.Ledger;
        var foreground = new ForegroundService();
        return new()
        {
            PowerShell = new PowerShellRunner(),
            FileSystem = new FileSystemService(),
            Registry = new RegistryService(),
            Processes = new ProcessService(),
            Clipboard = clipboard,
            Toasts = new ToastSender(),
            Scrape = new ScrapeClient(),
            Displays = new Win32Displays(),
            Snapshots = snapshots,
            Machine = machine,
            Input = new DesktopInput(ledger, clipboard, control.Checkpoint, allowlist),
            Targets = new ActionTargets(machine, snapshots.State),
            Foreground = foreground,
            ReadBack = new UiaTypeReadBack(machine),
            Apps = new AppService(machine, foreground),
            WaitConditions = new WaitConditionService(machine),
            Control = control,
            Ledger = ledger,
            Time = TimeProvider.System,
        };
    }
}
