using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Capture;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Imaging;
using DesktopMcp.Desktop.Overlay;
using DesktopMcp.Desktop.Snapshots;
using Microsoft.Graphics.Canvas;
using Windows.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DesktopMcp.Probe;

/// <summary>
/// P4 Task 1 overlay technology spike (plan Decisions 2), the winning candidate only: Win2D renders into a
/// CanvasRenderTarget on the thread pool, its premultiplied BGRA goes into a top-down DIB section and onto a layered
/// popup with UpdateLayeredWindow(ULW_ALPHA). Shows one 400x200 layer in the primary monitor's top-left corner for about
/// 2 s, filled with (45, 145, 255), and runs the checks in order: graphics-not-loaded, click-through, visible,
/// capture-excluded, opacity, off-dispatcher, cleanup. Prints <c>overlay-spike: ulw &lt;check&gt;: OK|FAIL (&lt;detail&gt;)</c>
/// per check and a final <c>overlay-spike: ulw -&gt; ACCEPT|REJECT</c>.
/// </summary>
internal static partial class OverlayProbe
{
    private const string Candidate = "ulw";
    private const int Width = 400, Height = 200, Tolerance = 8;
    private const byte R = 45, G = 145, B = 255;
    private const string ClassName = "DesktopMcpOverlaySpike";
    private const byte AcSrcOver = 0, AcSrcAlpha = 1;  // wingdi.h macros, which CsWin32 does not emit
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private const WINDOW_EX_STYLE LayerStyle = WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT |
        WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;

