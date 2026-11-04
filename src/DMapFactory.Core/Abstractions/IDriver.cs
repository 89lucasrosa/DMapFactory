using DMapFactory.Core.Models;

namespace DMapFactory.Core.Abstractions;

/// <summary>
/// Destino das amostras produzidas por um driver (normalmente o pipeline de aquisição).
/// A implementação deve ser rápida e não bloqueante — é chamada a partir das threads do driver.
/// </summary>
public interface ISampleSink
{
    /// <summary>Publica uma amostra. Retorna false se o buffer estiver cheio (amostra descartada).</summary>
    bool TryPublish(in Sample sample);
}

/// <summary>Argumentos do evento de mudança de estado da conexão.</summary>
public sealed class ConnectionStateChangedEventArgs(ConnectionState state, string? reason) : EventArgs
{
    public ConnectionState State { get; } = state;
    public string? Reason { get; } = reason;
}

/// <summary>
/// Contrato comum para todos os protocolos (OPC UA na v1; Modbus, S7, CIP, ADS no futuro).
/// </summary>
public interface IDriver : IAsyncDisposable
{
    /// <summary>Identificador do protocolo: "OPCUA", "MODBUS", "S7"...</summary>
    string Protocol { get; }

    ConnectionConfig Connection { get; }

    ConnectionState State { get; }

    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    Task ConnectAsync(CancellationToken ct = default);

    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>Lista os filhos de um nó. <paramref name="parentId"/> null = raiz.</summary>
    Task<IReadOnlyList<BrowseNode>> BrowseAsync(string? parentId, CancellationToken ct = default);

    /// <summary>Leitura pontual de um conjunto de tags.</summary>
    Task<IReadOnlyList<Sample>> ReadAsync(IReadOnlyList<TagConfig> tags, CancellationToken ct = default);

    /// <summary>Inicia a aquisição de um grupo (subscription ou polling), publicando no <paramref name="sink"/>.</summary>
    Task StartGroupAsync(AcquisitionGroup group, ISampleSink sink, CancellationToken ct = default);

    Task StopGroupAsync(Guid groupId, CancellationToken ct = default);
}
