using DesktopMcp.Core.Config;
using DesktopMcp.Server.Cli;

namespace DesktopMcp.Server.Tests;

public sealed class CliAppTests : IDisposable
{
    private static readonly string[] ServeFlags =
    [
        "--transport", "--host", "--port", "--debug", "--config", "--auth-key", "--allow-insecure-remote", "--ip-allowlist",
        "--tools", "--exclude-tools", "--cors-origins", "--ssl-certfile", "--ssl-keyfile", "--oauth-client-id",
        "--oauth-client-secret", "--stateless-http", "--control", "--no-control", "--control-idle-timeout", "--control-policy",
    ];

    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dmcp-cli-" + Guid.NewGuid().ToString("N"));
    private ServePlan? _served;

    public CliAppTests() => Directory.CreateDirectory(_home);

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private Task<int> RunAsync(params string[] args) => CliApp.RunAsync(args, new CliEnvironment
    {
        Out = _out,
        Error = _error,
        GetVariable = name => _variables.GetValueOrDefault(name),
        HomeDirectory = _home,
        Serve = (plan, _) =>
        {
            _served = plan;
            return Task.FromResult(0);
        },
    }, CancellationToken.None);

    [Fact]
    public async Task NoArgumentsServesStdioWithDefaults()
    {
        Assert.Equal(0, await RunAsync());
        Assert.NotNull(_served);
        Assert.Equal("stdio", _served.Transport);
        Assert.Equal("localhost", _served.Host);
        Assert.Equal(8000, _served.Port);
        Assert.Equal(new ControlSettings(true, "relaxed", 10.0, 500, 500), _served.Control);
        Assert.Empty(_served.ExplicitTools);
    }

    [Fact]
    public async Task ServeFlagsReachThePlan()
    {
        Assert.Equal(0, await RunAsync("serve", "--port", "9001", "--tools", "Wait, Scrape", "--debug", "--control-policy", "strict", "--no-control"));
        Assert.Equal(9001, _served!.Port);
        Assert.Equal(["Wait", "Scrape"], _served.ExplicitTools);
        Assert.True(_served.Debug);
        Assert.Equal("strict", _served.Control.Policy);
        Assert.False(_served.Control.Enabled);
    }

    [Fact]
    public async Task TheLastControlFlagWins()
    {
        Assert.Equal(0, await RunAsync("serve", "--no-control", "--control"));
        Assert.True(_served!.Control.Enabled);
    }

    [Fact]
    public async Task EnvironmentVariablesFillOptionsThatWereNotGiven()
    {
        _variables["DESKTOP_MCP_EXCLUDE_TOOLS"] = "PowerShell";
        _variables["DESKTOP_MCP_DEBUG"] = "1";
        _variables["DESKTOP_MCP_CONTROL"] = "off";
        _variables["DESKTOP_MCP_CONTROL_IDLE_TIMEOUT"] = "2.5";
        _variables["DESKTOP_MCP_STATELESS_HTTP"] = "yes";
        _variables["DESKTOP_MCP_CONTROL_POLICY"] = "balanced";
        Assert.Equal(0, await RunAsync("serve"));
        Assert.Equal(["PowerShell"], _served!.ExcludeTools);
        Assert.True(_served.Debug);
        Assert.False(_served.Control.Enabled);
        Assert.Equal(2.5, _served.Control.IdleTimeoutSeconds);
        Assert.True(_served.StatelessHttp);
        Assert.Equal("balanced", _served.Control.Policy);
    }

    [Fact]
    public async Task TheCommandLineBeatsTheEnvironment()
    {
        _variables["DESKTOP_MCP_EXCLUDE_TOOLS"] = "PowerShell";
        Assert.Equal(0, await RunAsync("serve", "--exclude-tools", "Registry"));
        Assert.Equal(["Registry"], _served!.ExcludeTools);
    }

    [Fact]
    public async Task AnExplicitConfigFileIsRead()
    {
        var path = Path.Combine(_home, "custom.toml");
        File.WriteAllText(path, "[server]\nport = 8123\n[tools]\nexclude = [\"Click\"]\n");
        Assert.Equal(0, await RunAsync("serve", "--config", path));
        Assert.Equal(8123, _served!.Port);
        Assert.Equal(["Click"], _served.ExcludeTools);
    }

