using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Serilog.Core;
using Serilog.Events;

namespace DMapFactory.App.Services;

/// <summary>Uma linha do visualizador de log na interface.</summary>
public sealed record LogEntry(DateTime Timestamp, LogEventLevel Level, string Message);

/// <summary>
/// Sink do Serilog que mantém as últimas entradas em memória para exibição na UI (RF-LOG-01/03).
/// As entradas são agrupadas e enviadas à thread de UI em lote, para não sobrecarregar o Dispatcher.
/// </summary>
public sealed class UiLogSink : ILogEventSink
{
    private const int MaxEntries = 2_000;
    private readonly Queue<LogEntry> _pending = new();
    private readonly object _lock = new();
    private DispatcherTimer? _timer;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry(logEvent.Timestamp.LocalDateTime, logEvent.Level, logEvent.RenderMessage());
        lock (_lock) _pending.Enqueue(entry);
    }

    /// <summary>Deve ser chamado na thread de UI após a criação da Application.</summary>
    public void AttachToDispatcher()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Flush(),
            Application.Current.Dispatcher);
        _timer.Start();
    }

    private void Flush()
    {
        LogEntry[] batch;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
            _pending.Clear();
        }

        foreach (var e in batch) Entries.Add(e);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }
}
