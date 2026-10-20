using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DesktopMcp.Desktop.Overlay;

/// <summary>
/// One click-through overlay layer (spec §7). Every member runs on the dispatcher thread, the layer's own; only the
/// pixels arrive from the thread pool, already rendered.
/// </summary>
internal interface IOverlayLayer : IDisposable
{
    ScreenRect Bounds { get; }

    bool IsVisible { get; }

    /// <summary>Replaces the layer's premultiplied BGRA content; the layer takes the bitmap's size.</summary>
    void SetContent(OverlayBitmap bitmap);

    /// <summary>The whole layer's opacity over its per-pixel alpha, without re-uploading the content.</summary>
    void SetOpacity(byte opacity);

    void MoveTo(ScreenPoint topLeft);

    /// <summary>Shows the layer topmost without activating it; throws <see cref="InvalidOperationException"/> when it is not visible afterwards.</summary>
    void Show();

    void Hide();
}

internal interface IOverlayLayerFactory
{
    /// <summary>Creates a hidden layer (dispatcher thread).</summary>
    IOverlayLayer Create(ScreenRect bounds, string name);
}

/// <summary>Task 1's verdict: <see cref="LayeredWindow"/>s.</summary>
internal sealed class LayeredWindowFactory(DesktopLog? log = null) : IOverlayLayerFactory
{
    public IOverlayLayer Create(ScreenRect bounds, string name) => LayeredWindow.Create(bounds, name, log);
}

/// <summary>
/// A layered popup shown with <c>UpdateLayeredWindow(ULW_ALPHA)</c> (P4 Task 1 verdict, Decisions 2; upstream
/// <c>flash_overlay._create_layered_window</c> and <c>control_overlay._Layer</c>). <c>WS_EX_LAYERED | WS_EX_TRANSPARENT</c>
/// with a <c>WM_NCHITTEST</c> answer of <c>HTTRANSPARENT</c> make it click-through, so <c>WindowFromPoint</c> and UIA hit
/// testing (and P3 Decisions 42's covered check) see the window below (Decisions 28); <c>WS_EX_TOOLWINDOW</c> keeps it off the
/// taskbar and Alt+Tab; <c>WS_EX_NOACTIVATE</c>, <c>SW_SHOWNA</c> and <c>SWP_NOACTIVATE</c> keep it from taking the foreground;
/// <c>WDA_EXCLUDEFROMCAPTURE</c> keeps it out of every capture (Decisions 10). It belongs to this process, so the window
/// catalog leaves it out of Snapshot (Decisions 28). It registers no Raw Input: the mouse registration is process-wide and
/// belongs to the input monitor (Task 7).
/// <para>
/// Every Win32 call runs with the thread's DPI awareness set to per-monitor v2, so the window is per-monitor aware and its
/// positions and sizes are the physical pixels the art is drawn in, whatever the dispatcher thread's own context is.
/// </para>
/// </summary>
internal sealed unsafe class LayeredWindow : IOverlayLayer
{
    /// <summary>The window class of every overlay layer is this prefix and the layer's name.</summary>
    internal const string ClassPrefix = "DesktopMcp.Overlay.";

    internal const WINDOW_EX_STYLE ExStyle = WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT |
        WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;

    private const int ErrorClassAlreadyExists = 1410;

    private readonly string _className;
    private HWND _window;
    private byte _opacity = 255;
    private bool _hasContent;

    private LayeredWindow(string className, ScreenRect bounds)
    {
        _className = className;
        Bounds = bounds;
    }

    public ScreenRect Bounds { get; private set; }

    /// <summary>The window handle (the probe's hidden read-back of the styles and the display affinity), 0 once disposed.</summary>
    internal long Handle => (nint)_window.Value;

    public bool IsVisible => !_window.IsNull && PInvoke.IsWindowVisible(_window);

    private static HINSTANCE Instance => new(PInvoke.GetModuleHandle((PCWSTR)null).Value);

