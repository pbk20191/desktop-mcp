using System.Text.Json;
using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Control;
using DesktopMcp.Desktop.Input;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Control;

/// <summary>
/// Desktop ownership for tool calls (upstream <c>tools/control_notifications.py:204-296</c> <c>on_call_tool</c>; spec §5
/// serialisation, §7 rules). One lock serialises the calls that use the desktop: with control disabled, every call but
/// ControlStatus and <see cref="NeverUsesTheDesktop"/> (P3's one input lock, widened); with control enabled, the calls the
/// policy gates, each inside a <see cref="ControlLease"/>. The gate runs before the tool's own argument checks.
/// </summary>
public sealed class ControlGate(IDesktopControl control, TimeProvider time)
{
    /// <summary>How often a call waiting for the lock re-checks the status (upstream's 0.1 s).</summary>
    public static TimeSpan RecheckInterval { get; } = TimeSpan.FromSeconds(0.1);

    /// <summary>
    /// The tools that run without the lock when control is disabled (P5 plan, Decisions 22). Scrape only: it fetches a
    /// URL and may then wait, for as long as the client takes, on a <c>sampling/createMessage</c> answer that is often a
    /// human approval; holding the one lock for that would block every other call from every session. Under a policy
    /// that gates Scrape (strict) the policy still decides and the call keeps its lease, as upstream.
    /// </summary>
    internal static IReadOnlySet<string> NeverUsesTheDesktop { get; } = new HashSet<string>(StringComparer.Ordinal) { "Scrape" };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async ValueTask<CallToolResult> InvokeAsync(string toolName, IDictionary<string, JsonElement>? arguments,
        Func<CancellationToken, ValueTask<CallToolResult>> next, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(next);
        if (toolName == "ControlStatus") return await next(ct).ConfigureAwait(false);

        // DisabledControl always reports Enabled = false, whatever it was built with.
        var settings = control.Settings;
        if (!settings.Enabled)
        {
            if (NeverUsesTheDesktop.Contains(toolName)) return await RunOutsideALeaseAsync(toolName, next, ct).ConfigureAwait(false);
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await RunOutsideALeaseAsync(toolName, next, ct).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        if (!ControlPolicy.RequiresControl(settings.Policy, toolName, ControlPolicy.ModeOf(toolName, arguments)))
            return await RunOutsideALeaseAsync(toolName, next, ct).ConfigureAwait(false);

        if (await WaitForLockAsync(ct).ConfigureAwait(false) is { } rejected) return rejected;
        ControlLease? lease = null;
        try
        {
            try
            {
                lease = await control.BeginCallAsync(ct).ConfigureAwait(false);
            }
            catch (ControlRejectedException e)
            {
                return ToolResults.Rejection(e);
            }
            using (lease.Enter())
            {
                control.Checkpoint.Checkpoint();
                return await next(ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (ControlErrors.IsInterruption(e))
        {
            return ToolResults.Rejection(control.Interrupted(e, lease));
        }
        finally
        {
            lease?.Dispose();
            _lock.Release();
        }
    }

    /// <summary>
    /// Runs a call that has no lease (control disabled, or a call the policy does not gate) and maps its interruptions to
    /// results, so none escapes as the SDK's generic error.
    /// </summary>
    private static async ValueTask<CallToolResult> RunOutsideALeaseAsync(string toolName,
        Func<CancellationToken, ValueTask<CallToolResult>> next, CancellationToken ct)
    {
        try
        {
            return await next(ct).ConfigureAwait(false);
        }
        catch (InputUnavailableException e)
        {
            // Shutdown blocked new presses (Decisions 19): an ordinary error, as P3 reported it.
            return ToolResults.Error(toolName, e.Message);
        }
        catch (ControlRejectedException e)
        {
            return ToolResults.Rejection(e);
        }
    }

    /// <summary>
    /// Takes the lock, or returns the rejection for a status that is not <c>ready</c> or <c>ai</c>; the status is checked
    /// before the first wait and after every <see cref="RecheckInterval"/> slice, so a waiting call learns of a takeover.
    /// </summary>
    private async Task<CallToolResult?> WaitForLockAsync(CancellationToken ct)
    {
        while (true)
        {
            var status = control.Status();
            if (status.State is not (ControlState.Ready or ControlState.Ai))
                return ToolResults.Rejection(new ControlRejectedException(ControlCodes.ForState(status.State), status));
            using var slice = new CancellationTokenSource(RecheckInterval, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, slice.Token);
            try
            {
                await _lock.WaitAsync(linked.Token).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The slice ended: re-check the status.
            }
        }
    }
}

/// <summary>The <see cref="ControlGate"/> as the SDK's call-tool filter.</summary>
public static class ControlGateFilter
{
    /// <param name="remember">Called with each call's server before the gate (the session registry, Task 13).</param>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(ControlGate gate, Action<McpServer> remember)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(remember);
        return next => (request, ct) =>
        {
            remember(request.Server);
            var name = request.Params?.Name ?? "";
            return gate.InvokeAsync(name, request.Params?.Arguments, token => next(request, token), ct);
        };
    }
}
