using System.Runtime.InteropServices;
using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Tests.Errors;

public class ComErrorsTests
{
    [Fact]
    public void TranslatesComExceptionsWithTheHresult()
    {
        var error = ComErrors.Translate("Screen capture", new COMException("The device was removed.", unchecked((int)0x887A0005)));
        var desktop = Assert.IsType<DesktopException>(error);
        Assert.Equal("Screen capture failed: The device was removed. (HRESULT 0x887A0005)", desktop.Message);
        Assert.IsType<COMException>(desktop.InnerException);
    }

    [Fact]
    public void AnEmptyMessageStillNamesTheOperation() =>
        Assert.Equal("Image rendering failed (HRESULT 0x80004005)",
            ComErrors.Translate("Image rendering", new COMException("", unchecked((int)0x80004005))).Message);

    [Fact]
    public void ProjectedWinRtFailuresAreTranslatedToo() =>
        Assert.IsType<DesktopException>(ComErrors.Translate("Screen capture", new UnauthorizedAccessException("denied")));

    [Fact]
    public void OtherExceptionsPassThrough()
    {
        var argument = new ArgumentException("bad");
        Assert.Same(argument, ComErrors.Translate("x", argument));
        var canceled = new OperationCanceledException();
        Assert.Same(canceled, ComErrors.Translate("x", canceled));
        var ours = new DesktopException("already clear");
        Assert.Same(ours, ComErrors.Translate("x", ours));
    }

    [Theory]
    [InlineData(unchecked((int)0x80040201), true)]
    [InlineData(unchecked((int)0x80010108), true)]
    [InlineData(unchecked((int)0x80004005), false)]
    public void DeadElementCodes(int hresult, bool dead) => Assert.Equal(dead, ComErrors.IsDeadElement(new COMException("x", hresult)));

    [Fact]
    public void TimeoutsIncludeTheHangBreaker()
    {
        Assert.True(ComErrors.IsTimeout(new COMException("x", unchecked((int)0x80131505))));
        Assert.True(ComErrors.IsTimeout(new UiaTimeoutException("x")));
        Assert.False(ComErrors.IsTimeout(new TimeoutException("x")));
    }
}
