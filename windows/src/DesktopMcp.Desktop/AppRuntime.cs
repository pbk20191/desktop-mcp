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
    /// <summary>The Windows App SDK downloads page, named in the install hint (checked in P2 Task 6).</summary>
    internal const string DownloadsPage = "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads";

    private static readonly AppRuntimeInitializer Default = new(
        osSupported: () => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621),
        installActivation: Imaging.Win2DStaticActivation.Install,
        addDependency: AddFrameworkDependency);

    /// <summary>
    /// The full name of the Windows App SDK framework package resolved into this process (null before initialisation).
    /// When the OS returns no full name it is the family name with "(family name; no full name returned)" appended.
    /// </summary>
    public static string? PackageFullName => Default.PackageFullName;

    /// <summary>
    /// Adds the Windows App SDK framework package to this process's package graph (idempotent, thread-safe).
    /// Throws <see cref="InvalidOperationException"/> with an install hint when the runtime is missing.
    /// </summary>
    public static void EnsureInitialized() => Default.EnsureInitialized();

    private static unsafe string AddFrameworkDependency()
    {
        HRESULT hresult;
        var call = "TryCreatePackageDependency";
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
                call = "AddPackageDependency";
                PACKAGEDEPENDENCY_CONTEXT context;
                hresult = PInvoke.AddPackageDependency(dependencyId, (int)PInvoke.PACKAGE_DEPENDENCY_RANK_DEFAULT,
                    AddPackageDependencyOptions.AddPackageDependencyOptions_None, &context, &fullName);
            }
            if (hresult.Succeeded)
                return fullName.ToString() ?? $"{Framework.PackageFamilyName} (family name; no full name returned)";
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
        throw new InvalidOperationException(InstallHint(call, hresult.Value));
    }

    /// <summary>
    /// "Install the Windows App SDK x.y runtime (a.b.c.d) or later from &lt;downloads page&gt;", naming the minimum version;
    /// shared with <c>ComErrors</c> for REGDB_E_CLASSNOTREG.
    /// </summary>
    internal static string InstallHint() =>
        $"Install the Windows App SDK {Release.Major}.{Release.Minor} runtime ({RuntimeVersion.DotQuadString}) or later from {DownloadsPage}";

    /// <summary>The message for a failed <paramref name="call"/>, ending with <see cref="InstallHint()"/>.</summary>
    internal static string InstallHint(string call, int hresult) =>
        $"The Windows App SDK runtime ({Framework.PackageFamilyName}, " +
        $"{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}) is not installed: {call} failed with HRESULT 0x{hresult:X8}. " +
        $"{InstallHint()}.";

    /// <summary>
    /// The framework package to depend on: the one built for this process's architecture, not the machine's. An x64
    /// process on an ARM64 machine (emulated) therefore asks for the x64 runtime, which Windows App SDK installs on ARM64
    /// for that purpose; an x86 process on x64 asks for x86. ARM64EC and other architectures have no runtime package.
    /// </summary>
    private static PackageDependencyProcessorArchitectures CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => PackageDependencyProcessorArchitectures.PackageDependencyProcessorArchitectures_X64,
        Architecture.Arm64 => PackageDependencyProcessorArchitectures.PackageDependencyProcessorArchitectures_Arm64,
        Architecture.X86 => PackageDependencyProcessorArchitectures.PackageDependencyProcessorArchitectures_X86,
        var other => throw new PlatformNotSupportedException($"The Windows App SDK runtime has no {other} build."),
    };
}

/// <summary>The once-only initialisation behind <see cref="AppRuntime"/>, with its OS calls injected for tests.</summary>
internal sealed class AppRuntimeInitializer(Func<bool> osSupported, Action installActivation, Func<string> addDependency)
{
    private readonly Lock _gate = new();
    private volatile bool _initialized;

    public string? PackageFullName { get; private set; }

    public void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_gate)
        {
            if (_initialized) return;
            // The Dynamic Dependencies API in kernelbase arrived in Windows 11 22H2; check before binding to it.
            if (!osSupported())
                throw new InvalidOperationException(
                    $"The Windows App SDK runtime needs Windows 11 22H2 (build 22621) or later; this is Windows {Environment.OSVersion.Version}.");
            // NativeAOT publishes link Win2D statically: route its activation before any Win2D type is used.
            installActivation();
            PackageFullName = addDependency();
            _initialized = true;
        }
    }
}
