using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Control;

/// <summary>One MCP log message for the control notifications (spec §7; the fields of <c>notifications/message</c>).</summary>
/// <param name="Level">An MCP log level name, such as <c>notice</c>.</param>
public sealed record ControlLogMessage(string Level, string Logger, JsonElement Data);

/// <summary>A client session the control notifications go to.</summary>
public interface IControlSession
{
    ValueTask NotifyAsync(ControlLogMessage message, CancellationToken ct);
}

/// <summary>An MCP server session: the message goes out as raw <c>notifications/message</c> (<see cref="McpLogging"/>).</summary>
internal sealed class McpServerSession(McpServer server) : IControlSession
{
    public async ValueTask NotifyAsync(ControlLogMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        // A node per send: the element is shared by every session, a JsonNode over it is not safe to share.
        await McpLogging.SendMessageAsync(server, message.Level, message.Logger, JsonObject.Create(message.Data), ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The sessions that called a tool recently (upstream <c>tools/control_notifications.py</c> sessions; spec §7). A session
/// is remembered on each tool call and is live until <c>ttl</c> passes without one: <c>null</c> keeps it for ever (stdio
/// has one session for the process's life), <see cref="TimeSpan.Zero"/> never notifies (stateless transports). Expired
/// sessions are dropped on every <see cref="Remember"/> as well as on <see cref="Live"/>, so a long-running HTTP server
/// whose control state never changes does not keep every session it ever served.
/// </summary>
public sealed class SessionRegistry(TimeProvider time, TimeSpan? ttl)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<object, (IControlSession Session, long LastSeen)> _sessions = [];

    /// <summary>Remembers <paramref name="session"/> under <paramref name="key"/> and refreshes its last-seen time.</summary>
    public void Remember(object key, IControlSession session)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(session);
        if (ttl == TimeSpan.Zero) return;
        var now = time.GetTimestamp();
        lock (_lock)
        {
            DropExpired(now);
            _sessions[key] = (session, now);
        }
    }

    /// <summary>The live sessions; expired ones are dropped.</summary>
    public IReadOnlyList<(object Key, IControlSession Session)> Live()
    {
        if (ttl == TimeSpan.Zero) return [];
        var now = time.GetTimestamp();
        lock (_lock)
        {
            DropExpired(now);
            return [.. _sessions.Select(pair => (pair.Key, pair.Value.Session))];
        }
    }

    /// <summary>The number of remembered sessions, expired ones included until the next sweep (for tests).</summary>
    internal int Count
    {
        get
        {
            lock (_lock) return _sessions.Count;
        }
    }

    public void Forget(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock) _sessions.Remove(key);
    }

    /// <summary>Forgets <paramref name="key"/> only while <paramref name="session"/> is still its session (a newer call keeps it).</summary>
    internal void Forget(object key, IControlSession session)
    {
        lock (_lock)
            if (_sessions.TryGetValue(key, out var entry) && ReferenceEquals(entry.Session, session)) _sessions.Remove(key);
    }

    /// <summary>Drops the sessions whose last call is more than <c>ttl</c> before <paramref name="now"/>; the caller holds the lock.</summary>
    private void DropExpired(long now)
    {
        if (ttl is not { } limit) return;
        List<object>? expired = null;
        foreach (var (key, entry) in _sessions)
            if (time.GetElapsedTime(entry.LastSeen, now) > limit) (expired ??= []).Add(key);
        if (expired is not null)
            foreach (var key in expired) _sessions.Remove(key);
    }
}
