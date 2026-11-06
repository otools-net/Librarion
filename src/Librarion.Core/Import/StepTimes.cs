using System.Collections.Concurrent;
using System.Diagnostics;

namespace Librarion.Core.Import;

/// <summary>
/// Where an import's time goes, by step (the benchmark's profile of a 2M import, agents/docs/features/imports.md#scale):
/// off unless <see cref="Enabled"/>; then each <see cref="Step"/> adds its elapsed time under its name. Steps may nest
/// (a nested one's time is also in its parent's).
/// </summary>
public static class StepTimes
{
    private static readonly ConcurrentDictionary<string, (long Ticks, long Count)> totals = new();

    public static bool Enabled { get; set; }

    /// <summary>Times what runs until the result is disposed; nothing when off.</summary>
    public static Timer Step(string name) => Enabled ? new Timer(name, Stopwatch.GetTimestamp()) : default;

    public static void Reset() => totals.Clear();

    /// <summary>The totals so far, the longest first.</summary>
    public static IReadOnlyList<(string Name, TimeSpan Time, long Count)> Totals() =>
        totals.Select(t => (t.Key, Stopwatch.GetElapsedTime(0, t.Value.Ticks), t.Value.Count)).OrderByDescending(t => t.Item2).ToList();

    public readonly struct Timer(string? name, long start) : IDisposable
    {
        public void Dispose()
        {
            if (name is null)
                return;
            var elapsed = Stopwatch.GetTimestamp() - start;
            totals.AddOrUpdate(name, (elapsed, 1), (_, t) => (t.Ticks + elapsed, t.Count + 1));
        }
    }
}
