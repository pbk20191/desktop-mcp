using DesktopMcp.Core.Config;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Server.Http;

namespace DesktopMcp.Server.Hosting;

/// <summary>serve: the process root for every transport (spec §5; Decisions 19), then the stdio or HTTP host.</summary>
public static class ServeCommand
{
    public static Task<int> RunAsync(ServePlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // spec §5: every process the server starts dies with it. Here and not in the hosts, so the test host never joins
        // a kill-on-close job.
        if (!ProcessJob.TryAssignKillOnClose())
            plan = plan with { Warnings = [.. plan.Warnings, "Could not put the server in a kill-on-close Job Object; PowerShell children may outlive it."] };
        return plan.Transport == "stdio" ? StdioServer.RunAsync(plan, ct) : HttpServer.RunAsync(plan, ct);
    }
}
