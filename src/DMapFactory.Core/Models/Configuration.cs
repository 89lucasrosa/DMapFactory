namespace DMapFactory.Core.Models;

/// <summary>Configuração de uma conexão (persistida no arquivo de projeto .dmproj).</summary>
public sealed class ConnectionConfig
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Protocol { get; set; } = "OPCUA";
    public bool Enabled { get; set; } = true;

    /// <summary>URL do endpoint, ex.: opc.tcp://192.168.0.10:4840.</summary>
    public string EndpointUrl { get; set; } = "";

    /// <summary>URI da política de segurança (ex.: http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256).</summary>
    public string SecurityPolicyUri { get; set; } = "http://opcfoundation.org/UA/SecurityPolicy#None";
    public MessageSecurity SecurityMode { get; set; } = MessageSecurity.None;

    public UserAuthentication Authentication { get; set; } = UserAuthentication.Anonymous;
    public string? UserName { get; set; }

    /// <summary>Senha protegida com DPAPI (Base64). Nunca armazenar em texto claro.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>Caminho do certificado de usuário (.pfx) quando Authentication = Certificate.</summary>
    public string? UserCertificatePath { get; set; }

    public int SessionTimeoutMs { get; set; } = 60_000;
    public int OperationTimeoutMs { get; set; } = 15_000;
    public int KeepAliveIntervalMs { get; set; } = 5_000;
}

/// <summary>Grupo de aquisição: compartilha modo e intervalos (RF-TAG-07).</summary>
public sealed class AcquisitionGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Default";
    public int ConnectionId { get; set; }
    public AcquisitionMode Mode { get; set; } = AcquisitionMode.Subscription;

    /// <summary>Intervalo de amostragem (subscription) ou de leitura (polling), em ms.</summary>
    public double SamplingIntervalMs { get; set; } = 100;

    /// <summary>Intervalo de publicação da subscription, em ms.</summary>
    public double PublishingIntervalMs { get; set; } = 100;

    /// <summary>Tamanho da fila por MonitoredItem (> 1 evita perder transições rápidas).</summary>
    public uint QueueSize { get; set; } = 10;

    public List<TagConfig> Tags { get; set; } = [];
}

/// <summary>Configuração de uma tag (RF-TAG-04).</summary>
public sealed class TagConfig
{
    public int Id { get; set; }
    public int ConnectionId { get; set; }

    /// <summary>Endereço no protocolo (OPC UA: NodeId em formato texto, ex.: ns=3;s="DB1"."Motor").</summary>
    public string Address { get; set; } = "";

    public string Alias { get; set; } = "";
    public string? Description { get; set; }
    public string? DataType { get; set; }
    public string? Unit { get; set; }

    public DeadbandType DeadbandType { get; set; } = DeadbandType.None;
    public double DeadbandValue { get; set; }

    public bool Historize { get; set; } = true;

    /// <summary>Aparência (cor da pena, formato) — RF-GRD-09.</summary>
    public TagAppearance Appearance { get; set; } = new();
}

/// <summary>Aparência configurável da tag na planilha e no gráfico.</summary>
public sealed class TagAppearance
{
    /// <summary>Cor em #RRGGBB; null = cor automática.</summary>
    public string? Color { get; set; }
    public string? NumberFormat { get; set; }
    public int YAxis { get; set; }

    /// <summary>Condição para colorir a linha inteira na planilha (ex.: "= 1"). Null = desativado.</summary>
    public string? RowHighlightCondition { get; set; }
    public string? RowHighlightColor { get; set; }
}
