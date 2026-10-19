using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.Packaging.Appx;
using Framework = Microsoft.WindowsAppSDK.Runtime.Packages.Framework;
using Release = Microsoft.WindowsAppSDK.Release;
using RuntimeVersion = Microsoft.WindowsAppSDK.Runtime.Version;

namespace DesktopMcp.Desktop;

/// <summary>
/// Brings the framework-dependent Windows App SDK runtime into this unpackaged process through the OS
/// Dynamic Dependencies API (kernelbase, Windows 11 22H2+), without the SDK's bootstrap DLL.
/// </summary>
public static class AppRuntime
{
    private const string DownloadsPage = "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads";

    private static readonly Lock Gate = new();
    private static bool _initialized;

    /// <summary>The full name of the Windows App SDK framework package resolved into this process (null before initialisation).</summary>
    public static string? PackageFullName { get; private set; }

    /// <summary>
    /// Adds the Windows App SDK framework package to this process's package graph (idempotent, thread-safe).
    /// Throws <see cref="InvalidOperationException"/> with an install hint when the runtime is missing.
    /// </summary>
    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
                return;
            // The Dynamic Dependencies API in kernelbase arrived in Windows 11 22H2; check before binding to it.
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
                throw new InvalidOperationException(
                    $"The Windows App SDK runtime needs Windows 11 22H2 (build 22621) or later; this is Windows {Environment.OSVersion.Version}.");
            // NativeAOT publishes link Win2D statically: route its activation before any Win2D type is used.
            Imaging.Win2DStaticActivation.Install();
            PackageFullName = AddFrameworkDependency();
            _initialized = true;
        }
    }

    private static unsafe string AddFrameworkDependency()
    {
        HRESULT hresult;
        PWSTR dependencyId = default, fullName = default;
        try
        {
            var minVersion = default(PACKAGE_VERSION);
            minVersion.Version = RuntimeVersion.UInt64;
            hresult = PInvoke.TryCreatePackageDependency(
                default, Framework.PackageFamilyName, minVersion, CurrentArchitecture(),
                PackageDependencyLifetimeKind.PackageDependencyLifetimeKind_Process, null,
                CreatePackageDependencyOptions.CreatePackageDependencyOptions_None, out dependencyId);
            if (hresult.Succeeded)
            {
                PACKAGEDEPENDENCY_CONTEXT context;
                hresult = PInvoke.AddPackageDependency(dependencyId, (int)PInvoke.PACKAGE_DEPENDENCY_RANK_DEFAULT,
                    AddPackageDependencyOptions.AddPackageDependencyOptions_None, &context, &fullName);
            }
            if (hresult.Succeeded)
                return fullName.ToString() ?? Framework.PackageFamilyName;
        }
        catch (EntryPointNotFoundException e)
        {
            throw new InvalidOperationException(
                $"The Windows App SDK runtime needs Windows 11 22H2 (build 22621) or later; this Windows has no Dynamic Dependencies API ({e.Message}).", e);
        }
        finally
        {
            var heap = PInvoke.GetProcessHeap();
            if (fullName.Value != null) PInvoke.HeapFree(heap, 0, fullName);
            if (dependencyId.Value != null) PInvoke.HeapFree(heap, 0, dependencyId);
        }
        throw new InvalidOperationException(
            $"The Windows App SDK runtime {RuntimeVersion.DotQuadString} or later ({Framework.PackageFamilyName}, {Release.Major}.{Release.Minor}, " +
            $"{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}) is not installed. " +
            $"Install the Windows App SDK {Release.Major}.{Release.Minor} runtime from {DownloadsPage} (HRESULT 0x{hresult.Value:X8}).");
    }

    private static PackageDependencyProcessorArchitectures CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => PackageDependencyProcessorArchitectures.PackageDependencyProcessorArchitectures_X64,
        Architecture.Arm64 => PackageDependencyProcessorArchitectures.PackageDependencyProcessorArchitectures_Arm64,
        Architecture.X86 => PackageDependencyProcessorArchitectures.PackageDependencyProcessorArchitectures_X86,
        var other => throw new PlatformNotSupportedException($"The Windows App SDK runtime has no {other} build."),
    };
}
