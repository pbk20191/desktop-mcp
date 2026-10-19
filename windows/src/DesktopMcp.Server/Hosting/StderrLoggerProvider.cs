using Microsoft.Extensions.Logging;

namespace DesktopMcp.Server.Hosting;

/// <summary>
/// Plain log lines on stderr; stdout carries the stdio protocol. Hand-written instead of the console logger so there is
/// no options binding to worry about under NativeAOT.
/// </summary>
internal sealed class StderrLoggerProvider : ILoggerProvider
{
    private static readonly Lock Gate = new();

    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);

    public void Dispose() { }

    private sealed class StderrLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{Level(logLevel)}: {category}: {formatter(state, exception)}";
            lock (Gate)
            {
                Console.Error.WriteLine(line);
                if (exception is not null) Console.Error.WriteLine(exception);
            }
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            _ => "crit",
        };
    }
}
