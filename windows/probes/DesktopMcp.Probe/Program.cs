using System.Diagnostics;
using DesktopMcp.Desktop.Dispatching;
using DesktopMcp.Probe;

// AOT probes for P0. Usage: desktop-mcp-probe dispatcher
var command = args.Length > 0 ? args[0] : "dispatcher";
var ok = command switch
{
    "dispatcher" => await ProbeDispatcherAsync(),
    _ => Fail($"unknown probe '{command}'"),
};
return ok ? 0 : 1;

static async Task<bool> ProbeDispatcherAsync()
{
    var clock = Stopwatch.StartNew();
    await using var dispatcher = DispatcherQueueThread.Start();
    var onThread = await dispatcher.InvokeAsync(() => dispatcher.HasThreadAccess);
    var ticks = 0;
    using (await dispatcher.StartTimerAsync(TimeSpan.FromMilliseconds(20), () => ticks++))
        await Task.Delay(200);
    Console.WriteLine($"dispatcher: started+invoked in {clock.ElapsedMilliseconds} ms, onThread={onThread}, ticks={ticks}");
    var hookOk = await HookProbe.RunAsync(dispatcher);
    return onThread && ticks >= 3 && hookOk;
}

static bool Fail(string message) { Console.Error.WriteLine(message); return false; }
