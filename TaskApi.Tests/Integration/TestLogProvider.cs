using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TaskApi.Tests.Integration;

internal sealed class TestLogProvider : ILoggerProvider
{
    public ConcurrentQueue<Entry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new TestLogger(Entries);

    public void Dispose() { }

    internal sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    private sealed class TestLogger(ConcurrentQueue<Entry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new Entry(logLevel, formatter(state, exception), exception));
    }
}
