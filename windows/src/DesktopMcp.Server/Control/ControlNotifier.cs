using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using DesktopMcp.Core.Control;
using DesktopMcp.Desktop.Control;
using Microsoft.Extensions.Logging;

namespace DesktopMcp.Server.Control;

/// <summary>
/// Tells the live sessions when the user takes control and when it is the AI's again (spec §7; upstream
/// <c>tools/control_notifications.py:19-160</c>): an MCP log message at level <c>notice</c> from logger
/// <c>desktop-mcp.control</c> with <c>{"event": E, "status": S}</c>. Best effort: a failed send forgets the session.
/// </summary>
public sealed class ControlNotifier(IDesktopControl control, SessionRegistry sessions, ILogger log) : IAsyncDisposable
{
    /// <summary>The notifications' MCP logger name.</summary>
    public const string LoggerName = "desktop-mcp.control";

    /// <summary>The notifications' MCP log level.</summary>
    public const string Level = "notice";

    /// <summary>How long disposal waits for sends in flight before cancelling them.</summary>
    private static readonly TimeSpan SendGrace = TimeSpan.FromSeconds(1);

    private readonly Channel<ControlStatus> _queue = Channel.CreateUnbounded<ControlStatus>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _sendsLock = new();
    private readonly HashSet<Task> _sends = [];
    private readonly Dictionary<object, Task> _tails = [];
    private Task? _consumer;
    private ControlState _state;
    private long _generation;
    private int _started;
    private int _disposed;

    /// <summary>
    /// The event for a change from <paramref name="previous"/> to <paramref name="current"/>: <c>USER_CONTROL</c> when the
    /// user takes the desktop from <c>ready</c> or <c>ai</c>, <c>AI_CONTROL_AVAILABLE</c> when it returns from <c>user</c>
    /// to <c>ready</c>, else none.
    /// </summary>
    public static string? EventFor(ControlState previous, ControlState current) => (previous, current) switch
    {
        (ControlState.Ready or ControlState.Ai, ControlState.User) => ControlCodes.UserControl,
        (ControlState.User, ControlState.Ready) => "AI_CONTROL_AVAILABLE",
        _ => null,
    };

    /// <summary>Reads the initial status and subscribes to the control's state changes.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        var initial = control.Status();
        _state = initial.State;
        _generation = initial.Generation;
        control.StateChanged += OnStateChanged;
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>
    /// Unsubscribes, handles the changes already queued, and waits for the sends in flight (cancelling those still running
    /// after a short grace).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        control.StateChanged -= OnStateChanged;
        _queue.Writer.TryComplete();
        if (_consumer is { } consumer) await consumer.ConfigureAwait(false);
        Task[] sends;
        lock (_sendsLock) sends = [.. _sends];
        var all = Task.WhenAll(sends);
        try
        {
            await all.WaitAsync(SendGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await all.ConfigureAwait(false);
        }
        _stop.Dispose();
    }

    /// <summary>Raised by the control off its lock: only queues the status (handlers must not block).</summary>
    private void OnStateChanged(ControlStatus status) => _queue.Writer.TryWrite(status);

    private async Task ConsumeAsync()
    {
        await foreach (var status in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Handle(status);
            }
            catch (Exception e)
            {
                log.LogDebug(e, "A control notification could not be dispatched: {Message}", e.Message);
            }
        }
    }

    private void Handle(ControlStatus status)
    {
        // Changes are raised off the control's lock, so an older one can arrive late.
        if (status.Generation <= _generation) return;
        _generation = status.Generation;
        var previous = _state;
        _state = status.State;
        if (EventFor(previous, status.State) is not { } name) return;
        var message = new ControlLogMessage(Level, LoggerName, Data(name, status));
        foreach (var (key, session) in sessions.Live())
        {
            Task send;
            lock (_sendsLock)
            {
                // One chain per session: its notifications go out in order; a slow session holds up only itself.
                var previousSend = _tails.GetValueOrDefault(key) ?? Task.CompletedTask;
                send = SendAfterAsync(previousSend, key, session, message);
                _tails[key] = send;
                _sends.Add(send);
            }
            _ = send.ContinueWith(done =>
            {
                lock (_sendsLock)
                {
                    _sends.Remove(done);
                    if (_tails.TryGetValue(key, out var tail) && tail == done) _tails.Remove(key);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Sends after <paramref name="previous"/> (which never fails) has finished, on the thread pool.</summary>
    private async Task SendAfterAsync(Task previous, object key, IControlSession session, ControlLogMessage message)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing);
        await SendAsync(key, session, message).ConfigureAwait(false);
    }

    private async Task SendAsync(object key, IControlSession session, ControlLogMessage message)
    {
        try
        {
            await session.NotifyAsync(message, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            sessions.Forget(key, session);
            log.LogDebug(e, "MCP control notification failed; session removed: {Message}", e.Message);
        }
    }

    /// <summary><c>{"event": E, "status": S}</c>, the status in <see cref="ControlJson.Status"/>'s keys and order.</summary>
    private static JsonElement Data(string name, ControlStatus status)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("event", name);
            writer.WritePropertyName("status");
            ControlJson.WriteStatus(writer, status);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
