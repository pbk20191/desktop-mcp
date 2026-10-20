using System.Xml.Linq;
using DesktopMcp.Desktop.Imaging;

namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// Guards the static-Win2D feature switch. These tests need no desktop session, so the class carries no
/// Category=Desktop trait and runs in the default <c>dotnet test</c>.
/// </summary>
public class Win2DStaticActivationTests
{
    [Fact]
    public void TargetsFileSetsTheSwitchTheActivationReads()
    {
        var targets = XDocument.Load(RepoFile.Find(Path.Combine("windows", "build", "Win2DStatic.targets")));
        var switches = targets.Descendants().Where(e => e.Name.LocalName == "Win2DStaticFeatureSwitch").Select(e => e.Value.Trim()).ToArray();
        Assert.Equal([Win2DStaticActivation.Switch], switches);
    }

    [Fact]
    public void JitBuildsKeepTheDefaultActivation()
    {
        Assert.False(Win2DStaticActivation.IsLinked);
        var before = WinRT.ActivationFactory.ActivationHandler;
        Win2DStaticActivation.Install();
        Assert.Same(before, WinRT.ActivationFactory.ActivationHandler);
    }
}
