namespace DMapFactory.Core.Models;

/// <summary>Estado de uma conexão com um servidor/dispositivo.</summary>
public enum ConnectionState
{
    Disabled,
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Faulted,
}

/// <summary>Modo de aquisição de um grupo de tags.</summary>
public enum AcquisitionMode
{
    /// <summary>Notificação por mudança (OPC UA Subscription / MonitoredItems).</summary>
    Subscription,

    /// <summary>Leitura cíclica (serviço Read); registra apenas quando o valor muda.</summary>
    Polling,
}

/// <summary>Origem do timestamp gravado (RF-ACQ-04).</summary>
public enum TimestampOrigin : byte
{
    /// <summary>SourceTimestamp — fornecido pela fonte (CLP/dispositivo).</summary>
    Source = 0,

    /// <summary>ServerTimestamp — fornecido pelo servidor OPC UA.</summary>
    Server = 1,

    /// <summary>Relógio local do coletor (fallback).</summary>
    Local = 2,
}

/// <summary>Classificação simplificada da qualidade (derivada do StatusCode).</summary>
public enum QualityClass : byte
{
    Good = 0,
    Uncertain = 1,
    Bad = 2,
}

/// <summary>Tipo de deadband aplicado ao filtro de mudança.</summary>
public enum DeadbandType
{
    None,
    Absolute,
    Percent,
}

/// <summary>Modo de segurança de mensagens OPC UA.</summary>
public enum MessageSecurity
{
    None,
    Sign,
    SignAndEncrypt,
}

/// <summary>Tipo de autenticação de usuário no servidor.</summary>
public enum UserAuthentication
{
    Anonymous,
    UserName,
    Certificate,
}
