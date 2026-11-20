using System.Diagnostics;
using System.Net;
using System.Text;
using Librarion.Benchmarks.Data;
using Librarion.Core.Access;
using Librarion.Core.Data;

namespace Librarion.Benchmarks.Bench;

/// <summary>
/// The real server (<c>Librarion.Api</c>, built in Release) started on a generated catalog for the commands that ask it
/// over HTTP (<c>opds</c>, <c>ereader</c>): its data and cache under <c>&lt;work&gt;/&lt;folder&gt;</c>, Production, warnings
/// only, the OPDS rate limit lifted (the benchmark asks far more than a reader app may). Killed when disposed.
/// </summary>
public sealed class BenchServer : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _log = new();

    private BenchServer(Process process, Uri baseAddress)
    {
        _process = process;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    /// <summary>The server's working set now, in MB.</summary>
    public double WorkingSetMb
    {
        get
        {
            _process.Refresh();
            return _process.WorkingSet64 / 1048576.0;
        }
    }

    /// <summary>The server's private memory now, in MB.</summary>
    public double PrivateMb
    {
        get
        {
            _process.Refresh();
            return _process.PrivateMemorySize64 / 1048576.0;
        }
    }

    /// <summary>Starts the server and waits until it answers (an anonymous <c>/opds</c> is a 401 once it is up and set up).</summary>
    public static async Task<BenchServer> StartAsync(BenchTarget target, string workFolder, string folder, string apiPath, int port, CancellationToken cancellationToken)
    {
        var api = Path.GetFullPath(apiPath);
        if (!File.Exists(api))
            throw new FileNotFoundException($"{api}: build it first (dotnet build src/Librarion.Api -c Release -p:LibrarionSkipWebClient=true)");
        var data = Path.Combine(workFolder, folder);
        Directory.CreateDirectory(data);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(api)!, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[]
                 {
                     api, "--urls", $"http://127.0.0.1:{port}", "--environment", "Production",
                     $"--{DatabaseOptions.ProviderKey}={target.Provider}", $"--ConnectionStrings:{DatabaseOptions.ConnectionStringName}={target.ConnectionString}",
                     $"--Librarion:Data:Directory={Path.Combine(data, "data")}", $"--Librarion:Cache:Directory={Path.Combine(data, "cache")}",
                     $"--{SecurityOptions.SectionName}:OpdsRequestsPerMinute=1000000", "--Logging:LogLevel:Default=Warning",
                 })
            start.ArgumentList.Add(argument);
        var server = new BenchServer(Process.Start(start)!, new Uri($"http://127.0.0.1:{port}/"));
        server._process.OutputDataReceived += (_, e) => { lock (server._log) server._log.AppendLine(e.Data); };
        server._process.ErrorDataReceived += (_, e) => { lock (server._log) server._log.AppendLine(e.Data); };
        server._process.BeginOutputReadLine();
        server._process.BeginErrorReadLine();
        try
        {
            using var anonymous = new HttpClient { BaseAddress = server.BaseAddress };
            var started = Stopwatch.StartNew();
            while (true)
            {
                if (server._process.HasExited)
                    throw new InvalidOperationException("The server stopped:\n" + server.Log);
                try
                {
                    using var probe = await anonymous.GetAsync("opds", cancellationToken);
                    if (probe.StatusCode == HttpStatusCode.Unauthorized)
                        return server;
                    if (probe.StatusCode == HttpStatusCode.ServiceUnavailable)
                        throw new InvalidOperationException("The server wants setup: generate the catalog again (stage 7 adds its administrator).");
                }
                catch (HttpRequestException) when (started.Elapsed < TimeSpan.FromMinutes(3))
                {
                }
                await Task.Delay(250, cancellationToken);
            }
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>What the server wrote (warnings and errors).</summary>
    public string Log
    {
        get
        {
            lock (_log)
                return _log.ToString();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync(CancellationToken.None);
        _process.Dispose();
    }
}
