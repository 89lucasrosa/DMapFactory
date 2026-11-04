using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DMapFactory.Core.Abstractions;
using DMapFactory.Core.Models;
using DMapFactory.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using CoreDeadbandType = DMapFactory.Core.Models.DeadbandType;

namespace DMapFactory.Drivers.OpcUa;

/// <summary>
/// Atributos detalhados de um nó OPC UA (RF-TAG-02).
/// </summary>
public sealed record OpcUaNodeAttributes(
    string NodeId,
    string BrowseName,
    string DisplayName,
    string DataTypeName,
    int ValueRank,
    byte AccessLevel,
    string? Description,
    object? CurrentValue,
    QualityClass Quality,
    DateTime? SourceTimestampUtc);

/// <summary>
/// Implementação de <see cref="IDriver"/> para OPC UA usando o SDK oficial da OPC Foundation (MIT).
/// Cobre RF-CON-01..09, RF-TAG-01..04 e RF-ACQ-01..05.
/// </summary>
public sealed class OpcUaDriver : IDriver
{
    private readonly OpcUaSecurityManager _securityManager;
    private readonly ILogger<OpcUaDriver> _logger;
    private readonly ConcurrentDictionary<Guid, RunningGroup> _groups = new();

    private ISession? _session;
    private SessionReconnectHandler? _reconnectHandler;
    private ConnectionState _state = ConnectionState.Disconnected;

    public OpcUaDriver(
        ConnectionConfig connection,
        OpcUaSecurityManager securityManager,
        ILogger<OpcUaDriver>? logger = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
        _logger = logger ?? NullLogger<OpcUaDriver>.Instance;

        if (!connection.Enabled)
            _state = ConnectionState.Disabled;
    }

    public string Protocol => "OPCUA";
    public ConnectionConfig Connection { get; }
    public ConnectionState State => _state;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (!Connection.Enabled)
        {
            SetState(ConnectionState.Disabled, "Conexão desabilitada");
            return;
        }

        if (_session is { Connected: true }) return;

        SetState(ConnectionState.Connecting, null);

        try
        {
            var appConfig = await _securityManager.GetOrCreateConfigurationAsync(ct).ConfigureAwait(false);
            var endpointDescription = await SelectEndpointAsync(Connection, ct).ConfigureAwait(false);

            if (endpointDescription.ServerCertificate is { Length: > 0 } &&
                (endpointDescription.SecurityMode != MessageSecurityMode.None ||
                 Connection.Authentication != UserAuthentication.Anonymous))
            {
                bool trusted = await _securityManager
                    .EnsureServerCertificateTrustedBeforeConnectAsync(endpointDescription.ServerCertificate, ct)
                    .ConfigureAwait(false);
                if (!trusted)
                {
                    throw new ServiceResultException(
                        StatusCodes.BadCertificateUntrusted,
                        "Certificado do servidor OPC UA rejeitado ou não confiável.");
                }
            }

            var endpointConfig = EndpointConfiguration.Create(appConfig);
            endpointConfig.OperationTimeout = Connection.OperationTimeoutMs;
            var configuredEndpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfig);

            var userIdentity = BuildUserIdentity(Connection);

#pragma warning disable CS0618
            var session = await DefaultSessionFactory.Instance.CreateAsync(
                appConfig,
                configuredEndpoint,
                updateBeforeConnect: false,
                checkDomain: false,
                sessionName: $"DMapFactory-{Connection.Name}",
                sessionTimeout: (uint)Math.Max(10_000, Connection.SessionTimeoutMs),
                identity: userIdentity,
                preferredLocales: null,
                ct: ct).ConfigureAwait(false);
#pragma warning restore CS0618

            session.KeepAliveInterval = Math.Max(1_000, Connection.KeepAliveIntervalMs);
            session.KeepAlive += OnSessionKeepAlive;

            try
            {
                await session.FetchNamespaceTablesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Aviso ao atualizar tabela de namespaces em {Name}", Connection.Name);
            }

