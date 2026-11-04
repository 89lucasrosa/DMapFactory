namespace DMapFactory.Core.Models;

/// <summary>
/// Uma amostra (mudança de valor) recebida de um driver.
/// Estrutura imutável e compacta: é o item que trafega no pipeline de aquisição.
/// </summary>
/// <param name="TagId">Identificador da tag no projeto.</param>
/// <param name="TimestampUtc">Timestamp efetivo (fonte, servidor ou local — ver <paramref name="Origin"/>).</param>
/// <param name="ReceivedUtc">Momento em que o coletor recebeu a amostra.</param>
/// <param name="Origin">Origem do timestamp.</param>
/// <param name="Value">Valor (escalar, string, array ou estrutura decodificada).</param>
/// <param name="StatusCode">StatusCode bruto (OPC UA) ou equivalente do protocolo.</param>
public readonly record struct Sample(
    int TagId,
    DateTime TimestampUtc,
    DateTime ReceivedUtc,
    TimestampOrigin Origin,
    object? Value,
    uint StatusCode)
{
    /// <summary>Qualidade derivada dos 2 bits mais significativos do StatusCode (padrão OPC UA).</summary>
    public QualityClass Quality => (StatusCode >> 30) switch
    {
        0 => QualityClass.Good,
        1 => QualityClass.Uncertain,
        _ => QualityClass.Bad,
    };
}
