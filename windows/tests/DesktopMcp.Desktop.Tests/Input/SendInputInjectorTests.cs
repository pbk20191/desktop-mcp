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
        Create(recorder).Send([InjectedInput.Keyboard(new(0x12, '\0', false)), InjectedInput.Keyboard(new(0x25, '\0', true))]);
        Assert.Equal((ushort)0x38, recorder.Inputs[0].Anonymous.ki.wScan);
        Assert.False(recorder.Inputs[0].Anonymous.ki.dwFlags.HasFlag(KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY));
        Assert.True(recorder.Inputs[1].Anonymous.ki.dwFlags.HasFlag(KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY | KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP));
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
}
