using System.CommandLine;
using DesktopMcp.Core.Config;

namespace DesktopMcp.Server.Cli;

/// <summary>The serve command's options with upstream's help texts (__main__.py:551-698), renamed for desktop-mcp.</summary>
internal sealed class ServeOptions
{
    public ServeOptions()
    {
        Transport.AcceptOnlyFromAmong([.. ConfigFile.Transports]);
        ControlPolicy.AcceptOnlyFromAmong([.. ConfigFile.Policies]);
    }

    public Option<string> Transport { get; } = new("--transport") { Description = "The transport layer used by the MCP server.", DefaultValueFactory = _ => "stdio" };
    public Option<string> Host { get; } = new("--host") { Description = "Host to bind the SSE/Streamable HTTP server.", DefaultValueFactory = _ => "localhost" };
    public Option<int> Port { get; } = new("--port") { Description = "Port to bind the SSE/Streamable HTTP server.", DefaultValueFactory = _ => 8000 };
    public Option<bool> Debug { get; } = new("--debug") { Description = "Enable debug mode to provide verbose logging for troubleshooting.", Arity = ArgumentArity.Zero };
    public Option<string> Config { get; } = new("--config") { Description = "Path to desktop-mcp config file (default: ~/.desktop-mcp/config.toml)." };
    public Option<string> AuthKey { get; } = new("--auth-key") { Description = "Bearer token required on all HTTP requests. Can also be set via DESKTOP_MCP_AUTH_KEY." };
    public Option<bool> AllowInsecureRemote { get; } = new("--allow-insecure-remote") { Description = "Allow binding to non-loopback addresses without authentication (not recommended).", Arity = ArgumentArity.Zero };
    public Option<string> IpAllowlist { get; } = new("--ip-allowlist") { Description = "Comma-separated list of allowed client IPs or CIDR ranges (e.g. '10.0.0.0/8,192.168.1.5'). IPv4 and IPv6 supported." };
    public Option<string> Tools { get; } = new("--tools") { Description = "Comma-separated explicit list of tools to enable (e.g. 'Screenshot,Click,Snapshot'). Overrides --exclude-tools." };
    public Option<string> ExcludeTools { get; } = new("--exclude-tools") { Description = "Comma-separated list of tools to remove from the active set (e.g. 'PowerShell,Registry')." };
    public Option<string> CorsOrigins { get; } = new("--cors-origins") { Description = "Comma-separated list of allowed CORS origins (e.g. 'https://my-client.example.com'). Defaults to none — no CORS headers are emitted, so browsers block cross-origin requests via Same-Origin Policy." };
    public Option<string> SslCertfile { get; } = new("--ssl-certfile") { Description = "Path to TLS certificate file (.pem) for HTTPS. Requires --ssl-keyfile." };
    public Option<string> SslKeyfile { get; } = new("--ssl-keyfile") { Description = "Path to TLS private key file (.pem) for HTTPS. Requires --ssl-certfile." };
    public Option<string> OAuthClientId { get; } = new("--oauth-client-id") { Description = "OAuth client ID (pre-provisioned confidential client). Requires --oauth-client-secret." };
    public Option<string> OAuthClientSecret { get; } = new("--oauth-client-secret") { Description = "OAuth client secret. Requires --oauth-client-id." };
    public Option<bool> StatelessHttp { get; } = new("--stateless-http") { Description = "Run the streamable-http transport in stateless mode (no Mcp-Session-Id header). Lets reconnecting clients survive a server restart without re-handshaking, and removes the per-connection state that prevents horizontal scaling. Has no effect on stdio/sse transports.", Arity = ArgumentArity.Zero };
    public Option<bool> Control { get; } = new("--control") { Description = "Enable desktop ownership, input protection and control indicator (the default). Restart required.", Arity = ArgumentArity.Zero };
    public Option<bool> NoControl { get; } = new("--no-control") { Description = "Disable desktop ownership, input protection and control indicator. Restart required.", Arity = ArgumentArity.Zero };
    public Option<double> ControlIdleTimeout { get; } = new("--control-idle-timeout") { Description = "Positive finite user idle wait in seconds before control resumes. Restart required.", DefaultValueFactory = _ => 10.0 };
    public Option<string> ControlPolicy { get; } = new("--control-policy") { Description = "Desktop gate: relaxed (direct actions), balanced (also possible interference), strict (all tools). Restart required.", DefaultValueFactory = _ => "relaxed" };

