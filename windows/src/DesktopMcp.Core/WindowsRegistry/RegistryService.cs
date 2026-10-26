using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DesktopMcp.Core.WindowsRegistry;

/// <summary>upstream registry/service.py on Microsoft.Win32.Registry, printing what pwsh 7 printed (Decisions 13).</summary>
public sealed partial class RegistryService
{
    /// <summary>upstream ALLOWED_REGISTRY_TYPES (registry/views.py:7-16).</summary>
    public static IReadOnlyList<string> AllowedTypes { get; } = ["String", "ExpandString", "Binary", "DWord", "MultiString", "QWord"];

    public string GetValue(string path, string name)
    {
        try
        {
            var location = RegistryLocation.Parse(path);
            using var key = location.Open(path, writable: false);
            var valueName = ValueName(name);
            // An empty name is no property at all; only "(default)" names the default value.
            if (name.Length == 0 || !HasValue(key, valueName)) throw new RegistryCommandException($"Property {name} does not exist at path {location.ProviderPath}.");
            // Get-ItemProperty | Select-Object -ExpandProperty: one line per item, then Python's strip().
            return $"Registry value [{path}] \"{name}\" = {ExpandedText(key.GetValue(valueName)).Trim()}";
        }
        catch (Exception e) when (IsRegistryError(e))
        {
            return $"Error reading registry: Get-ItemProperty: {e.Message}";
        }
    }

    public string SetValue(string path, string name, string value, string type)
    {
        if (!AllowedTypes.Contains(type))
            return $"Error: invalid registry type '{type}'. Allowed: {string.Join(", ", AllowedTypes.Order(StringComparer.Ordinal))}";
        try
        {
            var location = RegistryLocation.Parse(path);
            // Upstream creates the key first, then converts and sets the value.
            using var key = location.CreateOrOpen();
            // pwsh rejects the empty name when binding Set-ItemProperty, after New-Item has created the key.
            if (name.Length == 0) throw new RegistryCommandException("Cannot bind argument to parameter 'Name' because it is an empty string.");
            var (data, kind) = Convert(value, type);
            key.SetValue(ValueName(name), data, kind);
            return $"Registry value [{path}] \"{name}\" set to \"{value}\" (type: {type}).";
        }
        catch (Exception e) when (IsRegistryError(e))
        {
            return $"Error writing registry: Set-ItemProperty: {e.Message}";
        }
    }

