using DesktopMcp.Core.Config;

namespace DesktopMcp.Core.Tests;

public sealed class ConfigFileTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private DesktopMcpConfig Load(string toml) => ConfigFile.Load(_temp.WriteFile("config.toml", toml));

    private string Error(string toml) => Assert.Throws<ConfigException>(() => Load(toml)).Message;

    [Fact]
    public void NoPathGivesUpstreamDefaults()
    {
        var config = ConfigFile.Load(null);
        Assert.Equal("stdio", config.Server.Transport);
        Assert.Equal("localhost", config.Server.Host);
        Assert.Equal(8000, config.Server.Port);
        Assert.False(config.Server.AllowInsecureRemote);
        Assert.Null(config.Server.AuthKey);
        Assert.False(config.Server.StatelessHttp);
        Assert.Empty(config.Security.IpAllowlist);
        Assert.Empty(config.Tools.Exclude);
        Assert.True(config.Control.Enabled);
        Assert.Equal(10.0, config.Control.IdleTimeoutSeconds);
        Assert.Equal("relaxed", config.Control.Policy);
        Assert.Equal(500, config.Control.MouseTakeoverUnits);
        Assert.Equal(500, config.Control.MouseTakeoverPixels);
        Assert.Null(config.SourcePath);
    }

    [Fact]
    public void MissingFileIsAnError()
    {
        var path = _temp.Combine("nope.toml");
        Assert.Equal($"Config file not found: {path}", Assert.Throws<ConfigException>(() => ConfigFile.Load(path)).Message);
    }

    [Fact]
    public void ReadsEverySection()
    {
        var config = Load("""
            [server]
            transport = "sse"
            host = "0.0.0.0"
            port = 9000
            allow_insecure_remote = true
            auth_key = "secret"
            ssl_certfile = "cert.pem"
            ssl_keyfile = "C:/keys/key.pem"
            stateless_http = true

            [security]
            ip_allowlist = ["10.0.0.0/8", "192.168.1.5"]
            cors_origins = ["https://a.example"]
            oauth_client_id = "id"
            oauth_client_secret = "shh"

            [tools]
            exclude = ["PowerShell", "Registry"]

            [control]
            policy = "strict"
            enabled = false
            idle_timeout_seconds = 2.5
            mouse_takeover_units = 40
            mouse_takeover_pixels = 2000
            """);
        Assert.Equal("sse", config.Server.Transport);
        Assert.Equal("0.0.0.0", config.Server.Host);
        Assert.Equal(9000, config.Server.Port);
        Assert.True(config.Server.AllowInsecureRemote);
        Assert.Equal("secret", config.Server.AuthKey);
        Assert.Equal(_temp.Combine("cert.pem"), config.Server.SslCertfile);
        Assert.Equal(@"C:\keys\key.pem", config.Server.SslKeyfile);
        Assert.True(config.Server.StatelessHttp);
        Assert.Equal(["10.0.0.0/8", "192.168.1.5"], config.Security.IpAllowlist);
        Assert.Equal(["https://a.example"], config.Security.CorsOrigins);
        Assert.Equal("id", config.Security.OAuthClientId);
        Assert.Equal("shh", config.Security.OAuthClientSecret);
        Assert.Equal(["PowerShell", "Registry"], config.Tools.Exclude);
        Assert.Equal("strict", config.Control.Policy);
        Assert.False(config.Control.Enabled);
        Assert.Equal(2.5, config.Control.IdleTimeoutSeconds);
        Assert.Equal(40, config.Control.MouseTakeoverUnits);
        Assert.Equal(2000, config.Control.MouseTakeoverPixels);
        Assert.Equal(_temp.Combine("config.toml"), config.SourcePath);
    }

    [Fact]
    public void IntegerIdleTimeoutIsAccepted() => Assert.Equal(3.0, Load("[control]\nidle_timeout_seconds = 3").Control.IdleTimeoutSeconds);

    [Fact]
    public void EmptyStringsMeanUnset()
    {
        var config = Load("[server]\nauth_key = \"\"\nssl_certfile = \"\"\n[security]\noauth_client_id = \"\"");
        Assert.Null(config.Server.AuthKey);
        Assert.Null(config.Server.SslCertfile);
        Assert.Null(config.Security.OAuthClientId);
    }

    [Theory]
    [InlineData("[server]\nport = true", "server.port must be a TOML integer, not bool")]
    [InlineData("[server]\nport = \"80\"", "server.port must be a TOML integer, not str")]
    [InlineData("[server]\nport = 1.5", "server.port must be a TOML integer, not float")]
    [InlineData("[server]\nport = 70000", "server.port must be between 0 and 65535")]
    [InlineData("[server]\ntransport = \"tcp\"", "server.transport must be one of {'stdio', 'sse', 'streamable-http'}, got 'tcp'")]
    [InlineData("[server]\ntransport = 1", "server.transport must be a TOML string, not int")]
    [InlineData("[server]\nhost = [\"a\"]", "server.host must be a TOML string, not list")]
    [InlineData("[server]\nallow_insecure_remote = 1", "server.allow_insecure_remote must be a TOML boolean, not int")]
    [InlineData("[server]\nstateless_http = \"true\"", "server.stateless_http must be a TOML boolean, not str")]
    [InlineData("[security]\nip_allowlist = [1]", "security.ip_allowlist must be an array of strings")]
    [InlineData("[security]\ncors_origins = \"x\"", "security.cors_origins must be an array of strings")]
    [InlineData("[security]\noauth_client_secret = 5", "security.oauth_client_secret must be a TOML string, not int")]
    [InlineData("[tools]\nexclude = \"PowerShell\"", "tools.exclude must be an array of strings")]
    [InlineData("[control]\npolicy = \"lax\"", "control.policy must be relaxed, balanced, or strict")]
    [InlineData("[control]\npolicy = 1", "control.policy must be relaxed, balanced, or strict")]
    [InlineData("[control]\nenabled = \"no\"", "control.enabled must be a TOML boolean, not str")]
    [InlineData("[control]\nidle_timeout_seconds = 0", "control.idle_timeout_seconds must be a finite positive number")]
    [InlineData("[control]\nidle_timeout_seconds = true", "control.idle_timeout_seconds must be a finite positive number")]
    [InlineData("[control]\nidle_timeout_seconds = inf", "control.idle_timeout_seconds must be a finite positive number")]
    [InlineData("[control]\nmouse_takeover_units = 39", "control.mouse_takeover_units must be between 40 and 2000")]
    [InlineData("[control]\nmouse_takeover_pixels = true", "control.mouse_takeover_pixels must be a TOML integer, not bool")]
    [InlineData("server = 5", "server must be a TOML table")]
    public void StrictTypesUseUpstreamMessages(string toml, string expected) => Assert.Equal(expected, Error(toml));

    [Fact]
    public void SyntaxErrorsNameTheFile() =>
        Assert.StartsWith($"Invalid TOML in {_temp.Combine("config.toml")}: (1,8)", Error("[server\nport = 1\n"));

    [Fact]
    public void DiscoverPrefersTheExplicitPathAndExpandsTilde()
    {
        Assert.Equal(_temp.Combine("x.toml"), ConfigFile.Discover("~/x.toml", _temp.Path));
        Assert.Equal(_temp.Combine("y.toml"), ConfigFile.Discover(@"~\y.toml", _temp.Path));
        Assert.Equal(@"D:\cfg.toml", ConfigFile.Discover(@"D:\cfg.toml", _temp.Path));
    }

    [Fact]
    public void DiscoverFindsTheDefaultFileOnlyWhenItExists()
    {
        Assert.Null(ConfigFile.Discover(null, _temp.Path));
        var path = _temp.WriteFile(Path.Combine(".desktop-mcp", "config.toml"), "");
        Assert.Equal(path, ConfigFile.Discover(null, _temp.Path));
        Assert.Equal("stdio", ConfigFile.Load(path).Server.Transport);
    }

    [Fact]
    public void ProcessAllowlistIsAListOfNames() =>
        Assert.Equal(["notepad.exe", "code"], Load("[control]\nprocess_allowlist = [\"notepad.exe\", \"code\"]").Control.ProcessAllowlist);

    [Fact]
    public void ProcessAllowlistMustBeAList() =>
        Assert.Equal("control.process_allowlist must be an array of strings", Error("[control]\nprocess_allowlist = \"notepad.exe\""));

    [Fact]
    public void ProcessAllowlistRejectsPaths() =>
        Assert.Equal(DesktopMcp.Core.Control.ProcessAllowlist.InvalidEntry, Error("[control]\nprocess_allowlist = [\"tools/x.exe\"]"));
}
