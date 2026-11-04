using DMapFactory.Core.Models;

namespace DMapFactory.Acquisition;

/// <summary>
/// Mantém o estado em tempo real de todas as tags e constrói as linhas largas da planilha
/// agrupando mudanças com o mesmo timestamp de origem quando configurado (RF-GRD-01..03).
/// </summary>
public sealed class LiveSnapshot
{
    private readonly object _lock = new();
    private readonly Dictionary<int, Sample> _latestByTag = new();
    private readonly Dictionary<int, object?> _workingState = new();
    private readonly Queue<WideSpreadsheetRow> _rows = new();
    private long _nextRowNumber = 1;

    private DateTime? _pendingTimestampUtc;
    private Dictionary<int, object?>? _pendingSnapshot;
    private HashSet<int>? _pendingChangedTags;
    private int? _pendingCycleNumber;
    private bool _pendingIsCycleStart;
    private DateTime? _lastRowTimestampUtc;

    /// <summary>Quando true (padrão P3), mudanças com o mesmo SourceTimestamp são agrupadas na mesma linha.</summary>
    public bool GroupSameTimestamp { get; set; } = true;

    /// <summary>Número máximo de linhas mantidas em memória para visualização ao vivo (o CSV em disco guarda 100%).</summary>
    public int MaxRowsInMemory { get; set; } = 200_000;

    /// <summary>Tag booleana opcional cujo valor true/false demarca o ciclo de máquina (RF-STO-08 / RF-GRD-10).</summary>
    public int? CycleTriggerTagId { get; set; }

    private int _currentCycleNumber;
    private bool _inCycle;
    private DateTime _cycleStartUtc;
    private readonly List<MachineCycle> _completedCycles = new();

    public void Reset()
    {
        lock (_lock)
        {
            _latestByTag.Clear();
            _workingState.Clear();
            _rows.Clear();
            _nextRowNumber = 1;
            _pendingTimestampUtc = null;
            _pendingSnapshot = null;
            _pendingChangedTags = null;
            _currentCycleNumber = 0;
            _inCycle = false;
            _lastRowTimestampUtc = null;
            _completedCycles.Clear();
        }
    }

    public IReadOnlyDictionary<int, Sample> GetLatestSamples()
    {
        lock (_lock)
        {
            return new Dictionary<int, Sample>(_latestByTag);
        }
    }

    public IReadOnlyList<MachineCycle> GetCycles()
    {
        lock (_lock)
        {
            return [.. _completedCycles];
        }
    }

    /// <summary>Processa um lote de amostras e retorna as novas linhas largas geradas.</summary>
    public IReadOnlyList<WideSpreadsheetRow> ApplyBatch(IReadOnlyList<Sample> samples)
    {
        if (samples.Count == 0) return [];

        var committed = new List<WideSpreadsheetRow>();

        lock (_lock)
        {
            foreach (var s in samples)
            {
                object? displayValue = s.Value ?? (s.Quality != QualityClass.Good ? $"[{s.Quality}]" : null);
                _latestByTag[s.TagId] = s;
                _workingState[s.TagId] = displayValue;

                bool isCycleStart = UpdateCycleState(in s);
                int? activeCycle = _inCycle ? _currentCycleNumber : null;

                if (GroupSameTimestamp &&
                    _pendingTimestampUtc.HasValue &&
                    _pendingTimestampUtc.Value == s.TimestampUtc &&
                    _pendingSnapshot is not null &&
                    _pendingChangedTags is not null)
                {
                    _pendingSnapshot[s.TagId] = displayValue;
                    _pendingChangedTags.Add(s.TagId);
                    if (isCycleStart) _pendingIsCycleStart = true;
                    _pendingCycleNumber = activeCycle;
                }
                else
                {
                    CommitPendingLocked(committed);
                    _pendingTimestampUtc = s.TimestampUtc;
                    _pendingSnapshot = new Dictionary<int, object?>(_workingState);
                    _pendingChangedTags = [s.TagId];
                    _pendingCycleNumber = activeCycle;
                    _pendingIsCycleStart = isCycleStart;

                    if (!GroupSameTimestamp)
                    {
                        CommitPendingLocked(committed);
                    }
                }
            }

            CommitPendingLocked(committed);
        }

        return committed;
    }

    public IReadOnlyList<WideSpreadsheetRow> GetAllRows()
    {
        lock (_lock)
        {
            return [.. _rows];
        }
    }

