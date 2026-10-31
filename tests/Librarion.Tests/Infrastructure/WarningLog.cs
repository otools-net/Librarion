using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Librarion.Tests.Infrastructure;

/// <summary>
/// Keeps the warnings and errors logged by services, so that an assertion can say what went wrong (use it as the
/// assertion's message). Ported from SFE.
/// </summary>
public sealed class WarningLog : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public void AddTo(ILoggingBuilder builder) => builder.AddProvider(this);

    public override string ToString() => _entries.IsEmpty ? "(nothing logged)" : string.Join(Environment.NewLine, _entries);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private sealed class Logger(WarningLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                log._entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)}{(exception is null ? "" : Environment.NewLine + exception)}");
        }
    }
}
