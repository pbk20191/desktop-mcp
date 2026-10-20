using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Input;

public sealed class InputUnavailableException(string message) : DesktopException(message);

/// <summary>
/// Records every injected key or button down that is not released in the same batch, so it can always be released
/// (upstream control_ledger.py). The down and up actions only send; waits happen outside Press and Release.
/// </summary>
public sealed class InputLedger(Func<string, bool>? physicalDownDelivered = null)
{
    private readonly Lock _lock = new();
    private readonly List<(string Name, Action Up)> _held = [];
    // Written without the lock by BlockNew (the hook thread calls it through the filter, and Press holds the lock across
    // SendInput, which waits for the hooks); Press reads it under the lock, Enable writes it under the lock.
    private volatile bool _blocked;

    public IReadOnlyList<string> Pending
    {
        get
        {
            lock (_lock) return [.. _held.Select(h => h.Name)];
        }
    }

    public void Press(string name, Action sendDown, Action sendUp)
    {
        lock (_lock)
        {
            if (_blocked) throw new InputUnavailableException("AI input is unavailable");
            if (_held.Exists(h => h.Name == name)) throw new InvalidOperationException($"AI input already held: {name}");
            _held.Add((name, sendUp));
            try
            {
                sendDown();
            }
            catch
            {
                try
                {
                    ReleaseLocked(name);
                }
                catch
                {
                    // the down's own error is the one that matters
                }
                throw;
            }
        }
    }

    public void Release(string name)
    {
        lock (_lock) ReleaseLocked(name);
    }

    public void ReleaseAll()
    {
        lock (_lock)
        {
            Exception? first = null;
            foreach (var name in _held.Select(h => h.Name).ToArray())
            {
                try
                {
                    ReleaseLocked(name);
                }
                catch (Exception e)
                {
                    first ??= e;
                }
            }
            if (first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    /// <summary>
    /// Refuses new presses from now on; lock-free, so it never waits for a press in progress (the low-level hook calls
    /// it, and a press holds the lock across <c>SendInput</c>, which waits for the hooks). Held input stays held.
    /// </summary>
    public void BlockNew() => _blocked = true;

    public void Enable()
    {
        lock (_lock)
        {
            if (_held.Count > 0) throw new InvalidOperationException("AI input remains held");
            _blocked = false;
        }
    }

    // A failed up keeps its entry (shutdown tries again); a delivered physical down means the user holds the key.
    private void ReleaseLocked(string name)
    {
        var index = _held.FindIndex(h => h.Name == name);
        if (index < 0) return;
        if (physicalDownDelivered?.Invoke(name) != true) _held[index].Up();
        _held.RemoveAt(index);
    }
}
