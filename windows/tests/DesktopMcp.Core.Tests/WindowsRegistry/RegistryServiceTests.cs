using DesktopMcp.Core.WindowsRegistry;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace DesktopMcp.Core.Tests;

/// <summary>Works in a throwaway key HKCU\Software\desktop-mcp-tests\&lt;guid&gt; that Dispose deletes.</summary>
public sealed class RegistryServiceTests : IDisposable
{
    private const string Parent = @"Software\desktop-mcp-tests";
    private readonly string _subKey = Parent + @"\" + Guid.NewGuid().ToString("N");
    private readonly string _path;
    private readonly RegistryService _registry = new();

    public RegistryServiceTests() => _path = @"HKCU:\" + _subKey;

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false);
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(Parent);
            if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException) { }
    }

    [Fact]
    public void SetCreatesTheKeyAndGetReadsTheValue()
    {
        Assert.Equal($"Registry value [{_path}] \"s\" set to \"hello\" (type: String).", _registry.SetValue(_path, "s", "hello", "String"));
        Assert.Equal($"Registry value [{_path}] \"s\" = hello", _registry.GetValue(_path, "s"));
    }

    [Theory]
    [InlineData("DWord", "42", "42")]
    [InlineData("DWord", "-1", "4294967295")]
    [InlineData("DWord", "0x10", "16")]
    [InlineData("DWord", "4294967295", "4294967295")]
    [InlineData("QWord", "-5", "18446744073709551611")]
    [InlineData("QWord", "1234567890123", "1234567890123")]
    [InlineData("Binary", "7", "7")]
    [InlineData("Binary", "0x1F", "31")]
    [InlineData("MultiString", "a,b", "a,b")]
    [InlineData("String", "  padded  ", "padded")]
    public void ValuesReadBackAsPowerShellShowsThem(string type, string value, string shown)
    {
        _registry.SetValue(_path, "v", value, type);
        Assert.Equal($"Registry value [{_path}] \"v\" = {shown}", _registry.GetValue(_path, "v"));
    }

    [Fact]
    public void ExpandStringIsExpandedOnRead()
    {
        _registry.SetValue(_path, "e", @"%USERNAME%\x", "ExpandString");
        Assert.Equal($@"Registry value [{_path}] ""e"" = {Environment.GetEnvironmentVariable("USERNAME")}\x", _registry.GetValue(_path, "e"));
    }

    [Fact]
    public void MultiStringAndBinaryWithSeveralItemsPrintOneItemPerLine()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey))
        {
            key.SetValue("m", new[] { "a", "b" }, RegistryValueKind.MultiString);
            key.SetValue("b", new byte[] { 1, 2 }, RegistryValueKind.Binary);
        }
        Assert.Equal($"Registry value [{_path}] \"m\" = a\nb", _registry.GetValue(_path, "m"));
        Assert.Equal($"Registry value [{_path}] \"b\" = 1\n2", _registry.GetValue(_path, "b"));
    }

    [Theory]
    [InlineData("DWord", "abc", "Cannot convert value \"abc\" to type \"System.UInt32\". Error: \"The input string 'abc' was not in a correct format.\"")]
    [InlineData("DWord", "5000000000", "Cannot convert value \"5000000000\" to type \"System.UInt32\". Error: \"Value was either too large or too small for a UInt32.\"")]
    [InlineData("QWord", "x1", "Cannot convert value \"x1\" to type \"System.UInt64\". Error: \"The input string 'x1' was not in a correct format.\"")]
    [InlineData("Binary", "1,2,255", "Cannot convert value \"1,2,255\" to type \"System.Byte[]\". Error: \"Cannot convert value \"1,2,255\" to type \"System.Byte\". Error: \"The input string '1,2,255' was not in a correct format.\"\"")]
    [InlineData("Binary", "300", "Cannot convert value \"300\" to type \"System.Byte[]\". Error: \"Cannot convert value \"300\" to type \"System.Byte\". Error: \"Value was either too large or too small for an unsigned byte.\"\"")]
    public void ConversionErrorsUsePowerShellsText(string type, string value, string message) =>
        Assert.Equal($"Error writing registry: Set-ItemProperty: {message}", _registry.SetValue(_path, "v", value, type));

    [Fact]
    public void AnInvalidTypeIsRejectedBeforeAnyWrite()
    {
        Assert.Equal("Error: invalid registry type 'Text'. Allowed: Binary, DWord, ExpandString, MultiString, QWord, String",
            _registry.SetValue(_path, "v", "x", "Text"));
        using var key = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Null(key);
    }

    [Fact]
    public void GetErrors()
    {
        _registry.SetValue(_path, "s", "x", "String");
        Assert.Equal($@"Error reading registry: Get-ItemProperty: Property nope does not exist at path HKEY_CURRENT_USER\{_subKey}.", _registry.GetValue(_path, "nope"));
        Assert.Equal($@"Error reading registry: Get-ItemProperty: Cannot find path '{_path}\none' because it does not exist.", _registry.GetValue(_path + @"\none", "x"));
        Assert.Equal("Error reading registry: Get-ItemProperty: Cannot find drive. A drive with the name 'HKXX' does not exist.", _registry.GetValue(@"HKXX:\Software", "x"));
    }

    [Fact]
    public void TheDefaultValueIsNamedDefault()
    {
        _registry.SetValue(_path, "(default)", "d", "String");
        Assert.Equal($"Registry value [{_path}] \"(default)\" = d", _registry.GetValue(_path, "(default)"));
        Assert.Contains("(default) : d", _registry.ListKey(_path));
    }

    [Fact]
    public void ListShowsValuesInFormatListLayoutAndSubKeys()
    {
        _registry.SetValue(_path, "s", "hello", "String");
        _registry.SetValue(_path, "dword", "42", "DWord");
        _registry.SetValue(_path, "m", "a", "MultiString");
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey))
        {
            key.SetValue("bytes", new byte[] { 1, 2, 3, 4, 5 }, RegistryValueKind.Binary);
            key.CreateSubKey("Beta").Dispose();
            key.CreateSubKey("Alpha").Dispose();
        }
        Assert.Equal(
            $"Registry key [{_path}]:\nValues:\ns     : hello\ndword : 42\nm     : {{a}}\nbytes : {{1, 2, 3, 4…}}\n\nSub-Keys:\nAlpha\nBeta",
            _registry.ListKey(_path));
    }

    [Fact]
    public void ListOfEmptyAndSubKeyOnlyKeys()
    {
        using (Registry.CurrentUser.CreateSubKey(_subKey)) { }
        Assert.Equal($"Registry key [{_path}]:\nNo values or sub-keys found.", _registry.ListKey(_path));
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey)) key.CreateSubKey("child").Dispose();
        Assert.Equal($"Registry key [{_path}]:\nSub-Keys:\nchild", _registry.ListKey(_path));
        Assert.Equal($@"Error listing registry: Get-ItemProperty: Cannot find path '{_path}\none' because it does not exist.", _registry.ListKey(_path + @"\none"));
    }

    [Fact]
    public void DeleteValuesAndKeys()
    {
        _registry.SetValue(_path, "s", "x", "String");
        Assert.Equal($"Registry value [{_path}] \"s\" deleted.", _registry.DeleteEntry(_path, "s"));
        Assert.Equal($@"Error deleting registry value: Remove-ItemProperty: Property s does not exist at path HKEY_CURRENT_USER\{_subKey}.", _registry.DeleteEntry(_path, "s"));
        Assert.Equal($"Registry key [{_path}] deleted.", _registry.DeleteEntry(_path, null));
        Assert.Equal($"Error deleting registry key: Remove-Item: Cannot find path '{_path}' because it does not exist.", _registry.DeleteEntry(_path, ""));
    }

    [Fact]
    public void APathWithANulNamesNoKeyAndDeletesNothing()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey + @"\target")) key.CreateSubKey("child").Dispose();
        var nul = _path + "\\target\\\0";
        Assert.Equal($"Error deleting registry key: Remove-Item: Cannot find path '{nul}' because it does not exist.", _registry.DeleteEntry(nul, null));
        Assert.Equal($"Error writing registry: Set-ItemProperty: Cannot find path '{nul}' because it does not exist.", _registry.SetValue(nul, "v", "1", "String"));
        Assert.Equal($"Error reading registry: Get-ItemProperty: Cannot find path '{_path}\t' because it does not exist.", _registry.GetValue(_path + "\t", "v"));
        using var child = Registry.CurrentUser.OpenSubKey(_subKey + @"\target\child");
        Assert.NotNull(child);
        using var target = Registry.CurrentUser.OpenSubKey(_subKey + @"\target");
        Assert.Equal(0, target!.ValueCount);
    }

    // The guard refuses these before any registry call; .NET's own hive guard would also refuse them.
    [Theory]
    [InlineData(@"HKCU:\")]
    [InlineData("Registry::HKEY_CURRENT_USER")]
    public void AHiveIsNeverDeleted(string path) =>
        Assert.Equal("Error deleting registry key: Remove-Item: Cannot delete a registry hive's subtree.", _registry.DeleteEntry(path, null));

    [Fact]
    public void ADeniedKeyGivesPowerShellsAccessText()
    {
        var denied = _subKey + @"\denied";
        Registry.CurrentUser.CreateSubKey(denied).Dispose();
        var rule = new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, RegistryRights.SetValue, AccessControlType.Deny);
        SetRule(denied, security => security.AddAccessRule(rule));
        try
        {
            Assert.Equal("Error writing registry: Set-ItemProperty: Requested registry access is not allowed.",
                _registry.SetValue(_path + @"\denied", "v", "1", "String"));
        }
        finally
        {
            SetRule(denied, security => security.RemoveAccessRule(rule));
        }
    }

    private static void SetRule(string subKey, Action<RegistrySecurity> change)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions)!;
        var security = key.GetAccessControl();
        change(security);
        key.SetAccessControl(security);
    }

    [Fact]
    public void AnEmptyNameIsNotTheDefaultValue()
    {
        Assert.Equal("Error writing registry: Set-ItemProperty: Cannot bind argument to parameter 'Name' because it is an empty string.",
            _registry.SetValue(_path, "", "x", "String"));
        using (var key = Registry.CurrentUser.OpenSubKey(_subKey))
        {
            Assert.NotNull(key); // New-Item ran before the binding error, as in pwsh.
            Assert.Equal(0, key.ValueCount);
        }
        _registry.SetValue(_path, "(default)", "d", "String");
        Assert.Equal($@"Error reading registry: Get-ItemProperty: Property  does not exist at path HKEY_CURRENT_USER\{_subKey}.", _registry.GetValue(_path, ""));
    }

    [Fact]
    public void AnOverLongKeyNameIsAnErrorText() =>
        Assert.StartsWith("Error reading registry: Get-ItemProperty: ", _registry.GetValue(_path + @"\" + new string('k', 300), "v"));

    [Fact]
    public void ProviderPathsWork()
    {
        var provider = @"Registry::HKEY_CURRENT_USER\" + _subKey;
        _registry.SetValue(provider, "p", "1", "String");
        Assert.Equal($"Registry value [{provider}] \"p\" = 1", _registry.GetValue(provider, "p"));
        Assert.Equal($"Registry value [{_path}] \"p\" = 1", _registry.GetValue(_path.Replace('\\', '/'), "p").Replace('/', '\\'));
    }
}
