using DesktopMcp.Core.Config;
using DesktopMcp.Server.Hosting;

namespace DesktopMcp.Server.Cli;

/// <summary>Where the CLI reads and writes. Tests replace every member; production uses the console and the process.</summary>
public sealed class CliEnvironment
{
    public required TextWriter Out { get; init; }
    public required TextWriter Error { get; init; }
    public required Func<string, string?> GetVariable { get; init; }
    public required string HomeDirectory { get; init; }
    public required Func<ServePlan, CancellationToken, Task<int>> Serve { get; init; }

    public static CliEnvironment CreateDefault() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        GetVariable = Environment.GetEnvironmentVariable,
        HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Serve = ServeCommand.RunAsync,
    };
}
