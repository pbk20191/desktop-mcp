namespace DesktopMcp.Core.Config;

/// <summary>~/.desktop-mcp/config.toml with upstream's sections and defaults (infrastructure/config.py:22-69).</summary>
public sealed class DesktopMcpConfig
{
    public ServerConfig Server { get; } = new();
    public SecurityConfig Security { get; } = new();
    public ToolsConfig Tools { get; } = new();
    public ControlConfig Control { get; } = new();
    public string? SourcePath { get; set; }
}

public sealed class ServerConfig
{
    public string Transport { get; set; } = "stdio";
    public string Host { get; set; } = "localhost";
    public long Port { get; set; } = 8000;
    public bool AllowInsecureRemote { get; set; }
    public string? AuthKey { get; set; }
    public string? SslCertfile { get; set; }
    public string? SslKeyfile { get; set; }
    public bool StatelessHttp { get; set; }
}

public sealed class SecurityConfig
{
    public List<string> IpAllowlist { get; set; } = [];
    public List<string> CorsOrigins { get; set; } = [];
    public string? OAuthClientId { get; set; }
    public string? OAuthClientSecret { get; set; }
}

public sealed class ToolsConfig
{
    public List<string> Exclude { get; set; } = [];
}

public sealed class ControlConfig
{
    public long MouseTakeoverUnits { get; set; } = 500;
    public long MouseTakeoverPixels { get; set; } = 500;
    public bool Enabled { get; set; } = true;
    public double IdleTimeoutSeconds { get; set; } = 10.0;
    public string Policy { get; set; } = "relaxed";
    public List<string> ProcessAllowlist { get; set; } = [];
}
