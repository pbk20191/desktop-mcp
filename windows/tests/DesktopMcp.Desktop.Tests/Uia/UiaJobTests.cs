using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Uia;

namespace DesktopMcp.Desktop.Tests.Uia;

public class UiaJobTests
{
    [Theory]
    [InlineData(0x80040201u)] // UIA_E_ELEMENTNOTAVAILABLE
    [InlineData(0x80010108u)] // RPC_E_DISCONNECTED
    [InlineData(0x80131505u)] // UIA_E_TIMEOUT
    [InlineData(0x8001011Fu)] // RPC_E_TIMEOUT
    public void DeadElementAndTimeoutErrorsStayRaw(uint hresult)
    {
        var error = new COMException("raw", unchecked((int)hresult));
        Assert.Same(error, Assert.Throws<COMException>(() => UiaJob.Run<int>("Probe", () => throw error)));
    }

    [Fact]
    public void OtherComErrorsBecomeDesktopExceptions()
    {
        var error = new COMException("broken", unchecked((int)0x80004005));
        var thrown = Assert.Throws<DesktopException>(() => UiaJob.Run<int>("Probe", () => throw error));
        Assert.Equal("Probe failed: broken (HRESULT 0x80004005)", thrown.Message);
        Assert.Same(error, thrown.InnerException);
    }

    [Fact]
    public void ManagedErrorsAndResultsPassThrough()
    {
        Assert.Equal(7, UiaJob.Run("Probe", () => 7));
        Assert.Throws<InvalidOperationException>(() => UiaJob.Run<int>("Probe", () => throw new InvalidOperationException()));
        Assert.Throws<OperationCanceledException>(() => UiaJob.Run<int>("Probe", () => throw new OperationCanceledException()));
    }
}
