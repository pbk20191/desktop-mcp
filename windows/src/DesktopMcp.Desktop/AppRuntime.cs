using System.Runtime.InteropServices;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using Release = Microsoft.WindowsAppSDK.Release;
using RuntimeVersion = Microsoft.WindowsAppSDK.Runtime.Version;

namespace DesktopMcp.Desktop;

/// <summary>Brings the framework-dependent Windows App SDK runtime into this unpackaged process.</summary>
public static class AppRuntime
{
    private static readonly Lock Gate = new();
    private static bool _initialized;

    /// <summary>
    /// Initialises the Windows App SDK runtime for this unpackaged process (idempotent, thread-safe).
    /// Throws <see cref="InvalidOperationException"/> with an install hint when the runtime is missing.
    /// </summary>
    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
                return;
            if (!Bootstrap.TryInitialize(Release.MajorMinor, Release.VersionTag, new PackageVersion(RuntimeVersion.UInt64), Bootstrap.InitializeOptions.None, out var hresult))
            {
                throw new InvalidOperationException(
                    $"The Windows App SDK runtime {RuntimeVersion.DotQuadString} ({Release.Major}.{Release.Minor}) is not installed. " +
                    $"Install it from https://aka.ms/windowsappsdk/{Release.Major}.{Release.Minor}/latest/windowsappruntimeinstall-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}.exe " +
                    $"(HRESULT 0x{hresult:X8}).");
            }
            _initialized = true;
        }
    }
}
