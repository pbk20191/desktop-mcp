namespace DesktopMcp.Desktop.Tests;

/// <summary>A clock the test sets by hand. The only fake clock in this project (P2 preflight F18): share it.</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