    public IEnumerable<Option> All =>
    [
        Transport, Host, Port, Debug, Config, AuthKey, AllowInsecureRemote, IpAllowlist, Tools, ExcludeTools, CorsOrigins,
        SslCertfile, SslKeyfile, OAuthClientId, OAuthClientSecret, StatelessHttp, Control, NoControl, ControlIdleTimeout, ControlPolicy,
    ];

    /// <summary>The values given on the command line, else through their DESKTOP_MCP_* variable (Decisions 2).</summary>
    public ServeArguments Read(ParseResult result, Func<string, string?> environment)
    {
        var idle = Given(result, ControlIdleTimeout)
            ? result.GetValue(ControlIdleTimeout)
            : EnvValues.Float(environment("DESKTOP_MCP_CONTROL_IDLE_TIMEOUT"), "--control-idle-timeout");
        if (idle is { } seconds)
        {
            try { IdleTimeout.Validate(seconds); }
            catch (ConfigException e) { throw new CliUsageException($"Invalid value for '--control-idle-timeout': {e.Message}"); }
        }

        return new ServeArguments
        {
            Transport = Given(result, Transport) ? result.GetValue(Transport) : null,
            Host = Given(result, Host) ? result.GetValue(Host) : null,
            Port = Given(result, Port) ? result.GetValue(Port) : null,
            Debug = result.GetValue(Debug) || EnvValues.IsDebug(environment("DESKTOP_MCP_DEBUG")),
            AuthKey = StringOrVariable(result, AuthKey, environment, "DESKTOP_MCP_AUTH_KEY"),
            AllowInsecureRemote = Given(result, AllowInsecureRemote) ? true : null,
            IpAllowlist = StringOrVariable(result, IpAllowlist, environment, "DESKTOP_MCP_IP_ALLOWLIST"),
            Tools = StringOrVariable(result, Tools, environment, "DESKTOP_MCP_TOOLS"),
            ExcludeTools = StringOrVariable(result, ExcludeTools, environment, "DESKTOP_MCP_EXCLUDE_TOOLS"),
            CorsOrigins = StringOrVariable(result, CorsOrigins, environment, "DESKTOP_MCP_CORS_ORIGINS"),
            SslCertfile = StringOrVariable(result, SslCertfile, environment, "DESKTOP_MCP_SSL_CERTFILE"),
            SslKeyfile = StringOrVariable(result, SslKeyfile, environment, "DESKTOP_MCP_SSL_KEYFILE"),
            OAuthClientId = StringOrVariable(result, OAuthClientId, environment, "DESKTOP_MCP_OAUTH_CLIENT_ID"),
            OAuthClientSecret = StringOrVariable(result, OAuthClientSecret, environment, "DESKTOP_MCP_OAUTH_CLIENT_SECRET"),
            StatelessHttp = Given(result, StatelessHttp) ? true : EnvValues.Bool(environment("DESKTOP_MCP_STATELESS_HTTP"), "--stateless-http"),
            Control = LastControlFlag(result) ?? EnvValues.Bool(environment("DESKTOP_MCP_CONTROL"), "--control"),
            ControlIdleTimeout = idle,
            ControlPolicy = Given(result, ControlPolicy)
                ? result.GetValue(ControlPolicy)
                : EnvValues.Choice(environment("DESKTOP_MCP_CONTROL_POLICY"), "--control-policy", ConfigFile.Policies),
        };
    }

    private static bool Given(ParseResult result, Option option) => result.GetResult(option) is { Implicit: false };

    private static string? StringOrVariable(ParseResult result, Option<string> option, Func<string, string?> environment, string variable) =>
        Given(result, option) ? result.GetValue(option) : environment(variable) is { Length: > 0 } value ? value : null;

    // click's --control/--no-control is one option; the last occurrence wins.
    private static bool? LastControlFlag(ParseResult result)
    {
        bool? value = null;
        foreach (var token in result.Tokens)
        {
            if (token.Value == "--control") value = true;
            else if (token.Value == "--no-control") value = false;
        }
        return value;
    }
}
