using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Core.Processes;
using DesktopMcp.Core.Scrape;
using DesktopMcp.Core.WindowsRegistry;
using DesktopMcp.Desktop;
using DesktopMcp.Desktop.Actions;
using DesktopMcp.Desktop.Apps;
using DesktopMcp.Desktop.ClipboardAccess;
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
    /// Every key or button the input holds; the server releases it on shutdown (Decisions 19). It must be the ledger
    /// <see cref="Input"/> presses through, or the shutdown release misses what the input holds: replace both together.
    /// </summary>
    public required InputLedger Ledger { get; init; }
    public required DesktopInputLock InputLock { get; init; }
    public required TimeProvider Time { get; init; }

    public static ToolServices CreateDefault()
    {
        var machine = DesktopMachine.CreateDefault();
        var snapshots = new SnapshotService(machine);
        var clipboard = new SystemClipboard();
        var ledger = new InputLedger();
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
            Input = new DesktopInput(ledger, clipboard, NoCheckpoint.Instance),
            Targets = new ActionTargets(machine, snapshots.State),
            Foreground = foreground,
            ReadBack = new UiaTypeReadBack(machine),
            Apps = new AppService(machine, foreground),
            WaitConditions = new WaitConditionService(machine),
            Ledger = ledger,
            InputLock = new DesktopInputLock(),
            Time = TimeProvider.System,
        };
    }
}
