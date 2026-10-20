using System.Runtime.InteropServices.Marshalling;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace DesktopMcp.Desktop.Actions;

/// <summary>What the label locator reads (P3 Decisions 1-3); the real one is <see cref="UiaElementSource"/>.</summary>
internal interface IElementSource
{
    /// <summary>The WM_NULL check of the top-level window (Decisions 38); a Win32 call, not UIA.</summary>
    WindowResponse CheckWindow(long window);

    /// <summary>IsWindow; a Win32 call, not UIA.</summary>
    bool WindowExists(long window);

    /// <summary>The top-level window (GA_ROOT) under the point, or 0; a Win32 call, not UIA.</summary>
    long RootAt(ScreenPoint point);

    /// <summary>
    /// Whether <paramref name="root"/> is a top-level window owned by <paramref name="window"/> (GA_ROOTOWNER) and run by
    /// its GUI thread, such as a menu or a combo box's drop-down (Decisions 42); Win32 calls, not UIA.
    /// </summary>
    bool IsSameThreadOwnedRoot(long root, long window);

    /// <summary>
    /// The element under the point, or null when there is none or the point's top-level window is neither
    /// <paramref name="window"/> (whose WM_NULL check the caller has made) nor a popup it owns on its GUI thread.
    /// </summary>
    UiaElementData? ElementAt(ScreenPoint point, long window);

    /// <summary>The control-view parent, or null at the root.</summary>
    UiaElementData? Parent(UiaElementData element);

    UiaElementData ReadWindow(long window);

    /// <summary>The control-view children (at most <see cref="UiaTreeSource.MaxChildren"/>); <paramref name="capped"/> when more exist.</summary>
    IReadOnlyList<UiaElementData> Children(UiaElementData parent, out bool capped);

    /// <summary>ScrollItemPattern.ScrollIntoView; false without the pattern.</summary>
    bool ScrollIntoView(UiaElementData element);

    /// <summary>The element read again (after a scroll).</summary>
    UiaElementData Refresh(UiaElementData element);

    /// <summary>Whether the element supports ScrollItemPattern.</summary>
    bool CanScrollIntoView(UiaElementData element);
}

/// <summary>Why a label's element cannot be acted on (public for the test theories, preflight F5).</summary>
public enum LocateFailure
{
    Stale,
    Disabled,
    OffScreen,
    NotResponding,

    /// <summary>Another window covers the action point (Decisions 42); <see cref="ActionTargets"/> reports it, the locator never does.</summary>
    Covered,
}

/// <summary>The found element and the point to act on, or why there is none (public for the tests, preflight F5).</summary>
public readonly record struct LocateResult
{
    internal LocateResult(UiaElementData? element, ScreenPoint point, LocateFailure? failure)
    {
        Element = element;
        Point = point;
        Failure = failure;
    }

    internal UiaElementData? Element { get; }
    public ScreenPoint Point { get; }
    public LocateFailure? Failure { get; }

    internal static LocateResult Failed(LocateFailure failure) => new(null, default, failure);
}

/// <summary>
/// Finds a label's element again and gates it (P3 Decisions 1-3; upstream desktop/service.py:663-697 returns the stored
/// centre). By RuntimeId under the stored point and up to <see cref="MaxAncestors"/> ancestors, else by a bounded walk of
/// the label's top-level window; then enabled, on a monitor (scrolled into view when it can be), and the action point.
/// </summary>
internal sealed class LabelLocator(IElementSource source, IReadOnlyList<ScreenRect> monitors, ScreenRect windowBounds, TimeProvider? time = null)
{
    public const int MaxAncestors = 32;
    public const int MaxVisited = 2000;

    /// <summary>
    /// How long the walk may run, from the start of <see cref="Locate"/>, before it stops as stale: inside the 5 s locate
    /// deadline, so a slow but responsive window ends in a result instead of the worker's timeout.
    /// </summary>
    public static readonly TimeSpan WalkBudget = TimeSpan.FromSeconds(4);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public LocateResult Locate(ElementIdentity identity, ScreenPoint storedCenter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var started = _time.GetUtcNow();
        if (!source.WindowExists(identity.Window)) return LocateResult.Failed(LocateFailure.Stale);
        if (!Responds(identity.Window)) return LocateResult.Failed(LocateFailure.NotResponding);

        ct.ThrowIfCancellationRequested();
        var element = FindUnderPoint(identity, storedCenter, ct);
        if (element is null)
        {
            var (found, failure) = Walk(identity, started, ct);
            if (failure is { } f) return LocateResult.Failed(f);
            element = found!;
        }
        return Gate(element, ct);
    }

