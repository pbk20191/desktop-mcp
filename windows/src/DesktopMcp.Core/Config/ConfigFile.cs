using System.Text;
using DesktopMcp.Core.Text;
using Tomlyn;
using Tomlyn.Model;

namespace DesktopMcp.Core.Config;

/// <summary>Finds and reads config.toml with upstream's rules and messages (infrastructure/config.py:72-212).</summary>
public static class ConfigFile
{
    public const string DirectoryName = ".desktop-mcp";
    public const string FileName = "config.toml";
    public static IReadOnlyList<string> Transports { get; } = ["stdio", "sse", "streamable-http"];
    public static IReadOnlyList<string> Policies { get; } = ["relaxed", "balanced", "strict"];

    public static string DefaultPath(string home) => Path.Combine(home, DirectoryName, FileName);

    /// <summary>The explicit path (with ~ expanded), else the default file when it exists, else null.</summary>
    public static string? Discover(string? explicitPath, string home)
    {
        if (!string.IsNullOrEmpty(explicitPath)) return ExpandUser(explicitPath, home);
        var path = DefaultPath(home);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Python's expanduser for "~", "~/..." and "~\...".</summary>
    public static string ExpandUser(string path, string home)
    {
        if (path == "~") return home;
        return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal)
            ? Path.Combine(home, path[2..])
            : path;
    }

    /// <summary>Defaults when <paramref name="path"/> is null; otherwise the parsed, validated file.</summary>
    public static DesktopMcpConfig Load(string? path)
    {
        if (path is null) return new DesktopMcpConfig();
        if (!File.Exists(path)) throw new ConfigException($"Config file not found: {path}");
        return Parse(File.ReadAllText(path, Encoding.UTF8), path);
    }

    public static DesktopMcpConfig Parse(string toml, string path)
    {
        TomlTable document;
        try
        {
            document = TomlSerializer.Deserialize(toml, ConfigTomlContext.Default.TomlTable) ?? new TomlTable();
        }
        catch (TomlException e)
        {
            throw new ConfigException($"Invalid TOML in {path}: {Reason(e)}");
        }

        var config = new DesktopMcpConfig { SourcePath = path };
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
        var server = Section(document, "server");
        var security = Section(document, "security");
        var tools = Section(document, "tools");
        var control = Section(document, "control");

        if (server.TryGetValue("transport", out var transport))
        {
            var value = StrictString(transport, "server.transport");
            if (!Transports.Contains(value))
                throw new ConfigException($"server.transport must be one of {{'stdio', 'sse', 'streamable-http'}}, got {PyText.Repr(value)}");
            config.Server.Transport = value;
        }
        if (server.TryGetValue("host", out var host)) config.Server.Host = StrictString(host, "server.host");
        if (server.TryGetValue("port", out var port))
        {
            config.Server.Port = StrictInt(port, "server.port");
            if (config.Server.Port is < 0 or > 65535) throw new ConfigException("server.port must be between 0 and 65535");
        }
        if (server.TryGetValue("allow_insecure_remote", out var insecure))
            config.Server.AllowInsecureRemote = StrictBool(insecure, "server.allow_insecure_remote");
        if (server.TryGetValue("auth_key", out var authKey)) config.Server.AuthKey = Blank(StrictString(authKey, "server.auth_key"));
        // TLS paths are relative to the config file's folder.
        if (server.TryGetValue("ssl_certfile", out var cert) && Blank(StrictString(cert, "server.ssl_certfile")) is { } certPath)
            config.Server.SslCertfile = Path.GetFullPath(certPath, baseDirectory);
        if (server.TryGetValue("ssl_keyfile", out var key) && Blank(StrictString(key, "server.ssl_keyfile")) is { } keyPath)
            config.Server.SslKeyfile = Path.GetFullPath(keyPath, baseDirectory);
        if (server.TryGetValue("stateless_http", out var stateless))
            config.Server.StatelessHttp = StrictBool(stateless, "server.stateless_http");

        if (security.TryGetValue("ip_allowlist", out var allowlist)) config.Security.IpAllowlist = ListOfStrings(allowlist, "security.ip_allowlist");
        if (security.TryGetValue("cors_origins", out var cors)) config.Security.CorsOrigins = ListOfStrings(cors, "security.cors_origins");
        if (security.TryGetValue("oauth_client_id", out var clientId))
            config.Security.OAuthClientId = Blank(StrictString(clientId, "security.oauth_client_id"));
        if (security.TryGetValue("oauth_client_secret", out var clientSecret))
            config.Security.OAuthClientSecret = Blank(StrictString(clientSecret, "security.oauth_client_secret"));

        if (tools.TryGetValue("exclude", out var exclude)) config.Tools.Exclude = ListOfStrings(exclude, "tools.exclude");

        if (control.TryGetValue("policy", out var policy))
        {
            if (policy is not string name || !Policies.Contains(name))
                throw new ConfigException("control.policy must be relaxed, balanced, or strict");
            config.Control.Policy = name;
        }
        if (control.TryGetValue("enabled", out var enabled)) config.Control.Enabled = StrictBool(enabled, "control.enabled");
        if (control.TryGetValue("idle_timeout_seconds", out var idle)) config.Control.IdleTimeoutSeconds = IdleTimeout.Validate(idle);
        if (control.TryGetValue("mouse_takeover_units", out var units)) config.Control.MouseTakeoverUnits = TakeoverDistance(units, "control.mouse_takeover_units");
        if (control.TryGetValue("mouse_takeover_pixels", out var pixels)) config.Control.MouseTakeoverPixels = TakeoverDistance(pixels, "control.mouse_takeover_pixels");
        return config;
    }

    private static TomlTable Section(TomlTable document, string name)
    {
        if (!document.TryGetValue(name, out var value)) return new TomlTable();
        return value as TomlTable ?? throw new ConfigException($"{name} must be a TOML table");
    }

    private static long TakeoverDistance(object? raw, string key)
    {
        var value = StrictInt(raw, key);
        if (value is < 40 or > 2000) throw new ConfigException($"{key} must be between 40 and 2000");
        return value;
    }

    private static string StrictString(object? raw, string key) =>
        raw as string ?? throw new ConfigException($"{key} must be a TOML string, not {PyTypeName(raw)}");

    private static bool StrictBool(object? raw, string key) =>
        raw is bool value ? value : throw new ConfigException($"{key} must be a TOML boolean, not {PyTypeName(raw)}");

    // TOML booleans are a subclass of int in Python; upstream rejects them explicitly.
    private static long StrictInt(object? raw, string key) =>
        raw is long value ? value : throw new ConfigException($"{key} must be a TOML integer, not {PyTypeName(raw)}");

    private static List<string> ListOfStrings(object? raw, string key)
    {
        if (raw is not TomlArray array) throw new ConfigException($"{key} must be an array of strings");
        var items = new List<string>();
        foreach (var item in array)
        {
            if (item is not string text) throw new ConfigException($"{key} must be an array of strings");
            items.Add(text);
        }
        return items;
    }

    private static string? Blank(string value) => value.Length == 0 ? null : value;

    // The names Python's type(raw).__name__ prints for TOML values.
    private static string PyTypeName(object? value) => value switch
    {
        null => "NoneType",
        bool => "bool",
        long => "int",
        double => "float",
        string => "str",
        TomlArray or TomlTableArray => "list",
        TomlTable => "dict",
        _ => "datetime",
    };

    private static string Reason(TomlException e)
    {
        const string marker = "Reason: ";
        var at = e.Message.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? e.Message : e.Message[(at + marker.Length)..];
    }
}
