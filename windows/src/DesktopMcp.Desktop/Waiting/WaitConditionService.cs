using DesktopMcp.Core.Snapshots;
using DesktopMcp.Core.Text;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Snapshots;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using Windows.Win32.Foundation;

namespace DesktopMcp.Desktop.Waiting;

/// <summary>One WaitFor poll: reads only what the condition needs (P3 Decisions 6).</summary>
public interface IWaitConditions
{
    /// <summary>
    /// Throws a <see cref="DesktopException"/> when no desktop the user sees is available (P2 Decisions 18); WaitFor checks
    /// it once before the first poll (Decisions 28).
    /// </summary>
    void EnsureSession();

    /// <summary>
    /// Whether the condition holds now, and upstream's detail text (tools/input.py:127-170). A UIA timeout, a dead element
    /// or a window that stops answering is "not matched" with the condition's absent text (Decisions 28); other failures
    /// propagate.
    /// </summary>
    Task<(bool Matched, string Detail)> CheckAsync(WaitCondition condition, string? text, string? windowName, CancellationToken ct);
}

/// <summary>
/// The WaitFor conditions without full snapshots (P3 Decisions 6, 24, 25, 27, 28 and 41): <c>active_window</c> probes the
/// foreground window; <c>focused_element</c> reads the focused element and its top-level window's name; the others scan the
/// windows (hang-blocked windows skipped) and run an early-exit walk of the windows Snapshot walks (excluded processes
/// skipped), stopping at the first matching element. No capture, no virtual-desktop read, no store update, no retry.
/// </summary>
public sealed class WaitConditionService : IWaitConditions
{
    private readonly IWindowScanner _windows;
    private readonly ITreeCapture _tree;
    private readonly Func<ScreenRect> _screen;
    private readonly Func<CancellationToken, Task<UiElement?>> _focused;
    private readonly Action _ensureSession;

    /// <summary>The real reads over <paramref name="machine"/>'s window catalog, tree service and UIA worker; cheap: no thread or COM.</summary>
    public WaitConditionService(DesktopMachine machine)
        : this(
            (machine ?? throw new ArgumentNullException(nameof(machine))).Catalog,
            machine.Tree,
            machine.Displays.GetVirtualScreen,
            new UiaFocusedElementReader(machine.Uia, machine.Windows, machine.Blocks, machine.Displays, Environment.GetEnvironmentVariable).ReadAsync)
    {
    }

    /// <param name="focused">The focused element as the walk would list it, or null (<see cref="UiaFocusedElementReader"/>).</param>
    /// <param name="ensureSession">The desktop session check; <see cref="DesktopSession.ThrowIfUnavailable"/> when null.</param>
    internal WaitConditionService(
        IWindowScanner windows, ITreeCapture tree, Func<ScreenRect> screen, Func<CancellationToken, Task<UiElement?>> focused,
        Action? ensureSession = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(focused);
        _windows = windows;
        _tree = tree;
        _screen = screen;
        _focused = focused;
        _ensureSession = ensureSession ?? DesktopSession.ThrowIfUnavailable;
    }

    public void EnsureSession() => _ensureSession();

