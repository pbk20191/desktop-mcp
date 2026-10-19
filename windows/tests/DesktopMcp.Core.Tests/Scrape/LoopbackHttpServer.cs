using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DesktopMcp.Core.Tests;

/// <summary>
/// A one-connection-at-a-time HTTP/1.1 server on 127.0.0.1 for Scrape tests. <c>respond(path, host)</c> returns the
/// raw response; every request's path and Host header are recorded.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, string, string> _respond;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public LoopbackHttpServer(Func<string, string, string> respond)
    {
        _respond = respond;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public ConcurrentQueue<(string Path, string Host)> Requests { get; } = new();

    public static string Response(int status, string reason, string body, string contentType = "text/html; charset=utf-8", string? location = null) =>
        $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n" +
        (location is null ? "" : $"Location: {location}\r\n") + "\r\n" + body;

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            using (client)
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream);
                var lines = head.Split("\r\n");
                var path = lines[0].Split(' ')[1];
                var host = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim() ?? "";
                Requests.Enqueue((path, host));
                await stream.WriteAsync(Encoding.UTF8.GetBytes(_respond(path, host)));
            }
        }
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n') break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        _stop.Dispose();
    }
}
