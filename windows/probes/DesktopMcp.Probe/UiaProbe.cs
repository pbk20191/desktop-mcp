using System.Diagnostics;
using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Uia;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace DesktopMcp.Probe;

/// <summary>
/// UI Automation through CsWin32's [GeneratedComInterface] interfaces under NativeAOT (P2 Task 1): creates the client
/// on an MTA thread, reads the root element, then the foreground window and its cached children with the traversal
/// cache request. Read-only: it never sets focus or invokes a pattern.
/// </summary>
internal static class UiaProbe
{
    public static async Task<bool> RunAsync()
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(Run()); }
            catch (Exception e) { result.SetException(e); }
        }) { IsBackground = true, Name = "uia probe" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        try
        {
            return await result.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception e)
        {
            Console.WriteLine($"uia: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            return false;
        }
    }

    private static unsafe bool Run()
    {
        var clock = Stopwatch.StartNew();
        var session = UiaSession.Create();
        var root = session.Automation.GetRootElement();
        var rootName = Take(root.get_CurrentName());
        Console.WriteLine($"uia: client created and root read in {clock.ElapsedMilliseconds} ms, root='{rootName}', " +
            $"timeouts={session.Automation.get_ConnectionTimeout()}/{session.Automation.get_TransactionTimeout()} ms");

        var foreground = PInvoke.GetForegroundWindow();
        if (foreground.IsNull)
        {
            Console.WriteLine("uia: no foreground window; skipping the cached walk");
            return true;
        }
        clock.Restart();
        var window = session.Automation.ElementFromHandleBuildCache(foreground, session.ElementRequest);
        var name = Take(window.get_CachedName());
        var children = window.BuildUpdatedCache(session.ChildrenRequest).GetCachedChildren();
        var count = children?.get_Length() ?? 0;
        var runtimeIds = 0;
        for (var i = 0; i < count; i++)
        {
            var child = children!.GetElement(i);
            _ = Take(child.get_CachedName());
            using var runtimeId = child.GetCachedPropertyValue(UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId);
            if (runtimeId.VarType == (VarEnum.VT_ARRAY | VarEnum.VT_I4)) runtimeIds++;
        }
        Console.WriteLine($"uia: foreground '{name}' ({window.get_CachedControlType()}), {count} cached children " +
            $"({runtimeIds} with a RuntimeId) in {clock.ElapsedMilliseconds} ms, nativeAot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
        return true;
    }

    private static string Take(BSTR value)
    {
        try { return value.ToString() ?? ""; }
        finally { PInvoke.SysFreeString(value); }
    }
}
