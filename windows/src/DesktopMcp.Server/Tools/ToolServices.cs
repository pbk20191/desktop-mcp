namespace DesktopMcp.Server.Tools;

/// <summary>
/// The services the tools call. Tests replace the ones with side effects (clipboard, toasts) with fakes through
/// <c>with</c>. Creating the defaults touches nothing: no Windows App SDK, no desktop.
/// </summary>
public sealed record ToolServices
{
    public static ToolServices CreateDefault() => new();
}
