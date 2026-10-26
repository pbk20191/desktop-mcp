using DesktopMcp.Desktop.Tree;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace DesktopMcp.Desktop.Uia;

/// <summary>
/// The element reads the label locator and WaitFor's focused-element read share, each with
/// <see cref="UiaSession.ActionRequest"/> (which also caches the native window handle). Runs only on the UIA worker thread;
/// dead elements and UIA timeouts propagate raw.
/// </summary>
internal static class UiaActionReads
{
    /// <summary>The top-level window's element.</summary>
    public static UiaElementData ReadWindow(UiaSession session, long window) =>
        UiaTreeSource.ReadAction(session.Automation.ElementFromHandleBuildCache((HWND)(nint)window, session.ActionRequest));

    /// <summary>The control-view parent, or null at the root.</summary>
    public static UiaElementData? Parent(UiaSession session, UiaElementData element)
    {
        var parent = session.ControlView.GetParentElementBuildCache(Element(element), session.ActionRequest);
        return parent is null ? null : UiaTreeSource.ReadAction(parent);
    }

    /// <summary>The COM element behind <paramref name="data"/>.</summary>
    public static IUIAutomationElement Element(UiaElementData data) =>
        data.Handle as IUIAutomationElement ?? throw new InvalidOperationException("The element has no UIA handle.");
}