    /// <summary>
    /// Insere uma linha de marcador de sessão (RF-STO-06) capturando o snapshot atual de todas as variáveis.
    /// </summary>
    public WideSpreadsheetRow InsertMarker(string text, DateTime? timestampUtc = null)
    {
        lock (_lock)
        {
            var now = timestampUtc ?? DateTime.UtcNow;
            TimeSpan? delta = _lastRowTimestampUtc.HasValue ? now - _lastRowTimestampUtc.Value : TimeSpan.Zero;
            _lastRowTimestampUtc = now;

            var currentValues = new Dictionary<int, object?>(_workingState);
            var row = new WideSpreadsheetRow
            {
                RowNumber = _nextRowNumber++,
                TimestampUtc = now,
                ValuesByTagId = currentValues,
                ChangedTagIds = new HashSet<int>(),
                CycleNumber = _currentCycleNumber > 0 ? _currentCycleNumber : null,
                IsCycleStart = false,
                DeltaTime = delta,
                MarkerText = text.Trim()
            };

            _rows.Enqueue(row);
            if (_rows.Count > MaxRowsInMemory)
            {
                _rows.Dequeue();
            }

            return row;
        }
    }

    public void UpdateMarkerText(DateTime timestampUtc, string? oldText, string newText)
    {
        lock (_lock)
        {
            foreach (var r in _rows)
            {
                if (!r.IsMarker) continue;
                bool timeMatch = Math.Abs((r.TimestampUtc - timestampUtc).TotalMilliseconds) < 5;
                bool textMatch = string.IsNullOrEmpty(oldText) || string.Equals(r.MarkerText, oldText, StringComparison.Ordinal);
                if (timeMatch && textMatch)
                {
                    r.SetMarker(newText.Trim());
                }
            }
        }
    }

    public void RemoveMarker(DateTime timestampUtc, string? oldText = null)
    {
        lock (_lock)
        {
            bool removedSyntheticRow = false;
            var kept = new List<WideSpreadsheetRow>(_rows.Count);
            foreach (var r in _rows)
            {
                if (r.IsMarker)
                {
                    bool timeMatch = Math.Abs((r.TimestampUtc - timestampUtc).TotalMilliseconds) < 5;
                    bool textMatch = string.IsNullOrEmpty(oldText) || string.Equals(r.MarkerText, oldText, StringComparison.Ordinal);
                    if (timeMatch && textMatch)
                    {
                        r.SetMarker(null);
                        if (r.ChangedTagIds.Count == 0)
                        {
                            removedSyntheticRow = true;
                            continue;
                        }
                    }
                }
                kept.Add(r);
            }

            if (removedSyntheticRow)
            {
                _rows.Clear();
                foreach (var r in kept)
                {
                    _rows.Enqueue(r);
                }
            }
        }
    }

    private void CommitPendingLocked(List<WideSpreadsheetRow> committed)
    {
        if (!_pendingTimestampUtc.HasValue || _pendingSnapshot is null || _pendingChangedTags is null)
            return;

        TimeSpan? delta = _lastRowTimestampUtc.HasValue ? _pendingTimestampUtc.Value - _lastRowTimestampUtc.Value : TimeSpan.Zero;
        _lastRowTimestampUtc = _pendingTimestampUtc.Value;

        var row = new WideSpreadsheetRow
        {
            RowNumber = _nextRowNumber++,
            TimestampUtc = _pendingTimestampUtc.Value,
            ValuesByTagId = _pendingSnapshot,
            ChangedTagIds = _pendingChangedTags,
            CycleNumber = _pendingCycleNumber,
            IsCycleStart = _pendingIsCycleStart,
            DeltaTime = delta
        };

        _rows.Enqueue(row);
        if (_rows.Count > MaxRowsInMemory)
        {
            _rows.Dequeue();
        }

        committed.Add(row);

        _pendingTimestampUtc = null;
        _pendingSnapshot = null;
        _pendingChangedTags = null;
        _pendingIsCycleStart = false;
    }

    private bool UpdateCycleState(in Sample s)
    {
        if (CycleTriggerTagId is null || s.TagId != CycleTriggerTagId.Value)
            return false;

        bool active = s.Value switch
        {
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            double d => Math.Abs(d) > double.Epsilon,
            string str => str == "1" || string.Equals(str, "true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        if (active && !_inCycle)
        {
            _inCycle = true;
            _currentCycleNumber++;
            _cycleStartUtc = s.TimestampUtc;
            return true;
        }

        if (!active && _inCycle)
        {
            _inCycle = false;
            double durationMs = Math.Max(0, (s.TimestampUtc - _cycleStartUtc).TotalMilliseconds);
            _completedCycles.Add(new MachineCycle(_currentCycleNumber, _cycleStartUtc, s.TimestampUtc, durationMs));
        }

        return false;
    }
}
