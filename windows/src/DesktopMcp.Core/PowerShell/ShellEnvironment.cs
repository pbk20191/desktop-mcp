using System.Collections;
using System.Security;
using Microsoft.Win32;

namespace DesktopMcp.Core.PowerShell;

/// <summary>The variables of one registry Environment key: PATH and PATHEXT apart, other string values by name.</summary>
internal sealed record RegistryEnvironment(Dictionary<string, string> Variables, string Path, string PathExt);

/// <summary>
/// upstream _prepare_env (powershell/service.py:98-175). MCP hosts may start the server with a stripped environment,
/// so the child's environment starts from the server's and fills in what is missing from HKLM and HKCU (HKCU wins),
/// appends missing PATH entries, repairs PATHEXT and adds the session variables. Inherited values are never replaced.
/// </summary>
public static class ShellEnvironment
{
    internal const string FallbackPathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC;.CPL;.PY;.PYW";
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>The server's own secrets; both Build overloads keep them out of the child's environment.</summary>
    internal static IReadOnlyList<string> SecretVariables { get; } = ["DESKTOP_MCP_AUTH_KEY", "DESKTOP_MCP_OAUTH_CLIENT_SECRET"];

    public static Dictionary<string, string> Build()
    {
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            inherited[(string)entry.Key] = entry.Value as string ?? "";
        RegistryEnvironment? machine = null, user = null;
        try
        {
            machine = ReadRegistry(Registry.LocalMachine, MachineKey);
            user = ReadRegistry(Registry.CurrentUser, "Environment");
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException)
        {
            machine = user = null;
        }
        return Build(inherited, machine, user, Environment.MachineName, Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    internal static Dictionary<string, string> Build(IReadOnlyDictionary<string, string> inherited, RegistryEnvironment? machine,
        RegistryEnvironment? user, string computerName, string userName, string userProfile)
    {
        var environment = new Dictionary<string, string>(inherited, StringComparer.OrdinalIgnoreCase);
        if (machine is not null && user is not null)
        {
            var merged = new Dictionary<string, string>(machine.Variables, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in user.Variables) merged[name] = value;
            foreach (var (name, value) in merged)
            {
                if (string.IsNullOrEmpty(environment.GetValueOrDefault(name))) environment[name] = value;
            }
            if (machine.Path.Length > 0 || user.Path.Length > 0)
                environment["PATH"] = DedupPath(environment.GetValueOrDefault("PATH") ?? "", machine.Path, user.Path);
            var pathExt = user.PathExt.Length > 0 ? user.PathExt : machine.PathExt;
            if (pathExt.Length > 0 && !HasExe(environment)) environment["PATHEXT"] = pathExt;
        }
        else if (!HasExe(environment))
        {
            environment["PATHEXT"] = FallbackPathExt;
        }

        if (string.IsNullOrEmpty(environment.GetValueOrDefault("COMPUTERNAME")) && computerName.Length > 0) environment["COMPUTERNAME"] = computerName;
        if (string.IsNullOrEmpty(environment.GetValueOrDefault("USERNAME")) && userName.Length > 0) environment["USERNAME"] = userName;
        environment.TryAdd("USERPROFILE", userProfile);
        // os.path.splitdrive: "C:" and "\Users\me", or "\\server\share" and the rest.
        var drive = Path.GetPathRoot(userProfile)?.TrimEnd('\\') ?? "";
        environment.TryAdd("HOMEDRIVE", drive);
        environment.TryAdd("HOMEPATH", userProfile[drive.Length..]);
        environment.TryAdd("USERDOMAIN", environment.GetValueOrDefault("COMPUTERNAME") ?? "");
        // Last, so neither the inherited environment nor HKLM/HKCU can hand the server's secrets to the child.
        foreach (var secret in SecretVariables) environment.Remove(secret);
        return environment;
    }

    /// <summary>upstream _dedup_path: join, then drop empty and repeated entries (case-insensitive, trailing \ ignored).</summary>
    internal static string DedupPath(params string[] segments)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var entry in string.Join(';', segments.Where(s => s.Length > 0)).Split(';'))
        {
            var normalized = entry.ToLowerInvariant().TrimEnd('\\');
            if (normalized.Length > 0 && seen.Add(normalized)) kept.Add(entry);
        }
        return string.Join(';', kept);
    }

    // upstream _read_reg_env: REG_SZ and REG_EXPAND_SZ values (the latter expanded against this process's environment);
    // a missing or unreadable key gives nothing.
    internal static RegistryEnvironment ReadRegistry(RegistryKey hive, string subKey)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string path = "", pathExt = "";
        try
        {
            using var key = hive.OpenSubKey(subKey);
            if (key is not null)
            {
                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValueKind(name) is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;
                    var value = key.GetValue(name) as string ?? "";
                    if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase)) path = value;
                    else if (name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase)) pathExt = value;
                    else variables[name] = value;
                }
            }
        }
        catch (IOException)
        {
        }
        return new RegistryEnvironment(variables, path, pathExt);
    }

    private static bool HasExe(Dictionary<string, string> environment) =>
        (environment.GetValueOrDefault("PATHEXT") ?? "").Contains(".EXE", StringComparison.OrdinalIgnoreCase);
}