    /// <summary>With a name, deletes that value; with no name (or an empty one), deletes the key and its subkeys.</summary>
    public string DeleteEntry(string path, string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            try
            {
                var location = RegistryLocation.Parse(path);
                using var key = location.Open(path, writable: true);
                var valueName = ValueName(name);
                if (!HasValue(key, valueName)) throw new RegistryCommandException($"Property {name} does not exist at path {location.ProviderPath}.");
                key.DeleteValue(valueName);
                return $"Registry value [{path}] \"{name}\" deleted.";
            }
            catch (Exception e) when (IsRegistryError(e))
            {
                return $"Error deleting registry value: Remove-ItemProperty: {e.Message}";
            }
        }
        try
        {
            var location = RegistryLocation.Parse(path);
            // The only guard that keeps a hive from being deleted: a path with no subkey names the hive itself.
            if (location.SubKey.Length == 0) throw new RegistryCommandException(HiveDeleteRefused);
            // Opening first reports a missing key with the same error as the other commands.
            using (location.Open(path, writable: false)) { }
            using var root = RegistryKey.OpenBaseKey(location.Hive, RegistryView.Default);
            root.DeleteSubKeyTree(location.SubKey);
            return $"Registry key [{path}] deleted.";
        }
        catch (Exception e) when (IsRegistryError(e))
        {
            return $"Error deleting registry key: Remove-Item: {e.Message}";
        }
    }

    public string ListKey(string path)
    {
        try
        {
            var location = RegistryLocation.Parse(path);
            using var key = location.Open(path, writable: false);
            var values = FormatList(key.GetValueNames().Select(n => (n.Length == 0 ? "(default)" : n, key.GetValue(n))).ToList());
            var subKeys = string.Join("\n", key.GetSubKeyNames());
            var parts = new List<string>();
            if (values.Length > 0) parts.Add($"Values:\n{values}");
            if (subKeys.Length > 0) parts.Add($"\nSub-Keys:\n{subKeys}");
            if (parts.Count == 0) parts.Add("No values or sub-keys found.");
            return $"Registry key [{path}]:\n{string.Join("\n", parts).Trim()}";
        }
        catch (Exception e) when (IsRegistryError(e))
        {
            return $"Error listing registry: Get-ItemProperty: {e.Message}";
        }
    }

    // Set-ItemProperty -Type conversions as pwsh 7 performs them.
    internal static (object Data, RegistryValueKind Kind) Convert(string value, string type) => type switch
    {
        "String" => (value, RegistryValueKind.String),
        "ExpandString" => (value, RegistryValueKind.ExpandString),
        "MultiString" => (new[] { value }, RegistryValueKind.MultiString),
        "DWord" => (unchecked((int)ParseUnsigned(value, 32, "System.UInt32", "a UInt32")), RegistryValueKind.DWord),
        "QWord" => (unchecked((long)ParseUnsigned(value, 64, "System.UInt64", "a UInt64")), RegistryValueKind.QWord),
        "Binary" => (new[] { ParseByte(value) }, RegistryValueKind.Binary),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "not a registry type"),
    };

    // Decimal (a negative value wraps, so -1 becomes 0xFFFFFFFF) or 0x hex.
    private static ulong ParseUnsigned(string value, int bits, string typeName, string rangeName)
    {
        var text = value.Trim();
        var max = bits == 32 ? uint.MaxValue : ulong.MaxValue;
        var min = bits == 32 ? int.MinValue : long.MinValue;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) && hex <= max) return hex;
        }
        else if (text.StartsWith('-'))
        {
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var negative) && negative >= min)
                return unchecked((ulong)negative) & max;
        }
        else if (ulong.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var positive) && positive <= max)
        {
            return positive;
        }
        var inner = NumberPattern().IsMatch(text)
            ? $"Value was either too large or too small for {rangeName}."
            : $"The input string '{value}' was not in a correct format.";
        throw new RegistryCommandException($"Cannot convert value \"{value}\" to type \"{typeName}\". Error: \"{inner}\"");
    }

    // A string becomes a one-byte array: PowerShell converts it to a single Byte.
    private static byte ParseByte(string value)
    {
        var text = value.Trim();
        byte parsed;
        var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out parsed)
            : byte.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out parsed);
        if (ok) return parsed;
        var inner = NumberPattern().IsMatch(text)
            ? "Value was either too large or too small for an unsigned byte."
            : $"The input string '{value}' was not in a correct format.";
        throw new RegistryCommandException(
            $"Cannot convert value \"{value}\" to type \"System.Byte[]\". Error: \"Cannot convert value \"{value}\" to type \"System.Byte\". Error: \"{inner}\"\"");
    }

    // Get-ItemProperty shows DWORD and QWORD unsigned, and each array item on its own line.
    private static string ExpandedText(object? value) => value switch
    {
        byte[] bytes => string.Join("\n", bytes.Select(b => b.ToString(CultureInfo.InvariantCulture))),
        string[] strings => string.Join("\n", strings),
        _ => ScalarText(value),
    };

    // Format-List: names padded to the longest, arrays as {a, b} cut after $FormatEnumerationLimit (4) items with "…".
    private static string FormatList(IReadOnlyList<(string Name, object? Value)> items)
    {
        if (items.Count == 0) return "";
        var width = items.Max(i => i.Name.Length);
        return string.Join("\n", items.Select(i => $"{i.Name.PadRight(width)} : {ListText(i.Value)}".TrimEnd()));
    }

    private static string ListText(object? value) => value switch
    {
        byte[] bytes => Enumeration(bytes.Select(b => b.ToString(CultureInfo.InvariantCulture)).ToList()),
        string[] strings => Enumeration(strings),
        _ => ScalarText(value),
    };

    private static string Enumeration(IReadOnlyList<string> items) =>
        "{" + string.Join(", ", items.Take(4)) + (items.Count > 4 ? "…" : "") + "}";

    private static string ScalarText(object? value) => value switch
    {
        null => "",
        int dword => unchecked((uint)dword).ToString(CultureInfo.InvariantCulture),
        long qword => unchecked((ulong)qword).ToString(CultureInfo.InvariantCulture),
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    // Only "(default)" names the default value; callers reject the empty name with pwsh's texts.
    private static string ValueName(string name) => name.Equals("(default)", StringComparison.OrdinalIgnoreCase) ? "" : name;

    private static bool HasValue(RegistryKey key, string valueName) =>
        key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase);

    private static bool IsRegistryError(Exception e) =>
        e is RegistryCommandException or SecurityException or UnauthorizedAccessException or IOException or ArgumentException;

    private const string HiveDeleteRefused = "Cannot delete a registry hive's subtree.";

    [GeneratedRegex("^([+-]?[0-9]+|0[xX][0-9a-fA-F]+)$")]
    private static partial Regex NumberPattern();
}
