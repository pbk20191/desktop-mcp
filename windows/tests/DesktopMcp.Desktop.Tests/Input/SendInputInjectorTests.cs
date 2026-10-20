using DesktopMcp.Core.Input;
using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Input;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DesktopMcp.Desktop.Tests.Input;

public class SendInputInjectorTests
{
    private sealed class Recorder
    {
        public List<INPUT> Inputs { get; } = [];
        public uint Result { get; set; } = uint.MaxValue; // MaxValue: report everything inserted
        public int Error { get; set; }
        public uint Send(ReadOnlySpan<INPUT> inputs, out int lastError)
        {
            Inputs.AddRange(inputs.ToArray());
            lastError = Error;
            return Result == uint.MaxValue ? (uint)inputs.Length : Result;
        }
    }

    private static SendInputInjector Create(Recorder recorder, bool elevated = false) =>
        new(recorder.Send, vk => vk == 0x12 ? (ushort)0x38 : (ushort)0x1E, () => elevated, () => "Taskmgr.exe");

    [Fact]
    public void EveryEventCarriesTheSignature()
    {
        var recorder = new Recorder();
        Create(recorder).Send([InjectedInput.MouseAt(MouseFlags.AbsoluteMove, (10, 20)), InjectedInput.Keyboard(new(0x41, '\0', false))]);
        Assert.All(recorder.Inputs, input => Assert.Equal((nuint)0x444D4350, input.type == INPUT_TYPE.INPUT_MOUSE ? input.Anonymous.mi.dwExtraInfo : input.Anonymous.ki.dwExtraInfo));
    }

    // Decisions 14: Alt is the left Alt (no extended flag); Right Ctrl, arrows and Win are extended.
    [Fact]
    public void OnlyExtendedKeysGetTheExtendedFlag()
    {
        var recorder = new Recorder();
        Create(recorder).Send([InjectedInput.Keyboard(new(0x12, '\0', false)), InjectedInput.Keyboard(new(0x25, '\0', true)),
            InjectedInput.Keyboard(new(0xA3, '\0', false)), InjectedInput.Keyboard(new(0x5B, '\0', false))]);
        Assert.Equal((ushort)0x38, recorder.Inputs[0].Anonymous.ki.wScan);
        Assert.False(recorder.Inputs[0].Anonymous.ki.dwFlags.HasFlag(KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY));
        Assert.True(recorder.Inputs[1].Anonymous.ki.dwFlags.HasFlag(KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY | KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP));
        Assert.True(recorder.Inputs[2].Anonymous.ki.dwFlags.HasFlag(KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY));
        Assert.True(recorder.Inputs[3].Anonymous.ki.dwFlags.HasFlag(KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY));
    }

    [Fact]
    public void UnicodeUnitsUseTheUnicodeFlag()
    {
        var recorder = new Recorder();
        Create(recorder).Send([InjectedInput.Keyboard(new(0, '한', false))]);
        Assert.Equal(('한', (ushort)0, KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE),
            ((char)recorder.Inputs[0].Anonymous.ki.wScan, (ushort)recorder.Inputs[0].Anonymous.ki.wVk, recorder.Inputs[0].Anonymous.ki.dwFlags));
    }

    [Fact]
    public void AccessDeniedWhileNotElevatedIsTheElevatedError()
    {
        var recorder = new Recorder { Result = 0, Error = 5 };
        var error = Assert.Throws<InputBlockedException>(() => Create(recorder).Send([InjectedInput.Wheel(120)]));
        Assert.Equal("Input blocked: the target window (Taskmgr.exe) runs elevated (as administrator) and this server does not, so Windows blocks input to it (UIPI). Run desktop-mcp elevated to control it.", error.Message);
    }

    [Fact]
    public void AnyOtherShortCountIsTheGenericError()
    {
        var recorder = new Recorder { Result = 1, Error = 0 };
        var error = Assert.Throws<DesktopException>(() => Create(recorder, elevated: true).Send([InjectedInput.Wheel(120), InjectedInput.Wheel(120)]));
        Assert.Equal("SendInput inserted 1 of 2 input events (Win32 error 0): the input was blocked, for example by the secure desktop or another application.", error.Message);
    }

