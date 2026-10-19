using System.Diagnostics;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Probe;

// AOT probes. Usage: desktop-mcp-probe bootstrap | dispatcher | imaging [out.png] | capture [out.png]
var command = args.Length > 0 ? args[0] : "dispatcher";
var ok = command switch
{
    "bootstrap" => BootstrapProbe.Run(),
    "dispatcher" => await ProbeDispatcherAsync(),
    "imaging" => await ProbeImagingAsync(args.Length > 1 ? args[1] : "probe-imaging.png"),
    "capture" => await ProbeCaptureAsync(args.Length > 1 ? args[1] : "probe-capture.png"),
    _ => Fail($"unknown probe '{command}'"),
};
return ok ? 0 : 1;

static async Task<bool> ProbeDispatcherAsync()
{
    var clock = Stopwatch.StartNew();
    await using var dispatcher = DispatcherQueueThread.Start();
    var onThread = await dispatcher.InvokeAsync(() => dispatcher.HasThreadAccess);
    var apartment = await dispatcher.InvokeAsync(BootstrapProbe.DescribeApartment);
    var ticks = 0;
    using (await dispatcher.StartTimerAsync(TimeSpan.FromMilliseconds(20), () => ticks++))
        await Task.Delay(200);
    Console.WriteLine($"dispatcher: started+invoked in {clock.ElapsedMilliseconds} ms, onThread={onThread}, ticks={ticks}, apartment={apartment}");
    var hookOk = await HookProbe.RunAsync(dispatcher);
    return onThread && ticks >= 3 && hookOk;
}

static async Task<bool> ProbeImagingAsync(string path)
{
    DesktopMcp.Desktop.AppRuntime.EnsureInitialized();
    const int width = 1920, height = 1080;
    var bgra = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var i = (y * width + x) * 4;
        bgra[i] = (byte)x; bgra[i + 1] = (byte)y; bgra[i + 2] = 0x40; bgra[i + 3] = 0xFF;
    }
    var annotations = Enumerable.Range(0, 40)
        .Select(n => new DesktopMcp.Desktop.Imaging.Annotation(new(40 + n % 8 * 230, 60 + n / 8 * 200, 200 + n % 8 * 230, 200 + n / 8 * 200), n.ToString()))
        .ToArray();
    var clock = Stopwatch.StartNew();
    var png = await new DesktopMcp.Desktop.Imaging.Win2DRenderer().RenderPngAsync(new(width, height, bgra), 0.75, annotations);
    File.WriteAllBytes(path, png);
    Console.WriteLine($"imaging: {png.Length / 1024} KB PNG in {clock.ElapsedMilliseconds} ms -> {Path.GetFullPath(path)}");
    return png.Length > 0 && CheckWin2DLinkage("imaging");
}

static async Task<bool> ProbeCaptureAsync(string path)
{
    DesktopMcp.Desktop.AppRuntime.EnsureInitialized();
    Console.WriteLine($"capture: supported={DesktopMcp.Desktop.Capture.GraphicsCaptureService.IsSupported}");
    var clock = Stopwatch.StartNew();
    var image = await new DesktopMcp.Desktop.Capture.GraphicsCaptureService().CapturePrimaryMonitorAsync(CancellationToken.None);
    var captureMs = clock.ElapsedMilliseconds;
    var png = await new DesktopMcp.Desktop.Imaging.Win2DRenderer().RenderPngAsync(image, 1.0, []);
    File.WriteAllBytes(path, png);
    Console.WriteLine($"capture: borderSuppressed={DesktopMcp.Desktop.Capture.GraphicsCaptureService.LastBorderSuppressed}");
    Console.WriteLine($"capture: {image.Width}x{image.Height} in {captureMs} ms, png {png.Length / 1024} KB -> {Path.GetFullPath(path)}");
    return image.Width > 0 && CheckWin2DLinkage("capture");
}

// NativeAOT: Win2D is linked into the exe, so its DLL must never load. JIT: the package's DLL is expected.
static bool CheckWin2DLinkage(string probe)
{
    var aot = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    var dllLoaded = BootstrapProbe.IsCanvasDllLoaded();
    Console.WriteLine($"{probe}: nativeAot={aot}, Microsoft.Graphics.Canvas.dll loaded={dllLoaded}");
    return !aot || !dllLoaded;
}

static bool Fail(string message) { Console.Error.WriteLine(message); return false; }
