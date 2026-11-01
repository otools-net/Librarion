using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Librarion.Benchmarks.Bench;

/// <summary>A SQL command EF ran, with its parameters' values, to explain it afterwards.</summary>
public sealed record CapturedCommand(string Sql, IReadOnlyList<(string Name, object? Value)> Parameters);

/// <summary>
/// Collects the commands EF runs while <see cref="Recording"/> is on, from its diagnostic events: no change to Core's
/// contexts, and every query a scenario makes (the page, then its authors, files, series) is checked.
/// </summary>
public sealed class CommandCapture : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly IDisposable _subscription;
    private readonly List<IDisposable> _listeners = [];
    private readonly List<CapturedCommand> _commands = [];

    public CommandCapture() => _subscription = DiagnosticListener.AllListeners.Subscribe(this);

    public bool Recording { get; set; }

    public IReadOnlyList<CapturedCommand> Take()
    {
        lock (_commands)
        {
            var taken = _commands.ToList();
            _commands.Clear();
            return taken;
        }
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == DbLoggerCategory.Name)
            _listeners.Add(listener.Subscribe(this));
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (!Recording || value.Key != RelationalEventId.CommandExecuting.Name || value.Value is not CommandEventData { Command: DbCommand command })
            return;
        var parameters = command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value)).ToList();
        lock (_commands)
            _commands.Add(new CapturedCommand(command.CommandText, parameters));
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void Dispose()
    {
        _subscription.Dispose();
        foreach (var listener in _listeners)
            listener.Dispose();
    }
}