            _session = session;
            SetState(ConnectionState.Connected, "OK");
            _logger.LogInformation("Conectado a {Name} ({Endpoint}) [{Policy}/{Mode}] — Namespaces: {Namespaces}",
                Connection.Name, Connection.EndpointUrl,
                OpcUaSecurityManager.GetPolicyShortName(endpointDescription.SecurityPolicyUri),
                endpointDescription.SecurityMode,
                session.NamespaceUris.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao conectar a {Name} ({Endpoint})", Connection.Name, Connection.EndpointUrl);
            SetState(ConnectionState.Faulted, ex.Message);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        foreach (var groupId in _groups.Keys.ToArray())
        {
            await StopGroupAsync(groupId, ct).ConfigureAwait(false);
        }

        _reconnectHandler?.Dispose();
        _reconnectHandler = null;

        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            try
            {
                session.KeepAlive -= OnSessionKeepAlive;
                await session.CloseAsync(5_000, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Aviso ao fechar sessão OPC UA {Name}", Connection.Name);
            }
            finally
            {
                session.Dispose();
            }
        }

        SetState(Connection.Enabled ? ConnectionState.Disconnected : ConnectionState.Disabled, null);
    }

    public async Task<IReadOnlyList<BrowseNode>> BrowseAsync(string? parentId, CancellationToken ct = default)
    {
        var session = RequireSession();
        NodeId nodeToBrowse = string.IsNullOrWhiteSpace(parentId)
            ? ObjectIds.ObjectsFolder
            : NodeId.Parse(parentId);

        var browseDesc = new BrowseDescription
        {
            NodeId = nodeToBrowse,
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.References,
            IncludeSubtypes = true,
            NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable | NodeClass.View),
            ResultMask = (uint)BrowseResultMask.All
        };

        var refs = new ReferenceDescriptionCollection();
        var browseResponse = await session.BrowseAsync(
            null,
            null,
            0u,
            new BrowseDescriptionCollection { browseDesc },
            ct).ConfigureAwait(false);

        if (browseResponse.Results is { Count: > 0 })
        {
            var firstResult = browseResponse.Results[0];
            if (StatusCode.IsBad(firstResult.StatusCode))
            {
                _logger.LogWarning("Browse OPC UA em {NodeId} retornou status {Status}", nodeToBrowse, firstResult.StatusCode);
            }

            if (firstResult.References is not null)
                refs.AddRange(firstResult.References);

            byte[]? cp = firstResult.ContinuationPoint;
            int pages = 0;
            while (cp is { Length: > 0 } && pages++ < 50)
            {
                var nextResponse = await session.BrowseNextAsync(
                    null,
                    releaseContinuationPoints: false,
                    new ByteStringCollection { cp },
                    ct).ConfigureAwait(false);

                if (nextResponse.Results is not { Count: > 0 })
                    break;

                if (nextResponse.Results[0].References is not null)
                    refs.AddRange(nextResponse.Results[0].References);

                cp = nextResponse.Results[0].ContinuationPoint;
            }

            if (cp is { Length: > 0 })
            {
                try
                {
                    await session.BrowseNextAsync(null, true, new ByteStringCollection { cp }, ct).ConfigureAwait(false);
                }
                catch { }
            }
        }