    // Probe-only reads for the checks and the module handle (not product code).
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("gdi32.dll")] private static partial uint GetPixel(nint hdc, int x, int y);

    [LibraryImport("user32.dll")] private static partial nint GetWindowLongPtrW(nint hwnd, int index);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindow(nint hwnd);

    private const int GwlExStyle = -20;
    private const uint WdaExcludeFromCapture = 0x11;

    /// <summary>
    /// P4 Task 14, <c>overlay</c>: the release check of the product's overlay path under NativeAOT, without showing anything.
    /// Through <see cref="OverlayArt"/> and <see cref="LayeredWindowFactory"/> it renders the indicator for the real monitors
    /// (edges, notices and the cursor ring) and one flash (inward strips on every monitor), creates every layer on its own
    /// dispatcher, pushes its content and sets an opacity, never shows a layer, reads back that each window has the
    /// click-through ex-styles and <c>WDA_EXCLUDEFROMCAPTURE</c>, and destroys them. Prints
    /// <c>overlay: &lt;n&gt; layers, &lt;bytes&gt; bytes rendered, hidden -&gt; OK</c>.
    /// </summary>
    public static async Task<bool> RunHiddenAsync()
    {
        var host = new DispatcherHost();
        var layers = new List<LayeredWindow>();
        IDesktopDispatcher? dispatcher = null;
        try
        {
            var displays = new Win32Displays();
            var monitors = displays.GetDisplays().Select(d => d.Bounds).ToArray();
            var art = OverlayArt.Shared;
            // The notice is measured on the thread pool, never on the dispatcher (Task 8).
            // Each spec with the overlay it belongs to, so the layers are created by the factory the product uses for it.
            var specs = await Task.Run(() =>
            {
                var all = OverlayLayout.Indicator(monitors, art.NoticeSize).Select(spec => (Spec: spec, Flash: false)).ToList();
                all.Add((OverlayLayout.CursorRing(DesktopSession.CursorPosition()), false));
                all.AddRange(OverlayLayout.Flash(null, monitors, displays.GetVirtualScreen()).Select(spec => (Spec: spec, Flash: true)));
                return all;
            });
            var bitmaps = await Task.WhenAll(specs.Select(item => art.RenderAsync(item.Spec.Art, CancellationToken.None)));
            var bytes = bitmaps.Sum(b => (long)b.Pixels.Length);

            dispatcher = await host.GetAsync(CancellationToken.None);
            var indicatorFactory = new LayeredWindowFactory(null, LayeredWindow.IndicatorOverlay);
            var flashFactory = new LayeredWindowFactory(null, LayeredWindow.FlashOverlay);
            var problems = await dispatcher.InvokeAsync(() =>
            {
                var found = new List<string>();
                for (var i = 0; i < specs.Count; i++)
                {
                    var (spec, flash) = specs[i];
                    var layer = (LayeredWindow)(flash ? flashFactory : indicatorFactory).Create(spec.Bounds, spec.Name);
                    layers.Add(layer);
                    layer.SetContent(bitmaps[i]);
                    layer.SetOpacity(128);
                    var hwnd = (nint)layer.Handle;
                    var exStyle = (WINDOW_EX_STYLE)(uint)GetWindowLongPtrW(hwnd, GwlExStyle);
                    if ((exStyle & LayeredWindow.ExStyle) != LayeredWindow.ExStyle)
                        found.Add($"{spec.Name}: ex-style 0x{(uint)exStyle:X8} lacks 0x{(uint)LayeredWindow.ExStyle:X8}");
                    if (!GetWindowDisplayAffinity(hwnd, out var affinity) || affinity != WdaExcludeFromCapture || !layer.ExcludedFromCapture)
                        found.Add($"{spec.Name}: display affinity 0x{affinity:X} (excluded={layer.ExcludedFromCapture})");
                    if (layer.IsVisible) found.Add($"{spec.Name}: visible");
                }
                return found;
            });
            var count = layers.Count;
            var destroyed = await dispatcher.InvokeAsync(() =>
            {
                var gone = 0;
                foreach (var layer in layers)
                {
                    var hwnd = (nint)layer.Handle;
                    layer.Dispose();
                    if (!IsWindow(hwnd)) gone++;
                }
                layers.Clear();
                return gone;
            });
            if (destroyed != count) problems.Add($"{count - destroyed} of {count} windows still exist after disposal");
            foreach (var problem in problems) Console.WriteLine($"overlay: {problem}");
            var ok = problems.Count == 0 && count > 0;
            Console.WriteLine($"overlay: {count} layers, {bytes} bytes rendered, hidden -> {(ok ? "OK" : "FAIL")}");
            return ok;
        }
        catch (Exception e)
        {
            Console.WriteLine($"overlay: {e.GetType().Name} 0x{e.HResult:X8} {e.Message} -> FAIL");
            return false;
        }
        finally
        {
            if (dispatcher is not null && layers.Count > 0)
                await dispatcher.InvokeAsync(() => { foreach (var layer in layers) layer.Dispose(); });
            await host.ShutdownAsync();
        }
    }

    public static async Task<bool> RunAsync()
    {
        var allOk = true;
        void Report(string check, bool ok, string detail)
        {
            allOk &= ok;
            Console.WriteLine($"overlay-spike: {Candidate} {check}: {(ok ? "OK" : "FAIL")} ({detail})");
        }

        var started = Stopwatch.StartNew();
        await using var dispatcher = DispatcherQueueThread.Start();
        var d2d1AfterDispatcher = IsLoaded("d2d1.dll");
        var displays = new Win32Displays().GetDisplays();
        var primary = displays.First(d => d.Primary).Bounds;
        var rect = new ScreenRect(primary.Left, primary.Top, primary.Left + Width, primary.Top + Height);
        var centre = new System.Drawing.Point(rect.Left + Width / 2, rect.Top + Height / 2);

        var layer = new UlwLayer(rect);
        await dispatcher.InvokeAsync(layer.Create);
        try
        {
            // graphics-not-loaded: nothing before the first Win2D call (dispatcher start, window creation) loads d2d1.
            var d2d1BeforeWin2D = IsLoaded("d2d1.dll");
            Report("graphics-not-loaded", !d2d1BeforeWin2D,
                $"d2d1 after dispatcher start={d2d1AfterDispatcher}, before the first Win2D call={d2d1BeforeWin2D}");

            // The content is drawn and uploaded on the thread pool; off-dispatcher reports the outcome further down.
            var dispatcherThread = dispatcher.ManagedThreadId;
            var upload = await Task.Run(async () =>
            {
                var thread = Environment.CurrentManagedThreadId;
                try
                {
                    using var lease = await GpuGate.Shared.EnterAsync("Overlay spike", TimeSpan.FromSeconds(5), CancellationToken.None);
                    layer.Upload(Color.FromArgb(255, R, G, B));
                    return (Ok: thread != dispatcherThread, Detail: $"uploaded on thread {thread}, dispatcher thread {dispatcherThread}");
                }
                catch (Exception e)
                {
                    return (Ok: false, Detail: $"thread {thread}: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
                }
            });
            await dispatcher.InvokeAsync(layer.Show);
            await Task.Delay(Settle);

            // click-through: the root window at the layer's centre is not the layer.
            var below = await dispatcher.InvokeAsync(() => (nint)PInvoke.GetAncestor(PInvoke.WindowFromPoint(centre), GET_ANCESTOR_FLAGS.GA_ROOT));
            Report("click-through", below != layer.Handle && below != 0,
                $"WindowFromPoint({centre.X},{centre.Y}) root=0x{below:X}, layer=0x{layer.Handle:X}");

            // visible: WDA_NONE, the screen pixel at the centre is the colour.
            var none = await dispatcher.InvokeAsync(() => layer.SetAffinity(WINDOW_DISPLAY_AFFINITY.WDA_NONE));
            await Task.Delay(Settle);
            var shown = ScreenPixel(centre);
            Report("visible", none && Near(shown, R, G, B), $"WDA_NONE set={none}, screen pixel {Describe(shown)}, expected rgb({R},{G},{B})");

            // capture-excluded: WDA_EXCLUDEFROMCAPTURE, a Windows.Graphics.Capture frame of the rect has no pixel of the colour.
            var excluded = await dispatcher.InvokeAsync(() => layer.SetAffinity(WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE));
            await Task.Delay(Settle);
            try
            {
                var image = await new GraphicsCaptureService().CaptureAsync(rect, displays.Select(d => d.Bounds).ToArray(), CancellationToken.None);
                var matches = 0;
                for (var i = 0; i < image.Bgra.Length; i += 4)
                    if (Near(image.Bgra[i + 2], image.Bgra[i + 1], image.Bgra[i], R, G, B)) matches++;
                Report("capture-excluded", excluded && matches == 0,
                    $"WDA_EXCLUDEFROMCAPTURE set={excluded}, {matches} of {image.Width * image.Height} captured pixels within {Tolerance} of the colour");
            }
            catch (Exception e)
            {
                Report("capture-excluded", false, $"capture failed: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            }

            // opacity: 128 without new content; the screen pixel changes (read with WDA_NONE again).
            await dispatcher.InvokeAsync(() => layer.SetAffinity(WINDOW_DISPLAY_AFFINITY.WDA_NONE));
            await Task.Delay(Settle);
            var before = ScreenPixel(centre);
            var opacitySet = await dispatcher.InvokeAsync(() => layer.SetOpacity(128));
            await Task.Delay(Settle);
            var after = ScreenPixel(centre);
            Report("opacity", opacitySet && after != before && !Near(after, R, G, B),
                $"set={opacitySet}, screen pixel {Describe(before)} -> {Describe(after)}");

            Report("off-dispatcher", upload.Ok, upload.Detail);

            // Keep the layer up for about 2 s in all, as the user was told.
            var remaining = TimeSpan.FromSeconds(2) - started.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        }
        finally
        {
            var (destroyed, unregistered, gone) = await dispatcher.InvokeAsync(layer.Destroy);
            Report("cleanup", destroyed && unregistered && gone,
                $"DestroyWindow={destroyed}, UnregisterClass={unregistered}, IsWindow afterwards={!gone}");
        }
        Console.WriteLine($"overlay-spike: {Candidate} -> {(allOk ? "ACCEPT" : "REJECT")}");
        return allOk;
    }

    private static bool IsLoaded(string module) => GetModuleHandleW(module) != 0;

    private static uint ScreenPixel(System.Drawing.Point point)
    {
        var dc = PInvoke.GetDC(HWND.Null);
        try { return GetPixel((nint)dc, point.X, point.Y); }
        finally { _ = PInvoke.ReleaseDC(HWND.Null, dc); }
    }

    // COLORREF is 0x00BBGGRR; CLR_INVALID (0xFFFFFFFF) never matches.
    private static bool Near(uint colorRef, byte r, byte g, byte b) =>
        colorRef != 0xFFFFFFFF && Near((byte)colorRef, (byte)(colorRef >> 8), (byte)(colorRef >> 16), r, g, b);

    private static bool Near(byte r, byte g, byte b, byte er, byte eg, byte eb) =>
        Math.Abs(r - er) <= Tolerance && Math.Abs(g - eg) <= Tolerance && Math.Abs(b - eb) <= Tolerance;

    private static string Describe(uint colorRef) => colorRef == 0xFFFFFFFF
        ? "CLR_INVALID"
        : $"rgb({(byte)colorRef},{(byte)(colorRef >> 8)},{(byte)(colorRef >> 16)})";

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam) =>
        PInvoke.DefWindowProc(hwnd, message, wParam, lParam);

    /// <summary>The layer. Create, Show, SetAffinity, SetOpacity and Destroy run on the dispatcher thread; Upload on the thread pool.</summary>
    private sealed unsafe class UlwLayer(ScreenRect rect)
    {
        private HWND _hwnd;

        public nint Handle => (nint)_hwnd;

        private static HINSTANCE Instance => new((void*)GetModuleHandleW(null));

        public void Create()
        {
            fixed (char* name = ClassName)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WindowProc,
                    hInstance = Instance,
                    lpszClassName = name,
                };
                if (PInvoke.RegisterClassEx(&windowClass) == 0)
                    throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()})");
                _hwnd = PInvoke.CreateWindowEx(LayerStyle, name, null, WINDOW_STYLE.WS_POPUP,
                    rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, HWND.Null, default, Instance, null);
            }
            if (_hwnd.IsNull) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()})");
        }

        /// <summary>Win2D into a CanvasRenderTarget, its premultiplied BGRA into a top-down DIB section, UpdateLayeredWindow(ULW_ALPHA).</summary>
        public void Upload(Color colour)
        {
            byte[] pixels;
            using (var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), Width, Height, 96))
            {
                using (var session = target.CreateDrawingSession()) session.Clear(colour);
                pixels = target.GetPixelBytes();  // B8G8R8A8, premultiplied
            }
            var header = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = Width, biHeight = -Height,  // negative: top-down
                    biPlanes = 1, biBitCount = 32, biCompression = 0,  // BI_RGB
                },
            };
            var screen = PInvoke.GetDC(HWND.Null);
            var memory = PInvoke.CreateCompatibleDC(screen);
            void* bits;
            var bitmap = PInvoke.CreateDIBSection(memory, &header, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
            try
            {
                if (bitmap.IsNull) throw new InvalidOperationException("CreateDIBSection failed");
                pixels.CopyTo(new Span<byte>(bits, pixels.Length));
                var previous = PInvoke.SelectObject(memory, bitmap);
                var destination = new System.Drawing.Point(rect.Left, rect.Top);
                var source = new System.Drawing.Point(0, 0);
                var size = new SIZE(Width, Height);
                var blend = new BLENDFUNCTION { BlendOp = AcSrcOver, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
                var updated = PInvoke.UpdateLayeredWindow(_hwnd, screen, &destination, &size, memory, &source, default, &blend,
                    UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
                var error = Marshal.GetLastPInvokeError();
                PInvoke.SelectObject(memory, previous);
                if (!updated) throw new InvalidOperationException($"UpdateLayeredWindow failed ({error})");
            }
            finally
            {
                if (!bitmap.IsNull) PInvoke.DeleteObject(bitmap);
                PInvoke.DeleteDC(memory);
                _ = PInvoke.ReleaseDC(HWND.Null, screen);
            }
        }

        public void Show() => PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

        public bool SetAffinity(WINDOW_DISPLAY_AFFINITY affinity) => PInvoke.SetWindowDisplayAffinity(_hwnd, affinity);

        /// <summary>No new content: a null source DC and only the BLENDFUNCTION.</summary>
        public bool SetOpacity(byte alpha)
        {
            var blend = new BLENDFUNCTION { BlendOp = AcSrcOver, SourceConstantAlpha = alpha, AlphaFormat = AcSrcAlpha };
            return PInvoke.UpdateLayeredWindow(_hwnd, HDC.Null, null, null, HDC.Null, null, default, &blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
        }

        public (bool Destroyed, bool Unregistered, bool Gone) Destroy()
        {
            var destroyed = !_hwnd.IsNull && PInvoke.DestroyWindow(_hwnd);
            bool unregistered;
            fixed (char* name = ClassName)
                unregistered = PInvoke.UnregisterClass(name, Instance);
            return (destroyed, unregistered, !PInvoke.IsWindow(_hwnd));
        }
    }
}
