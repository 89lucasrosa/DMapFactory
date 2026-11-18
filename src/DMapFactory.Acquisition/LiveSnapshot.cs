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
    private DateTime? _pendingSourceTimestampUtc;
    private DateTime? _pendingServerTimestampUtc;
    private DateTime? _pendingLocalTimestampUtc;
    private Dictionary<int, object?>? _pendingSnapshot;
    private HashSet<int>? _pendingChangedTags;
    private DateTime? _lastRowTimestampUtc;

    /// <summary>Quando true (padrão P3), mudanças com o mesmo SourceTimestamp são agrupadas na mesma linha.</summary>
    public bool GroupSameTimestamp { get; set; } = true;

    /// <summary>Modo de fonte de timestamp ativo para exibição na planilha e no gráfico.</summary>
    public TimestampSourceMode TimestampSource { get; set; } = TimestampSourceMode.Auto;

    /// <summary>Número máximo de linhas mantidas em memória para visualização ao vivo (sem limitação artificial).</summary>
    public int MaxRowsInMemory { get; set; } = int.MaxValue;

    public void Reset()
    {
        lock (_lock)
        {
            _latestByTag.Clear();
            _workingState.Clear();
            _rows.Clear();
            _nextRowNumber = 1;
            _pendingTimestampUtc = null;
            _pendingSourceTimestampUtc = null;
            _pendingServerTimestampUtc = null;
            _pendingLocalTimestampUtc = null;
            _pendingSnapshot = null;
            _pendingChangedTags = null;
            _lastRowTimestampUtc = null;
        }
    }

    public void ApplyTimestampSourceToAllRows(TimestampSourceMode mode)
    {
        lock (_lock)
        {
            TimestampSource = mode;

            var tagKeys = _latestByTag.Keys.ToList();
            foreach (var key in tagKeys)
            {
                var prev = _latestByTag[key];
                DateTime resolved = prev.ResolveTimestamp(mode, out var origin);
                _latestByTag[key] = prev with
                {
                    TimestampUtc = resolved,
                    Origin = origin
                };
            }

            DateTime? prevRowTs = null;
            foreach (var r in _rows)
            {
                DateTime newTs = r.ResolveTimestamp(mode);
                TimeSpan? delta = prevRowTs.HasValue ? newTs - prevRowTs.Value : TimeSpan.Zero;
                prevRowTs = newTs;
                r.ApplyTimestampSource(mode, delta);
            }
            _lastRowTimestampUtc = prevRowTs;
        }
    }

    public IReadOnlyDictionary<int, Sample> GetLatestSamples()
    {
        lock (_lock)
        {
            return new Dictionary<int, Sample>(_latestByTag);
        }
    }

    /// <summary>Processa um lote de amostras e retorna as novas linhas largas geradas.</summary>
    public IReadOnlyList<WideSpreadsheetRow> ApplyBatch(IReadOnlyList<Sample> samples)
    {
        if (samples.Count == 0) return [];

        var committed = new List<WideSpreadsheetRow>();

        lock (_lock)
        {
            foreach (var rawSample in samples)
            {
                DateTime effectiveTs = rawSample.ResolveTimestamp(TimestampSource, out var effectiveOrigin);
                var s = rawSample with
                {
                    TimestampUtc = effectiveTs,
                    Origin = effectiveOrigin
                };

                object? displayValue = s.Value ?? (s.Quality != QualityClass.Good ? $"[{s.Quality}]" : null);
                bool valueChanged = !_workingState.TryGetValue(s.TagId, out var prevVal) || !Equals(prevVal, displayValue);
                _latestByTag[s.TagId] = s;
                _workingState[s.TagId] = displayValue;

                DateTime sampleLocalTs = s.LocalTimestampUtc ?? DateTime.SpecifyKind(s.ReceivedUtc.ToLocalTime(), DateTimeKind.Utc);

                if (GroupSameTimestamp &&
                    _pendingTimestampUtc.HasValue &&
                    _pendingTimestampUtc.Value == s.TimestampUtc &&
                    _pendingSnapshot is not null &&
                    _pendingChangedTags is not null)
                {
                    _pendingSnapshot[s.TagId] = displayValue;
                    if (s.SourceTimestampUtc.HasValue)
                        _pendingSourceTimestampUtc = s.SourceTimestampUtc;
                    if (s.ServerTimestampUtc.HasValue)
                        _pendingServerTimestampUtc = s.ServerTimestampUtc;
                    _pendingLocalTimestampUtc = sampleLocalTs;
                    if (valueChanged)
                        _pendingChangedTags.Add(s.TagId);
                }
                else
                {
                    CommitPendingLocked(committed);
                    _pendingTimestampUtc = s.TimestampUtc;
                    _pendingSourceTimestampUtc = s.SourceTimestampUtc;
                    _pendingServerTimestampUtc = s.ServerTimestampUtc;
                    _pendingLocalTimestampUtc = sampleLocalTs;
                    _pendingSnapshot = new Dictionary<int, object?>(_workingState);
                    _pendingChangedTags = valueChanged ? [s.TagId] : [];

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
            var localNow = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Utc);
            TimeSpan? delta = _lastRowTimestampUtc.HasValue ? now - _lastRowTimestampUtc.Value : TimeSpan.Zero;
            _lastRowTimestampUtc = now;

            var currentValues = new Dictionary<int, object?>(_workingState);
            var row = new WideSpreadsheetRow
            {
                RowNumber = _nextRowNumber++,
                TimestampUtc = now,
                SourceTimestampUtc = now,
                ServerTimestampUtc = now,
                LocalTimestampUtc = timestampUtc ?? localNow,
                ValuesByTagId = currentValues,
                ChangedTagIds = new HashSet<int>(),
                DeltaTime = delta,
                MarkerText = text.Trim()
            };

            _rows.Enqueue(row);

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
            SourceTimestampUtc = _pendingSourceTimestampUtc,
            ServerTimestampUtc = _pendingServerTimestampUtc,
            LocalTimestampUtc = _pendingLocalTimestampUtc,
            ValuesByTagId = _pendingSnapshot,
            ChangedTagIds = _pendingChangedTags,
            DeltaTime = delta
        };

        _rows.Enqueue(row);

        committed.Add(row);

        _pendingTimestampUtc = null;
        _pendingSourceTimestampUtc = null;
        _pendingServerTimestampUtc = null;
        _pendingLocalTimestampUtc = null;
        _pendingSnapshot = null;
        _pendingChangedTags = null;
    }
}
