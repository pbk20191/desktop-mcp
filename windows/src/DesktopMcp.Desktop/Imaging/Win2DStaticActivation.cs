using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Interop;

namespace DesktopMcp.Desktop.Imaging;

/// <summary>
/// Routes CsWinRT activation of <c>Microsoft.Graphics.Canvas.*</c> classes to Win2D linked statically into
/// the NativeAOT exe (windows/build/Win2DStatic.targets), instead of loading Microsoft.Graphics.Canvas.dll.
/// Active only when the publish sets the <see cref="Switch"/> feature switch; JIT builds never install it and
/// keep the default activation of the package's DLL.
/// </summary>
internal static unsafe partial class Win2DStaticActivation
{
    /// <summary>Set (Trim="true") by Win2DStatic.targets for NativeAOT publishes that link the static libraries.</summary>
    internal const string Switch = "DesktopMcp.Desktop.Win2DStatic";

    private const string CanvasPrefix = "Microsoft.Graphics.Canvas.";

    private static readonly Lock Gate = new();
    private static bool _installed;

    /// <summary>True when Win2D is linked into this exe (constant-folded by ILC from the feature switch).</summary>
    [FeatureSwitchDefinition(Switch)]
    internal static bool IsLinked => AppContext.TryGetSwitch(Switch, out var linked) && linked;

    /// <summary>Installs the activation handler once (idempotent, thread-safe); a no-op unless <see cref="IsLinked"/>.</summary>
    internal static void Install()
    {
        if (!IsLinked)
            return;
        lock (Gate)
        {
            if (_installed)
                return;
            var previous = WinRT.ActivationFactory.ActivationHandler;
            WinRT.ActivationFactory.ActivationHandler = (typeName, iid) =>
                typeName.StartsWith(CanvasPrefix, StringComparison.Ordinal) ? Activate(typeName, iid)
                : previous is null ? IntPtr.Zero
                : previous(typeName, iid);
            _installed = true;
        }
    }

    /// <summary>Gets the class factory from the linked shim and returns it as the requested interface (CsWinRT owns the reference).</summary>
    private static IntPtr Activate(string typeName, Guid iid)
    {
        IntPtr hstring;
        fixed (char* chars = typeName)
            Marshal.ThrowExceptionForHR(Native.WindowsCreateString(chars, (uint)typeName.Length, &hstring));
        IntPtr factory;
        int hr;
        try { hr = DllGetActivationFactory(hstring, &factory); }
        finally { Native.WindowsDeleteString(hstring); }
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory, in iid, out var result));
            return result;
        }
        finally { Marshal.Release(factory); }
    }

    /// <summary>Exported by win2dmod.lib; bound statically through DirectPInvoke="Microsoft.Graphics.Canvas".</summary>
    [LibraryImport("Microsoft.Graphics.Canvas", EntryPoint = "Win2DStatic_DllGetActivationFactory")]
    private static partial int DllGetActivationFactory(IntPtr activatableClassId, IntPtr* factory);
}
