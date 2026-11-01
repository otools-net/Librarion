// Librarion's scale benchmark (agents/tools/benchmark/README.md): generates real-shaped catalogs of millions of books
// and measures the main queries against the budgets (agents/docs/development/scale.md).
//   generate --provider sqlite|postgres --connection <string> [--books 2000000] [--seed 1] [--work <folder>]
//   run --provider sqlite|postgres --connection <string> [--iterations 20] [--work <folder>]
using System.Globalization;
using Librarion.Benchmarks.Bench;
using Librarion.Benchmarks.Data;
using Librarion.Benchmarks.Generation;

var arguments = ParseArguments(args.Skip(1));
var command = args.FirstOrDefault();
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

string Option(string name, string? fallback = null) =>
    arguments.TryGetValue(name, out var value) ? value : fallback ?? throw new ArgumentException($"--{name} is needed");

var work = Path.GetFullPath(Option("work", Path.Combine(Path.GetTempPath(), "librarion-bench")));
Directory.CreateDirectory(work);

switch (command)
{
    case "generate":
    {
        var target = new BenchTarget(Option("provider", "sqlite"), Option("connection"));
        var shape = new CatalogShape(int.Parse(Option("books", "2000000"), CultureInfo.InvariantCulture), int.Parse(Option("seed", "1"), CultureInfo.InvariantCulture));
        await GenerateCommand.RunAsync(target, shape, work, Console.WriteLine, cancel.Token);
        return 0;
    }
    case "run":
    {
        var target = new BenchTarget(Option("provider", "sqlite"), Option("connection"));
        return await RunCommand.RunAsync(target, work, int.Parse(Option("iterations", "20"), CultureInfo.InvariantCulture), Console.WriteLine, cancel.Token);
    }
    default:
        Console.Error.WriteLine("Commands: generate, run (see agents/tools/benchmark/README.md)");
        return 2;
}

static Dictionary<string, string> ParseArguments(IEnumerable<string> args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    string? name = null;
    foreach (var arg in args)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal))
            name = arg[2..];
        else if (name is not null)
        {
            result[name] = arg;
            name = null;
        }
    }

    return result;
}
