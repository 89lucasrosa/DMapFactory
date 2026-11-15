using System.Collections.Concurrent;
using System.Threading.Channels;
using DMapFactory.Core.Abstractions;
using DMapFactory.Core.Models;
using DMapFactory.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DMapFactory.Acquisition;

/// <summary>Métricas instantâneas do motor de aquisição (RF-ACQ-08/09).</summary>
public readonly record struct AcquisitionStats(
    bool IsOnline,
    long TotalSamples,
    long DroppedSamples,
    double EventsPerSecond,
    double BufferUsagePercent,
    bool IsBufferAboveWarningThreshold,
    string? CurrentCsvPath);

/// <summary>
/// Orquestra o ciclo de vida On-line / Off-line da sessão de aquisição (RF-ACQ-06..09, RF-STO-01..04):
/// <list type="bullet">
/// <item>Ao entrar On-line: abre novo arquivo CSV + JSON, lê o snapshot inicial de todas as tags (RF-ACQ-07) e inicia os grupos nos drivers.</item>
/// <item>Mantém um <see cref="Channel{T}"/> limitado com capacidade de 30 s (mínimo 50.000 amostras — RF-ACQ-08).</item>
/// <item>Consome em lote a cada 250 ms, atualiza o <see cref="LiveSnapshot"/> e grava no <see cref="CsvSessionWriter"/>.</item>
/// <item>Ao ficar Off-line: para os grupos, drena o buffer residual e fecha a sessão.</item>
/// </list>
/// </summary>
public sealed class SessionManager : ISampleSink, IAsyncDisposable
{
    private readonly int _bufferCapacity;
    private readonly ILogger<SessionManager> _logger;
    private Channel<Sample>? _channel;
    private CsvSessionWriter? _writer;
    private CancellationTokenSource? _consumerCts;
    private Task? _consumerTask;
    private IReadOnlyList<IDriver> _activeDrivers = [];
    private IReadOnlyList<AcquisitionGroup> _activeGroups = [];

    private long _totalSamples;
    private long _droppedSamples;
    private long _windowCounter;
    private DateTime _lastRateCalcUtc = DateTime.UtcNow;
    private double _eventsPerSecond;
    private int _bufferedCount;
    private bool _isOnline;

    public SessionManager(
        LiveSnapshot? snapshot = null,
        int bufferCapacity = 500_000,
        ILogger<SessionManager>? logger = null)
    {
        Snapshot = snapshot ?? new LiveSnapshot();
        _bufferCapacity = Math.Max(50_000, bufferCapacity);
        _logger = logger ?? NullLogger<SessionManager>.Instance;
    }

    public LiveSnapshot Snapshot { get; }
    public bool IsOnline => _isOnline;
    public string? CurrentCsvPath => _writer?.CsvFilePath;

    /// <summary>Disparado a cada lote processado (aprox. 4 Hz) com as novas linhas largas da planilha.</summary>
    public event Action<IReadOnlyList<WideSpreadsheetRow>>? BatchProcessed;

    public bool TryPublish(in Sample sample)
    {
        var ch = _channel;
        if (ch is null) return false;

        if (ch.Writer.TryWrite(sample))
        {
            Interlocked.Increment(ref _bufferedCount);
            Interlocked.Increment(ref _windowCounter);
            return true;
        }

        Interlocked.Increment(ref _droppedSamples);
        return false;
    }

