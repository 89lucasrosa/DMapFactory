namespace DMapFactory.Core.Models;

/// <summary>Arquivo de projeto .dmproj (RF-PRJ-01).</summary>
public sealed class ProjectConfig
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "NovoProjeto";
    public List<ConnectionConfig> Connections { get; set; } = [];
    public List<AcquisitionGroup> Groups { get; set; } = [];

    /// <summary>Pasta onde as sessões (.csv + .session.json) são gravadas. Null = padrão do usuário.</summary>
    public string? SessionsFolder { get; set; }

    public IEnumerable<TagConfig> AllTags => Groups.SelectMany(g => g.Tags);
}

/// <summary>Nó retornado pela navegação (browse) no espaço de endereços.</summary>
/// <param name="Id">Identificador do nó (OPC UA: NodeId em texto).</param>
/// <param name="DisplayName">Nome exibido.</param>
/// <param name="IsVariable">True se o nó possui valor (pode virar tag).</param>
/// <param name="HasChildren">True se o nó pode ser expandido.</param>
/// <param name="DataType">Tipo de dado (somente variáveis).</param>
public sealed record BrowseNode(
    string Id,
    string DisplayName,
    bool IsVariable,
    bool HasChildren,
    string? DataType = null);
