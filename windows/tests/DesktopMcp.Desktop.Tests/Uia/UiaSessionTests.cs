using DesktopMcp.Desktop.Uia;
using Windows.Win32;

namespace DesktopMcp.Desktop.Tests.Uia;

[Trait("Category", "Desktop")]
public sealed class UiaSessionTests : IDisposable
{
    private readonly UiaWorker<UiaSession> _worker = new(UiaSession.Create);

    public void Dispose() => _worker.Dispose();

    [Fact]
    public async Task CreatesTheClientOnAnMtaThreadAndReadsTheRoot()
    {
        var (name, connection, transaction, apartment) = await OnWorker(session =>
        {
            var root = session.Automation.GetRootElement();
            var bstr = root.get_CurrentName();
            try
            {
                return (bstr.ToString() ?? "", session.Automation.get_ConnectionTimeout(), session.Automation.get_TransactionTimeout(),
                    Thread.CurrentThread.GetApartmentState());
            }
            finally { PInvoke.SysFreeString(bstr); }
        });
        Assert.NotEqual("", name);
        Assert.Equal(UiaSession.ConnectionTimeoutMs, connection);
        Assert.Equal(UiaSession.TransactionTimeoutMs, transaction);
        Assert.Equal(ApartmentState.MTA, apartment);
    }

    [Fact]
    public async Task CachesTheTraversalPropertiesForTheRootsChildren()
    {
        var count = await OnWorker(session =>
        {
            var children = session.Automation.GetRootElement().BuildUpdatedCache(session.ChildrenRequest).GetCachedChildren();
            var length = children?.get_Length() ?? 0;
            for (var i = 0; i < length; i++)
            {
                var child = children!.GetElement(i);
                // Each read throws E_INVALIDARG if the property is not in the cache request.
                PInvoke.SysFreeString(child.get_CachedName());
                _ = child.get_CachedBoundingRectangle();
                using var role = child.GetCachedPropertyValue(Windows.Win32.UI.Accessibility.UIA_PROPERTY_ID.UIA_LegacyIAccessibleRolePropertyId);
            }
            return length;
        });
        Assert.True(count > 0, "the desktop root has no top-level children");
    }

    private Task<T> OnWorker<T>(Func<UiaSession, T> work) => _worker.RunAsync((session, _) => work(session), TimeSpan.FromSeconds(30), default);
}
