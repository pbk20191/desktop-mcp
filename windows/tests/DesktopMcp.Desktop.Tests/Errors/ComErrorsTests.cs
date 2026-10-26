using System.Runtime.InteropServices;
using DesktopMcp.Desktop;
using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Tests.Errors;

public class ComErrorsTests
{
    [Fact]
    public void TranslatesComExceptionsWithTheHresult()
    {
        var error = ComErrors.Translate("Screen capture", new COMException("The device was removed.", unchecked((int)0x80004005)));
        var desktop = Assert.IsType<DesktopException>(error);
        Assert.Equal("Screen capture failed: The device was removed. (HRESULT 0x80004005)", desktop.Message);
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

    [Theory]
    [InlineData(0x80040201u, "the window or control is no longer available")]
    [InlineData(0x80000013u, "the window or control is no longer available")]
    [InlineData(0x80010108u, "the window or control is no longer available")]
    [InlineData(0x80010007u, "the window or control is no longer available")]
    [InlineData(0x80010012u, "the window or control is no longer available")]
    [InlineData(0x80010006u, "the window or control is no longer available")]
    [InlineData(0x800401FDu, "the window or control is no longer available")]
    [InlineData(0x800401FFu, "the window or control is no longer available")]
    [InlineData(0x80131505u, "the application did not respond in time")]
    [InlineData(0x8001011Fu, "the application did not respond in time")]
    [InlineData(0x80070005u, "access was denied; the target may be running elevated")]
    [InlineData(0x887A0005u, "the graphics device was lost")]
    [InlineData(0x887A0006u, "the graphics device was lost")]
    [InlineData(0x887A0007u, "the graphics device was lost")]
    [InlineData(0x887A0020u, "the graphics device was lost")]
    public void KnownHresultsGetFixedEnglishText(uint hresult, string text) =>
        Assert.Equal($"Op failed: {text} (HRESULT 0x{hresult:X8})",
            ComErrors.Translate("Op", new COMException("localised OS text", unchecked((int)hresult))).Message);

    [Fact]
    public void AHresultOnlyComExceptionGetsEnglishNotOsText() =>
        Assert.Equal("Op failed: the graphics device was lost (HRESULT 0x887A0005)",
            ComErrors.Translate("Op", new COMException { HResult = unchecked((int)0x887A0005) }).Message);

    [Fact]
    public void ClassNotRegisteredCarriesTheInstallHint()
    {
        var message = ComErrors.Translate("Screen capture", new COMException("x", unchecked((int)0x80040154))).Message;
        Assert.Contains(AppRuntime.InstallHint(), message);
        Assert.Contains(") or later from https://", message);
        Assert.EndsWith("(HRESULT 0x80040154)", message);
    }

    [Fact]
    public void PlainClrExceptionsPassThrough()
    {
        var cast = new InvalidCastException("bug");
        Assert.Same(cast, ComErrors.Translate("x", cast));
        var notImplemented = new NotImplementedException();
        Assert.Same(notImplemented, ComErrors.Translate("x", notImplemented));
    }
}
