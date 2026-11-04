using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DMapFactory.Core.Models;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

#pragma warning disable CS0618

namespace DMapFactory.Drivers.OpcUa;

/// <summary>Status de confiança de um certificado de servidor OPC UA.</summary>
public enum CertificateTrustStatus
{
    Unknown = 0,
    Trusted = 1,
    Rejected = 2
}

/// <summary>Opções para geração do certificado de cliente OPC UA (autoassinado).</summary>
public sealed record ClientCertificateGenerationOptions(
    string CommonName = "DMapFactory OPC UA Client",
    string Organization = "DMapFactory",
    ushort KeySize = 2048,
    DateTime? NotBeforeUtc = null,
    DateTime? NotAfterUtc = null);

/// <summary>Endpoint descoberto via GetEndpoints em um servidor OPC UA (RF-CON-02).</summary>
public sealed record DiscoveredEndpoint(
    string EndpointUrl,
    string SecurityPolicyUri,
    string SecurityPolicyShortName,
    MessageSecurity SecurityMode,
    byte SecurityLevel,
    bool IsDeprecatedPolicy,
    IReadOnlyList<UserAuthentication> SupportedAuthentications)
{
    public string DisplayText =>
        $"{SecurityPolicyShortName} / {SecurityMode}{(IsDeprecatedPolicy ? " ⚠ (obsoleta)" : "")}";
}

/// <summary>Resumo de um certificado X.509 (RF-CON-05/06).</summary>
public sealed record CertificateInfo(
    string Subject,
    string Issuer,
    string Thumbprint,
    DateTime NotBeforeUtc,
    DateTime NotAfterUtc,
    string? FilePath = null,
    string? ApplicationUri = null,
    int KeySize = 0,
    string SignatureAlgorithm = "",
    bool IsSelfSigned = false,
    CertificateTrustStatus TrustStatus = CertificateTrustStatus.Unknown)
{
    public DateTime NotBeforeLocal => NotBeforeUtc.ToLocalTime();
    public DateTime NotAfterLocal => NotAfterUtc.ToLocalTime();
    public bool IsExpired => DateTime.UtcNow > NotAfterUtc;
    public bool IsNotYetValid => DateTime.UtcNow < NotBeforeUtc;
    public int RemainingDays => (int)Math.Ceiling((NotAfterUtc - DateTime.UtcNow).TotalDays);
    public bool ExpiresSoon => !IsExpired && RemainingDays <= 30;
    public bool IsTrusted => TrustStatus == CertificateTrustStatus.Trusted;
    public bool IsRejected => TrustStatus == CertificateTrustStatus.Rejected;

    public string ValidityRangeText =>
        $"{NotBeforeLocal:dd/MM/yyyy} a {NotAfterLocal:dd/MM/yyyy}";

    public string ValidityStatusText =>
        IsExpired ? $"Expirado ({NotAfterLocal:dd/MM/yyyy})" :
        IsNotYetValid ? $"Ainda não válido (início {NotBeforeLocal:dd/MM/yyyy})" :
        ExpiresSoon ? $"Expira em {RemainingDays}d ({NotAfterLocal:dd/MM/yyyy})" :
        $"Válido ({RemainingDays} dias restantes)";

    public string TrustStatusText => TrustStatus switch
    {
        CertificateTrustStatus.Trusted => "Confiável",
        CertificateTrustStatus.Rejected => "Rejeitado",
        _ => "Desconhecido"
    };
}

/// <summary>Certificado de servidor OPC UA obtido via descoberta de endpoints (GetEndpoints).</summary>
public sealed record DiscoveredServerCertificate(
    CertificateInfo Certificate,
    string DiscoveryUrl,
    IReadOnlyList<string> EndpointsSummary,
    byte[] RawData)
{
    public string EndpointsDisplay => string.Join(" | ", EndpointsSummary);
}

/// <summary>
/// Gerencia a <see cref="ApplicationConfiguration"/> OPC UA do cliente, o certificado autoassinado da aplicação,
/// as listas Trusted/Rejected e a descoberta de endpoints e certificados de servidores (RF-CON-02..06).
/// </summary>
public sealed class OpcUaSecurityManager
{
    private readonly string _pkiRoot;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _recentRejections = new(StringComparer.OrdinalIgnoreCase);
    private ApplicationConfiguration? _config;

    public OpcUaSecurityManager(string? pkiRootDirectory = null)
    {
        _pkiRoot = pkiRootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DMapFactory", "pki");
    }

    public string PkiRoot => _pkiRoot;
    public string OwnCertsDirectory => Path.Combine(_pkiRoot, "own");
    public string TrustedPeersDirectory => Path.Combine(_pkiRoot, "trusted");
    public string RejectedPeersDirectory => Path.Combine(_pkiRoot, "rejected");

    /// <summary>
    /// Quando true, aceita automaticamente certificados de servidores desconhecidos e os grava em Trusted.
    /// Por padrão é false (exige confiança explícita ou aprovação).
    /// </summary>
    public bool AutoAcceptUntrustedServerCertificates { get; set; } = false;

    /// <summary>Callback opcional para aprovar/rejeitar interativamente um certificado de servidor desconhecido (RF-CON-06).</summary>
    public Func<CertificateInfo, bool>? UntrustedCertificatePrompt { get; set; }

    public async Task<ApplicationConfiguration> GetOrCreateConfigurationAsync(CancellationToken ct = default)
    {
        if (_config is not null) return _config;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_config is not null) return _config;