    public async Task StartSessionAsync(
        ProjectConfig project,
        IReadOnlyList<IDriver> drivers,
        string sessionsFolder,
        string appVersion = "0.1.0",
        CancellationToken ct = default,
        bool resetSnapshot = true)
    {
        if (_isOnline) return;

        if (resetSnapshot)
        {
            Snapshot.Reset();
            Interlocked.Exchange(ref _totalSamples, 0);
            Interlocked.Exchange(ref _droppedSamples, 0);
        }
        Interlocked.Exchange(ref _windowCounter, 0);
        Interlocked.Exchange(ref _bufferedCount, 0);
        _lastRateCalcUtc = DateTime.UtcNow;
        _eventsPerSecond = 0;

        // Configura gatilho de ciclo de máquina automático se houver tag chamada Cycle_Active
        var cycleTag = project.AllTags.FirstOrDefault(t =>
            string.Equals(t.Alias, "Cycle_Active", StringComparison.OrdinalIgnoreCase));
        Snapshot.CycleTriggerTagId = cycleTag?.Id;

        var metadata = new SessionMetadata
        {
            Project = project.Name,
            StartedUtc = DateTime.UtcNow,
            AppVersion = appVersion,
            Connections = project.Connections
                .Select(c => new SessionConnectionSummary(c.Id, c.Name, c.EndpointUrl, $"{c.SecurityPolicyUri}/{c.SecurityMode}"))
                .ToList(),
            Tags = project.AllTags
                .Where(t => t.Historize)
                .Select(t => new SessionTagSummary(t.Id, t.ConnectionId, t.Address, t.Alias, t.DataType, t.Unit))
                .ToList()
        };

        _writer = await CsvSessionWriter.CreateAsync(sessionsFolder, metadata, ct).ConfigureAwait(false);

        _channel = Channel.CreateBounded<Sample>(new BoundedChannelOptions(_bufferCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _consumerCts = new CancellationTokenSource();
        _consumerTask = Task.Run(() => ConsumeLoopAsync(_consumerCts.Token), CancellationToken.None);
        _isOnline = true;

        _activeDrivers = drivers;
        _activeGroups = project.Groups;

        // RF-ACQ-07: grava snapshot inicial de todas as tags conectadas ao entrar On-line
        foreach (var drv in drivers.Where(d => d.State == ConnectionState.Connected))
        {
            var connTags = project.AllTags
                .Where(t => t.ConnectionId == drv.Connection.Id && t.Historize)
                .ToList();

            if (connTags.Count > 0)
            {
                try
                {
                    var initialSamples = await drv.ReadAsync(connTags, ct).ConfigureAwait(false);
                    foreach (var s in initialSamples)
                    {
                        TryPublish(in s);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha ao ler snapshot inicial das tags na conexão {Conn}", drv.Connection.Name);
                }
            }

            foreach (var grp in project.Groups.Where(g => g.ConnectionId == drv.Connection.Id && g.Tags.Count > 0))
            {
                await drv.StartGroupAsync(grp, this, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task UpdateGroupsAsync(
        ProjectConfig project,
        IReadOnlyList<IDriver> drivers,
        CancellationToken ct = default)
    {
        if (!_isOnline) return;

        foreach (var drv in _activeDrivers)
        {
            foreach (var grp in _activeGroups.Where(g => g.ConnectionId == drv.Connection.Id))
            {
                try
                {
                    await drv.StopGroupAsync(grp.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Erro ao parar grupo {GroupId} no driver {Conn}", grp.Id, drv.Connection.Name);
                }
            }
        }

        _activeDrivers = drivers;
        _activeGroups = project.Groups.ToList();

        var cycleTag = project.AllTags.FirstOrDefault(t =>
            string.Equals(t.Alias, "Cycle_Active", StringComparison.OrdinalIgnoreCase));
        Snapshot.CycleTriggerTagId = cycleTag?.Id;

        if (_writer is not null)
        {
            try
            {
                await _writer.ModifyMetadataAsync(m =>
                {
                    m.Tags = project.AllTags
                        .Where(t => t.Historize)
                        .Select(t => new SessionTagSummary(t.Id, t.ConnectionId, t.Address, t.Alias, t.DataType, t.Unit))
                        .ToList();
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao atualizar lista de tags nos metadados da sessão");
            }
        }

        foreach (var drv in drivers.Where(d => d.State == ConnectionState.Connected))
        {
            foreach (var grp in _activeGroups.Where(g => g.ConnectionId == drv.Connection.Id && g.Tags.Count > 0))
            {
                await drv.StartGroupAsync(grp, this, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task StopSessionAsync(CancellationToken ct = default)
    {
        if (!_isOnline) return;
        _isOnline = false;

        foreach (var drv in _activeDrivers)
        {
            foreach (var grp in _activeGroups.Where(g => g.ConnectionId == drv.Connection.Id))
            {
                try
                {
                    await drv.StopGroupAsync(grp.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Erro ao parar grupo {GroupId} no driver {Conn}", grp.Id, drv.Connection.Name);
                }
            }
        }

        _channel?.Writer.TryComplete();

        if (_consumerTask is not null)
        {
            try
            {
                await _consumerTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Erro no loop consumidor de amostras da sessão");
            }
        }

        _consumerCts?.Dispose();
        _consumerCts = null;
        _consumerTask = null;

        if (_writer is not null)
        {
            _writer.Metadata.DroppedSamples = Interlocked.Read(ref _droppedSamples);
            _writer.Metadata.Cycles = Snapshot.GetCycles().ToList();
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }

        _channel = null;
        _eventsPerSecond = 0;
    }

    public async Task<SessionMarker?> AddMarkerAsync(
        string text,
        DateTime? timestampUtc = null,
        CancellationToken ct = default,
        bool insertSnapshotRow = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var marker = new SessionMarker(timestampUtc ?? DateTime.UtcNow, text.Trim());
        if (insertSnapshotRow)
        {
            Snapshot.InsertMarker(marker.Text, marker.TimestampUtc);
        }

        if (_writer is not null)
        {
            try
            {
                await _writer.ModifyMetadataAsync(m => m.Markers.Add(marker), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao gravar marcador nos metadados da sessão");
            }
        }

        return marker;
    }

    public async Task UpdateMarkerAsync(
        DateTime timestampUtc,
        string? oldText,
        string newText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newText)) return;
        string trimmed = newText.Trim();

        Snapshot.UpdateMarkerText(timestampUtc, oldText, trimmed);

        if (_writer is not null)
        {
            try
            {
                await _writer.ModifyMetadataAsync(m =>
                {
                    var markers = m.Markers;
                    int idx = markers.FindIndex(item =>
                        Math.Abs((item.TimestampUtc - timestampUtc).TotalMilliseconds) < 5 &&
                        (string.IsNullOrEmpty(oldText) || string.Equals(item.Text, oldText, StringComparison.Ordinal)));

                    if (idx < 0)
                    {
                        idx = markers.FindIndex(item => Math.Abs((item.TimestampUtc - timestampUtc).TotalMilliseconds) < 50);
                    }

                    if (idx >= 0)
                    {
                        markers[idx] = new SessionMarker(markers[idx].TimestampUtc, trimmed);
                    }
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao atualizar marcador nos metadados da sessão");
            }
        }
    }

    public async Task RemoveMarkerAsync(
        DateTime timestampUtc,
        string? oldText = null,
        CancellationToken ct = default)
    {
        Snapshot.RemoveMarker(timestampUtc, oldText);

        if (_writer is not null)
        {
            try
            {
                await _writer.ModifyMetadataAsync(m =>
                {
                    var markers = m.Markers;
                    int removed = markers.RemoveAll(item =>
                        Math.Abs((item.TimestampUtc - timestampUtc).TotalMilliseconds) < 5 &&
                        (string.IsNullOrEmpty(oldText) || string.Equals(item.Text, oldText, StringComparison.Ordinal)));

                    if (removed == 0)
                    {
                        markers.RemoveAll(item => Math.Abs((item.TimestampUtc - timestampUtc).TotalMilliseconds) < 50);
                    }
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao remover marcador nos metadados da sessão");
            }
        }
    }

    public AcquisitionStats GetStats()
    {
        DateTime now = DateTime.UtcNow;
        double elapsedSec = (now - _lastRateCalcUtc).TotalSeconds;
        if (elapsedSec >= 0.5)
        {
            long count = Interlocked.Exchange(ref _windowCounter, 0);
            _eventsPerSecond = count / elapsedSec;
            _lastRateCalcUtc = now;
        }

        int buffered = Math.Max(0, Volatile.Read(ref _bufferedCount));
        double usagePct = Math.Min(100.0, buffered * 100.0 / _bufferCapacity);

        return new AcquisitionStats(
            _isOnline,
            Interlocked.Read(ref _totalSamples),
            Interlocked.Read(ref _droppedSamples),
            _isOnline ? _eventsPerSecond : 0,
            usagePct,
            usagePct >= 80.0,
            _writer?.CsvFilePath);
    }

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync().ConfigureAwait(false);
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        var reader = _channel!.Reader;
        var batch = new List<Sample>(2048);

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                // Agrupa amostras por até ~40 ms ou 5.000 itens para escrita eficiente em lote sem atrasar taxas rápidas
                var deadline = DateTime.UtcNow.AddMilliseconds(40);
                while (batch.Count < 5_000)
                {
                    if (reader.TryRead(out var item))
                    {
                        Interlocked.Decrement(ref _bufferedCount);
                        batch.Add(item);
                    }
                    else if (DateTime.UtcNow < deadline && !reader.Completion.IsCompleted)
                    {
                        await Task.Delay(10, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        break;
                    }
                }

                if (batch.Count > 0)
                {
                    await FlushBatchAsync(batch, ct).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            // Drena quaisquer amostras restantes após o fechamento do canal
            while (reader.TryRead(out var leftover))
            {
                Interlocked.Decrement(ref _bufferedCount);
                batch.Add(leftover);
            }

            if (batch.Count > 0)
            {
                await FlushBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task FlushBatchAsync(List<Sample> batch, CancellationToken ct)
    {
        if (_writer is not null)
        {
            await _writer.WriteBatchAsync(batch, ct).ConfigureAwait(false);
        }

        Interlocked.Add(ref _totalSamples, batch.Count);
        var newRows = Snapshot.ApplyBatch(batch);

        if (newRows.Count > 0)
        {
            BatchProcessed?.Invoke(newRows);
        }
    }
}