    public async Task<(bool Matched, string Detail)> CheckAsync(WaitCondition condition, string? text, string? windowName, CancellationToken ct)
    {
        try
        {
            return condition switch
            {
                WaitCondition.ActiveWindow => await ActiveWindowAsync(text, windowName, ct).ConfigureAwait(false),
                WaitCondition.FocusedElement => await FocusedAsync(text, windowName, ct).ConfigureAwait(false),
                WaitCondition.TextExists => await TextAsync(text, ct).ConfigureAwait(false),
                WaitCondition.ElementExists or WaitCondition.ElementEnabled => await ElementAsync(condition, text, windowName, ct).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(condition)),
            };
        }
        catch (Exception e) when (ComErrors.IsTimeout(e) || ComErrors.IsDeadElement(e))
        {
            // Decisions 28: the next poll is the retry.
            return (false, Absent(condition, text, ""));
        }
    }

    // upstream input.py:146-153.
    private async Task<(bool, string)> ActiveWindowAsync(string? text, string? windowName, CancellationToken ct)
    {
        var expected = string.IsNullOrEmpty(windowName) ? text : windowName;
        var name = (await _windows.ScanActiveAsync(ct).ConfigureAwait(false))?.Name ?? "";
        return WaitConditions.TextMatches(name, expected) ? (true, $"active window matched {PyText.Repr(name)}") : (false, Absent(WaitCondition.ActiveWindow, text, name));
    }

    // upstream input.py:161-166: the focused element must carry the focus flag and match.
    private async Task<(bool, string)> FocusedAsync(string? text, string? windowName, CancellationToken ct)
    {
        var element = await _focused(ct).ConfigureAwait(false);
        return element is not null && element.Metadata.HasFocus && WaitConditions.ElementMatches(element, text, windowName)
            ? (true, $"focused element matched {PyText.Repr(element.Name)}")
            : (false, Absent(WaitCondition.FocusedElement, text, ""));
    }

    // upstream input.py:96-115 and 139-144: the window names first, then the elements' names, types, windows and metadata.
    private async Task<(bool, string)> TextAsync(string? text, CancellationToken ct)
    {
        var scan = await _windows.ScanAsync(ct).ConfigureAwait(false);
        IEnumerable<string> names = scan.Active is { } active ? [active.Name, .. scan.Windows.Select(w => w.Name)] : scan.Windows.Select(w => w.Name);
        if (names.Any(name => WaitConditions.TextMatches(name, text)))
            return (true, $"text {PyText.Repr(text ?? "")} appeared");
        var found = await FindAsync(scan, element =>
            WaitConditions.TextMatches(element.Name, text) || WaitConditions.TextMatches(element.ControlType, text)
            || WaitConditions.TextMatches(element.WindowName, text) || WaitConditions.MetadataTexts(element.Metadata).Any(v => WaitConditions.TextMatches(v, text)),
            ct).ConfigureAwait(false);
        return found is not null ? (true, $"text {PyText.Repr(text ?? "")} appeared") : (false, Absent(WaitCondition.TextExists, text, ""));
    }

    // upstream input.py:155-159; element_enabled adds the enabled state (Decisions 27).
    private async Task<(bool, string)> ElementAsync(WaitCondition condition, string? text, string? windowName, CancellationToken ct)
    {
        var scan = await _windows.ScanAsync(ct).ConfigureAwait(false);
        var enabledOnly = condition == WaitCondition.ElementEnabled;
        var found = await FindAsync(scan, element =>
            (!enabledOnly || element.Metadata.IsEnabled) && WaitConditions.ElementMatches(element, text, windowName), ct).ConfigureAwait(false);
        return found is not null ? (true, $"element matched {PyText.Repr(found.Name)}") : (false, Absent(condition, text, ""));
    }

    private Task<UiElement?> FindAsync(WindowScan scan, Func<UiElement, bool> match, CancellationToken ct)
    {
        var (active, others) = SnapshotService.TreeTargets(scan, null);
        return _tree.FindAsync(active, others, _screen(), match, ct);
    }

    private static string Absent(WaitCondition condition, string? text, string activeName) => condition switch
    {
        WaitCondition.TextExists => $"text {PyText.Repr(text ?? "")} was absent",
        WaitCondition.ActiveWindow => $"active window was {PyText.Repr(activeName)}",
        WaitCondition.FocusedElement => "matching focused element was absent",
        _ => "matching element was absent",
    };
}

/// <summary>The UIA reads of the focused element (WaitFor's focused_element); the real one is <see cref="UiaFocusSource"/>.</summary>
internal interface IFocusSource
{
    /// <summary>The focused element, or null.</summary>
    UiaElementData? Focused();

    /// <summary>The control-view parent, or null at the root.</summary>
    UiaElementData? Parent(UiaElementData element);

    UiaElementData ReadWindow(long window);
}

/// <summary>The focused element as the tree walk would list it, in its top-level window (P3 Decisions 6).</summary>
internal static class FocusedElement
{
    /// <summary>The most parents climbed to find an element with a native window handle.</summary>
    public const int MaxAncestors = 32;