        var result = new List<BrowseNode>(refs.Count);
        var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);
        var varNodeIds = new ReadValueIdCollection();
        var varIndices = new List<int>();

        for (int i = 0; i < refs.Count; i++)
        {
            var r = refs[i];
            if (r.NodeId is null || !r.IsForward) continue;
            if (IsNonBrowsableMetadataReference(r.ReferenceTypeId)) continue;

            var nodeId = ResolveLocalNodeId(r.NodeId, session);
            if (NodeId.IsNull(nodeId)) continue;

            string nodeIdStr = nodeId.ToString();
            if (!seenNodeIds.Add(nodeIdStr)) continue;

            bool isVar = r.NodeClass == NodeClass.Variable;

            if (isVar)
            {
                varIndices.Add(result.Count);
                varNodeIds.Add(new ReadValueId
                {
                    NodeId = nodeId,
                    AttributeId = Attributes.DataType
                });
            }

            string displayName = !string.IsNullOrWhiteSpace(r.DisplayName?.Text)
                ? r.DisplayName!.Text
                : !string.IsNullOrWhiteSpace(r.BrowseName?.Name)
                    ? r.BrowseName!.Name
                    : nodeIdStr;

            result.Add(new BrowseNode(
                Id: nodeIdStr,
                DisplayName: displayName,
                IsVariable: isVar,
                HasChildren: true,
                DataType: null));
        }

        if (varNodeIds.Count > 0)
        {
            try
            {
                const int batchSize = 200;
                for (int offset = 0; offset < varNodeIds.Count; offset += batchSize)
                {
                    int count = Math.Min(batchSize, varNodeIds.Count - offset);
                    var batch = new ReadValueIdCollection(count);
                    for (int k = 0; k < count; k++)
                        batch.Add(varNodeIds[offset + k]);

                    var readResponse = await session.ReadAsync(
                        null, 0, TimestampsToReturn.Neither, batch, ct).ConfigureAwait(false);
                    var values = readResponse.Results;

                    for (int i = 0; i < count && i < values.Count; i++)
                    {
                        string? dtName = ResolveDataTypeName(values[i].Value as NodeId);
                        int idx = varIndices[offset + i];
                        result[idx] = result[idx] with { DataType = dtName };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Aviso ao ler atributo DataType dos nós filhos de {ParentId}", parentId);
            }
        }

        // Ordena priorizando canais/dispositivos/pastas do usuário antes de nós internos (_System, Server)
        result.Sort(CompareBrowseNodes);

        if (string.IsNullOrWhiteSpace(parentId))
        {
            var userFolders = result.Where(n => !IsSystemNode(n)).Select(n => n.DisplayName).Take(10).ToList();
            _logger.LogInformation(
                "Navegação raiz de '{Name}': {Total} nó(s) encontrado(s). Canais/Pastas principais: {Folders}",
                Connection.Name,
                result.Count,
                userFolders.Count > 0 ? string.Join(", ", userFolders) : "(apenas nós de sistema)");
        }

        return result;
    }

    private static bool IsNonBrowsableMetadataReference(NodeId? refTypeId)
    {
        if (refTypeId is null || NodeId.IsNull(refTypeId))
            return false;

        return refTypeId == ReferenceTypeIds.HasTypeDefinition
            || refTypeId == ReferenceTypeIds.HasModellingRule
            || refTypeId == ReferenceTypeIds.HasEncoding
            || refTypeId == ReferenceTypeIds.HasDescription
            || refTypeId == ReferenceTypeIds.GeneratesEvent
            || refTypeId == ReferenceTypeIds.AlwaysGeneratesEvent
            || refTypeId == ReferenceTypeIds.HasNotifier
            || refTypeId == ReferenceTypeIds.HasCondition;
    }

    private static NodeId ResolveLocalNodeId(ExpandedNodeId? expandedNodeId, ISession session)
    {
        if (expandedNodeId is null || NodeId.IsNull(expandedNodeId))
            return NodeId.Null;

        var converted = ExpandedNodeId.ToNodeId(expandedNodeId, session.NamespaceUris);
        if (!NodeId.IsNull(converted))
            return converted;

        if (!NodeId.IsNull(expandedNodeId.InnerNodeId))
        {
            if (expandedNodeId.InnerNodeId.NamespaceIndex == 0 && !string.IsNullOrEmpty(expandedNodeId.NamespaceUri))
            {
                int nsIdx = session.NamespaceUris.GetIndex(expandedNodeId.NamespaceUri);
                if (nsIdx > 0)
                    return new NodeId(expandedNodeId.InnerNodeId.Identifier, (ushort)nsIdx);

                if (session.NamespaceUris.Count > 2)
                    return new NodeId(expandedNodeId.InnerNodeId.Identifier, 2);
            }

            return expandedNodeId.InnerNodeId;
        }

        return NodeId.Null;
    }

    private static bool IsSystemNode(BrowseNode node) =>
        string.IsNullOrWhiteSpace(node.DisplayName)
        || node.DisplayName.StartsWith('_')
        || !node.Id.StartsWith("ns=", StringComparison.Ordinal)
        || string.Equals(node.DisplayName, "Server", StringComparison.OrdinalIgnoreCase)
        || string.Equals(node.DisplayName, "Aliases", StringComparison.OrdinalIgnoreCase)
        || string.Equals(node.DisplayName, "Locations", StringComparison.OrdinalIgnoreCase);

    private static int CompareBrowseNodes(BrowseNode a, BrowseNode b)
    {
        int prioA = GetBrowsePriority(a);
        int prioB = GetBrowsePriority(b);
        if (prioA != prioB)
            return prioA.CompareTo(prioB);

        return StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
    }

    private static int GetBrowsePriority(BrowseNode node)
    {
        bool isSys = IsSystemNode(node);
        if (!isSys && !node.IsVariable) return 0; // Pastas / Canais / Dispositivos do usuário (ns >= 1) primeiro
        if (!isSys && node.IsVariable) return 1;  // Tags do usuário (ns >= 1) em seguida
        if (isSys && !node.IsVariable) return 2;  // Pastas de sistema (_System, Server, Locations, ns=0)
        return 3;                                 // Variáveis de sistema
    }

    /// <summary>Lê os atributos detalhados de um nó para exibição no painel de inspeção (RF-TAG-02).</summary>
    public async Task<OpcUaNodeAttributes> ReadNodeAttributesAsync(string nodeIdText, CancellationToken ct = default)
    {
        var session = RequireSession();
        var nodeId = NodeId.Parse(nodeIdText);

        uint[] attrIds =
        [
            Attributes.BrowseName,
            Attributes.DisplayName,
            Attributes.DataType,
            Attributes.ValueRank,
            Attributes.AccessLevel,
            Attributes.Description,
            Attributes.Value
        ];

        var nodesToRead = new ReadValueIdCollection(
            attrIds.Select(a => new ReadValueId { NodeId = nodeId, AttributeId = a }));

        var response = await session.ReadAsync(
            null, 0, TimestampsToReturn.Both, nodesToRead, ct).ConfigureAwait(false);

        var v = response.Results;
        string browseName = (v[0].Value as QualifiedName)?.ToString() ?? nodeIdText;
        string displayName = (v[1].Value as LocalizedText)?.Text ?? browseName;
        string dataType = ResolveDataTypeName(v[2].Value as NodeId) ?? "Variant";
        int valueRank = v[3].Value is int vr ? vr : ValueRanks.Scalar;
        byte accessLevel = v[4].Value is byte al ? al : (byte)0;
        string? description = (v[5].Value as LocalizedText)?.Text;

        var valDv = v[6];
        var sample = ToSample(0, valDv, DateTime.UtcNow);
        object? safeCurrentValue = sample.Value switch
        {
            Array arr => $"[{arr.Length}] {string.Join(", ", arr.Cast<object?>().Take(6))}",
            ExtensionObject ext => ext.Body?.ToString() ?? ext.TypeId.ToString(),
            _ => sample.Value
        };

        return new OpcUaNodeAttributes(
            nodeIdText,
            browseName,
            displayName,
            dataType,
            valueRank,
            accessLevel,
            description,
            safeCurrentValue,
            sample.Quality,
            valDv.SourceTimestamp != DateTime.MinValue ? valDv.SourceTimestamp : null);
    }

    public async Task<IReadOnlyList<Sample>> ReadAsync(IReadOnlyList<TagConfig> tags, CancellationToken ct = default)
    {
        if (tags.Count == 0) return [];
        var session = RequireSession();

        var nodesToRead = new ReadValueIdCollection(tags.Count);
        foreach (var t in tags)
        {
            nodesToRead.Add(new ReadValueId
            {
                NodeId = NodeId.Parse(t.Address),
                AttributeId = Attributes.Value
            });
        }

        var response = await session.ReadAsync(
            null, 0, TimestampsToReturn.Both, nodesToRead, ct).ConfigureAwait(false);

        DateTime receivedUtc = DateTime.UtcNow;
        var results = new List<Sample>(tags.Count);
        for (int i = 0; i < tags.Count && i < response.Results.Count; i++)
        {
            results.Add(ToSample(tags[i].Id, response.Results[i], receivedUtc));
        }

        return results;
    }

    public async Task StartGroupAsync(AcquisitionGroup group, ISampleSink sink, CancellationToken ct = default)
    {
        var session = RequireSession();
        await StopGroupAsync(group.Id, ct).ConfigureAwait(false);

        if (group.Tags.Count == 0) return;

        if (group.Mode == AcquisitionMode.Subscription)
        {
            var subscription = new Subscription(session.DefaultSubscription)
            {
                DisplayName = group.Name,
                PublishingEnabled = true,
                PublishingInterval = (int)Math.Max(10, group.PublishingIntervalMs),
                KeepAliveCount = 10,
                LifetimeCount = 100,
                MaxNotificationsPerPublish = 0,
                TimestampsToReturn = TimestampsToReturn.Both
            };

            foreach (var tag in group.Tags)
            {
                var item = new MonitoredItem(subscription.DefaultItem)
                {
                    StartNodeId = NodeId.Parse(tag.Address),
                    AttributeId = Attributes.Value,
                    DisplayName = string.IsNullOrWhiteSpace(tag.Alias) ? tag.Address : tag.Alias,
                    MonitoringMode = MonitoringMode.Reporting,
                    SamplingInterval = (int)Math.Max(0, group.SamplingIntervalMs),
                    QueueSize = Math.Max(1u, group.QueueSize),
                    DiscardOldest = false, // RF-ACQ-01: não descartar transições rápidas silenciosamente
                    Handle = tag
                };

                if (tag.DeadbandType != CoreDeadbandType.None && tag.DeadbandValue > 0)
                {
                    item.Filter = new DataChangeFilter
                    {
                        Trigger = DataChangeTrigger.StatusValue,
                        DeadbandType = (uint)(tag.DeadbandType == CoreDeadbandType.Percent
                            ? Opc.Ua.DeadbandType.Percent
                            : Opc.Ua.DeadbandType.Absolute),
                        DeadbandValue = tag.DeadbandValue
                    };
                }

                item.Notification += (monitoredItem, e) => OnMonitoredItemNotification(monitoredItem, e, sink);
                subscription.AddItem(item);
            }

            session.AddSubscription(subscription);
            await subscription.CreateAsync(ct).ConfigureAwait(false);

            int okCount = 0;
            foreach (var mi in subscription.MonitoredItems)
            {
                if (mi.Status?.Error is not null && ServiceResult.IsBad(mi.Status.Error))
                {
                    _logger.LogWarning("MonitoredItem '{Name}' ({NodeId}) retornou status {Status}",
                        mi.DisplayName, mi.StartNodeId, mi.Status.Error);
                }
                else
                {
                    okCount++;
                }
            }

            _logger.LogInformation("Subscription '{Group}' iniciada em '{Conn}' ({Ok}/{Total} tags ativas).",
                group.Name, Connection.Name, okCount, group.Tags.Count);

            _groups[group.Id] = new RunningGroup(subscription, null);
        }
        else
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(() => RunPollingLoopAsync(group, sink, cts.Token), CancellationToken.None);
            _groups[group.Id] = new RunningGroup(null, cts);
        }
    }

    public async Task StopGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        if (!_groups.TryRemove(groupId, out var running)) return;

        if (running.PollingCts is not null)
        {
            await running.PollingCts.CancelAsync().ConfigureAwait(false);
            running.PollingCts.Dispose();
        }

        if (running.Subscription is not null && _session is not null)
        {
            try
            {
                await running.Subscription.DeleteAsync(silent: true, ct).ConfigureAwait(false);
                await _session.RemoveSubscriptionAsync(running.Subscription, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Aviso ao remover subscription do grupo {GroupId}", groupId);
            }
            finally
            {
                running.Subscription.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    private async Task RunPollingLoopAsync(AcquisitionGroup group, ISampleSink sink, CancellationToken ct)
    {
        var lastValues = new Dictionary<int, (object? Value, uint Status)>();
        var interval = TimeSpan.FromMilliseconds(Math.Max(10, group.SamplingIntervalMs));
        using var timer = new PeriodicTimer(interval);

        try
        {
            do
            {
                if (_session is not { Connected: true }) continue;

                var samples = await ReadAsync(group.Tags, ct).ConfigureAwait(false);
                foreach (var s in samples)
                {
                    if (lastValues.TryGetValue(s.TagId, out var prev) &&
                        prev.Status == s.StatusCode &&
                        Equals(prev.Value, s.Value))
                    {
                        continue;
                    }

                    lastValues[s.TagId] = (s.Value, s.StatusCode);
                    sink.TryPublish(in s);
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro no loop de polling do grupo {Group}", group.Name);
        }
    }

    private static void OnMonitoredItemNotification(
        MonitoredItem item,
        MonitoredItemNotificationEventArgs e,
        ISampleSink sink)
    {
        if (item.Handle is not TagConfig tag) return;
        DateTime receivedUtc = DateTime.UtcNow;

        var values = item.DequeueValues();
        if (values is { Count: > 0 })
        {
            foreach (var dv in values)
            {
                var sample = ToSample(tag.Id, dv, receivedUtc);
                sink.TryPublish(in sample);
            }
        }
        else if (e?.NotificationValue is MonitoredItemNotification min && min.Value is not null)
        {
            var sample = ToSample(tag.Id, min.Value, receivedUtc);
            sink.TryPublish(in sample);
        }
    }

    private static Sample ToSample(int tagId, DataValue dv, DateTime receivedUtc)
    {
        DateTime ts;
        TimestampOrigin origin;

        if (dv.SourceTimestamp != DateTime.MinValue && dv.SourceTimestamp.Year > 1970)
        {
            ts = DateTime.SpecifyKind(dv.SourceTimestamp, DateTimeKind.Utc);
            origin = TimestampOrigin.Source;
        }
        else if (dv.ServerTimestamp != DateTime.MinValue && dv.ServerTimestamp.Year > 1970)
        {
            ts = DateTime.SpecifyKind(dv.ServerTimestamp, DateTimeKind.Utc);
            origin = TimestampOrigin.Server;
        }
        else
        {
            ts = receivedUtc;
            origin = TimestampOrigin.Local;
        }

        return new Sample(tagId, ts, receivedUtc, origin, dv.Value, dv.StatusCode.Code);
    }

    private void OnSessionKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (!ServiceResult.IsBad(e.Status)) return;
        if (_reconnectHandler is not null) return;

        string statusText = e.Status?.ToString() ?? "Bad";
        _logger.LogWarning("KeepAlive falhou em {Name}: {Status}. Iniciando reconexão...",
            Connection.Name, statusText);
        SetState(ConnectionState.Reconnecting, statusText);

#pragma warning disable CS0618
        _reconnectHandler = new SessionReconnectHandler(true, 10_000);
#pragma warning restore CS0618
        _reconnectHandler.BeginReconnect(session, 2_000, OnServerReconnectComplete);
    }

    private void OnServerReconnectComplete(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _reconnectHandler)) return;

        if (_reconnectHandler?.Session is not null)
        {
            _session = _reconnectHandler.Session;
        }

        _reconnectHandler?.Dispose();
        _reconnectHandler = null;

        SetState(ConnectionState.Connected, "Reconectado");
        _logger.LogInformation("Sessão OPC UA {Name} reconectada.", Connection.Name);
    }

    private static async Task<EndpointDescription> SelectEndpointAsync(ConnectionConfig conn, CancellationToken ct)
    {
        var endpointConfiguration = EndpointConfiguration.Create();
        endpointConfiguration.OperationTimeout = conn.OperationTimeoutMs;

#pragma warning disable CS0618
        using var client = DiscoveryClient.Create(new Uri(conn.EndpointUrl), endpointConfiguration);
#pragma warning restore CS0618
        var endpoints = await client.GetEndpointsAsync(null, ct).ConfigureAwait(false);

        if (endpoints.Count == 0)
            throw new InvalidOperationException($"Nenhum endpoint retornado por {conn.EndpointUrl}.");

        var desiredMode = conn.SecurityMode switch
        {
            MessageSecurity.Sign => MessageSecurityMode.Sign,
            MessageSecurity.SignAndEncrypt => MessageSecurityMode.SignAndEncrypt,
            _ => MessageSecurityMode.None
        };

        var match = endpoints.FirstOrDefault(ep =>
            ep.SecurityMode == desiredMode &&
            (string.IsNullOrEmpty(conn.SecurityPolicyUri) ||
             string.Equals(ep.SecurityPolicyUri, conn.SecurityPolicyUri, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(OpcUaSecurityManager.GetPolicyShortName(ep.SecurityPolicyUri), conn.SecurityPolicyUri, StringComparison.OrdinalIgnoreCase)));

        match ??= endpoints.FirstOrDefault(ep => ep.SecurityMode == desiredMode) ?? endpoints[0];

        var requestedUri = new Uri(conn.EndpointUrl);
        if (Uri.TryCreate(match.EndpointUrl, UriKind.Absolute, out var returnedUri) &&
            !string.Equals(requestedUri.Host, returnedUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(returnedUri)
            {
                Host = requestedUri.Host,
                Port = requestedUri.Port
            };
            match.EndpointUrl = builder.Uri.ToString();
        }

        return match;
    }

    private static UserIdentity BuildUserIdentity(ConnectionConfig conn) => conn.Authentication switch
    {
        UserAuthentication.UserName => new UserIdentity(
            conn.UserName ?? string.Empty,
            Encoding.UTF8.GetBytes(SecretProtector.Unprotect(conn.ProtectedPassword) ?? string.Empty)),
        UserAuthentication.Certificate when !string.IsNullOrWhiteSpace(conn.UserCertificatePath) =>
            new UserIdentity(X509CertificateLoader.LoadPkcs12FromFile(
                conn.UserCertificatePath,
                SecretProtector.Unprotect(conn.ProtectedPassword))),
        _ => new UserIdentity(new AnonymousIdentityToken())
    };

    private static string? ResolveDataTypeName(NodeId? dataTypeId)
    {
        if (dataTypeId is null || NodeId.IsNull(dataTypeId)) return null;

        if (dataTypeId.NamespaceIndex == 0 && dataTypeId.IdType == IdType.Numeric && dataTypeId.Identifier is uint id)
        {
            if (id is >= 1 and <= 25)
                return ((BuiltInType)id).ToString();

            string? browseName = DataTypes.GetBrowseName((int)id);
            if (!string.IsNullOrEmpty(browseName))
                return browseName;
        }

        return dataTypeId.ToString();
    }

    private ISession RequireSession() =>
        _session is { Connected: true } s
            ? s
            : throw new InvalidOperationException($"A conexão '{Connection.Name}' não está conectada.");

    private void SetState(ConnectionState state, string? reason)
    {
        _state = state;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, reason));
    }

    private sealed record RunningGroup(Subscription? Subscription, CancellationTokenSource? PollingCts);
}