    /// <summary>
    /// Registers the class <c>DesktopMcp.Overlay.&lt;name&gt;</c> (or reuses it while another layer of the same name, such as
    /// the one a rebuild replaces, still has it) and creates the hidden window. A failed <c>SetWindowDisplayAffinity</c> is
    /// logged at warning level and the layer is used anyway (Decisions 10, <c>control_overlay.py:71-74</c>).
    /// </summary>
    public static LayeredWindow Create(ScreenRect bounds, string name, DesktopLog? log)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentException($"A {bounds.Width}x{bounds.Height} overlay layer has no pixels.", nameof(bounds));
        var layer = new LayeredWindow(ClassPrefix + name, bounds);
        var previous = EnterPhysicalPixels();
        try
        {
            layer.CreateWindow();
        }
        finally
        {
            Leave(previous);
        }
        if (!PInvoke.SetWindowDisplayAffinity(layer._window, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
        {
            var error = Marshal.GetLastPInvokeError();
            try
            {
                log?.Invoke(DesktopLogLevel.Warning,
                    $"display affinity unavailable for AI control indicator (SetWindowDisplayAffinity failed, Win32 error {error})", null);
            }
            catch (Exception)
            {
                // A failing log sink never fails the layer.
            }
        }
        return layer;
    }

    private void CreateWindow()
    {
        var instance = Instance;
        fixed (char* name = _className)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = instance,
                lpszClassName = name,
            };
            // The same class name means the same window procedure: a layer of the same name still alive shares it.
            if (PInvoke.RegisterClassEx(&windowClass) == 0 && Marshal.GetLastPInvokeError() != ErrorClassAlreadyExists)
                throw Failure("RegisterClassEx");
            _window = PInvoke.CreateWindowEx(ExStyle, name, null, WINDOW_STYLE.WS_POPUP, Bounds.Left, Bounds.Top, Bounds.Width, Bounds.Height,
                HWND.Null, default, instance, null);
            if (_window.IsNull)
            {
                var failure = Failure("CreateWindowEx");
                // Unlike the probe, leave no class behind (this fails harmlessly while another layer still uses it).
                PInvoke.UnregisterClass(name, instance);
                throw failure;
            }
        }
    }

    /// <summary>The content goes into a top-down 32-bit DIB section once and onto the window with <c>ULW_ALPHA</c>.</summary>
    public void SetContent(OverlayBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ThrowIfDisposed();
        if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Pixels.Length != bitmap.Width * bitmap.Height * 4)
            throw new ArgumentException($"A {bitmap.Width}x{bitmap.Height} overlay bitmap needs {bitmap.Width * bitmap.Height * 4} bytes.", nameof(bitmap));
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = bitmap.Width,
                biHeight = -bitmap.Height, // negative: top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };
        var previous = EnterPhysicalPixels();
        var screen = PInvoke.GetDC(HWND.Null);
        var memory = PInvoke.CreateCompatibleDC(screen);
        HBITMAP dib = default;
        try
        {
            if (screen.IsNull || memory.IsNull) throw Failure("CreateCompatibleDC");
            void* bits;
            dib = PInvoke.CreateDIBSection(memory, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
            if (dib.IsNull) throw Failure("CreateDIBSection");
            bitmap.Pixels.CopyTo(new Span<byte>(bits, bitmap.Pixels.Length));
            var selected = PInvoke.SelectObject(memory, dib);
            var destination = new System.Drawing.Point(Bounds.Left, Bounds.Top);
            var source = new System.Drawing.Point(0, 0);
            var size = new SIZE(bitmap.Width, bitmap.Height);
            var blend = Blend(_opacity);
            var updated = PInvoke.UpdateLayeredWindow(_window, screen, &destination, &size, memory, &source, default, &blend,
                UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
            var error = Marshal.GetLastPInvokeError();
            PInvoke.SelectObject(memory, selected);
            if (!updated) throw new InvalidOperationException($"UpdateLayeredWindow failed (Win32 error {error}).");
            Bounds = new ScreenRect(Bounds.Left, Bounds.Top, Bounds.Left + bitmap.Width, Bounds.Top + bitmap.Height);
            _hasContent = true;
        }
        finally
        {
            if (!dib.IsNull) PInvoke.DeleteObject(dib);
            if (!memory.IsNull) PInvoke.DeleteDC(memory);
            if (!screen.IsNull) _ = PInvoke.ReleaseDC(HWND.Null, screen);
            Leave(previous);
        }
    }

    /// <summary>A null source DC and only the <c>BLENDFUNCTION</c>: Windows keeps the uploaded pixels (Task 1's opacity check).</summary>
    public void SetOpacity(byte opacity)
    {
        ThrowIfDisposed();
        if (opacity == _opacity) return;
        if (_hasContent)
        {
            var blend = Blend(opacity);
            if (!PInvoke.UpdateLayeredWindow(_window, HDC.Null, null, null, HDC.Null, null, default, &blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA))
                throw new InvalidOperationException($"UpdateLayeredWindow failed (Win32 error {Marshal.GetLastPInvokeError()}).");
        }
        _opacity = opacity; // without content yet, the first SetContent applies it
    }

    public void MoveTo(ScreenPoint topLeft)
    {
        ThrowIfDisposed();
        if (topLeft.X == Bounds.Left && topLeft.Y == Bounds.Top) return;
        var previous = EnterPhysicalPixels();
        try
        {
            if (!PInvoke.SetWindowPos(_window, HWND.HWND_TOPMOST, topLeft.X, topLeft.Y, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE))
                throw new InvalidOperationException("AI control cursor indicator could not move");
        }
        finally
        {
            Leave(previous);
        }
        Bounds = new ScreenRect(topLeft.X, topLeft.Y, topLeft.X + Bounds.Width, topLeft.Y + Bounds.Height);
    }

    /// <summary><c>control_overlay.py:90-104</c>.</summary>
    public void Show()
    {
        ThrowIfDisposed();
        var previous = EnterPhysicalPixels();
        try
        {
            PInvoke.ShowWindow(_window, SHOW_WINDOW_CMD.SW_SHOWNA);
            if (!PInvoke.SetWindowPos(_window, HWND.HWND_TOPMOST, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW) || !PInvoke.IsWindowVisible(_window))
                throw new InvalidOperationException("AI control indicator window could not be shown");
        }
        finally
        {
            Leave(previous);
        }
    }

    /// <summary><c>control_overlay.py:106-109</c>.</summary>
    public void Hide()
    {
        ThrowIfDisposed();
        PInvoke.ShowWindow(_window, SHOW_WINDOW_CMD.SW_HIDE);
        if (PInvoke.IsWindowVisible(_window)) throw new InvalidOperationException("AI control indicator window could not be hidden");
    }

    /// <summary>Destroys the window and unregisters its class (which fails harmlessly while another layer of the name lives).</summary>
    public void Dispose()
    {
        if (_window.IsNull) return;
        PInvoke.DestroyWindow(_window);
        _window = HWND.Null;
        fixed (char* name = _className)
            PInvoke.UnregisterClass(name, Instance);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_window.IsNull, this);

    private static BLENDFUNCTION Blend(byte opacity) => new()
    {
        BlendOp = (byte)PInvoke.AC_SRC_OVER,
        SourceConstantAlpha = opacity,
        AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA,
    };

    // The previous context, or null when the call failed (the process's own awareness then applies: PerMonitorV2 in the exe).
    private static DPI_AWARENESS_CONTEXT EnterPhysicalPixels() =>
        PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    private static void Leave(DPI_AWARENESS_CONTEXT previous)
    {
        if (previous.Value != null) PInvoke.SetThreadDpiAwarenessContext(previous);
    }

    private static InvalidOperationException Failure(string call) =>
        new($"The overlay layer could not be created: {call} failed (Win32 error {Marshal.GetLastPInvokeError()}).");

    /// <summary>Hit testing passes through (a second guard after <c>WS_EX_TRANSPARENT</c>); everything else is the default.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProc(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (message == PInvoke.WM_NCHITTEST) return (LRESULT)PInvoke.HTTRANSPARENT;
            return PInvoke.DefWindowProc(window, message, wParam, lParam);
        }
        catch (Exception)
        {
            return (LRESULT)0; // nothing may escape an [UnmanagedCallersOnly] method
        }
    }
}
