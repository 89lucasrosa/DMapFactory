namespace DMapFactory.Core.Models;

/// <summary>Anotação manual na linha do tempo da sessão (RF-STO-06).</summary>
public sealed record SessionMarker(
    DateTime TimestampUtc,
    string Text,
    string Kind = "User");

/// <summary>Ciclo de máquina detectado por borda de gatilho (RF-STO-08).</summary>
public sealed record MachineCycle(
    int Number,
    DateTime StartUtc,
    DateTime? EndUtc,
    double DurationMs);

/// <summary>Associação de lote/batelada à sessão ou faixa de tempo (RF-STO-07).</summary>
public sealed record BatchRecord(
    string Code,
    DateTime StartUtc,
    DateTime? EndUtc,
    string Source = "Manual");

/// <summary>Resumo de uma conexão gravado no arquivo .session.json.</summary>
public sealed record SessionConnectionSummary(
    int Id,
    string Name,
    string EndpointUrl,
    string Security);

/// <summary>Resumo de uma tag gravado no arquivo .session.json.</summary>
public sealed record SessionTagSummary(
    int Id,
    int ConnectionId,
    string Address,
    string Alias,
    string? DataType,
    string? Unit);

/// <summary>
/// Metadados completos de uma sessão de aquisição (.session.json — seção 7.1 da especificação).
/// </summary>
public sealed class SessionMetadata
{
    public int FormatVersion { get; set; } = 1;
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public string Project { get; set; } = "Default";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }
    public string AppVersion { get; set; } = "0.1.0";
    public string Machine { get; set; } = Environment.MachineName;
    public string WindowsUser { get; set; } = Environment.UserName;
    public long TotalSamples { get; set; }
    public long DroppedSamples { get; set; }

    public List<SessionConnectionSummary> Connections { get; set; } = [];
    public List<SessionTagSummary> Tags { get; set; } = [];
    public List<SessionMarker> Markers { get; set; } = [];
    public List<MachineCycle> Cycles { get; set; } = [];
    public List<BatchRecord> Batches { get; set; } = [];
}
