using System.CommandLine;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.Text;

namespace DesktopMcp.Server.Cli;

/// <summary>
/// The desktop-mcp command line (spec §8): serve (the default), install, uninstall and auth with upstream's flags and
/// messages (__main__.py:397-1168). In P1, install, uninstall, auth and the HTTP transports parse their options and
/// report the phase that brings them.
/// </summary>
public static class CliApp
{
    public const string Name = "desktop-mcp";

    private static readonly string[] SubcommandNames = ["serve", "install", "uninstall", "auth"];

    // __main__.py:397-417
    private static readonly HashSet<string> LegacyServeFlags =
    [
        "--transport", "--host", "--port", "--auth-key", "--stateless-http", "--ip-allowlist", "--cors-origins",
        "--ssl-certfile", "--ssl-keyfile", "--oauth-client-id", "--oauth-client-secret", "--tools", "--exclude-tools",
        "--allow-insecure-remote", "--debug", "--control", "--no-control", "--control-idle-timeout", "--control-policy",
    ];

    public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default) =>
        RunAsync(args, CliEnvironment.CreateDefault(), cancellationToken);

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CliEnvironment environment, CancellationToken cancellationToken)
    {
        // upstream _LegacyAwareGroup (__main__.py:420-444)
        if (FirstLegacyArgument(args) is not null)
        {
            var suggested = PyText.ShellJoin([Name, "serve", .. args]);
            return UsageError(environment, Name, "[OPTIONS] COMMAND [ARGS]...",
                $"`{Name}` is now a command group. Did you mean:\n    {suggested}\n" +
                $"These flags belong on the `serve` subcommand. See `{Name} serve --help`.");
        }

        // A bare desktop-mcp serves (Decisions 1).
        IReadOnlyList<string> effective = args.Count == 0 ? ["serve"] : args;
        var root = BuildRootCommand(environment);
        var result = root.Parse(effective, new ParserConfiguration { ResponseFileTokenReplacer = null });
        if (result.Errors.Count > 0)
        {
            var command = result.CommandResult.Command;
            var isRoot = ReferenceEquals(command, root);
            return UsageError(environment, isRoot ? Name : $"{Name} {command.Name}", isRoot ? "[OPTIONS] COMMAND [ARGS]..." : "[OPTIONS]",
                string.Join("\n", result.Errors.Select(e => e.Message)));
        }
        return await result.InvokeAsync(new InvocationConfiguration { Output = environment.Out, Error = environment.Error }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? FirstLegacyArgument(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (SubcommandNames.Contains(arg)) return null;
            if (LegacyServeFlags.Contains(arg.Split('=', 2)[0])) return arg;
        }
        return null;
    }

    private static RootCommand BuildRootCommand(CliEnvironment environment)
    {
        var root = new RootCommand($"{Name}: MCP server for Windows desktop automation.");

        var serveOptions = new ServeOptions();
        var serve = new Command("serve", "Run the MCP server (the default command).");
        foreach (var option in serveOptions.All) serve.Options.Add(option);
        serve.SetAction((result, cancellationToken) => ServeAsync(result, serveOptions, environment, cancellationToken));
        root.Subcommands.Add(serve);

        root.Subcommands.Add(NotYetAvailable(environment, "install", "Install desktop-mcp as a scheduled task that starts at login.", "P6",
            Choice("--transport", "Transport for the background server (stdio not supported as a service).", "streamable-http", "sse", "streamable-http"),
            Text("--host", "Host to bind.", "127.0.0.1"),
            Number("--port", "Port to bind.", 8000),
            Flag("--force", "Reinstall even if already installed.")));
        root.Subcommands.Add(NotYetAvailable(environment, "uninstall", "Remove the desktop-mcp scheduled task and stop the background server.", "P6"));
        root.Subcommands.Add(NotYetAvailable(environment, "auth", "Generate an auth key (and optionally TLS certs) and save to ~/.desktop-mcp/config.toml.", "P6",
            Choice("--transport", "Transport mode to configure. Saves the choice to config.toml.", "sse", "stdio", "sse", "streamable-http"),
            Text("--host", "Host to bind the server to.", "0.0.0.0"),
            Number("--port", "Port to bind the server to.", 8000),
            Flag("--with-tls", "Generate a self-signed TLS certificate and key."),
            Flag("--force", "Overwrite existing credentials without prompting.")));
        return root;
    }

    private static async Task<int> ServeAsync(ParseResult result, ServeOptions options, CliEnvironment environment, CancellationToken cancellationToken)
    {
        ServeArguments arguments;
        try { arguments = options.Read(result, environment.GetVariable); }
        catch (CliUsageException e) { return UsageError(environment, $"{Name} serve", "[OPTIONS]", e.Message); }

        ServePlan plan;
        try
        {
            var config = ConfigFile.Load(ConfigFile.Discover(result.GetValue(options.Config), environment.HomeDirectory));
            plan = ServePlan.Resolve(arguments, config);
        }
        catch (ConfigException e)
        {
            environment.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }

        if (plan.Transport != "stdio")
        {
            environment.Error.WriteLine($"Error: The {plan.Transport} transport is not available in this build yet; it arrives in phase P5. Use --transport stdio.");
            return 1;
        }
        return await environment.Serve(plan, cancellationToken).ConfigureAwait(false);
    }

    private static Command NotYetAvailable(CliEnvironment environment, string name, string description, string phase, params Option[] options)
    {
        var command = new Command(name, description);
        foreach (var option in options) command.Options.Add(option);
        command.SetAction(_ =>
        {
            environment.Error.WriteLine($"Error: `{Name} {name}` is not available in this build yet; it arrives in phase {phase}.");
            return 1;
        });
        return command;
    }

    private static Option<string> Choice(string name, string description, string defaultValue, params string[] choices)
    {
        var option = new Option<string>(name) { Description = description, DefaultValueFactory = _ => defaultValue };
        option.AcceptOnlyFromAmong(choices);
        return option;
    }

    private static Option<string> Text(string name, string description, string defaultValue) =>
        new(name) { Description = description, DefaultValueFactory = _ => defaultValue };

    private static Option<int> Number(string name, string description, int defaultValue) =>
        new(name) { Description = description, DefaultValueFactory = _ => defaultValue };

    private static Option<bool> Flag(string name, string description) =>
        new(name) { Description = description, Arity = ArgumentArity.Zero };

    // click's UsageError output (exit code 2).
    private static int UsageError(CliEnvironment environment, string commandPath, string usageArguments, string message)
    {
        environment.Error.WriteLine($"Usage: {commandPath} {usageArguments}");
        environment.Error.WriteLine($"Try '{commandPath} --help' for help.");
        environment.Error.WriteLine();
        environment.Error.WriteLine($"Error: {message}");
        return 2;
    }
}
