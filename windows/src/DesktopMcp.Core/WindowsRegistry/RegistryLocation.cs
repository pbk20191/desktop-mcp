using Microsoft.Win32;

namespace DesktopMcp.Core.WindowsRegistry;

/// <summary>A failure carrying the text PowerShell's registry provider prints, without the cmdlet prefix.</summary>
internal sealed class RegistryCommandException(string message) : Exception(message);

/// <summary>
/// A PowerShell registry path: the HKCU: and HKLM: drives (the only registry drives PowerShell defines), or a
/// Registry::&lt;hive&gt;\... provider path. "/" is accepted as a separator, as PowerShell does.
/// </summary>
internal sealed record RegistryLocation(RegistryHive Hive, string HiveName, string SubKey)
{
    private static readonly (string Short, string Long, RegistryHive Hive)[] Hives =
    [
        ("HKCU", "HKEY_CURRENT_USER", RegistryHive.CurrentUser),
        ("HKLM", "HKEY_LOCAL_MACHINE", RegistryHive.LocalMachine),
        ("HKCR", "HKEY_CLASSES_ROOT", RegistryHive.ClassesRoot),
        ("HKU", "HKEY_USERS", RegistryHive.Users),
        ("HKCC", "HKEY_CURRENT_CONFIG", RegistryHive.CurrentConfig),
    ];

    /// <summary>The path PowerShell shows in "does not exist at path" errors.</summary>
    public string ProviderPath => SubKey.Length == 0 ? HiveName : $@"{HiveName}\{SubKey}";

    public static RegistryLocation Parse(string path)
    {
        var normalized = path.Replace('/', '\\');
        foreach (var prefix in new[] { @"Microsoft.PowerShell.Core\Registry::", "Registry::" })
        {
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = normalized[prefix.Length..].Trim('\\');
            var separator = rest.IndexOf('\\');
            var hiveName = separator < 0 ? rest : rest[..separator];
            var index = Array.FindIndex(Hives, h => h.Long.Equals(hiveName, StringComparison.OrdinalIgnoreCase) || h.Short.Equals(hiveName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new RegistryCommandException($"Cannot find path '{path}' because it does not exist.");
            return new RegistryLocation(Hives[index].Hive, Hives[index].Long, separator < 0 ? "" : rest[(separator + 1)..].TrimEnd('\\'));
        }
        var colon = normalized.IndexOf(':');
        if (colon <= 0) throw new RegistryCommandException($"Cannot find path '{path}' because it does not exist.");
        var drive = normalized[..colon];
        var driveIndex = Array.FindIndex(Hives, 0, 2, h => h.Short.Equals(drive, StringComparison.OrdinalIgnoreCase));
        if (driveIndex < 0) throw new RegistryCommandException($"Cannot find drive. A drive with the name '{drive}' does not exist.");
        return new RegistryLocation(Hives[driveIndex].Hive, Hives[driveIndex].Long, normalized[(colon + 1)..].Trim('\\'));
    }

    /// <summary>The key, or PowerShell's "Cannot find path" error naming the path as the caller gave it.</summary>
    public RegistryKey Open(string originalPath, bool writable)
    {
        var root = RegistryKey.OpenBaseKey(Hive, RegistryView.Default);
        if (SubKey.Length == 0) return root;
        using (root)
        {
            return root.OpenSubKey(SubKey, writable)
                ?? throw new RegistryCommandException($"Cannot find path '{originalPath}' because it does not exist.");
        }
    }

    /// <summary>upstream's "if (-not (Test-Path ...)) { New-Item ... -Force }": the key, created when missing.</summary>
    public RegistryKey CreateOrOpen()
    {
        var root = RegistryKey.OpenBaseKey(Hive, RegistryView.Default);
        if (SubKey.Length == 0) return root;
        using (root) return root.CreateSubKey(SubKey, writable: true);
    }
}
