using System.Collections.Concurrent;
using System.Diagnostics;

namespace Librarion.Tests.Infrastructure;

/// <summary>
/// Records every outbound HTTP request and every name lookup the process makes while it is alive, from the runtime's
/// own activity sources (<c>System.Net.Http</c>, <c>System.Net.NameResolution</c>): whatever library makes the call, it
/// shows here. The test server's in-memory client is not a network handler and never shows. Process-wide: tests using
/// it run alone (<c>[DoNotParallelize]</c>). Privacy rule: agents/docs/development/privacy.md.
/// </summary>
public sealed class OutboundGuard : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<string> _seen = new();

    public OutboundGuard()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "System.Net.Http" or "System.Net.NameResolution",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            // When it ends: the address and method tags are filled in after the start (a refused request ends too).
            ActivityStopped = activity => _seen.Enqueue(Describe(activity)),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>What was attempted, one line each ("GET http://…", "lookup example.org").</summary>
    public IReadOnlyList<string> Seen => [.. _seen];

    public void Dispose() => _listener.Dispose();

    private static string Describe(Activity activity) => activity.Source.Name == "System.Net.NameResolution"
        ? $"lookup {activity.GetTagItem("dns.question.name") ?? activity.DisplayName}"
        : $"{activity.GetTagItem("http.request.method")} {activity.GetTagItem("url.full") ?? activity.DisplayName}";
}
