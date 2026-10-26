namespace DesktopMcp.Core.Tests;

/// <summary>
/// Tests that read or change the process working directory. xUnit runs test classes in parallel, so these run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CwdSensitiveCollection
{
    public const string Name = "CwdSensitive";
}
