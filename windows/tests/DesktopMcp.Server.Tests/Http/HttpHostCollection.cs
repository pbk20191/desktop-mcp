namespace DesktopMcp.Server.Tests.Http;

/// <summary>
/// Every test that builds a <c>WebApplication</c> runs in this collection, one at a time (Decisions 4: the legacy-SSE
/// switch is set per process).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HttpHostCollection
{
    public const string Name = "HTTP host";
}