    private bool Responds(long window) => source.CheckWindow(window) == WindowResponse.Responding;

    private UiaElementData? FindUnderPoint(ElementIdentity identity, ScreenPoint point, CancellationToken ct)
    {
        // The point may now lie over another application's window: no UIA call goes into it (no ElementFromPoint, no
        // parent climb); the bounded walk of the label's own window follows. A popup the window owns on its own GUI thread
        // (whose WM_NULL check the caller's check of the window stands for) uses this step too (Decisions 42).
        var root = source.RootAt(point);
        if (root != identity.Window && !source.IsSameThreadOwnedRoot(root, identity.Window)) return null;
        ct.ThrowIfCancellationRequested();
        var element = source.ElementAt(point, identity.Window);
        for (var level = 0; element is not null; level++)
        {
            if (element.RuntimeId == identity.RuntimeId) return element;
            if (level >= MaxAncestors) break;
            ct.ThrowIfCancellationRequested();
            element = source.Parent(element);
        }
        return null;
    }

    // Decisions 2: explicit stack, depth 256, at most MaxVisited elements, the WM_NULL check before each children read; a
    // walk past WalkBudget is stale (the window answers but is too slow to search in full).
    private (UiaElementData? Element, LocateFailure? Failure) Walk(ElementIdentity identity, DateTimeOffset started, CancellationToken ct)
    {
        var root = source.ReadWindow(identity.Window);
        var canMatch = identity.AutomationId.Length > 0 || identity.Name.Length > 0;
        var stack = new Stack<(UiaElementData Element, int Depth)>();
        stack.Push((root, 0));
        UiaElementData? match = null;
        var matches = 0;
        var visited = 0;
        var cut = false;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (_time.GetUtcNow() - started >= WalkBudget) return (null, LocateFailure.Stale);
            if (visited >= MaxVisited)
            {
                cut = true;
                break;
            }
            var (element, depth) = stack.Pop();
            visited++;
            if (element.RuntimeId == identity.RuntimeId) return (element, null);
            if (canMatch && SameIdentity(element, identity))
            {
                // A second match settles it: the identity is ambiguous.
                if (++matches > 1) break;
                match = element;
            }
            if (depth >= TreeWalker.MaxDepth)
            {
                // Its children are not read, so the walk is not complete.
                cut = true;
                continue;
            }
            if (!Responds(identity.Window)) return (null, LocateFailure.NotResponding);
            var children = source.Children(element, out var capped);
            cut |= capped;
            for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
        }
        return matches == 1 && !cut ? (match, null) : (null, LocateFailure.Stale);
    }

    // ElementIdentity.Name is the raw UIA name trimmed (TreeWalker.Identity), never the display name.
    private static bool SameIdentity(UiaElementData element, ElementIdentity identity) =>
        element.ControlType == identity.ControlTypeId
        && element.AutomationId == identity.AutomationId
        && element.Name.Trim() == identity.Name;

    // Decisions 1 and 3.
    private LocateResult Gate(UiaElementData element, CancellationToken ct)
    {
        if (!element.IsEnabled) return new LocateResult(element, default, LocateFailure.Disabled);
        // IsOffscreen alone never fails a label (terminator issue #473); it only asks for a scroll.
        if ((IsOff(element) || element.IsOffscreen) && source.CanScrollIntoView(element))
        {
            ct.ThrowIfCancellationRequested();
            if (source.ScrollIntoView(element))
            {
                ct.ThrowIfCancellationRequested();
                element = source.Refresh(element);
            }
        }
        if (IsOff(element)) return new LocateResult(element, default, LocateFailure.OffScreen);
        return ActionPoint(element) is { } point
            ? new LocateResult(element, point, null)
            : new LocateResult(element, default, LocateFailure.OffScreen);
    }

    private bool IsOff(UiaElementData element) =>
        element.Bounds.IsEmpty || !monitors.Any(m => ScreenRect.Clip(element.Bounds, m) is not null);

    // The cached ClickablePoint when inside the bounds and on a monitor, else the centre of bounds ∩ window ∩ first monitor.
    private ScreenPoint? ActionPoint(UiaElementData element)
    {
        if (element.ClickablePoint is { } clickable && element.Bounds.Contains(clickable) && monitors.Any(m => m.Contains(clickable)))
            return clickable;
        // An unreadable window rectangle (empty) does not clip.
        var inWindow = windowBounds.IsEmpty ? element.Bounds : ScreenRect.Clip(element.Bounds, windowBounds);
        if (inWindow is not { } clipped) return null;
        foreach (var monitor in monitors)
            if (ScreenRect.Clip(clipped, monitor) is { } visible) return visible.Center;
        return null;
    }
}

