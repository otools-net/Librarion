using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Librarion.Api.Security;

/// <summary>
/// Keeps API keys out of every log (D-7.5, agents/docs/features/opds-auth.md): the hosting layer logs a request's
/// address and puts it in each entry's scope before any middleware runs, so a key in the address
/// (<c>/opds/k/&lt;key&gt;</c>) would be written as it is whenever those logs or scopes are on. The logger factory is
/// wrapped: messages, the values of structured entries and scopes pass through <see cref="Redact"/>, whatever the
/// provider or level. Exceptions are passed on as they are (their messages never hold a request's address here).
/// </summary>
public static partial class LogRedaction
{
    public const string Mask = "***";

    /// <summary>The key's text anywhere (its secret masked, its id kept), and whatever follows <c>/opds/k/</c>.</summary>
    [GeneratedRegex(@"(?<keep>lib_\d+_)[A-Za-z0-9_-]+|(?<keep>/opds/k/)[^/?#\s""']+", RegexOptions.CultureInvariant)]
    private static partial Regex Secrets();

    public static string Redact(string text) =>
        text.Contains("lib_", StringComparison.Ordinal) || text.Contains("/opds/k/", StringComparison.Ordinal)
            ? Secrets().Replace(text, match => match.Groups["keep"].Value + Mask)
            : text;

    /// <summary>Wraps the app's logger factory (call after logging is added).</summary>
    public static IServiceCollection AddLogRedaction(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(provider => new RedactingLoggerFactory(new LoggerFactory(
            provider.GetServices<ILoggerProvider>(), provider.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>(),
            provider.GetService<IOptions<LoggerFactoryOptions>>(), provider.GetService<IExternalScopeProvider>()))));
        return services;
    }

    private sealed class RedactingLoggerFactory(ILoggerFactory inner) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

        public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

        public void Dispose() => inner.Dispose();
    }

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            state is IEnumerable<KeyValuePair<string, object?>> values
                ? inner.BeginScope(new Redacted(values, state.ToString()))
                : inner.BeginScope(Redact(state.ToString() ?? string.Empty));

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!inner.IsEnabled(logLevel))
                return;
            var message = Redact(formatter(state, exception));
            var redacted = new Redacted(state as IEnumerable<KeyValuePair<string, object?>> ?? [], message);
            inner.Log(logLevel, eventId, redacted, exception, static (s, _) => s.Message);
        }
    }

    /// <summary>An entry's or a scope's values with their texts redacted, and its message.</summary>
    private sealed class Redacted(IEnumerable<KeyValuePair<string, object?>> values, string? message) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly List<KeyValuePair<string, object?>> _values = values
            .Select(v => new KeyValuePair<string, object?>(v.Key, v.Value is string or PathString or Uri or QueryString ? Redact(v.Value.ToString()!) : v.Value))
            .ToList();

        public string Message { get; } = Redact(message ?? string.Empty);

        public KeyValuePair<string, object?> this[int index] => _values[index];

        public int Count => _values.Count;

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => Message;
    }
}
