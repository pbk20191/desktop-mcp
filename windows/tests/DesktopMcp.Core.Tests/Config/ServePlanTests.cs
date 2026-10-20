using DesktopMcp.Core.Config;

namespace DesktopMcp.Core.Tests;

public class ServePlanTests
{
    private const string RefusalForAll =
        "Refusing to bind HTTP transport to '0.0.0.0' without authentication.\n" +
        "  Use --auth-key <token> or --oauth-client-id/--oauth-client-secret.\n" +
        "  Or pass --allow-insecure-remote to explicitly allow unauthenticated access (not recommended).";

    private static ServePlan Resolve(ServeArguments arguments, DesktopMcpConfig? config = null) =>
        ServePlan.Resolve(arguments, config ?? new DesktopMcpConfig());

    private static string Error(ServeArguments arguments, DesktopMcpConfig? config = null) =>
        Assert.Throws<ConfigException>(() => Resolve(arguments, config)).Message;

    [Fact]
    public void DefaultsAreStdioOnLocalhost()
    {
        var plan = Resolve(new ServeArguments());
        Assert.Equal("stdio", plan.Transport);
        Assert.Equal("localhost", plan.Host);
        Assert.Equal(8000, plan.Port);
        Assert.Null(plan.AuthKey);
        Assert.Empty(plan.ExplicitTools);
        Assert.Empty(plan.ExcludeTools);
        Assert.Null(plan.AllowedHosts);
        Assert.Empty(plan.Warnings);
        Assert.Equal(new ControlSettings(true, "relaxed", 10.0, 500, 500), plan.Control);
        Assert.Equal("http", plan.Scheme);
    }

    [Fact]
    public void ExplicitValuesBeatTheConfigWhichBeatsDefaults()
    {
        var config = new DesktopMcpConfig();
        config.Server.Port = 9100;
        config.Server.Host = "127.0.0.1";
        config.Control.Policy = "strict";
        config.Control.Enabled = false;
        var plan = Resolve(new ServeArguments { Port = 9200, ControlPolicy = "balanced" }, config);
        Assert.Equal(9200, plan.Port);
        Assert.Equal("127.0.0.1", plan.Host);
        Assert.Equal("balanced", plan.Control.Policy);
        Assert.False(plan.Control.Enabled);
    }

    [Fact]
    public void ExplicitExcludeToolsReplaceTheConfigList()
    {
        var config = new DesktopMcpConfig();
        config.Tools.Exclude = ["Click"];
        Assert.Equal(["Click"], Resolve(new ServeArguments(), config).ExcludeTools);
        Assert.Equal(["PowerShell", "Registry"], Resolve(new ServeArguments { ExcludeTools = " PowerShell, ,Registry " }, config).ExcludeTools);
    }

    [Fact]
    public void ToolsListIsSplitAndTrimmed() =>
        Assert.Equal(["Wait", "Scrape"], Resolve(new ServeArguments { Tools = "Wait, Scrape,," }).ExplicitTools);

    [Fact]
    public void EmptyExplicitAuthKeyOverridesTheConfigKey()
    {
        var config = new DesktopMcpConfig();
        config.Server.AuthKey = "from-config";
        Assert.Equal("from-config", Resolve(new ServeArguments(), config).AuthKey);
        Assert.Null(Resolve(new ServeArguments { AuthKey = "" }, config).AuthKey);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(65536L)]
    public void PortOutsideTheRangeIsRejected(long port) =>
        Assert.Equal("server.port must be between 0 and 65535", Error(new ServeArguments { Port = port }));

    [Fact]
    public void TlsFilesComeInPairs() =>
        Assert.Equal("--ssl-certfile and --ssl-keyfile must be provided together.", Error(new ServeArguments { SslCertfile = "c.pem" }));

    [Fact]
    public void OAuthCredentialsComeInPairs() =>
        Assert.Equal("OAuth requires both --oauth-client-id and --oauth-client-secret.", Error(new ServeArguments { OAuthClientSecret = "s" }));

    [Fact]
    public void BadAllowlistEntriesAreReported() =>
        Assert.Equal("Invalid ip_allowlist: Invalid IP allowlist entries: 'nope': 'nope' does not appear to be an IPv4 or IPv6 address",
            Error(new ServeArguments { IpAllowlist = "10.0.0.0/8,nope" }));

    [Fact]
    public void RemoteHttpBindNeedsAuthentication() =>
        Assert.Equal(RefusalForAll, Error(new ServeArguments { Transport = "sse", Host = "0.0.0.0" }));

    [Fact]
    public void AuthKeyOAuthOrInsecureFlagAllowARemoteBind()
    {
        Assert.Equal("0.0.0.0", Resolve(new ServeArguments { Transport = "sse", Host = "0.0.0.0", AuthKey = "k" }).Host);
        Assert.Equal("0.0.0.0", Resolve(new ServeArguments { Transport = "sse", Host = "0.0.0.0", OAuthClientId = "i", OAuthClientSecret = "s" }).Host);
        var insecure = Resolve(new ServeArguments { Transport = "streamable-http", Host = "0.0.0.0", AllowInsecureRemote = true });
        Assert.Null(insecure.AllowedHosts);
    }

    [Fact]
    public void AllowedHostsFollowTheBindAddress()
    {
        Assert.Equal(["localhost", "127.0.0.1", "[::1]"], Resolve(new ServeArguments { Transport = "sse" }).AllowedHosts);
        Assert.Equal(["10.0.0.5"], Resolve(new ServeArguments { Transport = "sse", Host = "10.0.0.5", AuthKey = "k" }).AllowedHosts);
        Assert.Null(Resolve(new ServeArguments { Transport = "sse", Host = "::", AuthKey = "k" }).AllowedHosts);
        Assert.Null(Resolve(new ServeArguments { Transport = "stdio" }).AllowedHosts);
    }

    [Fact]
    public void AuthOrAllowlistOnStdioIsAWarning()
    {
        Assert.Equal(["--auth-key / --ip-allowlist have no effect on stdio transport"], Resolve(new ServeArguments { AuthKey = "k" }).Warnings);
        Assert.Equal(["--auth-key / --ip-allowlist have no effect on stdio transport"], Resolve(new ServeArguments { IpAllowlist = "10.0.0.1" }).Warnings);
    }

    [Fact]
    public void TlsMakesTheSchemeHttps() =>
        Assert.Equal("https", Resolve(new ServeArguments { SslCertfile = "c.pem", SslKeyfile = "k.pem" }).Scheme);

    [Fact]
    public void ProcessAllowlistComesFromTheConfig()
    {
        var config = new DesktopMcpConfig();
        config.Control.ProcessAllowlist = ["notepad.exe"];
        Assert.Equal(["notepad.exe"], Resolve(new ServeArguments(), config).Control.ProcessAllowlist);
    }
}
