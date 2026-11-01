namespace Librarion.Benchmarks.Generation;

/// <summary>
/// Picks among <c>n</c> items with Zipf weights (item k weighs 1 / (k+1)^s): a few authors with thousands of books,
/// most with one or none, as real catalogs.
/// </summary>
public sealed class Zipf
{
    private readonly double[] _cumulative;

    public Zipf(int count, double exponent)
    {
        _cumulative = new double[count];
        var sum = 0.0;
        for (var k = 0; k < count; k++)
        {
            sum += 1.0 / Math.Pow(k + 1, exponent);
            _cumulative[k] = sum;
        }
    }

    public int Count => _cumulative.Length;

    public int Next(Random random)
    {
        var target = random.NextDouble() * _cumulative[^1];
        var index = Array.BinarySearch(_cumulative, target);
        return index >= 0 ? index : Math.Min(~index, _cumulative.Length - 1);
    }
}
