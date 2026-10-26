using DesktopMcp.Core.Config;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Server.Cli;
using DesktopMcp.Server.Hosting;

namespace DesktopMcp.Server.Tests;

/// <summary>Tests that change this process's environment run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ServeCommandTests
{
    [Fact]
    public async Task ServeRemovesItsSecretsFromTheProcessEnvironmentAfterReadingThem()
    {
        // Final review M1: the App tool's children inherit the server's environment, so the secrets must not stay in it.
        string[] names = ["DESKTOP_MCP_AUTH_KEY", "DESKTOP_MCP_OAUTH_CLIENT_ID", "DESKTOP_MCP_OAUTH_CLIENT_SECRET"];
        Assert.Equal(["DESKTOP_MCP_AUTH_KEY", "DESKTOP_MCP_OAUTH_CLIENT_SECRET"], ShellEnvironment.SecretVariables);
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var home = Path.Combine(Path.GetTempPath(), "dmcp-serve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            Environment.SetEnvironmentVariable("DESKTOP_MCP_AUTH_KEY", "key-from-env");
            Environment.SetEnvironmentVariable("DESKTOP_MCP_OAUTH_CLIENT_ID", "client");
            Environment.SetEnvironmentVariable("DESKTOP_MCP_OAUTH_CLIENT_SECRET", "secret-from-env");
            ServePlan? served = null;
            (string? Key, string? Secret, string? ClientId)? seenByHost = null;
            var joined = false;
            using var error = new StringWriter();
            var code = await CliApp.RunAsync(["serve", "--transport", "streamable-http"], new CliEnvironment
            {
                Out = TextWriter.Null,
                Error = error,
                GetVariable = Environment.GetEnvironmentVariable,
                HomeDirectory = home,
                Serve = (plan, ct) => ServeCommand.RunAsync(plan, () => joined = true, (p, _) =>
                {
                    served = p;
                    seenByHost = (Environment.GetEnvironmentVariable("DESKTOP_MCP_AUTH_KEY"),
                        Environment.GetEnvironmentVariable("DESKTOP_MCP_OAUTH_CLIENT_SECRET"),
                        Environment.GetEnvironmentVariable("DESKTOP_MCP_OAUTH_CLIENT_ID"));
                    return Task.FromResult(0);
                }, ct),
            }, CancellationToken.None);

            Assert.True(code == 0, error.ToString());
            Assert.True(joined);
            Assert.NotNull(served);
            Assert.Equal(("key-from-env", "secret-from-env"), (served.AuthKey, served.OAuthClientSecret));
            // Gone before the host starts; the client id is not a secret and stays.
            Assert.Equal((null, null, "client"), seenByHost);
            Assert.Null(Environment.GetEnvironmentVariable("DESKTOP_MCP_AUTH_KEY"));
            Assert.Null(Environment.GetEnvironmentVariable("DESKTOP_MCP_OAUTH_CLIENT_SECRET"));
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            Directory.Delete(home, recursive: true);
        }
    }
}