/// <summary>
/// <see cref="IElementSource"/> over a <see cref="UiaSession"/>, every element read with <see cref="UiaSession.ActionRequest"/>.
/// Runs only on the UIA worker thread. Dead elements and UIA timeouts propagate raw (the caller classifies them).
/// </summary>
internal sealed unsafe class UiaElementSource(UiaSession session, IWindowSystem windows) : IElementSource
{
    public WindowResponse CheckWindow(long window) => Win32WindowSystem.CheckResponding(window);

    public bool WindowExists(long window) => PInvoke.IsWindow((HWND)(nint)window);

    public long RootAt(ScreenPoint point) => windows.WindowFromPoint(point);

    public bool IsSameThreadOwnedRoot(long root, long window) => root != window && LabelWindows.IsLabelWindow(windows, root, window);

    public UiaElementData? ElementAt(ScreenPoint point, long window)
    {
        // The root is checked again here (cheap; the caller checked it too), so no UIA call goes into another
        // application's window. The WM_NULL check of the label's window (Decisions 38) is the caller's, made just before.
        if (!LabelWindows.IsLabelWindow(windows, RootAt(point), window)) return null;
        var element = session.Automation.ElementFromPointBuildCache(new System.Drawing.Point(point.X, point.Y), session.ActionRequest);
        return element is null ? null : UiaTreeSource.ReadAction(element);
    }

    public UiaElementData? Parent(UiaElementData element) => UiaActionReads.Parent(session, element);

    public UiaElementData ReadWindow(long window) => UiaActionReads.ReadWindow(session, window);

    public IReadOnlyList<UiaElementData> Children(UiaElementData parent, out bool capped) => UiaTreeSource.ReadSiblings(
        Element(parent),
        element => session.ControlView.GetFirstChildElementBuildCache(element, session.ActionRequest),
        child => session.ControlView.GetNextSiblingElementBuildCache(child, session.ActionRequest),
        UiaTreeSource.ReadAction,
        UiaTreeSource.MaxChildren,
        out capped);

    public bool CanScrollIntoView(UiaElementData element) =>
        UiaValues.CachedBool(Element(element), UIA_PROPERTY_ID.UIA_IsScrollItemPatternAvailablePropertyId) ?? false;

    public bool ScrollIntoView(UiaElementData element)
    {
        var iid = typeof(IUIAutomationScrollItemPattern).GUID;
        Element(element).GetCachedPatternAs(UIA_PATTERN_ID.UIA_ScrollItemPatternId, &iid, out var raw);
        if (raw is null) return false;
        var pattern = ComInterfaceMarshaller<IUIAutomationScrollItemPattern>.ConvertToManaged(raw)!;
        ComInterfaceMarshaller<IUIAutomationScrollItemPattern>.Free(raw);
        pattern.ScrollIntoView();
        return true;
    }

    public UiaElementData Refresh(UiaElementData element) =>
        UiaTreeSource.ReadAction(Element(element).BuildUpdatedCache(session.ActionRequest));

    private static IUIAutomationElement Element(UiaElementData data) => UiaActionReads.Element(data);
}

/// <summary>
/// Runs <see cref="LabelLocator"/> as one job on the UIA worker within <see cref="Deadline"/> (global constraints:
/// bounded blocking). A dead element anywhere is <see cref="LocateFailure.Stale"/>; UIA's own timeout is
/// <see cref="LocateFailure.NotResponding"/>; a missed deadline throws the worker's <see cref="UiaTimeoutException"/>.
/// </summary>
internal sealed class UiaLabelLocator(UiaWorker<UiaSession> worker, IDisplayProvider displays, IWindowSystem windows)
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public Task<LocateResult> LocateAsync(ElementIdentity identity, ScreenPoint storedCenter, CancellationToken ct)
    {
        // Win32 reads that never message another process: done here, not on the worker.
        var monitors = displays.GetDisplays().Select(d => d.Bounds).ToArray();
        var windowBounds = windows.GetBounds(identity.Window);
        return worker.RunAsync((session, token) =>
        {
            try
            {
                return UiaJob.Run("UI Automation label lookup", () =>
                    new LabelLocator(new UiaElementSource(session, windows), monitors, windowBounds).Locate(identity, storedCenter, token));
            }
            catch (Exception e) when (ComErrors.IsDeadElement(e))
            {
                return LocateResult.Failed(LocateFailure.Stale);
            }
            catch (Exception e) when (ComErrors.IsTimeout(e))
            {
                return LocateResult.Failed(LocateFailure.NotResponding);
            }
        }, Deadline, ct);
    }
}