            Directory.CreateDirectory(OwnCertsDirectory);
            Directory.CreateDirectory(TrustedPeersDirectory);
            Directory.CreateDirectory(RejectedPeersDirectory);

            string hostName = Utils.GetHostName();
            string defaultSubject = $"CN=DMapFactory OPC UA Client, O=DMapFactory, DC={hostName}";

            var config = new ApplicationConfiguration
            {
                ApplicationName = "DMapFactory OPC UA Client",
                ApplicationUri = $"urn:{hostName}:DMapFactory:Client",
                ProductUri = "urn:dmapfactory.org:Client",
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = OwnCertsDirectory,
                        SubjectName = defaultSubject
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(_pkiRoot, "issuers")
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = TrustedPeersDirectory
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = RejectedPeersDirectory
                    },
                    AutoAcceptUntrustedCertificates = false,
                    RejectSHA1SignedCertificates = false,
                    MinimumCertificateKeySize = 1024,
                    AddAppCertToTrustedStore = false
                },
                TransportConfigurations = [],
                TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 }
            };

            await config.ValidateAsync(ApplicationType.Client).ConfigureAwait(false);
            config.CertificateValidator.CertificateValidation += OnCertificateValidation;

            var existingOwn = await TryLoadExistingOwnCertificateAsync(config).ConfigureAwait(false);
            if (existingOwn is not null)
            {
                config.SecurityConfiguration.ApplicationCertificate.Certificate = existingOwn;
                config.SecurityConfiguration.ApplicationCertificate.SubjectName = existingOwn.Subject;
                config.SecurityConfiguration.ApplicationCertificate.Thumbprint = existingOwn.Thumbprint;
                await config.CertificateValidator.UpdateAsync(config.SecurityConfiguration).ConfigureAwait(false);
            }
            else
            {
                await GenerateApplicationCertificateInternalAsync(
                    config,
                    new ClientCertificateGenerationOptions(),
                    ct).ConfigureAwait(false);
            }

            // Garante que o certificado próprio do cliente não fique poluindo a pasta de servidores confiáveis
            await PurgeOwnCertificateFromPeerStoresAsync(config).ConfigureAwait(false);

            _config = config;
            return config;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Retorna detalhes do certificado de aplicação (cliente OPC UA) atual (RF-CON-05).</summary>
    public async Task<CertificateInfo?> GetApplicationCertificateInfoAsync(CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        var cert = config.SecurityConfiguration.ApplicationCertificate.Certificate
                   ?? await TryLoadExistingOwnCertificateAsync(config).ConfigureAwait(false)
                   ?? await config.SecurityConfiguration.ApplicationCertificate.FindAsync(needPrivateKey: false).ConfigureAwait(false);

        if (cert is null) return null;

        string? filePath = FindFirstCertificateFileInDirectory(OwnCertsDirectory, cert.Thumbprint) ?? OwnCertsDirectory;
        return ToInfo(cert, filePath, CertificateTrustStatus.Trusted);
    }

    /// <summary>Exporta o certificado público da aplicação (.der) para importação no CLP/servidor (RF-CON-05).</summary>
    public async Task ExportApplicationCertificateDerAsync(string targetFilePath, CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        var cert = config.SecurityConfiguration.ApplicationCertificate.Certificate
                   ?? await TryLoadExistingOwnCertificateAsync(config).ConfigureAwait(false)
                   ?? await config.SecurityConfiguration.ApplicationCertificate.FindAsync(needPrivateKey: false).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Certificado da aplicação não encontrado.");

        await File.WriteAllBytesAsync(targetFilePath, cert.Export(X509ContentType.Cert), ct).ConfigureAwait(false);
    }

    /// <summary>Exporta o certificado da aplicação com chave privada (.pfx) para backup ou migração.</summary>
    public async Task ExportApplicationCertificatePfxAsync(string targetFilePath, string? password = null, CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        var cert = await TryLoadExistingOwnCertificateAsync(config).ConfigureAwait(false)
                   ?? config.SecurityConfiguration.ApplicationCertificate.Certificate
                   ?? await config.SecurityConfiguration.ApplicationCertificate.FindAsync(needPrivateKey: true).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Certificado da aplicação com chave privada não encontrado.");

        byte[] pfxBytes = string.IsNullOrEmpty(password)
            ? cert.Export(X509ContentType.Pfx)
            : cert.Export(X509ContentType.Pfx, password);
        await File.WriteAllBytesAsync(targetFilePath, pfxBytes, ct).ConfigureAwait(false);
    }

    /// <summary>Importa um certificado de cliente (.pfx/.p12 ou .der/.cer) como certificado da aplicação.</summary>
    public async Task<CertificateInfo> ImportApplicationCertificateAsync(
        string sourceFilePath,
        string? password = null,
        CancellationToken ct = default)
    {
        string ext = Path.GetExtension(sourceFilePath);
        X509Certificate2 importedCert;
        if (ext.Equals(".pfx", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".p12", StringComparison.OrdinalIgnoreCase))
        {
            importedCert = X509CertificateLoader.LoadPkcs12FromFile(
                sourceFilePath,
                password,
                X509KeyStorageFlags.Exportable);
        }
        else
        {
            importedCert = X509CertificateLoader.LoadCertificateFromFile(sourceFilePath);
        }

        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        ClearDirectoryStoreFiles(OwnCertsDirectory);

        using (var store = config.SecurityConfiguration.ApplicationCertificate.OpenStore())
        {
            await store.AddAsync(importedCert).ConfigureAwait(false);
        }

        var reloaded = await TryLoadExistingOwnCertificateAsync(config).ConfigureAwait(false) ?? importedCert;
        config.SecurityConfiguration.ApplicationCertificate.Certificate = reloaded;
        config.SecurityConfiguration.ApplicationCertificate.SubjectName = reloaded.Subject;
        config.SecurityConfiguration.ApplicationCertificate.Thumbprint = reloaded.Thumbprint;

        await config.CertificateValidator.UpdateAsync(config.SecurityConfiguration).ConfigureAwait(false);
        string? filePath = FindFirstCertificateFileInDirectory(OwnCertsDirectory, reloaded.Thumbprint) ?? OwnCertsDirectory;
        return ToInfo(reloaded, filePath, CertificateTrustStatus.Trusted);
    }

    /// <summary>
    /// Gera um novo certificado X.509 autoassinado para a aplicação cliente OPC UA com opções customizadas
    /// de validade (NotBefore / NotAfter), tamanho de chave, Common Name e Organização.
    /// </summary>
    public async Task<CertificateInfo> GenerateApplicationCertificateAsync(
        ClientCertificateGenerationOptions? options = null,
        CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        return await GenerateApplicationCertificateInternalAsync(
            config,
            options ?? new ClientCertificateGenerationOptions(),
            ct).ConfigureAwait(false);
    }

    /// <summary>Gera um novo certificado X.509 autoassinado para a aplicação cliente OPC UA com validade padrão de 2 anos.</summary>
    public async Task<CertificateInfo?> RegenerateApplicationCertificateAsync(
        ushort keySize = 2048,
        CancellationToken ct = default)
    {
        return await GenerateApplicationCertificateAsync(
            new ClientCertificateGenerationOptions(KeySize: keySize),
            ct).ConfigureAwait(false);
    }

    /// <summary>Lista os certificados de servidores OPC UA confiáveis (Trusted Peers).</summary>
    public async Task<IReadOnlyList<CertificateInfo>> GetTrustedServerCertificatesAsync(CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        string? ownThumbprint = config.SecurityConfiguration.ApplicationCertificate.Certificate?.Thumbprint
                                ?? config.SecurityConfiguration.ApplicationCertificate.Thumbprint;
        return await EnumerateStoreAndFolderAsync(
            config.SecurityConfiguration.TrustedPeerCertificates,
            TrustedPeersDirectory,
            CertificateTrustStatus.Trusted,
            ownThumbprint,
            config.ApplicationUri).ConfigureAwait(false);
    }

    /// <summary>Lista os certificados de servidores OPC UA rejeitados (Rejected Peers).</summary>
    public async Task<IReadOnlyList<CertificateInfo>> GetRejectedServerCertificatesAsync(CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        string? ownThumbprint = config.SecurityConfiguration.ApplicationCertificate.Certificate?.Thumbprint
                                ?? config.SecurityConfiguration.ApplicationCertificate.Thumbprint;
        return await EnumerateStoreAndFolderAsync(
            config.SecurityConfiguration.RejectedCertificateStore,
            RejectedPeersDirectory,
            CertificateTrustStatus.Rejected,
            ownThumbprint,
            config.ApplicationUri).ConfigureAwait(false);
    }

    /// <summary>Lista todos os certificados de servidores OPC UA (Confiáveis + Rejeitados).</summary>
    public async Task<IReadOnlyList<CertificateInfo>> GetAllServerCertificatesAsync(CancellationToken ct = default)
    {
        var trusted = await GetTrustedServerCertificatesAsync(ct).ConfigureAwait(false);
        var rejected = await GetRejectedServerCertificatesAsync(ct).ConfigureAwait(false);
        var byThumb = new Dictionary<string, CertificateInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rejected)
            byThumb[r.Thumbprint] = r;
        foreach (var t in trusted)
            byThumb[t.Thumbprint] = t;

        return byThumb.Values
            .OrderBy(c => c.TrustStatus == CertificateTrustStatus.Trusted ? 0 : 1)
            .ThenBy(c => c.Subject, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Importa o certificado de um servidor OPC UA (.der, .cer, .crt) para a lista Trusted ou Rejected.</summary>
    public async Task<CertificateInfo> ImportServerCertificateAsync(
        string sourceFilePath,
        bool trust = true,
        CancellationToken ct = default)
    {
        using var cert = X509CertificateLoader.LoadCertificateFromFile(sourceFilePath);
        return trust
            ? await TrustServerCertificateAsync(cert.RawData, ct).ConfigureAwait(false)
            : await RejectServerCertificateAsync(cert.RawData, ct).ConfigureAwait(false);
    }

    /// <summary>Importa o certificado de um servidor OPC UA (.der, .cer, .crt) diretamente para a lista de confiáveis.</summary>
    public Task<CertificateInfo> ImportTrustedServerCertificateAsync(string sourceFilePath, CancellationToken ct = default) =>
        ImportServerCertificateAsync(sourceFilePath, trust: true, ct);

    /// <summary>Exporta um certificado de servidor (Trusted ou Rejected) em formato DER (.der).</summary>
    public async Task ExportServerCertificateDerAsync(
        string thumbprint,
        bool fromTrusted,
        string targetFilePath,
        CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        var cert = await FindServerCertificateByThumbprintAsync(config, thumbprint, fromTrusted).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Certificado '{thumbprint}' não encontrado.");

        using (cert)
        {
            await File.WriteAllBytesAsync(targetFilePath, cert.Export(X509ContentType.Cert), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Adiciona ou move um certificado de servidor (em bytes DER) para a lista de servidores confiáveis (Trusted).</summary>
    public async Task<CertificateInfo> TrustServerCertificateAsync(byte[] rawDerBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawDerBytes);
        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        using var cert = X509CertificateLoader.LoadCertificate(rawDerBytes);

        using (var trustedStore = config.SecurityConfiguration.TrustedPeerCertificates.OpenStore())
        {
            var existing = await trustedStore.FindByThumbprintAsync(cert.Thumbprint).ConfigureAwait(false);
            if (existing.Count == 0)
            {
                await trustedStore.AddAsync(cert).ConfigureAwait(false);
            }
        }

        _recentRejections.TryRemove(cert.Thumbprint, out _);
        await RemoveServerCertificateInternalAsync(config, cert.Thumbprint, fromTrusted: false).ConfigureAwait(false);
        await config.CertificateValidator.UpdateAsync(config.SecurityConfiguration).ConfigureAwait(false);

        string? path = FindFirstCertificateFileInDirectory(TrustedPeersDirectory, cert.Thumbprint) ?? TrustedPeersDirectory;
        return ToInfo(cert, path, CertificateTrustStatus.Trusted);
    }

    /// <summary>Adiciona ou move um certificado de servidor (em bytes DER) para a lista de servidores rejeitados (Rejected).</summary>
    public async Task<CertificateInfo> RejectServerCertificateAsync(byte[] rawDerBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawDerBytes);
        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        using var cert = X509CertificateLoader.LoadCertificate(rawDerBytes);

        using (var rejectedStore = config.SecurityConfiguration.RejectedCertificateStore.OpenStore())
        {
            var existing = await rejectedStore.FindByThumbprintAsync(cert.Thumbprint).ConfigureAwait(false);
            if (existing.Count == 0)
            {
                await rejectedStore.AddAsync(cert).ConfigureAwait(false);
            }
        }

        await RemoveServerCertificateInternalAsync(config, cert.Thumbprint, fromTrusted: true).ConfigureAwait(false);
        await config.CertificateValidator.UpdateAsync(config.SecurityConfiguration).ConfigureAwait(false);

        string? path = FindFirstCertificateFileInDirectory(RejectedPeersDirectory, cert.Thumbprint) ?? RejectedPeersDirectory;
        return ToInfo(cert, path, CertificateTrustStatus.Rejected);
    }

    /// <summary>Move um certificado de servidor rejeitado para a lista de servidores confiáveis (Trusted).</summary>
    public async Task<bool> TrustRejectedCertificateAsync(string thumbprint, CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        var found = await FindServerCertificateByThumbprintAsync(config, thumbprint, fromTrusted: false).ConfigureAwait(false);
        if (found is null)
            return false;

        using (found)
        {
            await TrustServerCertificateAsync(found.RawData, ct).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>Move um certificado de servidor confiável (Trusted) para a lista de rejeitados (Rejected).</summary>
    public async Task<bool> RejectTrustedCertificateAsync(string thumbprint, CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        var found = await FindServerCertificateByThumbprintAsync(config, thumbprint, fromTrusted: true).ConfigureAwait(false);
        if (found is null)
            return false;

        using (found)
        {
            await RejectServerCertificateAsync(found.RawData, ct).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>Remove um certificado de servidor da lista Trusted ou Rejected.</summary>
    public async Task RemoveServerCertificateAsync(string thumbprint, bool fromTrusted, CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        await RemoveServerCertificateInternalAsync(config, thumbprint, fromTrusted).ConfigureAwait(false);
        await config.CertificateValidator.UpdateAsync(config.SecurityConfiguration).ConfigureAwait(false);
    }

    /// <summary>
    /// Conecta ao Discovery Endpoint de um servidor OPC UA (GetEndpoints) e retorna os certificados X.509
    /// apresentados pelo servidor, juntamente com o status de confiança atual (Trusted / Rejected / Unknown).
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredServerCertificate>> DiscoverServerCertificatesAsync(
        string discoveryUrl,
        int timeoutMs = 10_000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(discoveryUrl))
            throw new ArgumentException("Informe uma URL de servidor OPC UA válida (ex.: opc.tcp://localhost:4840).", nameof(discoveryUrl));

        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        var trustedList = await GetTrustedServerCertificatesAsync(ct).ConfigureAwait(false);
        var rejectedList = await GetRejectedServerCertificatesAsync(ct).ConfigureAwait(false);

        var trustedSet = new HashSet<string>(trustedList.Select(c => c.Thumbprint), StringComparer.OrdinalIgnoreCase);
        var rejectedSet = new HashSet<string>(rejectedList.Select(c => c.Thumbprint), StringComparer.OrdinalIgnoreCase);

        var endpointConfiguration = EndpointConfiguration.Create();
        endpointConfiguration.OperationTimeout = timeoutMs;

        using var client = DiscoveryClient.Create(new Uri(discoveryUrl.Trim()), endpointConfiguration);
        var collection = await client.GetEndpointsAsync(null, ct).ConfigureAwait(false);

        var grouped = new Dictionary<string, (X509Certificate2 Cert, byte[] Raw, List<string> Endpoints)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var ep in collection)
            {
                if (ep.ServerCertificate is not { Length: > 0 })
                    continue;

                var cert = X509CertificateLoader.LoadCertificate(ep.ServerCertificate);
                string policyShort = GetPolicyShortName(ep.SecurityPolicyUri);
                string epSummary = $"{policyShort} / {ep.SecurityMode}";

                if (!grouped.TryGetValue(cert.Thumbprint, out var entry))
                {
                    entry = (cert, ep.ServerCertificate, new List<string>());
                    grouped[cert.Thumbprint] = entry;
                }
                else
                {
                    cert.Dispose();
                }

                if (!entry.Endpoints.Contains(epSummary, StringComparer.OrdinalIgnoreCase))
                {
                    entry.Endpoints.Add(epSummary);
                }
            }

            var results = new List<DiscoveredServerCertificate>(grouped.Count);
            foreach (var (_, value) in grouped)
            {
                var status = trustedSet.Contains(value.Cert.Thumbprint)
                    ? CertificateTrustStatus.Trusted
                    : rejectedSet.Contains(value.Cert.Thumbprint)
                        ? CertificateTrustStatus.Rejected
                        : CertificateTrustStatus.Unknown;

                var info = ToInfo(value.Cert, null, status);
                results.Add(new DiscoveredServerCertificate(
                    info,
                    discoveryUrl.Trim(),
                    value.Endpoints,
                    value.Raw));
            }

            return results;
        }
        finally
        {
            foreach (var (_, value) in grouped)
            {
                value.Cert.Dispose();
            }
        }
    }

    /// <summary>Descobre os endpoints oferecidos por uma URL OPC UA (RF-CON-02).</summary>
    public static async Task<IReadOnlyList<DiscoveredEndpoint>> DiscoverEndpointsAsync(
        string discoveryUrl, int timeoutMs = 10_000, CancellationToken ct = default)
    {
        var endpointConfiguration = EndpointConfiguration.Create();
        endpointConfiguration.OperationTimeout = timeoutMs;

        using var client = DiscoveryClient.Create(new Uri(discoveryUrl), endpointConfiguration);
        var collection = await client.GetEndpointsAsync(null, ct).ConfigureAwait(false);

        var list = new List<DiscoveredEndpoint>(collection.Count);
        foreach (var ep in collection)
        {
            var mode = ep.SecurityMode switch
            {
                MessageSecurityMode.Sign => MessageSecurity.Sign,
                MessageSecurityMode.SignAndEncrypt => MessageSecurity.SignAndEncrypt,
                _ => MessageSecurity.None
            };

            string shortPolicy = GetPolicyShortName(ep.SecurityPolicyUri);
            bool deprecated = shortPolicy is "Basic128Rsa15" or "Basic256";

            var auths = ep.UserIdentityTokens
                .Select(t => t.TokenType switch
                {
                    UserTokenType.UserName => UserAuthentication.UserName,
                    UserTokenType.Certificate => UserAuthentication.Certificate,
                    _ => UserAuthentication.Anonymous
                })
                .Distinct()
                .ToList();

            if (auths.Count == 0) auths.Add(UserAuthentication.Anonymous);

            list.Add(new DiscoveredEndpoint(
                ep.EndpointUrl,
                ep.SecurityPolicyUri,
                shortPolicy,
                mode,
                ep.SecurityLevel,
                deprecated,
                auths));
        }

        return list;
    }

    public static string GetPolicyShortName(string? policyUri)
    {
        if (string.IsNullOrEmpty(policyUri)) return "None";
        int idx = policyUri.LastIndexOf('#');
        return idx >= 0 && idx < policyUri.Length - 1 ? policyUri[(idx + 1)..] : policyUri;
    }

    private async Task<CertificateInfo> GenerateApplicationCertificateInternalAsync(
        ApplicationConfiguration config,
        ClientCertificateGenerationOptions options,
        CancellationToken ct)
    {
        string hostName = Utils.GetHostName();
        string cn = string.IsNullOrWhiteSpace(options.CommonName)
            ? "DMapFactory OPC UA Client"
            : SanitizeDistinguishedNamePart(options.CommonName);
        string org = string.IsNullOrWhiteSpace(options.Organization)
            ? "DMapFactory"
            : SanitizeDistinguishedNamePart(options.Organization);

        ushort keySize = options.KeySize is 1024 or 2048 or 3072 or 4096 ? options.KeySize : (ushort)2048;
        DateTime notBeforeUtc = (options.NotBeforeUtc ?? DateTime.UtcNow.Date).ToUniversalTime();
        DateTime notAfterUtc = (options.NotAfterUtc ?? notBeforeUtc.AddYears(2)).ToUniversalTime();
        if (notAfterUtc <= notBeforeUtc)
        {
            notAfterUtc = notBeforeUtc.AddDays(1);
        }

        string subjectName = $"CN={cn}, O={org}, DC={hostName}";
        var domainNames = new List<string> { hostName };
        if (!string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            domainNames.Add("localhost");
        }

        string? previousThumbprint = config.SecurityConfiguration.ApplicationCertificate.Certificate?.Thumbprint
                                     ?? config.SecurityConfiguration.ApplicationCertificate.Thumbprint;

        using var generated = CertificateFactory.CreateCertificate(
                config.ApplicationUri,
                cn,
                subjectName,
                domainNames)
            .SetNotBefore(notBeforeUtc)
            .SetNotAfter(notAfterUtc)
            .SetHashAlgorithm(HashAlgorithmName.SHA256)
            .SetRSAKeySize(keySize)
            .CreateForRSA();

        var exportableCert = X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            null,
            X509KeyStorageFlags.Exportable);

        ClearDirectoryStoreFiles(OwnCertsDirectory);

        using (var store = config.SecurityConfiguration.ApplicationCertificate.OpenStore())
        {
            await store.AddAsync(exportableCert).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(previousThumbprint))
        {
            await RemoveServerCertificateInternalAsync(config, previousThumbprint, fromTrusted: true).ConfigureAwait(false);
        }

        var loaded = await TryLoadExistingOwnCertificateAsync(config).ConfigureAwait(false) ?? exportableCert;
        config.SecurityConfiguration.ApplicationCertificate.SubjectName = loaded.Subject;
        config.SecurityConfiguration.ApplicationCertificate.Thumbprint = loaded.Thumbprint;
        config.SecurityConfiguration.ApplicationCertificate.Certificate = loaded;

        await config.CertificateValidator.UpdateAsync(config.SecurityConfiguration).ConfigureAwait(false);

        string? filePath = FindFirstCertificateFileInDirectory(OwnCertsDirectory, loaded.Thumbprint) ?? OwnCertsDirectory;
        return ToInfo(loaded, filePath, CertificateTrustStatus.Trusted);
    }

    private async Task<X509Certificate2?> TryLoadExistingOwnCertificateAsync(ApplicationConfiguration config)
    {
        try
        {
            var loadedPriv = await config.SecurityConfiguration.ApplicationCertificate.LoadPrivateKeyExAsync(null).ConfigureAwait(false);
            if (loadedPriv is not null && loadedPriv.HasPrivateKey)
            {
                return loadedPriv;
            }
        }
        catch { }

        string privateDir = Path.Combine(OwnCertsDirectory, "private");
        if (Directory.Exists(privateDir))
        {
            var pfxFile = Directory.EnumerateFiles(privateDir, "*.pfx", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (pfxFile is not null)
            {
                try
                {
                    return X509CertificateLoader.LoadPkcs12FromFile(pfxFile, null, X509KeyStorageFlags.Exportable);
                }
                catch { }
            }
        }

        string certsDir = Path.Combine(OwnCertsDirectory, "certs");
        string searchRoot = Directory.Exists(certsDir) ? certsDir : OwnCertsDirectory;
        if (Directory.Exists(searchRoot))
        {
            var derFile = Directory.EnumerateFiles(searchRoot, "*.*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string ext = Path.GetExtension(f);
                    return ext.Equals(".der", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".cer", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".crt", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (derFile is not null)
            {
                try
                {
                    return X509CertificateLoader.LoadCertificateFromFile(derFile);
                }
                catch { }
            }
        }

        return null;
    }

    private async Task PurgeOwnCertificateFromPeerStoresAsync(ApplicationConfiguration config)
    {
        try
        {
            string? ownThumb = config.SecurityConfiguration.ApplicationCertificate.Certificate?.Thumbprint
                               ?? config.SecurityConfiguration.ApplicationCertificate.Thumbprint;
            string appUri = config.ApplicationUri;

            if (!string.IsNullOrWhiteSpace(ownThumb))
            {
                await RemoveServerCertificateInternalAsync(config, ownThumb, fromTrusted: true).ConfigureAwait(false);
                await RemoveServerCertificateInternalAsync(config, ownThumb, fromTrusted: false).ConfigureAwait(false);
            }

            if (Directory.Exists(TrustedPeersDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(TrustedPeersDirectory, "*.*", SearchOption.AllDirectories).ToList())
                {
                    try
                    {
                        using var c = X509CertificateLoader.LoadCertificateFromFile(file);
                        string? certUri = ExtractApplicationUri(c);
                        if (string.Equals(certUri, appUri, StringComparison.OrdinalIgnoreCase))
                        {
                            await RemoveServerCertificateInternalAsync(config, c.Thumbprint, fromTrusted: true).ConfigureAwait(false);
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Pré-valida o certificado do servidor OPC UA antes de iniciar o handshake de criação da sessão,
    /// permitindo exibir o prompt interativo ao usuário sem estourar o timeout do handshake OPC UA.
    /// </summary>
    public async Task<bool> EnsureServerCertificateTrustedBeforeConnectAsync(
        byte[]? serverCertificateDer,
        CancellationToken ct = default)
    {
        if (serverCertificateDer is not { Length: > 0 })
            return true;

        var config = await GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
        using var cert = X509CertificateLoader.LoadCertificate(serverCertificateDer);
        string thumbprint = cert.Thumbprint;

        var existingTrusted = await FindServerCertificateByThumbprintAsync(config, thumbprint, fromTrusted: true).ConfigureAwait(false);
        if (existingTrusted is not null)
        {
            existingTrusted.Dispose();
            return true;
        }

        if (AutoAcceptUntrustedServerCertificates)
        {
            await TrustServerCertificateAsync(serverCertificateDer, ct).ConfigureAwait(false);
            return true;
        }

        if (UntrustedCertificatePrompt is not null)
        {
            var info = ToInfo(cert, null, CertificateTrustStatus.Unknown);
            bool accepted = UntrustedCertificatePrompt(info);
            if (accepted)
            {
                await TrustServerCertificateAsync(serverCertificateDer, ct).ConfigureAwait(false);
                return true;
            }

            _recentRejections[thumbprint] = DateTime.UtcNow;
            await RejectServerCertificateAsync(serverCertificateDer, ct).ConfigureAwait(false);
            return false;
        }

        await RejectServerCertificateAsync(serverCertificateDer, ct).ConfigureAwait(false);
        return false;
    }

    private void OnCertificateValidation(CertificateValidator sender, CertificateValidationEventArgs e)
    {
        if (e.Error.StatusCode == StatusCodes.Good) return;
        if (e.Certificate is null) return;

        string thumbprint = e.Certificate.Thumbprint;

        // 1. Se o certificado já estiver explicitamente na pasta Trusted, aceita-o
        if (IsCertificateInDirectoryOrStore(_config?.SecurityConfiguration.TrustedPeerCertificates, TrustedPeersDirectory, thumbprint))
        {
            e.Accept = true;
            return;
        }

        // 2. Se auto-accept estiver ativo, aceita e persiste em Trusted
        if (AutoAcceptUntrustedServerCertificates)
        {
            e.Accept = true;
            SaveCertificateToStoreSafe(_config?.SecurityConfiguration.TrustedPeerCertificates, e.Certificate);
            DeleteCertificateFilesByThumbprint(RejectedPeersDirectory, thumbprint);
            return;
        }

        // 3. Evita perguntar duas vezes seguidas para o mesmo certificado se o usuário acabou de rejeitar no handshake
        if (_recentRejections.TryGetValue(thumbprint, out var rejectedAt) &&
            (DateTime.UtcNow - rejectedAt) < TimeSpan.FromSeconds(15))
        {
            e.Accept = false;
            SaveCertificateToStoreSafe(_config?.SecurityConfiguration.RejectedCertificateStore, e.Certificate);
            return;
        }

        // 4. Se houver callback interativo (UI), pergunta ao usuário se deseja confiar ou rejeitar
        if (UntrustedCertificatePrompt is not null)
        {
            var info = ToInfo(e.Certificate, null, CertificateTrustStatus.Unknown);
            bool accepted = UntrustedCertificatePrompt(info);
            e.Accept = accepted;

            if (accepted)
            {
                _recentRejections.TryRemove(thumbprint, out _);
                SaveCertificateToStoreSafe(_config?.SecurityConfiguration.TrustedPeerCertificates, e.Certificate);
                DeleteCertificateFilesByThumbprint(RejectedPeersDirectory, thumbprint);
            }
            else
            {
                _recentRejections[thumbprint] = DateTime.UtcNow;
                SaveCertificateToStoreSafe(_config?.SecurityConfiguration.RejectedCertificateStore, e.Certificate);
            }
            return;
        }

        // 5. Sem auto-accept e sem prompt (ou rejeitado): rejeita e garante persistência na pasta Rejected
        e.Accept = false;
        SaveCertificateToStoreSafe(_config?.SecurityConfiguration.RejectedCertificateStore, e.Certificate);
    }

    private static void SaveCertificateToStoreSafe(CertificateStoreIdentifier? trustList, X509Certificate2 cert)
    {
        if (trustList is null) return;
        byte[] raw = cert.Export(X509ContentType.Cert);
        try
        {
            Task.Run(async () =>
            {
                using var publicOnly = X509CertificateLoader.LoadCertificate(raw);
                using var store = trustList.OpenStore();
                var existing = await store.FindByThumbprintAsync(publicOnly.Thumbprint).ConfigureAwait(false);
                if (existing.Count == 0)
                {
                    await store.AddAsync(publicOnly).ConfigureAwait(false);
                }
            }).GetAwaiter().GetResult();
        }
        catch { }
    }

    private static bool IsCertificateInDirectoryOrStore(
        CertificateStoreIdentifier? trustList,
        string directoryPath,
        string thumbprint)
    {
        using var fileMatch = FindCertificateInDirectoryByThumbprint(directoryPath, thumbprint);
        if (fileMatch is not null) return true;

        if (trustList is not null)
        {
            try
            {
                return Task.Run(async () =>
                {
                    using var store = trustList.OpenStore();
                    var matches = await store.FindByThumbprintAsync(thumbprint).ConfigureAwait(false);
                    return matches.Count > 0;
                }).GetAwaiter().GetResult();
            }
            catch { }
        }

        return false;
    }

    private async Task<X509Certificate2?> FindServerCertificateByThumbprintAsync(
        ApplicationConfiguration config,
        string thumbprint,
        bool fromTrusted)
    {
        var trustList = fromTrusted
            ? config.SecurityConfiguration.TrustedPeerCertificates
            : config.SecurityConfiguration.RejectedCertificateStore;
        string folder = fromTrusted ? TrustedPeersDirectory : RejectedPeersDirectory;

        try
        {
            using var store = trustList.OpenStore();
            var matches = await store.FindByThumbprintAsync(thumbprint).ConfigureAwait(false);
            if (matches.Count > 0)
                return matches[0];
        }
        catch { }

        return FindCertificateInDirectoryByThumbprint(folder, thumbprint);
    }

    private async Task RemoveServerCertificateInternalAsync(
        ApplicationConfiguration config,
        string thumbprint,
        bool fromTrusted)
    {
        var trustList = fromTrusted
            ? config.SecurityConfiguration.TrustedPeerCertificates
            : config.SecurityConfiguration.RejectedCertificateStore;
        string folder = fromTrusted ? TrustedPeersDirectory : RejectedPeersDirectory;

        try
        {
            using var store = trustList.OpenStore();
            await store.DeleteAsync(thumbprint).ConfigureAwait(false);
        }
        catch { }

        DeleteCertificateFilesByThumbprint(folder, thumbprint);
    }

    private static async Task<IReadOnlyList<CertificateInfo>> EnumerateStoreAndFolderAsync(
        CertificateStoreIdentifier storeIdentifier,
        string directoryPath,
        CertificateTrustStatus status,
        string? excludeThumbprint,
        string? excludeApplicationUri)
    {
        var byThumbprint = new Dictionary<string, CertificateInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var store = storeIdentifier.OpenStore();
            var certs = await store.EnumerateAsync().ConfigureAwait(false);
            foreach (var c in certs)
            {
                if (ShouldExcludeOwnCert(c, excludeThumbprint, excludeApplicationUri))
                    continue;

                string? filePath = FindFirstCertificateFileInDirectory(directoryPath, c.Thumbprint) ?? directoryPath;
                byThumbprint[c.Thumbprint] = ToInfo(c, filePath, status);
            }
        }
        catch { }

        if (Directory.Exists(directoryPath))
        {
            foreach (var file in Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".der", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".cer", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".crt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var c = X509CertificateLoader.LoadCertificateFromFile(file);
                    if (ShouldExcludeOwnCert(c, excludeThumbprint, excludeApplicationUri))
                        continue;

                    if (!byThumbprint.ContainsKey(c.Thumbprint))
                        byThumbprint[c.Thumbprint] = ToInfo(c, file, status);
                }
                catch { }
            }
        }

        return byThumbprint.Values
            .OrderBy(c => c.Subject, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ShouldExcludeOwnCert(
        X509Certificate2 cert,
        string? excludeThumbprint,
        string? excludeApplicationUri)
    {
        if (!string.IsNullOrWhiteSpace(excludeThumbprint) &&
            string.Equals(cert.Thumbprint, excludeThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(excludeApplicationUri))
        {
            string? uri = ExtractApplicationUri(cert);
            if (string.Equals(uri, excludeApplicationUri, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindFirstCertificateFileInDirectory(string directoryPath, string thumbprint)
    {
        if (!Directory.Exists(directoryPath))
            return null;

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file);
            if (!ext.Equals(".der", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".cer", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".crt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var c = X509CertificateLoader.LoadCertificateFromFile(file);
                if (string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            catch { }
        }

        return null;
    }

    private static X509Certificate2? FindCertificateInDirectoryByThumbprint(string directoryPath, string thumbprint)
    {
        if (!Directory.Exists(directoryPath))
            return null;

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories))
        {
            try
            {
                var c = X509CertificateLoader.LoadCertificateFromFile(file);
                if (string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                    return c;
                c.Dispose();
            }
            catch { }
        }

        return null;
    }

    private static void DeleteCertificateFilesByThumbprint(string directoryPath, string thumbprint)
    {
        if (!Directory.Exists(directoryPath))
            return;

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories).ToList())
        {
            try
            {
                using var c = X509CertificateLoader.LoadCertificateFromFile(file);
                if (string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
            catch { }
        }
    }

    private static void ClearDirectoryStoreFiles(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return;

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories).ToList())
        {
            try
            {
                File.Delete(file);
            }
            catch { }
        }
    }

    private static string SanitizeDistinguishedNamePart(string value) =>
        value.Replace(",", " ", StringComparison.Ordinal)
             .Replace(";", " ", StringComparison.Ordinal)
             .Replace("=", "-", StringComparison.Ordinal)
             .Trim();

    private static string? ExtractApplicationUri(X509Certificate2 cert)
    {
        try
        {
            var uris = X509Utils.GetApplicationUrisFromCertificate(cert);
            if (uris is { Count: > 0 } && !string.IsNullOrWhiteSpace(uris[0]))
                return uris[0];
        }
        catch { }

        return null;
    }

    private static CertificateInfo ToInfo(
        X509Certificate2 cert,
        string? path = null,
        CertificateTrustStatus status = CertificateTrustStatus.Unknown)
    {
        int keySize = 0;
        try
        {
            using var rsa = cert.GetRSAPublicKey();
            if (rsa is not null)
            {
                keySize = rsa.KeySize;
            }
            else
            {
                using var ecdsa = cert.GetECDsaPublicKey();
                if (ecdsa is not null)
                    keySize = ecdsa.KeySize;
            }
        }
        catch { }

        string sigAlg = cert.SignatureAlgorithm.FriendlyName
                        ?? cert.SignatureAlgorithm.Value
                        ?? "";
        bool isSelfSigned = string.Equals(cert.Subject, cert.Issuer, StringComparison.OrdinalIgnoreCase);
        string? appUri = ExtractApplicationUri(cert);

        return new CertificateInfo(
            cert.Subject,
            cert.Issuer,
            cert.Thumbprint,
            cert.NotBefore.ToUniversalTime(),
            cert.NotAfter.ToUniversalTime(),
            path,
            appUri,
            keySize,
            sigAlg,
            isSelfSigned,
            status);
    }
}