    [Fact]
    public async Task TheDefaultConfigFileIsDiscovered()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".desktop-mcp"));
        File.WriteAllText(Path.Combine(_home, ".desktop-mcp", "config.toml"), "[control]\npolicy = \"strict\"\n");
        Assert.Equal(0, await RunAsync());
        Assert.Equal("strict", _served!.Control.Policy);
    }

    [Fact]
    public async Task AMissingConfigFileIsAnError()
    {
        var path = Path.Combine(_home, "missing.toml");
        Assert.Equal(1, await RunAsync("serve", "--config", path));
        Assert.Contains($"Error: Config file not found: {path}", _error.ToString());
        Assert.Null(_served);
    }

    [Theory]
    [InlineData("sse")]
    [InlineData("streamable-http")]
    public async Task HttpTransportsArriveInP5(string transport)
    {
        Assert.Equal(1, await RunAsync("serve", "--transport", transport));
        Assert.Contains($"Error: The {transport} transport is not available in this build yet; it arrives in phase P5. Use --transport stdio.", _error.ToString());
        Assert.Null(_served);
    }

    [Fact]
    public async Task ARemoteBindWithoutAuthenticationIsRefusedFirst()
    {
        Assert.Equal(1, await RunAsync("serve", "--transport", "streamable-http", "--host", "0.0.0.0"));
        Assert.Contains("Error: Refusing to bind HTTP transport to '0.0.0.0' without authentication.", _error.ToString());
    }

    [Fact]
    public async Task ServeFlagsBeforeASubcommandGetUpstreamsUsageError()
    {
        Assert.Equal(2, await RunAsync("--port", "9000", "--tools", "a b"));
        var error = _error.ToString();
        Assert.Contains("Usage: desktop-mcp [OPTIONS] COMMAND [ARGS]...", error);
        Assert.Contains("Try 'desktop-mcp --help' for help.", error);
        Assert.Contains(
            "Error: `desktop-mcp` is now a command group. Did you mean:\n    desktop-mcp serve --port 9000 --tools 'a b'\n" +
            "These flags belong on the `serve` subcommand. See `desktop-mcp serve --help`.",
            error);
        Assert.Null(_served);
    }

    [Fact]
    public async Task ABadEnvironmentBooleanIsAUsageError()
    {
        _variables["DESKTOP_MCP_STATELESS_HTTP"] = "maybe";
        Assert.Equal(2, await RunAsync("serve"));
        Assert.Contains("Usage: desktop-mcp serve [OPTIONS]", _error.ToString());
        Assert.Contains("Error: Invalid value for '--stateless-http': 'maybe' is not a valid boolean.", _error.ToString());
    }

    [Fact]
    public async Task ABadIdleTimeoutIsAUsageError()
    {
        Assert.Equal(2, await RunAsync("serve", "--control-idle-timeout", "0"));
        Assert.Contains("Error: Invalid value for '--control-idle-timeout': control.idle_timeout_seconds must be a finite positive number", _error.ToString());
    }

    [Fact]
    public async Task ABadPolicyInTheEnvironmentIsAUsageError()
    {
        _variables["DESKTOP_MCP_CONTROL_POLICY"] = "lax";
        Assert.Equal(2, await RunAsync("serve"));
        Assert.Contains("Error: Invalid value for '--control-policy': 'lax' is not one of 'relaxed', 'balanced', 'strict'.", _error.ToString());
    }

    [Fact]
    public async Task AnUnknownOptionIsAUsageError()
    {
        Assert.Equal(2, await RunAsync("serve", "--bogus"));
        Assert.Contains("Usage: desktop-mcp serve [OPTIONS]", _error.ToString());
        Assert.Contains("--bogus", _error.ToString());
    }

    [Theory]
    [InlineData("install --force")]
    [InlineData("uninstall")]
    [InlineData("auth --with-tls --port 9000")]
    public async Task InstallUninstallAndAuthArriveInP6(string commandLine)
    {
        var args = commandLine.Split(' ');
        Assert.Equal(1, await RunAsync(args));
        Assert.Contains($"Error: `desktop-mcp {args[0]}` is not available in this build yet; it arrives in phase P6.", _error.ToString());
    }

    [Fact]
    public async Task ServeHelpListsEveryFlag()
    {
        Assert.Equal(0, await RunAsync("serve", "--help"));
        var help = _out.ToString();
        Assert.All(ServeFlags, flag => Assert.Contains(flag, help));
        Assert.Contains("Comma-separated list of tools to remove from the active set", help);
    }

    [Fact]
    public async Task RootAndSubcommandHelpIsComplete()
    {
        Assert.Equal(0, await RunAsync("--help"));
        Assert.All(new[] { "serve", "install", "uninstall", "auth" }, command => Assert.Contains(command, _out.ToString()));
        Assert.Equal(0, await RunAsync("install", "--help"));
        Assert.Contains("--force", _out.ToString());
        Assert.Equal(0, await RunAsync("auth", "--help"));
        Assert.Contains("--with-tls", _out.ToString());
    }
}
