using DesktopMcp.Core.Net;

namespace DesktopMcp.Core.Config;

public sealed record ControlSettings(bool Enabled, string Policy, double IdleTimeoutSeconds, long MouseTakeoverUnits, long MouseTakeoverPixels)
{
    /// <summary>[control] process_allowlist; empty allows everything. Set only when non-empty so record equality of the default holds.</summary>
    public IReadOnlyList<string> ProcessAllowlist { get; init; } = [];
}

/// <summary>The validated settings serve runs with (spec §8; upstream serve, __main__.py:722-834).</summary>
public sealed record ServePlan
{
    public required string Transport { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? AuthKey { get; init; }
    public bool AllowInsecureRemote { get; init; }
    public bool StatelessHttp { get; init; }
    public IReadOnlyList<IpNetwork> IpAllowlist { get; init; } = [];
    public IReadOnlyList<string> CorsOrigins { get; init; } = [];
    public string? SslCertfile { get; init; }
    public string? SslKeyfile { get; init; }
    public string? OAuthClientId { get; init; }
    public string? OAuthClientSecret { get; init; }
    public IReadOnlyList<string> ExplicitTools { get; init; } = [];
    public IReadOnlyList<string> ExcludeTools { get; init; } = [];
    /// <summary>Hosts the Host header may name (DNS-rebinding protection), or null for no check.</summary>
    public IReadOnlyList<string>? AllowedHosts { get; init; }
    public required ControlSettings Control { get; init; }
    public bool Debug { get; init; }
    /// <summary>Non-fatal notes for the log.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string Scheme => SslCertfile is null ? "http" : "https";

    public static ServePlan Resolve(ServeArguments arguments, DesktopMcpConfig config)
    {
        var transport = arguments.Transport ?? config.Server.Transport;
        var host = arguments.Host ?? config.Server.Host;
        var port = arguments.Port ?? config.Server.Port;
        if (port is < 0 or > 65535) throw new ConfigException("server.port must be between 0 and 65535");
        var authKey = Blank(arguments.AuthKey ?? config.Server.AuthKey);
        var statelessHttp = arguments.StatelessHttp ?? config.Server.StatelessHttp;
        var allowInsecureRemote = arguments.AllowInsecureRemote ?? config.Server.AllowInsecureRemote;
        var sslCertfile = Blank(arguments.SslCertfile ?? config.Server.SslCertfile);
        var sslKeyfile = Blank(arguments.SslKeyfile ?? config.Server.SslKeyfile);
        var oauthClientId = Blank(arguments.OAuthClientId ?? config.Security.OAuthClientId);
        var oauthClientSecret = Blank(arguments.OAuthClientSecret ?? config.Security.OAuthClientSecret);

        var explicitTools = SplitList(arguments.Tools);
        var excludeTools = string.IsNullOrEmpty(arguments.ExcludeTools) ? config.Tools.Exclude.ToArray() : SplitList(arguments.ExcludeTools);
        // Upstream keeps empty allowlist entries here; ParseAllowlist skips them.
        var allowlistEntries = string.IsNullOrEmpty(arguments.IpAllowlist)
            ? config.Security.IpAllowlist.ToArray()
            : arguments.IpAllowlist.Split(',').Select(entry => entry.Trim()).ToArray();
        var corsOrigins = string.IsNullOrEmpty(arguments.CorsOrigins) ? config.Security.CorsOrigins.ToArray() : SplitList(arguments.CorsOrigins);

        if ((sslCertfile is null) != (sslKeyfile is null))
            throw new ConfigException("--ssl-certfile and --ssl-keyfile must be provided together.");
        if ((oauthClientId is null) != (oauthClientSecret is null))
            throw new ConfigException("OAuth requires both --oauth-client-id and --oauth-client-secret.");

        IReadOnlyList<IpNetwork> allowlist = [];
        if (allowlistEntries.Length > 0)
        {
            try { allowlist = IpNetwork.ParseAllowlist(allowlistEntries); }
            catch (FormatException e) { throw new ConfigException($"Invalid ip_allowlist: {e.Message}"); }
        }

        var oauth = oauthClientId is not null && oauthClientSecret is not null;
        var loopback = IpAddressText.IsLoopbackHost(host);
        if (transport != "stdio" && !loopback && authKey is null && !oauth && !allowInsecureRemote)
        {
            throw new ConfigException(
                $"Refusing to bind HTTP transport to '{host}' without authentication.\n" +
                "  Use --auth-key <token> or --oauth-client-id/--oauth-client-secret.\n" +
                "  Or pass --allow-insecure-remote to explicitly allow unauthenticated access (not recommended).");
        }

        var warnings = new List<string>();
        if ((authKey is not null || allowlistEntries.Length > 0) && transport == "stdio")
            warnings.Add("--auth-key / --ip-allowlist have no effect on stdio transport");

        // DNS-rebinding protection: loopback binds accept the loopback names, a specific address accepts itself, and
        // 0.0.0.0, :: and --allow-insecure-remote turn the check off.
        IReadOnlyList<string>? allowedHosts = null;
        if (transport != "stdio" && !allowInsecureRemote)
        {
            if (loopback) allowedHosts = ["localhost", "127.0.0.1", "[::1]"];
            else if (host is not ("0.0.0.0" or "::" or "")) allowedHosts = [host];
        }

        return new ServePlan
        {
            Transport = transport,
            Host = host,
            Port = (int)port,
            AuthKey = authKey,
            AllowInsecureRemote = allowInsecureRemote,
            StatelessHttp = statelessHttp,
            IpAllowlist = allowlist,
            CorsOrigins = corsOrigins,
            SslCertfile = sslCertfile,
            SslKeyfile = sslKeyfile,
            OAuthClientId = oauthClientId,
            OAuthClientSecret = oauthClientSecret,
            ExplicitTools = explicitTools,
            ExcludeTools = excludeTools,
            AllowedHosts = allowedHosts,
            Control = new ControlSettings(
                arguments.Control ?? config.Control.Enabled,
                arguments.ControlPolicy ?? config.Control.Policy,
                arguments.ControlIdleTimeout ?? config.Control.IdleTimeoutSeconds,
                config.Control.MouseTakeoverUnits,
                config.Control.MouseTakeoverPixels)
            {
                ProcessAllowlist = config.Control.ProcessAllowlist.Count == 0 ? [] : config.Control.ProcessAllowlist.ToArray(),
            },
            Debug = arguments.Debug,
            Warnings = warnings,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string[] SplitList(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();
}