    /// <summary>
    /// The foreground window must answer the WM_NULL check before the focused element is read (P2 Decisions 38); the
    /// focused element's parents are climbed (at most <see cref="MaxAncestors"/>) to the first with a native window handle,
    /// whose top-level window names the element. That window, when it is not the foreground window, must not be
    /// <paramref name="skip"/>ped (blocked or excluded) and must answer the check before it is read. Null when any step
    /// finds nothing or a window does not answer.
    /// </summary>
    public static UiElement? Describe(
        IFocusSource source, IWindowSystem windows, long foreground, Func<long, bool> skip, ScreenRect screen, CancellationToken ct)
    {
        if (windows.CheckResponding(foreground) != WindowResponse.Responding) return null;
        ct.ThrowIfCancellationRequested();
        if (source.Focused() is not { } focused) return null;
        var element = focused;
        for (var level = 0; element.NativeWindowHandle == 0; level++)
        {
            if (level >= MaxAncestors) return null;
            ct.ThrowIfCancellationRequested();
            if (source.Parent(element) is not { } parent) return null;
            element = parent;
        }
        var root = windows.GetRoot(element.NativeWindowHandle);
        if (root != foreground && (skip(root) || windows.CheckResponding(root) != WindowResponse.Responding)) return null;
        ct.ThrowIfCancellationRequested();
        var window = source.ReadWindow(root);
        // upstream tree/service.py:939-941 strips the title before app_name_correction.
        var name = TreeConfig.WindowName(window.Name.Trim(), window.ClassName);
        var isBrowser = TreeConfig.IsBrowser(windows.GetProcessName(windows.GetProcessId(root)));
        return TreeWalker.Describe(focused, new WindowContext(name, isBrowser, window.Bounds, screen) { Handle = root });
    }
}

/// <summary><see cref="IFocusSource"/> over a <see cref="UiaSession"/>, every element read with <see cref="UiaSession.ActionRequest"/> (preflight F11).</summary>
internal sealed class UiaFocusSource(UiaSession session) : IFocusSource
{
    public UiaElementData? Focused() =>
        session.Automation.GetFocusedElementBuildCache(session.ActionRequest) is { } element ? UiaTreeSource.ReadAction(element) : null;

    public UiaElementData? Parent(UiaElementData element)
    {
        var handle = element.Handle as Windows.Win32.UI.Accessibility.IUIAutomationElement
            ?? throw new InvalidOperationException("The element has no UIA handle.");
        var parent = session.ControlView.GetParentElementBuildCache(handle, session.ActionRequest);
        return parent is null ? null : UiaTreeSource.ReadAction(parent);
    }

    public UiaElementData ReadWindow(long window) =>
        UiaTreeSource.ReadAction(session.Automation.ElementFromHandleBuildCache((HWND)(nint)window, session.ActionRequest));
}

/// <summary>
/// The focused element read as one UIA job within <see cref="Deadline"/> (global constraints: WaitFor's focused-element
/// read 5 s). A foreground window that is blocked or belongs to an excluded process is not read; a job past its deadline
/// blocks the foreground window and its process (P2 Decisions 2) and throws <see cref="UiaTimeoutException"/>. A dead
/// element gives null; UIA's own timeout propagates (the service treats both as no match).
/// </summary>
internal sealed class UiaFocusedElementReader(
    UiaWorker<UiaSession> worker, IWindowSystem windows, HangBlockList blocks, IDisplayProvider displays, Func<string, string?> environment)
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public async Task<UiElement?> ReadAsync(CancellationToken ct)
    {
        var foreground = windows.GetForegroundWindow();
        if (foreground == 0) return null;
        var excluded = SnapshotSettings.ExcludedProcesses(environment(SnapshotSettings.ExcludeProcessesVariable));
        bool Skip(long window)
        {
            var processId = windows.GetProcessId(window);
            return blocks.IsBlocked(window, processId)
                || (windows.GetProcessName(processId) is { } name && excluded.Contains(name.ToLowerInvariant()));
        }
        if (Skip(foreground)) return null;
        var screen = displays.GetVirtualScreen();
        try
        {
            return await worker.RunAsync((session, token) =>
            {
                try
                {
                    return UiaJob.Run("UI Automation focused element read",
                        () => FocusedElement.Describe(new UiaFocusSource(session), windows, foreground, Skip, screen, token));
                }
                catch (Exception e) when (ComErrors.IsDeadElement(e))
                {
                    return null;
                }
            }, Deadline, ct).ConfigureAwait(false);
        }
        catch (UiaTimeoutException)
        {
            blocks.Block(foreground, windows.GetProcessId(foreground));
            throw;
        }
    }
}
