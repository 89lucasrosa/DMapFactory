using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DMapFactory.Core.Abstractions;
using DMapFactory.Core.Models;
using DMapFactory.Drivers.OpcUa;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace DMapFactory.IntegrationTests;

public sealed class OpcUaDriverIntegrationTests : IAsyncLifetime
{
    private ApplicationInstance? _serverApp;
    private StandardServer? _server;
    private string _endpointUrl = "";
    private string _tempPki = "";

    public async Task InitializeAsync()
    {
        int port = GetFreeTcpPort();
        _endpointUrl = $"opc.tcp://localhost:{port}/DMapFactory/TestServer";
        _tempPki = Path.Combine(Path.GetTempPath(), "DMapFactory.Tests.Pki", Guid.NewGuid().ToString("N"));

        var config = new ApplicationConfiguration
        {
            ApplicationName = "DMapFactory TestServer",
            ApplicationUri = $"urn:{Utils.GetHostName()}:DMapFactory:TestServer",
            ProductUri = "urn:dmapfactory.org:TestServer",
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(_tempPki, "server", "own"),
                    SubjectName = "CN=DMapFactory TestServer, O=DMapFactory"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(_tempPki, "server", "issuers")
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(_tempPki, "server", "trusted")
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(_tempPki, "server", "rejected")
                },
                AutoAcceptUntrustedCertificates = true,
                AddAppCertToTrustedStore = true
            },
            TransportConfigurations = [],
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { _endpointUrl },
                SecurityPolicies =
                {
                    new ServerSecurityPolicy
                    {
                        SecurityMode = MessageSecurityMode.None,
                        SecurityPolicyUri = SecurityPolicies.None
                    },
                    new ServerSecurityPolicy
                    {
                        SecurityMode = MessageSecurityMode.SignAndEncrypt,
                        SecurityPolicyUri = SecurityPolicies.Basic256Sha256
                    }
                },
                UserTokenPolicies =
                {
                    new UserTokenPolicy(UserTokenType.Anonymous),
                    new UserTokenPolicy(UserTokenType.UserName)
                }
            }
        };

        await config.ValidateAsync(ApplicationType.Server);
        config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = true;

#pragma warning disable CS0618
        _serverApp = new ApplicationInstance(config);
        await _serverApp.CheckApplicationInstanceCertificatesAsync(true, 2048);
