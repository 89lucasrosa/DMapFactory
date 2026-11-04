using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace DMapFactory.TestServer;

/// <summary>
/// Servidor OPC UA de simulação para testes de integração e validação de aceite (50 tags a 100 ms).
/// Uso: DMapFactory.TestServer [--port 48400] [--tags 50] [--interval 100] [--duration-sec 0]
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        int port = GetArgInt(args, "--port", 48400);
        int tagCount = GetArgInt(args, "--tags", 50);
        int intervalMs = GetArgInt(args, "--interval", 100);
        int durationSec = GetArgInt(args, "--duration-sec", 0);

        string endpointUrl = $"opc.tcp://localhost:{port}/DMapFactory/TestServer";
        string pkiRoot = Path.Combine(AppContext.BaseDirectory, "pki");

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
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = "CN=DMapFactory TestServer, O=DMapFactory"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "issuers")
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "trusted")
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "rejected")
                },
                AutoAcceptUntrustedCertificates = true,
                AddAppCertToTrustedStore = true
            },
            TransportConfigurations = [],
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { endpointUrl },
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxQueuedRequestCount = 200,
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
        var app = new ApplicationInstance(config);
        bool hasCert = await app.CheckApplicationInstanceCertificatesAsync(true, 2048);
#pragma warning restore CS0618
        if (!hasCert)
        {
            Console.Error.WriteLine("Falha ao criar/validar certificado do servidor.");
            return 1;
        }

        using var server = new SimulationServer(tagCount, intervalMs);
        await app.StartAsync(server);

        Console.WriteLine($"[TestServer] Rodando em {endpointUrl} ({tagCount} tags @ {intervalMs} ms)");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (durationSec > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(durationSec));

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { }

        await app.StopAsync();
        return 0;
    }

    private static int GetArgInt(string[] args, string name, int defaultValue)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out int v))
            {
                return v;
            }
        }
        return defaultValue;
    }
}

public sealed class SimulationServer(int tagCount, int intervalMs) : StandardServer
{
    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        var nodeManagers = new List<INodeManager>
        {
            new SimulationNodeManager(server, configuration, tagCount, intervalMs)
        };
        return new MasterNodeManager(server, configuration, null, nodeManagers.ToArray());
    }

    protected override ServerProperties LoadServerProperties() => new()
    {
        ManufacturerName = "DMapFactory",
        ProductName = "DMapFactory Simulation Server",
        ProductUri = "urn:dmapfactory.org:TestServer",
        SoftwareVersion = "0.1.0",
        BuildNumber = "1",
        BuildDate = DateTime.UtcNow
    };
}

internal sealed class SimulationNodeManager : CustomNodeManager2
{
    public const string Namespace = "urn:dmapfactory.org:Simulation";
    private readonly int _tagCount;
    private readonly int _intervalMs;
    private readonly List<BaseDataVariableState> _variables = [];
    private Timer? _timer;
    private long _tick;

    public SimulationNodeManager(IServerInternal server, ApplicationConfiguration configuration, int tagCount, int intervalMs)
        : base(server, configuration, Namespace)
    {
        SystemContext.NodeIdFactory = this;
        _tagCount = tagCount;
        _intervalMs = Math.Max(10, intervalMs);
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            base.CreateAddressSpace(externalReferences);

            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference>? references))
            {
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            }

            var root = CreateFolder(null, "Machine1", "Machine1");
            root.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, root.NodeId));

            // Sinais de ciclo de máquina e tags numeradas (Tag_001 .. Tag_NNN)
            var cycleActive = CreateVariable(root, "Cycle_Active", DataTypeIds.Boolean, false);
            var cycleCount = CreateVariable(root, "Cycle_Count", DataTypeIds.Int32, 0);
            _variables.Add(cycleActive);
            _variables.Add(cycleCount);

            for (int i = 1; i <= _tagCount; i++)
            {
                string name = $"Tag_{i:D3}";
                var (dataTypeId, initial) = (i % 3) switch
                {
                    1 => (DataTypeIds.Double, (object)0.0),
                    2 => (DataTypeIds.Int32, (object)0),
                    _ => (DataTypeIds.Boolean, (object)false)
                };
                _variables.Add(CreateVariable(root, name, dataTypeId, initial));
            }

            AddPredefinedNode(SystemContext, root);
            _timer = new Timer(OnTick, null, _intervalMs, _intervalMs);
        }
    }

    private void OnTick(object? state)
    {
        try
        {
            lock (Lock)
            {
                long t = ++_tick;
                DateTime now = DateTime.UtcNow;

                for (int i = 0; i < _variables.Count; i++)
                {
                    var v = _variables[i];
                    v.Value = v.SymbolicName switch
                    {
                        "Cycle_Active" => (t % 20) < 15,
                        "Cycle_Count" => (int)(t / 20),
                        _ when v.DataType == DataTypeIds.Double => Math.Round(Math.Sin((t + i) * 0.1) * 100.0, 3),
                        _ when v.DataType == DataTypeIds.Int32 => (int)(t + i),
                        _ => ((t + i) & 1) == 1
                    };
                    v.Timestamp = now;
                    v.StatusCode = StatusCodes.Good;
                    v.ClearChangeMasks(SystemContext, false);
                }
            }
        }
        catch
        {
            // Ignora exceções durante desligamento
        }
    }

    private FolderState CreateFolder(NodeState? parent, string path, string name)
    {
        var folder = new FolderState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            NodeId = new NodeId(path, NamespaceIndex),
            BrowseName = new QualifiedName(path, NamespaceIndex),
            DisplayName = new LocalizedText("en", name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            EventNotifier = EventNotifiers.None
        };
        parent?.AddChild(folder);
        return folder;
    }

    private BaseDataVariableState CreateVariable(NodeState parent, string name, NodeId dataTypeId, object initialValue)
    {
        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId(name, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText("en", name),
            WriteMask = AttributeWriteMask.DisplayName | AttributeWriteMask.Description,
            UserWriteMask = AttributeWriteMask.DisplayName | AttributeWriteMask.Description,
            DataType = dataTypeId,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentReadOrWrite,
            UserAccessLevel = AccessLevels.CurrentReadOrWrite,
            Historizing = false,
            Value = initialValue,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow
        };
        parent.AddChild(variable);
        return variable;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer?.Dispose();
        base.Dispose(disposing);
    }
}
