using DesktopMcp.Core.Config;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Server.Http;

namespace DesktopMcp.Server.Hosting;

/// <summary>serve: the process root for every transport (spec §5; Decisions 19), then the stdio or HTTP host.</summary>
public static class ServeCommand
{
    public static Task<int> RunAsync(ServePlan plan, CancellationToken ct) =>
        RunAsync(plan, ProcessJob.TryAssignKillOnClose, (p, token) => p.Transport == "stdio" ? StdioServer.RunAsync(p, token) : HttpServer.RunAsync(p, token), ct);

    /// <summary>
    /// The steps of <see cref="RunAsync(ServePlan, CancellationToken)"/> with the job and the host replaceable, so a test
    /// never joins a kill-on-close job or starts a host.
    /// </summary>
    internal static Task<int> RunAsync(ServePlan plan, Func<bool> assignKillOnClose, Func<ServePlan, CancellationToken, Task<int>> host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // The plan holds the secrets now; no process the server starts (PowerShell, the App tool's CreateProcess and
        // ShellExecuteEx) inherits them from here on (spec §8).
        RemoveSecretVariables();
        // spec §5: every process the server starts dies with it. Here and not in the hosts, so the test host never joins
        // a kill-on-close job.
        if (!assignKillOnClose())
            plan = plan with { Warnings = [.. plan.Warnings, "Could not put the server in a kill-on-close Job Object; PowerShell children may outlive it."] };
        return host(plan, ct);
    }

    /// <summary>Removes <see cref="ShellEnvironment.SecretVariables"/> from this process's environment.</summary>
    internal static void RemoveSecretVariables()
    {
        foreach (var name in ShellEnvironment.SecretVariables) Environment.SetEnvironmentVariable(name, null);
    }
}