#pragma warning restore CS0618

        _server = new DMapFactory.TestServer.SimulationServer(50, 50);
        await _serverApp.StartAsync(_server);
    }

    public async Task DisposeAsync()
    {
        if (_serverApp is not null)
            await _serverApp.StopAsync();
        _server?.Dispose();

        try
        {
            if (Directory.Exists(_tempPki))
                Directory.Delete(_tempPki, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void OpcUaDriver_DoesNotUseRecursiveNodeCacheOrBrowser()
    {
        var driverAsm = typeof(OpcUaDriver).Assembly;
        var forbiddenCalls = new List<string>();

        foreach (var t in driverAsm.GetTypes())
        {
            var allTypes = new[] { t }.Concat(t.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
            foreach (var sub in allTypes)
            {
                foreach (var m in sub.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    var body = m.GetMethodBody();
                    if (body is null) continue;
                    byte[] il = body.GetILAsByteArray()!;
                    for (int i = 0; i < il.Length - 4; i++)
                    {
                        if (il[i] is 0x28 or 0x6F or 0x73)
                        {
                            try
                            {
                                var target = driverAsm.ManifestModule.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                                string decl = target?.DeclaringType?.FullName ?? "";
                                if (decl.Contains("NodeCache") || decl.Contains("TypeTable") || decl.Contains("Opc.Ua.Client.Browser") || target?.Name == "get_TypeTree" || target?.Name == "get_IsAbsolute")
                                {
                                    forbiddenCalls.Add($"{sub.Name}.{m.Name} -> {decl}.{target?.Name}");
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
        }

        Assert.Empty(forbiddenCalls);
    }

    [Fact]
    public async Task DiscoverEndpoints_ReturnsNoneAndBasic256Sha256()
    {
        var endpoints = await OpcUaSecurityManager.DiscoverEndpointsAsync(_endpointUrl);

        Assert.NotEmpty(endpoints);
        Assert.Contains(endpoints, e => e.SecurityMode == MessageSecurity.None);
        Assert.Contains(endpoints, e => e.SecurityMode == MessageSecurity.SignAndEncrypt &&
                                        e.SecurityPolicyShortName == "Basic256Sha256");
    }

    [Theory]
    [InlineData("None", MessageSecurity.None)]
    [InlineData("Basic256Sha256", MessageSecurity.SignAndEncrypt)]
    public async Task Connect_Browse_Read_And_Subscribe_WorksInBothSecurityModes(
        string policyShortName, MessageSecurity mode)
    {
        var secMgr = new OpcUaSecurityManager(Path.Combine(_tempPki, "client"))
        {
            AutoAcceptUntrustedServerCertificates = true
        };
        var conn = new ConnectionConfig
        {
            Id = 1,
            Name = "SimPLC",
            EndpointUrl = _endpointUrl,
            SecurityPolicyUri = policyShortName,
            SecurityMode = mode,
            Authentication = UserAuthentication.Anonymous
        };

        await using var driver = new OpcUaDriver(conn, secMgr);
        await driver.ConnectAsync();

        Assert.Equal(ConnectionState.Connected, driver.State);

        // 1. Browse Objects -> Machine1 (pastas de usuário vêm antes de Server)
        var rootNodes = await driver.BrowseAsync(null);
        Assert.Equal("Machine1", rootNodes[0].DisplayName);
        var machine1 = Assert.Single(rootNodes, n => n.DisplayName == "Machine1");

        var children = await driver.BrowseAsync(machine1.Id);
        Assert.Contains(children, c => c.DisplayName == "Cycle_Active" && c.IsVariable && c.DataType == "Boolean");
        var tag001 = Assert.Single(children, c => c.DisplayName == "Tag_001");
        Assert.Equal("Double", tag001.DataType);

        // 2. ReadNodeAttributes on Object folder (Server) and Variable
        var serverNode = Assert.Single(rootNodes, n => n.DisplayName == "Server");
        var serverAttrs = await driver.ReadNodeAttributesAsync(serverNode.Id);
        Assert.NotNull(serverAttrs);

        // Browse Server node (has many complex OPC UA variables and sub-objects like KEPServerEX)
        var serverChildren = await driver.BrowseAsync(serverNode.Id);
        Assert.NotEmpty(serverChildren);

        // Browse Server -> ServerStatus (has complex Structure DataType ServerStatusDataType i=862)
        var statusNode = Assert.Single(serverChildren, c => c.DisplayName == "ServerStatus");
        var statusAttrs = await driver.ReadNodeAttributesAsync(statusNode.Id);
        Assert.NotNull(statusAttrs);
        var statusChildren = await driver.BrowseAsync(statusNode.Id);
        Assert.NotEmpty(statusChildren);

        var attrs = await driver.ReadNodeAttributesAsync(tag001.Id);
        Assert.Equal("Tag_001", attrs.DisplayName);
        Assert.Equal("Double", attrs.DataTypeName);
        Assert.Equal(QualityClass.Good, attrs.Quality);

        // 3. Subscription (recebe amostras em tempo real)
        var sink = new TestSampleSink();
        var group = new AcquisitionGroup
        {
            Name = "Fast",
            ConnectionId = 1,
            Mode = AcquisitionMode.Subscription,
            SamplingIntervalMs = 50,
            PublishingIntervalMs = 50,
            Tags =
            [
                new TagConfig { Id = 101, ConnectionId = 1, Address = tag001.Id, Alias = "Tag_001" }
            ]
        };

        await driver.StartGroupAsync(group, sink);
        await sink.WaitForSamplesAsync(minCount: 3, timeout: TimeSpan.FromSeconds(5));
        await driver.StopGroupAsync(group.Id);

        Assert.True(sink.Samples.Count >= 3);
        Assert.All(sink.Samples, s =>
        {
            Assert.Equal(101, s.TagId);
            Assert.Equal(QualityClass.Good, s.Quality);
            Assert.Equal(TimestampOrigin.Source, s.Origin);
        });
    }

    [Fact]
    public async Task SessionManager_RecordsOnlineSessionToCsvAndJson_WithZeroDroppedSamples()
    {
        var secMgr = new OpcUaSecurityManager(Path.Combine(_tempPki, "client-session"));
        var conn = new ConnectionConfig
        {
            Id = 1,
            Name = "SimPLC",
            EndpointUrl = _endpointUrl,
            SecurityPolicyUri = "None",
            SecurityMode = MessageSecurity.None
        };

        await using var driver = new OpcUaDriver(conn, secMgr);
        await driver.ConnectAsync();

        var project = new ProjectConfig
        {
            Name = "TesteSessao",
            Connections = [conn],
            Groups =
            [
                new AcquisitionGroup
                {
                    Name = "Fast50ms",
                    ConnectionId = 1,
                    Mode = AcquisitionMode.Subscription,
                    SamplingIntervalMs = 50,
                    PublishingIntervalMs = 50,
                    Tags =
                    [
                        new TagConfig { Id = 1, ConnectionId = 1, Address = "ns=2;s=Cycle_Active", Alias = "Cycle_Active", DataType = "Boolean" },
                        new TagConfig { Id = 2, ConnectionId = 1, Address = "ns=2;s=Tag_001", Alias = "Tag_001", DataType = "Double" },
                        new TagConfig { Id = 3, ConnectionId = 1, Address = "ns=2;s=Tag_002", Alias = "Tag_002", DataType = "Int32" },
                    ]
                }
            ]
        };

        string sessionsDir = Path.Combine(_tempPki, "sessions");
        await using var sm = new DMapFactory.Acquisition.SessionManager();

        await sm.StartSessionAsync(project, [driver], sessionsDir);
        await Task.Delay(750);
        await sm.AddMarkerAsync("Ponto de verificacao");
        string? csvFile = sm.CurrentCsvPath;
        await sm.StopSessionAsync();

        Assert.NotNull(csvFile);
        Assert.True(File.Exists(csvFile));

        var loaded = await DMapFactory.Storage.CsvSessionReader.LoadAsync(csvFile);
        Assert.Equal("TesteSessao", loaded.Metadata.Project);
        Assert.Equal(0, loaded.Metadata.DroppedSamples);
        Assert.Single(loaded.Metadata.Markers);
        Assert.True(loaded.Samples.Count >= 6);
        Assert.NotEmpty(sm.Snapshot.GetAllRows());
    }

    [Fact]
    public async Task PQ_MvpAcceptance_50Tags_ZeroDroppedSamples_And_ExportsXlsx()
    {
        var secMgr = new OpcUaSecurityManager(Path.Combine(_tempPki, "client-pq50"))
        {
            AutoAcceptUntrustedServerCertificates = true
        };
        var conn = new ConnectionConfig
        {
            Id = 1,
            Name = "SimPLC_50Tags",
            EndpointUrl = _endpointUrl,
            SecurityPolicyUri = "Basic256Sha256",
            SecurityMode = MessageSecurity.SignAndEncrypt
        };

        await using var driver = new OpcUaDriver(conn, secMgr);
        await driver.ConnectAsync();

        var tags = new List<TagConfig>(51)
        {
            new() { Id = 1, ConnectionId = 1, Address = "ns=2;s=Cycle_Active", Alias = "Cycle_Active", DataType = "Boolean" }
        };

        for (int i = 1; i <= 50; i++)
        {
            tags.Add(new TagConfig
            {
                Id = i + 1,
                ConnectionId = 1,
                Address = $"ns=2;s=Tag_{i:D3}",
                Alias = $"Tag_{i:D3}"
            });
        }

        var project = new ProjectConfig
        {
            Name = "PQ_50Tags_100ms",
            Connections = [conn],
            Groups =
            [
                new AcquisitionGroup
                {
                    Name = "PQ_Group",
                    ConnectionId = 1,
                    Mode = AcquisitionMode.Subscription,
                    SamplingIntervalMs = 100,
                    PublishingIntervalMs = 100,
                    QueueSize = 10,
                    Tags = tags
                }
            ]
        };

        string sessionsDir = Path.Combine(_tempPki, "sessions-pq50");
        await using var sm = new DMapFactory.Acquisition.SessionManager();

        await sm.StartSessionAsync(project, [driver], sessionsDir);
        await Task.Delay(1200);
        var liveStats = sm.GetStats();
        string? csvFile = sm.CurrentCsvPath;
        await sm.StopSessionAsync();

        Assert.NotNull(csvFile);
        Assert.Equal(0, liveStats.DroppedSamples);
        Assert.False(liveStats.IsBufferAboveWarningThreshold);

        var loaded = await DMapFactory.Storage.CsvSessionReader.LoadAsync(csvFile);
        Assert.Equal(0, loaded.Metadata.DroppedSamples);
        Assert.Equal(51, loaded.Metadata.Tags.Count);
        Assert.True(loaded.Samples.Count >= 300, $"Expected >= 300 samples, got {loaded.Samples.Count}");

        var wideRows = sm.Snapshot.GetAllRows();
        Assert.NotEmpty(wideRows);
        Assert.Equal(51, wideRows[^1].ValuesByTagId.Count);

        string xlsxPath = Path.Combine(sessionsDir, "pq_50tags.xlsx");
        DMapFactory.Storage.SpreadsheetExporter.ExportXlsx(xlsxPath, tags, wideRows);
        Assert.True(File.Exists(xlsxPath));
    }

    [Fact]
    public async Task SecurityManager_GeneratesClientCertificate_WithCustomValidity_AndDoesNotPolluteServerStore()
    {
        var secMgr = new OpcUaSecurityManager(Path.Combine(_tempPki, "client-certgen"));
        Assert.False(secMgr.AutoAcceptUntrustedServerCertificates);

        // 1. Certificado inicial deve ter validade padrão de ~2 anos e chave de 2048 bits
        var initialCert = await secMgr.GetApplicationCertificateInfoAsync();
        Assert.NotNull(initialCert);
        Assert.Equal(2048, initialCert.KeySize);
        int initialYears = (int)Math.Round((initialCert.NotAfterUtc - initialCert.NotBeforeUtc).TotalDays / 365.25);
        Assert.Equal(2, initialYears);

        // O certificado do próprio cliente não pode aparecer na lista de servidores confiáveis
        var trustedServers = await secMgr.GetTrustedServerCertificatesAsync();
        Assert.Empty(trustedServers);

        // 2. Gera um novo certificado de cliente com validade customizada (5 anos)
        var customStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var customEnd = new DateTime(2031, 6, 1, 23, 59, 59, DateTimeKind.Utc);
        var regenerated = await secMgr.GenerateApplicationCertificateAsync(new ClientCertificateGenerationOptions(
            CommonName: "Cliente Customizado Industrial",
            Organization: "Planta Automacao",
            KeySize: 2048,
            NotBeforeUtc: customStart,
            NotAfterUtc: customEnd));

        Assert.NotEqual(initialCert.Thumbprint, regenerated.Thumbprint);
        Assert.Contains("CN=Cliente Customizado Industrial", regenerated.Subject);
        Assert.Contains("O=Planta Automacao", regenerated.Subject);
        Assert.Equal(2026, regenerated.NotBeforeUtc.Year);
        Assert.Equal(2031, regenerated.NotAfterUtc.Year);

        // Verifica se GetApplicationCertificateInfoAsync retorna exatamente o novo certificado
        var currentCert = await secMgr.GetApplicationCertificateInfoAsync();
        Assert.NotNull(currentCert);
        Assert.Equal(regenerated.Thumbprint, currentCert.Thumbprint);
        Assert.Empty(await secMgr.GetTrustedServerCertificatesAsync());
    }

    [Fact]
    public async Task SecurityManager_DiscoversServerCertificates_AndSupportsTrustAndRejectLifecycle()
    {
        var secMgr = new OpcUaSecurityManager(Path.Combine(_tempPki, "client-discovery"));

        // 1. Descobre o certificado do servidor via GetEndpoints
        var discovered = await secMgr.DiscoverServerCertificatesAsync(_endpointUrl);
        var serverCert = Assert.Single(discovered);
        Assert.Equal(CertificateTrustStatus.Unknown, serverCert.Certificate.TrustStatus);
        Assert.NotEmpty(serverCert.RawData);
        Assert.Contains("Basic256Sha256", serverCert.EndpointsDisplay);

        // 2. Rejeita explicitamente o certificado descoberto
        var rejectedInfo = await secMgr.RejectServerCertificateAsync(serverCert.RawData);
        Assert.Equal(CertificateTrustStatus.Rejected, rejectedInfo.TrustStatus);
        Assert.Single(await secMgr.GetRejectedServerCertificatesAsync());
        Assert.Empty(await secMgr.GetTrustedServerCertificatesAsync());

        // 3. Move de Rejeitado -> Confiável (Trust)
        bool trustedOk = await secMgr.TrustRejectedCertificateAsync(serverCert.Certificate.Thumbprint);
        Assert.True(trustedOk);
        Assert.Single(await secMgr.GetTrustedServerCertificatesAsync());
        Assert.Empty(await secMgr.GetRejectedServerCertificatesAsync());

        // 4. Com o certificado em Trusted e AutoAccept = false, a conexão segura SignAndEncrypt deve funcionar!
        var conn = new ConnectionConfig
        {
            Id = 1,
            Name = "TrustedServerConn",
            EndpointUrl = _endpointUrl,
            SecurityPolicyUri = "Basic256Sha256",
            SecurityMode = MessageSecurity.SignAndEncrypt
        };
        await using (var driver = new OpcUaDriver(conn, secMgr))
        {
            await driver.ConnectAsync();
            Assert.Equal(ConnectionState.Connected, driver.State);
        }

        // 5. Move de Confiável -> Rejeitado (Reject)
        bool rejectedOk = await secMgr.RejectTrustedCertificateAsync(serverCert.Certificate.Thumbprint);
        Assert.True(rejectedOk);
        Assert.Empty(await secMgr.GetTrustedServerCertificatesAsync());
        Assert.Single(await secMgr.GetRejectedServerCertificatesAsync());
    }

    [Fact]
    public async Task SecurityManager_WhenAutoAcceptDisabled_SavesUntrustedToRejected_AndSupportsPromptApproval()
    {
        var secMgr = new OpcUaSecurityManager(Path.Combine(_tempPki, "client-prompt"));
        var conn = new ConnectionConfig
        {
            Id = 1,
            Name = "UntrustedServerConn",
            EndpointUrl = _endpointUrl,
            SecurityPolicyUri = "Basic256Sha256",
            SecurityMode = MessageSecurity.SignAndEncrypt
        };

        // 1. Sem prompt e com AutoAccept = false: deve recusar a conexão e gravar na pasta Rejected
        await using (var driver = new OpcUaDriver(conn, secMgr))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => driver.ConnectAsync());
        }

        var rejectedList = await secMgr.GetRejectedServerCertificatesAsync();
        Assert.Single(rejectedList);
        Assert.Empty(await secMgr.GetTrustedServerCertificatesAsync());

        // 2. Quando o usuário aprova no prompt interativo: aceita a conexão e move para Trusted
        CertificateInfo? promptedCert = null;
        secMgr.UntrustedCertificatePrompt = info =>
        {
            promptedCert = info;
            return true;
        };

        await using (var driver2 = new OpcUaDriver(conn, secMgr))
        {
            await driver2.ConnectAsync();
            Assert.Equal(ConnectionState.Connected, driver2.State);
        }

        Assert.NotNull(promptedCert);
        Assert.Single(await secMgr.GetTrustedServerCertificatesAsync());
        Assert.Empty(await secMgr.GetRejectedServerCertificatesAsync());
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TestSampleSink : ISampleSink
    {
        public ConcurrentQueue<Sample> Samples { get; } = new();

        public bool TryPublish(in Sample sample)
        {
            Samples.Enqueue(sample);
            return true;
        }

        public async Task WaitForSamplesAsync(int minCount, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Samples.Count < minCount && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
        }
    }
}
