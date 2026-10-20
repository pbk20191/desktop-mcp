namespace DesktopMcp.Desktop.Tree;

/// <summary>
/// upstream tree/budget.py:55-91 TreeElementBudget: how many output elements one capture may collect. This port also
/// counts visited elements (Decisions 9): a tree of output-free containers stops after <c>maxVisits</c> (at least 1), marked truncated.
/// </summary>
internal sealed class ElementBudget(int limit, int maxVisits)
{
    /// <summary>Visited elements allowed per output element.</summary>
    public const int VisitsPerElement = 50;

    private readonly int _maxVisits = Math.Max(1, maxVisits);
    private int _visits;

    /// <summary>
    /// A budget of <paramref name="limit"/> elements and <see cref="VisitsPerElement"/> visits per element. The cap is
    /// computed in long and clamped, since the limit may be int.MaxValue (DESKTOP_MCP_MAX_TREE_ELEMENTS).
    /// </summary>
    public ElementBudget(int limit) : this(limit, (int)Math.Min(int.MaxValue, (long)Math.Max(1, limit) * VisitsPerElement))
    {
    }

    public int Limit { get; } = Math.Max(1, limit);
    public int Count { get; private set; }
    public bool Truncated { get; private set; }

    public bool Exhausted => Count >= Limit || _visits >= _maxVisits;

    public int Remaining => Math.Max(0, Limit - Count);

    /// <summary>Visits left before the visit cap.</summary>
    public int RemainingVisits => Math.Max(0, _maxVisits - _visits);

    /// <summary>
    /// A budget for one walk attempt with what is left of this one (call it only when this one is not exhausted). It is
    /// merged back with <see cref="Absorb"/> only when the attempt succeeds, so an abandoned or failed walk, which may
    /// still be running on another thread, never touches this budget.
    /// </summary>
    public ElementBudget Slice() => new(Remaining, RemainingVisits);

    /// <summary>Adds what a successful <see cref="Slice"/> consumed; its truncation carries over.</summary>
    public void Absorb(ElementBudget slice)
    {
        Count = Math.Min(Limit, Count + slice.Count);
        _visits = Math.Min(_maxVisits, _visits + slice._visits);
        if (slice.Truncated || Count >= Limit) Truncated = true;
    }

    /// <summary>upstream try_consume: false (and truncated) once the limit is reached; reaching it exactly also truncates.</summary>
    public bool TryConsume(int amount = 1)
    {
        if (amount <= 0) return !Exhausted;
        if (Count >= Limit)
        {
            Truncated = true;
            return false;
        }
        if (amount > Remaining)
        {
            Count = Limit;
            Truncated = true;
            return false;
        }
        Count += amount;
        if (Count >= Limit) Truncated = true;
        return true;
    }

    /// <summary>Counts one visited element; reaching the visit cap marks the capture truncated, like reaching the limit.</summary>
    public bool Visit()
    {
        if (_visits >= _maxVisits)
        {
            Truncated = true;
            return false;
        }
        if (++_visits >= _maxVisits) Truncated = true;
        return true;
    }
}
