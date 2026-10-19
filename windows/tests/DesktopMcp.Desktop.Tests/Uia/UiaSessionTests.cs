using DesktopMcp.Desktop.Uia;
using Windows.Win32;

namespace DesktopMcp.Desktop.Tests.Uia;

[Trait("Category", "Desktop")]
public class UiaSessionTests
{
    [Fact]
    public async Task CreatesTheClientOnAnMtaThreadAndReadsTheRoot()
    {
        var (name, connection, transaction) = await OnMtaThread(() =>
        {
            var session = UiaSession.Create();
            var root = session.Automation.GetRootElement();
            var bstr = root.get_CurrentName();
            try { return (bstr.ToString() ?? "", session.Automation.get_ConnectionTimeout(), session.Automation.get_TransactionTimeout()); }
            finally { PInvoke.SysFreeString(bstr); }
        });
        Assert.NotEqual("", name);
        Assert.Equal(UiaSession.ConnectionTimeoutMs, connection);
        Assert.Equal(UiaSession.TransactionTimeoutMs, transaction);
    }

    [Fact]
    public async Task CachesTheTraversalPropertiesForTheRootsChildren()
    {
        var count = await OnMtaThread(() =>
        {
            var session = UiaSession.Create();
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

    private static Task<T> OnMtaThread<T>(Func<T> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(work()); }
            catch (Exception e) { result.SetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
