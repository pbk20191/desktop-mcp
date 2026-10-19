namespace DesktopMcp.Core.Config;

/// <summary>
/// The serve settings given on the command line or through their DESKTOP_MCP_* variables. A null member was given in
/// neither place, so <see cref="ServePlan.Resolve"/> falls back to config.toml and then to the default
/// (upstream _choose_value, __main__.py:123-133).
/// </summary>
public sealed record ServeArguments
{
    public string? Transport { get; init; }
    public string? Host { get; init; }
    public long? Port { get; init; }
    public bool Debug { get; init; }
    public string? AuthKey { get; init; }
    public bool? AllowInsecureRemote { get; init; }
    public string? IpAllowlist { get; init; }
    public string? Tools { get; init; }
    public string? ExcludeTools { get; init; }
    public string? CorsOrigins { get; init; }
    public string? SslCertfile { get; init; }
    public string? SslKeyfile { get; init; }
    public string? OAuthClientId { get; init; }
    public string? OAuthClientSecret { get; init; }
    public bool? StatelessHttp { get; init; }
    public bool? Control { get; init; }
    public double? ControlIdleTimeout { get; init; }
    public string? ControlPolicy { get; init; }
}