    [Fact]
    public void OneCallIsOneBatch()
    {
        var calls = 0;
        var injector = new SendInputInjector((ReadOnlySpan<INPUT> inputs, out int error) => { calls++; error = 0; return (uint)inputs.Length; },
            _ => 0, () => false, () => null);
        injector.Send([InjectedInput.Wheel(1), InjectedInput.Wheel(2), InjectedInput.Wheel(3)]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void OwnProcessIntegrityIsReadableAndNotHigherThanItself()
    {
        Assert.NotNull(ProcessIntegrity.IntegrityRid(Environment.ProcessId));
        Assert.False(ProcessIntegrity.IsHigherThanCurrent(Environment.ProcessId));
        Assert.False(ProcessIntegrity.IsHigherThanCurrent(int.MaxValue));
    }

    [Theory]
    [InlineData(0xA1, false, 0x36)] // Right Shift is not extended
    [InlineData(0xA3, true, 0x1D)] // Right Ctrl
    [InlineData(0xA5, true, 0x38)] // Right Alt
    [InlineData(0x6F, true, 0x35)] // Divide (fixed 0x135, masked)
    [InlineData(0x5B, true, 0x5B)] // Win (fixed 0x15B)
    [InlineData(0x62, false, 0x50)] // Numpad2 (fixed, no flag)
    public void FixedScanCodesGiveTheFlagOnlyForRealExtendedKeys(int vk, bool extended, int scan)
    {
        var recorder = new Recorder();
        Create(recorder).Send([InjectedInput.Keyboard(new((ushort)vk, '\0', false))]);
        var ki = recorder.Inputs[0].Anonymous.ki;
        Assert.Equal(extended, ki.dwFlags.HasFlag(KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY));
        Assert.Equal((ushort)scan, ki.wScan);
    }

    [Fact]
    public void ScanExtendedKeysGetTheFlagAndAMaskedScan()
    {
        var recorder = new Recorder();
        // VK_APPS: not in FixedScanCodes; ScanExtendedKeys adds 0x100 to the mapped scan code.
        Create(recorder).Send([InjectedInput.Keyboard(new(0x5D, '\0', false))]);
        var ki = recorder.Inputs[0].Anonymous.ki;
        Assert.True(ki.dwFlags.HasFlag(KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY));
        Assert.Equal((ushort)0x1E, ki.wScan);
    }

    [Fact]
    public void SurrogatePairUnitsAreSentAsTwoUnicodeEvents()
    {
        var recorder = new Recorder();
        var pair = "😀";
        Create(recorder).Send([InjectedInput.Keyboard(new(0, pair[0], false)), InjectedInput.Keyboard(new(0, pair[1], false))]);
        Assert.Equal([pair[0], pair[1]], recorder.Inputs.Select(i => (char)i.Anonymous.ki.wScan));
        Assert.All(recorder.Inputs, i => Assert.Equal(KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE, i.Anonymous.ki.dwFlags));
    }

    [Fact]
    public void NegativeWheelDeltaIsTheTwosComplementMouseData()
    {
        var recorder = new Recorder();
        Create(recorder).Send([InjectedInput.Wheel(-120)]);
        var mi = recorder.Inputs[0].Anonymous.mi;
        Assert.Equal(0xFFFFFF88u, mi.mouseData);
        Assert.Equal(MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL, mi.dwFlags);
    }

    [Fact]
    public void MouseCoordinatesAndFlagsReachTheStruct()
    {
        var recorder = new Recorder();
        Create(recorder).Send([InjectedInput.MouseAt(MouseFlags.AbsoluteMove, (123, 456)), InjectedInput.MouseAt(MouseFlags.LeftDown, (0, 0))]);
        var move = recorder.Inputs[0].Anonymous.mi;
        Assert.Equal((123, 456), (move.dx, move.dy));
        Assert.Equal(MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK, move.dwFlags);
        Assert.Equal(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN, recorder.Inputs[1].Anonymous.mi.dwFlags);
    }

    [Fact]
    public void AccessDeniedOnAForegroundThatDoesNotOutrankUsIsTheGenericError()
    {
        var recorder = new Recorder { Result = 0, Error = 5 };
        var injector = new SendInputInjector(recorder.Send, _ => 0, () => false, () => "LogonUI.exe", () => false);
        var error = Assert.Throws<DesktopException>(() => injector.Send([InjectedInput.Wheel(120)]));
        Assert.IsNotType<InputBlockedException>(error);
        Assert.StartsWith("SendInput inserted 0 of 1 input events (Win32 error 5)", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)] // unknown still counts as UIPI
    public void AccessDeniedOnAHigherOrUnknownForegroundIsUipi(bool? higher)
    {
        var recorder = new Recorder { Result = 0, Error = 5 };
        var injector = new SendInputInjector(recorder.Send, _ => 0, () => false, () => "Taskmgr.exe", () => higher);
        Assert.Throws<InputBlockedException>(() => injector.Send([InjectedInput.Wheel(120)]));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    public void AShortCountOffTheDefaultDesktopIsTheSessionTextNotUipi(int win32Error)
    {
        var recorder = new Recorder { Result = 0, Error = win32Error };
        var injector = new SendInputInjector(recorder.Send, _ => 0, () => false, () => "Taskmgr.exe", () => true,
            () => throw new DesktopException("The desktop is not available: locked"));
        var error = Assert.Throws<DesktopException>(() => injector.Send([InjectedInput.Wheel(120)]));
        Assert.IsNotType<InputBlockedException>(error);
        Assert.Equal("The desktop is not available: locked", error.Message);
    }

    [Fact]
    public void TheSessionIsCheckedOnlyAfterAShortCount()
    {
        var checks = 0;
        var injector = new SendInputInjector(new Recorder().Send, _ => 0, () => false, () => null, () => null, () => checks++);
        injector.Send([InjectedInput.Wheel(120)]);
        Assert.Equal(0, checks);
    }
}
