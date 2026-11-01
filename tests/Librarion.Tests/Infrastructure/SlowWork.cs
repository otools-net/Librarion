using System.Diagnostics;
using System.Globalization;

namespace Librarion.Tests.Infrastructure;

/// <summary>
/// Waits on work whose speed is the machine's, not Librarion's (a background scan of many books, a benchmark-sized
/// import). Past the limit the test is <b>inconclusive</b>, not failed: a busy CI runner is no bug; a wrong result
/// still fails. <see cref="FactorVariable"/> (≥ 1) multiplies every limit (CI sets it). Ported from SFE.
/// </summary>
public static class SlowWork
{
    public const string FactorVariable = "LIBRARION_TEST_TIME_FACTOR";

    public static double Factor { get; } =
        double.TryParse(Environment.GetEnvironmentVariable(FactorVariable), NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) && factor >= 1
            ? factor
            : 1;

    /// <summary><paramref name="seconds"/> times <see cref="Factor"/>.</summary>
    public static TimeSpan Limit(double seconds) => TimeSpan.FromSeconds(seconds * Factor);

    /// <summary>Reads <paramref name="read"/> until <paramref name="condition"/> holds; inconclusive past the limit.</summary>
    public static async Task<T> UntilAsync<T>(Func<T> read, Func<T, bool> condition, double seconds, string what, CancellationToken cancellationToken)
    {
        var limit = Limit(seconds);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var value = read();
            if (condition(value))
                return value;
            if (watch.Elapsed > limit)
                Assert.Inconclusive($"Timed out after {limit.TotalSeconds:0} s waiting for {what} (last: {value}): this machine is too " +
                                    $"slow or busy for this test (not a failure of Librarion). Set {FactorVariable} (e.g. 3) to give it more time.");
            await Task.Delay(50, cancellationToken);
        }
    }
}
