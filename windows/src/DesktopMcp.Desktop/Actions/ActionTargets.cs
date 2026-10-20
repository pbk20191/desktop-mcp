using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Desktop.Snapshots;
using DesktopMcp.Desktop.Tree;

namespace DesktopMcp.Desktop.Actions;

/// <summary>Where an input action lands: the point, the top-level window there and that window's name.</summary>
public sealed record ActionTarget(ScreenPoint Point, long Window, string WindowName);

/// <summary>Labels and points checked and resolved before any input is sent (P3 Decisions 1, 8, 15, 22, 39).</summary>
public interface IActionTargets
{
    /// <summary>The label's element found again and gated (Decisions 1), then checked like <see cref="Point"/>.</summary>
    Task<ActionTarget> LabelAsync(long label, CancellationToken ct);

    /// <summary>The desktop bounds check (Decisions 15) and the integrity check of the window under the point (Decisions 8).</summary>
    ActionTarget Point(ScreenPoint point);

    /// <summary>Range-checks every label against the stored snapshot (upstream tools/multi.py:43-50).</summary>
    void CheckLabels(IReadOnlyList<long> labels, string wrapper);

    /// <summary>The integrity check of the foreground window before keyboard input (Decisions 8).</summary>
    void CheckForegroundInput();
}

/// <summary>The label error texts (upstream tools/input.py:26-32, desktop/service.py:663-675; Decisions 1 and 39).</summary>
public static class LabelErrors
{
    public const string EmptyState = "Desktop state is empty. Please call Snapshot first.";

    public static string OutOfRange(long label) => $"Label {label} out of range";

    public static string NotFound(long label) => $"Failed to find element with label {label}: {OutOfRange(label)}";

    public static string Stale(long label, string type, string name, string window) =>
        $"Label {label} is stale: {type} \"{name}\" is no longer in {window}. Call Snapshot again to get current labels.";

    public static string Disabled(long label, string type, string name) => $"Label {label} ({type} \"{name}\") is disabled.";

    public static string OffScreen(long label, string type, string name) =>
        $"Label {label} ({type} \"{name}\") is off screen and could not be scrolled into view. Call Snapshot again.";

    public static string NotResponding(string window) => $"{window} is not responding; no input was sent.";

    /// <summary>The <see cref="Stale"/>, <see cref="Disabled"/>, <see cref="OffScreen"/> or <see cref="NotResponding"/> text for the element.</summary>
    public static string For(LocateFailure failure, long label, UiElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        // Decisions 39: the tree's rendering, lower-cased control type and escaped name.
        var type = UnicodeCleanup.EscapeControl(element.ControlType.ToLowerInvariant());
        var name = UnicodeCleanup.EscapeControl(element.Name);
        var window = UnicodeCleanup.EscapeControl(element.WindowName);
        return failure switch
        {
            LocateFailure.Stale => Stale(label, type, name, window),
            LocateFailure.Disabled => Disabled(label, type, name),
            LocateFailure.OffScreen => OffScreen(label, type, name),
            LocateFailure.NotResponding => NotResponding(window),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
        };
    }
}

/// <summary>
/// Resolves labels and points into <see cref="ActionTarget"/>s against the latest snapshot. Labels count interactive
/// elements, then scrollable ones (upstream desktop/service.py:663-675); a label whose element has an identity is found
/// again on the UIA worker (<see cref="UiaLabelLocator"/>), one without acts at the stored centre (Decisions 40).
/// </summary>
public sealed class ActionTargets : IActionTargets
{
    private readonly DesktopStateStore _store;
    private readonly Func<ElementIdentity, ScreenPoint, CancellationToken, Task<LocateResult>> _locate;
    private readonly IWindowSystem _windows;
    private readonly Func<ScreenRect> _virtualScreen;
    private readonly Func<int, bool> _higherIntegrity;

    public ActionTargets(DesktopMachine machine, DesktopStateStore store)
        : this(store, Locator(machine).LocateAsync, machine.Windows, machine.Displays.GetVirtualScreen, ProcessIntegrity.IsHigherThanCurrent)
    {
    }

    /// <param name="locate">The label locator (preflight F18: no label argument; the real one never needs it).</param>
    internal ActionTargets(DesktopStateStore store, Func<ElementIdentity, ScreenPoint, CancellationToken, Task<LocateResult>> locate,
        IWindowSystem windows, Func<ScreenRect> virtualScreen, Func<int, bool> higherIntegrity)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _locate = locate;
        _windows = windows;
        _virtualScreen = virtualScreen;
        _higherIntegrity = higherIntegrity;
    }

    private static UiaLabelLocator Locator(DesktopMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return new UiaLabelLocator(machine.Uia, machine.Displays, machine.Windows);
    }

    public async Task<ActionTarget> LabelAsync(long label, CancellationToken ct)
    {
        var tree = Tree();
        var element = Element(tree, label) ?? throw new ArgumentException(LabelErrors.NotFound(label));
        if (element.Identity is not { } identity) return Point(element.Center);

        LocateResult result;
        try
        {
            result = await _locate(identity, element.Center, ct).ConfigureAwait(false);
        }
        catch (UiaTimeoutException)
        {
            // The worker's locate deadline passed; UIA's own timeouts are already NotResponding inside the job.
            throw new DesktopException(LabelErrors.For(LocateFailure.NotResponding, label, element));
        }
        if (result.Failure is { } failure) throw new DesktopException(LabelErrors.For(failure, label, element));

        // Decisions 22: the point actually used. The checks are those of a point; the target is the label's window.
        Point(result.Point);
        return new ActionTarget(result.Point, identity.Window, element.WindowName);
    }

    public ActionTarget Point(ScreenPoint point)
    {
        if (MouseCoordinates.OutsideMessage(point, _virtualScreen()) is { } outside) throw new ArgumentException(outside);
        var window = _windows.WindowFromPoint(point);
        CheckIntegrity(window);
        return new ActionTarget(point, window, window == 0 ? "" : _windows.GetTitle(window));
    }

    public void CheckLabels(IReadOnlyList<long> labels, string wrapper)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var tree = Tree();
        foreach (var label in labels)
            if (Element(tree, label) is null) throw new ArgumentException($"{wrapper}: {LabelErrors.OutOfRange(label)}");
    }

    public void CheckForegroundInput() => CheckIntegrity(_windows.GetForegroundWindow());

    private TreeSnapshot Tree() => (_store.Latest ?? throw new ArgumentException(LabelErrors.EmptyState)).Tree;

    private static UiElement? Element(TreeSnapshot tree, long label)
    {
        if (label < 0) return null;
        if (label < tree.Interactive.Count) return tree.Interactive[(int)label];
        var scroll = label - tree.Interactive.Count;
        return scroll < tree.Scrollable.Count ? tree.Scrollable[(int)scroll] : null;
    }

    // Decisions 8: a window whose process runs at a higher integrity level than ours blocks input (UIPI) before any is sent.
    private void CheckIntegrity(long window)
    {
        if (window == 0) return;
        var processId = _windows.GetProcessId(window);
        if (processId != 0 && _higherIntegrity(processId))
            throw new InputBlockedException(InputBlockedException.ElevatedText(_windows.GetProcessName(processId)));
    }
}
