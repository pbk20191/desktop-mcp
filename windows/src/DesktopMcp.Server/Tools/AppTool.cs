using DesktopMcp.Core.Input;
using DesktopMcp.Core.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/app.py:83-113 and desktop/service.py:478-516 app. The argument rules run in upstream's order, then a
/// missing name for launch or switch is an error (P3 Decisions 35); every mode holds the input lock (spec §7 counts all
/// four as act modes). A result the service marks as an error (switch failures, launch_executable's path errors) is an
/// <c>isError</c> result, as upstream raised; anything else is plain content (no outputSchema).
/// </summary>
public sealed class AppTool(Tool protocolTool, ToolServices services) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var mode = ActArguments.Choice(arguments.Get("mode"), "mode", "launch", "launch", "launch_executable", "resize", "switch");
        var name = arguments.OptionalString("name");
        var loc = ActArguments.Pair(arguments.Get("window_loc"), "window_loc");
        var size = ActArguments.Pair(arguments.Get("window_size"), "window_size");
        var executable = arguments.OptionalString("executable");
        var args = arguments.Get("args");
        var cwd = arguments.OptionalString("cwd");

        if (mode != "launch_executable" && (executable is not null || args is not null || cwd is not null))
            throw new ArgumentException("executable, args, and cwd require mode=\"launch_executable\"");
        IReadOnlyList<string> argList = [];
        if (mode == "launch_executable")
        {
            if (executable is null) throw new ArgumentException("executable is required for mode=\"launch_executable\"");
            if (name is not null || loc is not null || size is not null)
                throw new ArgumentException("name, window_loc, and window_size are not supported for mode=\"launch_executable\"");
            if (args is { } value) argList = Coercion.StringList(value, "args");
        }
        else if (mode is "launch" or "switch" && name is null)
        {
            throw new ArgumentException($"name is required for mode=\"{mode}\"");
        }

        using (await services.InputLock.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            // The desktop-session check (P2 Decisions 18) before any target is resolved.
            services.Targets.CheckSession();
            var result = mode switch
            {
                "launch" => await services.Apps.LaunchAsync(name!, cancellationToken).ConfigureAwait(false),
                "switch" => await services.Apps.SwitchAsync(name!, cancellationToken).ConfigureAwait(false),
                "resize" => await services.Apps.ResizeAsync(name, loc, size, cancellationToken).ConfigureAwait(false),
                _ => await services.Apps.LaunchExecutableAsync(executable!, argList, cwd, cancellationToken).ConfigureAwait(false),
            };
            if (result.IsError) throw new InvalidOperationException(result.Text);
            return ToolResults.Content(result.Text, null);
        }
    }
}
