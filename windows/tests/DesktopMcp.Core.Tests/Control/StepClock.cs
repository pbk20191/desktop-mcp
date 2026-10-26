namespace DesktopMcp.Core.Tests;

/// <summary>
/// A clock whose timestamps are the ticks of <see cref="Now"/>. It starts at 1,000 s because a zero stamp
/// means "no input yet" (upstream <c>control.py:231</c>).
/// </summary>
public sealed class StepClock : TimeProvider
{
    public TimeSpan Now { get; set; } = TimeSpan.FromSeconds(1000);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Now.Ticks;

    public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);

    public long Stamp() => GetTimestamp();
}
