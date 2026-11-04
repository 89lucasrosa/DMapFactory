using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DMapFactory.Acquisition;
using DMapFactory.App.Services;
using DMapFactory.App.Views;
using DMapFactory.Core.Abstractions;
using DMapFactory.Core.Models;
using DMapFactory.Drivers.OpcUa;
using DMapFactory.Localization;
using DMapFactory.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Serilog.Events;

namespace DMapFactory.App.ViewModels;

/// <summary>
/// ViewModel da janela principal (M0 + M1 + M2: conexões, browse, aquisição on/off-line, sessão CSV+JSON e projeto .dmproj).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly OpcUaSecurityManager _securityManager;
    private readonly SessionManager _sessionManager;
    private readonly ILogger<MainViewModel> _logger;
    private readonly ILogger<OpcUaDriver> _driverLogger;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _periodicReadTimer;
    private readonly Stopwatch _sessionClock = new();

    private ProjectConfig _project = new();
    private string? _currentProjectPath;
    private int _nextConnectionId = 1;
    private int _nextTagId = 1;
    private int _markerCounter = 1;
    private bool _suppressOnlineToggle;
    private bool _isPeriodicReading;

    public MainViewModel(
        SettingsService settings,
        OpcUaSecurityManager securityManager,
        SessionManager sessionManager,
        UiLogSink logSink,
        ILogger<MainViewModel> logger,
        ILogger<OpcUaDriver> driverLogger)
    {
        _settings = settings;
        _securityManager = securityManager;
        _sessionManager = sessionManager;
        _logger = logger;
        _driverLogger = driverLogger;
        LogEntries = logSink.Entries;

        FilteredLogEntries = CollectionViewSource.GetDefaultView(LogEntries);
        FilteredLogEntries.Filter = o =>
        {
            if (o is not LogEntry entry) return false;
            return entry.Level switch
            {
                LogEventLevel.Information => LogShowInformation,
                LogEventLevel.Warning => LogShowWarning,
                LogEventLevel.Error or LogEventLevel.Fatal => LogShowError,
                LogEventLevel.Debug or LogEventLevel.Verbose => LogShowDebug,
                _ => true
            };
        };

        _selectedLanguage = Loc.SupportedLanguages.FirstOrDefault(l => l.Code == settings.Current.Language)
                            ?? Loc.SupportedLanguages[0];
        _verticalColumnHeaders = settings.Current.VerticalColumnHeaders;
        _spreadsheetHeaderHeight = settings.Current.SpreadsheetHeaderHeight;
        _showMarkerColumn = settings.Current.ShowMarkerColumn;

        Loc.Instance.PropertyChanged += OnLanguageChanged;
        _sessionManager.BatchProcessed += OnSessionBatchProcessed;

        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => RefreshStatusAndValues(), Application.Current.Dispatcher);
        _statusTimer.Start();

        _periodicReadTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => _ = RunPeriodicSweepAsync(), Application.Current.Dispatcher);
        _periodicReadTimer.Start();

        RefreshStatusAndValues();
    }

    public static string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public IReadOnlyList<LanguageOption> Languages => Loc.SupportedLanguages;

    public ObservableCollection<LogEntry> LogEntries { get; }
    public ICollectionView FilteredLogEntries { get; }

    [ObservableProperty] private bool _logShowInformation = true;
    [ObservableProperty] private bool _logShowWarning = true;
    [ObservableProperty] private bool _logShowError = true;
    [ObservableProperty] private bool _logShowDebug = true;

    partial void OnLogShowInformationChanged(bool value) => FilteredLogEntries.Refresh();
    partial void OnLogShowWarningChanged(bool value) => FilteredLogEntries.Refresh();
    partial void OnLogShowErrorChanged(bool value) => FilteredLogEntries.Refresh();
    partial void OnLogShowDebugChanged(bool value) => FilteredLogEntries.Refresh();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkerButtonToolTip))]
    private bool _isPlacingMarker;

    public string MarkerButtonToolTip =>
        IsPlacingMarker
            ? "Modo de inserção de marcador ativo: clique na planilha ou no gráfico onde deseja inserir (Esc para cancelar)"
            : "Inserir marcador de evento na sessão (Ctrl+M)";

    public bool IsSelectedConnectionConnected =>
        (SelectedConnection ?? Connections.FirstOrDefault())?.IsConnected == true;

    public string ConnectButtonText =>
        IsSelectedConnectionConnected ? "Desconectar" : "Conectar";

    public string ConnectButtonToolTip =>
        IsSelectedConnectionConnected
            ? $"Desconectar do servidor OPC UA '{(SelectedConnection ?? Connections.FirstOrDefault())?.Name}'"
            : Connections.Count == 0
                ? "Criar uma nova conexão e conectar ao servidor OPC UA"
                : $"Conectar ao servidor OPC UA '{(SelectedConnection ?? Connections.FirstOrDefault())?.Name}'";

    public string ConnectButtonIcon =>
        IsSelectedConnectionConnected ? "🔌❌" : "🔌";

    public void NotifyConnectionStateChanged()
    {
        OnPropertyChanged(nameof(IsSelectedConnectionConnected));
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(ConnectButtonToolTip));
        OnPropertyChanged(nameof(ConnectButtonIcon));
    }
    public ObservableCollection<ConnectionItemViewModel> Connections { get; } = [];
    public ObservableCollection<BrowseNodeViewModel> BrowserRoots { get; } = [];
    public ObservableCollection<BrowseNodeViewModel> SelectedBrowseNodes { get; } = [];
    public ObservableCollection<MonitoredTagViewModel> MonitoredTags { get; } = [];
    public ObservableCollection<WideSpreadsheetRow> SpreadsheetRows { get; } = [];
    public ObservableCollection<SessionMarker> SessionMarkers { get; } = [];

    [ObservableProperty] private LanguageOption _selectedLanguage;
    [ObservableProperty] private ConnectionItemViewModel? _selectedConnection;
    [ObservableProperty] private BrowseNodeViewModel? _selectedBrowseNode;
    [ObservableProperty] private OpcUaNodeAttributes? _selectedNodeAttributes;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoveTagButtonText), nameof(RemoveTagToolTip))]
    private MonitoredTagViewModel? _selectedMonitoredTag;
    [ObservableProperty] private string _manualTagAddress = "";

    /// <summary>True enquanto uma sessão de aquisição estiver ativa (RF-ACQ-06).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlineButtonText), nameof(OnlineStateText), nameof(OnlineButtonToolTip))]
    private bool _isOnline;

    [ObservableProperty] private string _sessionTimeText = "";
    [ObservableProperty] private string _eventsPerSecondText = "";
    [ObservableProperty] private string _bufferText = "";
    [ObservableProperty] private string _connectionsText = "";
    [ObservableProperty] private bool _autoScrollSpreadsheet = true;
    [ObservableProperty] private bool _periodicSpreadsheetSampling = false; // PF-02: Evita polling desnecessário por padrão
    [ObservableProperty] private bool _verticalColumnHeaders;
    [ObservableProperty] private bool _showMarkerColumn = true;

    // FN-01: Taxa de amostragem e modo configuráveis
    public IReadOnlyList<int> SamplingIntervalOptions { get; } = [10, 20, 50, 100, 250, 500, 1000];
    [ObservableProperty] private int _selectedSamplingIntervalMs = 100;
    public IReadOnlyList<string> AcquisitionModeOptions { get; } = ["Assinatura (Subscription)", "Polling Periódico"];
    [ObservableProperty] private int _selectedAcquisitionModeIndex = 0;

    // FN-02: Gatilho de ciclo de máquina configurável
    [ObservableProperty] private string? _cycleTriggerTagAlias;

    // FN-03: Busca de nós na árvore OPC UA
    [ObservableProperty] private string _browserSearchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpreadsheetHeaderHeightText))]
    private double _spreadsheetHeaderHeight;

    [ObservableProperty] private bool _autoFollowChart = true;
    [ObservableProperty] private int _chartWindowSeconds = 60;
    [ObservableProperty] private bool _showChartCursors = true;
    [ObservableProperty] private bool _chartStepInterpolation = true;
    [ObservableProperty] private bool _showChartSamplePoints;
    [ObservableProperty] private bool _showChartGrid = true;
    [ObservableProperty] private bool _showChartCrosshair = true;
    [ObservableProperty] private bool _showChartPensPanel = true;
    [ObservableProperty] private bool _fitAllChartRange;
    [ObservableProperty] private double _chartTimeOffsetSeconds;
    [ObservableProperty] private string _cursorDeltaText = "Δt = —";

    public IReadOnlyList<int> ChartWindowOptions { get; } = [5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];

    public string SpreadsheetHeaderHeightText =>
        SpreadsheetHeaderHeight <= 0 ? "Auto" : $"{SpreadsheetHeaderHeight:F0} px";

    public string AddSelectedBrowserButtonText =>
        SelectedBrowseNodes.Count > 1 ? $"+ Planilha ({SelectedBrowseNodes.Count})" : "+ Planilha";

    public string RemoveTagButtonText =>
        SelectedMonitoredTag is not null
            ? $"Excluir tag ({SelectedMonitoredTag.Alias})"
            : "Excluir tag";

    public string RemoveTagToolTip =>
        SelectedMonitoredTag is not null
            ? $"Excluir a tag selecionada '{SelectedMonitoredTag.Alias}' ({SelectedMonitoredTag.Address}) da coleta (Delete)"
            : "Excluir a tag selecionada (Delete) ou a última coluna adicionada à planilha";

    public bool GroupSameTimestamp
    {
        get => _sessionManager.Snapshot.GroupSameTimestamp;
        set
        {
            if (_sessionManager.Snapshot.GroupSameTimestamp != value)
            {
                _sessionManager.Snapshot.GroupSameTimestamp = value;
                OnPropertyChanged();
            }
        }
    }

    public string OnlineButtonText => Loc.Instance[IsOnline ? "Toolbar_GoOffline" : "Toolbar_GoOnline"];
    public string OnlineButtonToolTip => $"{OnlineButtonText} (F5)";
    public string OnlineStateText => Loc.Instance[IsOnline ? "Status_Online" : "Status_Offline"];

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        Loc.Instance.Culture = CultureInfo.GetCultureInfo(value.Code);
        _settings.Current.Language = value.Code;
        _settings.Save();
        _logger.LogInformation("{Message}", Loc.Instance.Format("Log_LanguageChanged", value.NativeName));
    }

    partial void OnVerticalColumnHeadersChanged(bool value)
    {
        _settings.Current.VerticalColumnHeaders = value;
        _settings.Save();
    }

    partial void OnShowMarkerColumnChanged(bool value)
    {
        _settings.Current.ShowMarkerColumn = value;
        _settings.Save();
    }

    partial void OnSpreadsheetHeaderHeightChanged(double value)
    {
        _settings.Current.SpreadsheetHeaderHeight = value;
        _settings.Save();
    }

    partial void OnAutoFollowChartChanged(bool value)
    {
        if (value)
        {
            ChartTimeOffsetSeconds = 0;
            FitAllChartRange = false;
        }
    }

    partial void OnChartWindowSecondsChanged(int value)
    {
        if (value > 0)
            FitAllChartRange = false;
    }

    partial void OnSelectedConnectionChanged(ConnectionItemViewModel? value)
    {
        NotifyConnectionStateChanged();
        _ = ReloadBrowserForConnectionAsync(value);
    }

    partial void OnIsOnlineChanged(bool value)
    {
        if (_suppressOnlineToggle) return;
        _ = TransitionOnlineStateAsync(value);
    }

    partial void OnBrowserSearchTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        string term = value.Trim();
        foreach (var root in BrowserRoots)
        {
            ExpandMatchingNodes(root, term);
        }
    }

    private static bool ExpandMatchingNodes(BrowseNodeViewModel node, string filter)
    {
        bool match = node.DisplayText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                     node.Node.Id.Contains(filter, StringComparison.OrdinalIgnoreCase);

        bool childMatch = false;
        foreach (var child in node.Children)
        {
            if (ExpandMatchingNodes(child, filter))
                childMatch = true;
        }

        if (childMatch)
            node.IsExpanded = true;

        return match || childMatch;
    }

    [RelayCommand]
    public void SetAsCycleTrigger(MonitoredTagViewModel? tagVm = null)
    {
        var target = tagVm ?? SelectedMonitoredTag;
        if (target is null) return;
        _sessionManager.Snapshot.CycleTriggerTagId = target.Config.Id;
        CycleTriggerTagAlias = target.Alias;
        _logger.LogInformation("Tag '{Alias}' definida como gatilho de ciclo de máquina.", target.Alias);
    }

    [RelayCommand]
    public void ClearCycleTrigger()
    {
        _sessionManager.Snapshot.CycleTriggerTagId = null;
        CycleTriggerTagAlias = null;
        _logger.LogInformation("Gatilho de ciclo de máquina desativado.");
    }

    [RelayCommand]
    private void ClearLogs()
    {
        LogEntries.Clear();
    }

    [RelayCommand]
    private static void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.LogsFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = SettingsService.LogsFolder,
                UseShellExecute = true
            });
        }
        catch { }
    }

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (IsOnline)
            await TransitionOnlineStateAsync(false);

        foreach (var c in Connections)
            await c.Driver.DisposeAsync();

        Connections.Clear();
        BrowserRoots.Clear();
        MonitoredTags.Clear();
        SpreadsheetRows.Clear();
        _project = new ProjectConfig();
        _currentProjectPath = null;
        _nextConnectionId = 1;
        _nextTagId = 1;
        _logger.LogInformation("Novo projeto iniciado.");
    }

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        var ofd = new OpenFileDialog
        {
            Title = Loc.Instance["Menu_OpenProject"],
            Filter = "DMapFactory Project (*.dmproj)|*.dmproj|JSON (*.json)|*.json"
        };
        if (ofd.ShowDialog() != true) return;

        await LoadProjectFromFileAsync(ofd.FileName);
    }

    [RelayCommand]
    private async Task SaveProjectAsync()
    {
        string? path = _currentProjectPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var sfd = new SaveFileDialog
            {
                Title = Loc.Instance["Menu_SaveProject"],
                FileName = $"{_project.Name}.dmproj",
                Filter = "DMapFactory Project (*.dmproj)|*.dmproj"
            };
            if (sfd.ShowDialog() != true) return;
            path = sfd.FileName;
            _project.Name = Path.GetFileNameWithoutExtension(path);
        }

        BuildProjectFromViewModels();
        await ProjectFileStore.SaveAsync(_project, path);
        _currentProjectPath = path;
        _settings.Current.LastProjectPath = path;
        _settings.Save();
        _logger.LogInformation("Projeto salvo em {Path}", path);
    }

    [RelayCommand]
    private async Task OpenRecordedSessionAsync()
    {
        var ofd = new OpenFileDialog
        {
            Title = Loc.Instance["Menu_OpenSession"],
            Filter = "Sessão DMapFactory (*.csv;*.session.json)|*.csv;*.session.json",
            InitialDirectory = Directory.Exists(SettingsService.DefaultSessionsFolder)
                ? SettingsService.DefaultSessionsFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (ofd.ShowDialog() != true) return;

        try
        {
            if (IsOnline)
                await TransitionOnlineStateAsync(false);

            var loaded = await CsvSessionReader.LoadAsync(ofd.FileName);
            _sessionManager.Snapshot.Reset();
            SessionMarkers.Clear();

            // FN-02: Identifica gatilho de ciclo configurado ou fallback para Cycle_Active
            var cycleTag = loaded.Metadata.Tags.FirstOrDefault(t =>
                string.Equals(t.Alias, "Cycle_Active", StringComparison.OrdinalIgnoreCase));
            if (cycleTag is not null)
            {
                _sessionManager.Snapshot.CycleTriggerTagId = cycleTag.Id;
                CycleTriggerTagAlias = cycleTag.Alias;
            }

            var rows = _sessionManager.Snapshot.ApplyBatch(loaded.Samples);

            // MG-02: Restaura marcadores salvos nos metadados da sessão
            if (loaded.Metadata.Markers is { Count: > 0 } loadedMarkers)
            {
                foreach (var m in loadedMarkers)
                {
                    SessionMarkers.Add(m);
                    var markerRow = _sessionManager.Snapshot.InsertMarker(m.Text, m.TimestampUtc);
                }
                rows = _sessionManager.Snapshot.GetAllRows();
            }

            MonitoredTags.Clear();
            var latest = _sessionManager.Snapshot.GetLatestSamples();
            foreach (var t in loaded.Metadata.Tags)
            {
                var cfg = new TagConfig
                {
                    Id = t.Id,
                    ConnectionId = t.ConnectionId,
                    Address = t.Address,
                    Alias = t.Alias,
                    DataType = t.DataType,
                    Unit = t.Unit
                };
                var vm = new MonitoredTagViewModel(cfg, $"Conn#{t.ConnectionId}")
                {
                    IsPlotted = MonitoredTags.Count < 8
                };
                if (latest.TryGetValue(t.Id, out var s))
                    vm.UpdateFromSample(in s);
                MonitoredTags.Add(vm);
            }

            SpreadsheetRows.Clear();
            foreach (var r in rows)
                SpreadsheetRows.Add(r);

            _logger.LogInformation("Sessão '{File}' aberta: {Samples:N0} amostras, {Rows:N0} linhas, {Markers} marcadores.",
                Path.GetFileName(loaded.CsvFilePath), loaded.Samples.Count, rows.Count, SessionMarkers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao abrir sessão gravada");
        }
    }

    [RelayCommand]
    private async Task AddConnectionAsync()
    {
        var dlgVm = new ConnectionDialogViewModel(_securityManager);
        var dlg = new ConnectionDialog(dlgVm) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var cfg = dlgVm.BuildConfig(_nextConnectionId++);
        var itemVm = RegisterConnectionViewModel(cfg);
        SelectedConnection = itemVm;
        _logger.LogInformation("Conexão '{Name}' ({Endpoint}) adicionada.", cfg.Name, cfg.EndpointUrl);
        RefreshStatusAndValues();

        await ConnectOrDisconnectSelectedAsync();
    }

    [RelayCommand]
    private async Task EditConnectionAsync(ConnectionItemViewModel? connVm = null)
    {
        var target = connVm ?? SelectedConnection;
        if (target is null) return;

        var dlgVm = new ConnectionDialogViewModel(_securityManager, target.Config);
        var dlg = new ConnectionDialog(dlgVm) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var updatedConfig = dlgVm.BuildConfig(target.Config.Id);
        bool wasConnected = target.IsConnected;

        try
        {
            if (wasConnected)
            {
                await target.Driver.DisconnectAsync();
            }
            await target.Driver.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Aviso ao encerrar driver anterior da conexão '{Name}': {Message}", target.Name, ex.Message);
        }

        var newDriver = new OpcUaDriver(updatedConfig, _securityManager, _driverLogger);
        newDriver.StateChanged += (_, args) => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            target.State = args.State;
            target.StatusMessage = args.Reason;
            RefreshStatusAndValues();
            NotifyConnectionStateChanged();
        });

        target.Update(updatedConfig, newDriver);

        _logger.LogInformation("Conexão '{Name}' ({Endpoint}) atualizada.", updatedConfig.Name, updatedConfig.EndpointUrl);

        if (wasConnected)
        {
            try
            {
                await target.Driver.ConnectAsync();
                target.State = target.Driver.State;
                if (ReferenceEquals(SelectedConnection, target))
                {
                    await ReloadBrowserForConnectionAsync(target);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao reconectar '{Name}' após edição", target.Name);
            }
        }

        RefreshStatusAndValues();
        NotifyConnectionStateChanged();
    }

    [RelayCommand]
    public async Task ConnectOrDisconnectSelectedAsync(ConnectionItemViewModel? connVm = null)
    {
        var item = connVm ?? SelectedConnection ?? Connections.FirstOrDefault();
        if (item is null)
        {
            await AddConnectionAsync();
            return;
        }

        if (!ReferenceEquals(SelectedConnection, item))
        {
            SelectedConnection = item;
        }

        try
        {
            if (item.IsConnected)
            {
                await item.Driver.DisconnectAsync();
                item.State = item.Driver.State;
                if (ReferenceEquals(SelectedConnection, item))
                {
                    BrowserRoots.Clear();
                    SelectedNodeAttributes = null;
                }

                if (IsOnline)
                {
                    var remainingConnected = Connections.Where(c => c.IsConnected).ToList();
                    if (remainingConnected.Count == 0)
                    {
                        await TransitionOnlineStateAsync(false);
                    }
                    else
                    {
                        BuildProjectFromViewModels();
                        var drivers = remainingConnected.Select(c => (IDriver)c.Driver).ToList();
                        await _sessionManager.UpdateGroupsAsync(_project, drivers);
                    }
                }

                _logger.LogInformation("Conexão '{Name}' ({Endpoint}) desconectada.", item.Name, item.EndpointUrl);
            }
            else
            {
                await item.Driver.ConnectAsync();
                item.State = item.Driver.State;

                if (ReferenceEquals(SelectedConnection, item))
                {
                    await ReloadBrowserForConnectionAsync(item);
                }

                var connTags = MonitoredTags.Where(t => t.Config.ConnectionId == item.Config.Id).ToList();
                if (connTags.Count > 0)
                {
                    if (IsOnline)
                    {
                        BuildProjectFromViewModels();
                        var drivers = Connections.Where(c => c.IsConnected).Select(c => (IDriver)c.Driver).ToList();
                        await _sessionManager.UpdateGroupsAsync(_project, drivers);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            item.State = item.Driver.State;
            item.StatusMessage = ex.Message;
            _logger.LogError(ex, "Erro ao alternar conexão '{Name}'", item.Name);
        }
        finally
        {
            RefreshStatusAndValues();
            NotifyConnectionStateChanged();
        }
    }

    [RelayCommand]
    private async Task RemoveConnectionAsync(ConnectionItemViewModel? connVm = null)
    {
        var target = connVm ?? SelectedConnection;
        if (target is null) return;

        try
        {
            var tagsToRemove = MonitoredTags.Where(t => t.Config.ConnectionId == target.Config.Id).ToList();
            foreach (var t in tagsToRemove)
            {
                MonitoredTags.Remove(t);
            }

            if (IsOnline)
            {
                if (MonitoredTags.Count == 0 || Connections.Count <= 1)
                {
                    await TransitionOnlineStateAsync(false);
                }
                else
                {
                    BuildProjectFromViewModels();
                    var drivers = Connections.Where(c => c != target).Select(c => (IDriver)c.Driver).ToList();
                    await _sessionManager.UpdateGroupsAsync(_project, drivers);
                }
            }

            await target.Driver.DisposeAsync();
            Connections.Remove(target);

            if (ReferenceEquals(SelectedConnection, target))
            {
                SelectedConnection = Connections.FirstOrDefault();
                if (SelectedConnection is null)
                {
                    BrowserRoots.Clear();
                    SelectedNodeAttributes = null;
                }
            }

            _logger.LogInformation("Conexão '{Name}' excluída ({TagsCount} tag(s) associada(s) removida(s)).",
                target.Name, tagsToRemove.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao excluir conexão '{Name}'", target.Name);
        }
        finally
        {
            RefreshStatusAndValues();
        }
    }

    [RelayCommand]
    private async Task ReloadBrowserAsync()
    {
        if (SelectedConnection is null) return;
        await ReloadBrowserForConnectionAsync(SelectedConnection);
    }

    [RelayCommand]
    private async Task AddSelectedNodeAsTagAsync()
    {
        if (SelectedConnection is null) return;
        var targets = SelectedBrowseNodes.Count > 0
            ? SelectedBrowseNodes.ToList()
            : (SelectedBrowseNode is not null ? [SelectedBrowseNode] : []);

        if (targets.Count == 0) return;
        await AddNodesOrChildrenAsTagsAsync(targets, includeChildrenIfFolder: false);
    }

    [RelayCommand]
    private async Task AddFolderVariablesAsTagsAsync()
    {
        if (SelectedConnection is null) return;
        var targets = SelectedBrowseNodes.Count > 0
            ? SelectedBrowseNodes.ToList()
            : (SelectedBrowseNode is not null ? [SelectedBrowseNode] : []);

        if (targets.Count == 0) return;
        await AddNodesOrChildrenAsTagsAsync(targets, includeChildrenIfFolder: true);
    }

    [RelayCommand]
    private async Task AddManualTagAsync()
    {
        if (SelectedConnection is null || string.IsNullOrWhiteSpace(ManualTagAddress))
            return;

        var conn = SelectedConnection;
        string raw = ManualTagAddress.Trim();
        // Se o usuário digitar apenas "Canal.Dispositivo.Tag" (padrão KEPServerEX), normaliza para "ns=2;s=Canal.Dispositivo.Tag"
        string address = raw.Contains('=', StringComparison.Ordinal)
            ? raw
            : $"ns=2;s={raw}";

        string defaultAlias = raw;
        int lastDot = raw.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < raw.Length - 1)
            defaultAlias = raw[(lastDot + 1)..];
        else if (raw.Contains(";s=", StringComparison.OrdinalIgnoreCase))
            defaultAlias = raw[(raw.IndexOf(";s=", StringComparison.OrdinalIgnoreCase) + 3)..];

        string? dataType = null;
        if (conn.IsConnected)
        {
            try
            {
                var attrs = await conn.Driver.ReadNodeAttributesAsync(address);
                if (!string.IsNullOrWhiteSpace(attrs.DisplayName) && attrs.DisplayName != address)
                    defaultAlias = attrs.DisplayName;
                if (!string.IsNullOrWhiteSpace(attrs.DataTypeName))
                    dataType = attrs.DataTypeName;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Aviso ao inspecionar tag manual '{Address}'", address);
            }
        }

        var node = new BrowseNode(address, defaultAlias, true, false, dataType);
        var vm = TryRegisterTag(conn, node);
        if (vm is not null)
        {
            ManualTagAddress = "";
            _logger.LogInformation("Tag manual '{Alias}' ({Address}) adicionada.", vm.Alias, vm.Address);
            await ActivateOrRefreshOnlineAfterAddingTagsAsync(conn, [vm]);
        }
    }

    [RelayCommand]
    public async Task RemoveTagAsync(MonitoredTagViewModel? tagVm = null)
    {
        var target = tagVm ?? SelectedMonitoredTag ?? MonitoredTags.LastOrDefault();
        if (target is null) return;

        MonitoredTags.Remove(target);
        if (ReferenceEquals(SelectedMonitoredTag, target))
            SelectedMonitoredTag = null;

        _logger.LogInformation("Tag '{Alias}' ({Address}) removida da planilha e do gráfico.", target.Alias, target.Address);

        if (IsOnline)
        {
            if (MonitoredTags.Count == 0)
            {
                await TransitionOnlineStateAsync(false);
            }
            else
            {
                BuildProjectFromViewModels();
                var drivers = Connections.Select(c => (IDriver)c.Driver).ToList();
                await _sessionManager.UpdateGroupsAsync(_project, drivers);
            }
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedTagAsync() => await RemoveTagAsync(SelectedMonitoredTag);

    [RelayCommand]
    private async Task ClearAllTagsAsync()
    {
        if (MonitoredTags.Count == 0 && SpreadsheetRows.Count == 0) return;

        if (IsOnline)
        {
            await TransitionOnlineStateAsync(false);
        }

        MonitoredTags.Clear();
        _sessionManager.Snapshot.Reset();
        SpreadsheetRows.Clear();
        _logger.LogInformation("Todas as tags e linhas da planilha foram limpas.");
    }

    [RelayCommand]
    private void ClearSpreadsheetRows()
    {
        _sessionManager.Snapshot.Reset();
        SpreadsheetRows.Clear();
        SessionMarkers.Clear();
        _markerCounter = 1;
        _logger.LogInformation("Linhas da planilha limpas.");
    }

    [RelayCommand]
    private void OpenCertificateManager()
    {
        var knownUrls = Connections
            .Select(c => c.EndpointUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dlg = new CertificateManagerDialog(_securityManager, _logger, _settings, knownUrls)
        {
            Owner = Application.Current.MainWindow
        };
        dlg.ShowDialog();
    }

    [RelayCommand]
    private async Task ExportApplicationCertificateAsync()
    {
        var sfd = new SaveFileDialog
        {
            Title = Loc.Instance["Toolbar_ExportCert"],
            FileName = "DMapFactory-Client.der",
            Filter = "DER Certificate (*.der)|*.der"
        };
        if (sfd.ShowDialog() != true) return;

        try
        {
            await _securityManager.ExportApplicationCertificateDerAsync(sfd.FileName);
            _logger.LogInformation("Certificado da aplicação exportado para {File}", sfd.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao exportar certificado");
        }
    }

    [RelayCommand]
    private void ToggleOnline() => IsOnline = !IsOnline;

    [RelayCommand]
    private void AddMarker()
    {
        IsPlacingMarker = !IsPlacingMarker;
        if (IsPlacingMarker)
        {
            _logger.LogInformation("Modo de inserção de marcador ativado. Clique em um ponto do gráfico ou linha da planilha para posicionar.");
        }
        else
        {
            _logger.LogInformation("Modo de inserção de marcador desativado.");
        }
    }

    public async Task CommitMarkerAtRowAsync(WideSpreadsheetRow row)
    {
        IsPlacingMarker = false;
        if (row.IsMarker)
        {
            await EditMarkerAtRowAsync(row);
            return;
        }

        string defaultText = string.IsNullOrWhiteSpace(row.MarkerText)
            ? $"Marcador #{_markerCounter}"
            : row.MarkerText;

        var dlg = new MarkerInputDialog(defaultText)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dlg.ShowDialog() != true)
            return;

        string markerText = dlg.MarkerText;
        _markerCounter++;

        row.SetMarker(markerText);

        var marker = await _sessionManager.AddMarkerAsync(markerText, row.TimestampUtc, insertSnapshotRow: false);
        if (marker is not null)
        {
            SessionMarkers.Add(marker);
        }

        _logger.LogInformation("Marcador '{Marker}' atribuído à linha #{Row} ({Timestamp:HH:mm:ss.fff}).",
            markerText, row.RowNumber, row.TimestampUtc.ToLocalTime());
    }

    public async Task EditMarkerAtRowAsync(WideSpreadsheetRow row)
    {
        IsPlacingMarker = false;
        string oldText = row.MarkerText ?? string.Empty;
        var dlg = new MarkerInputDialog(oldText, isEditing: true)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dlg.ShowDialog() != true)
            return;

        string newText = dlg.MarkerText;
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
            return;

        row.SetMarker(newText);

        int idx = -1;
        for (int i = SessionMarkers.Count - 1; i >= 0; i--)
        {
            if (Math.Abs((SessionMarkers[i].TimestampUtc - row.TimestampUtc).TotalMilliseconds) < 10.0 &&
                (string.IsNullOrEmpty(oldText) || string.Equals(SessionMarkers[i].Text, oldText, StringComparison.Ordinal)))
            {
                idx = i;
                break;
            }
        }
        if (idx >= 0)
        {
            SessionMarkers[idx] = new SessionMarker(SessionMarkers[idx].TimestampUtc, newText);
        }
        else
        {
            SessionMarkers.Add(new SessionMarker(row.TimestampUtc, newText));
        }

        await _sessionManager.UpdateMarkerAsync(row.TimestampUtc, oldText, newText);

        _logger.LogInformation("Marcador da linha #{Row} atualizado para '{NewText}' ({Timestamp:HH:mm:ss.fff}).",
            row.RowNumber, newText, row.TimestampUtc.ToLocalTime());
    }

    public async Task RemoveMarkerAtRowAsync(WideSpreadsheetRow row)
    {
        IsPlacingMarker = false;
        if (!row.IsMarker)
            return;

        string? oldText = row.MarkerText;
        DateTime ts = row.TimestampUtc;
        long rowNumber = row.RowNumber;

        row.SetMarker(null);

        if (row.ChangedTagIds.Count == 0)
        {
            SpreadsheetRows.Remove(row);
        }

        for (int i = SessionMarkers.Count - 1; i >= 0; i--)
        {
            if (Math.Abs((SessionMarkers[i].TimestampUtc - ts).TotalMilliseconds) < 10.0 &&
                (oldText is null || string.Equals(SessionMarkers[i].Text, oldText, StringComparison.Ordinal)))
            {
                SessionMarkers.RemoveAt(i);
                break;
            }
        }

        await _sessionManager.RemoveMarkerAsync(ts, oldText);

        _logger.LogInformation("Marcador '{OldText}' removido da linha #{Row} ({Timestamp:HH:mm:ss.fff}).",
            oldText, rowNumber, ts.ToLocalTime());
    }

    public async Task EditSessionMarkerAsync(SessionMarker marker)
    {
        IsPlacingMarker = false;
        var matchingRow = SpreadsheetRows.FirstOrDefault(r =>
            r.IsMarker &&
            Math.Abs((r.TimestampUtc - marker.TimestampUtc).TotalMilliseconds) < 10.0 &&
            string.Equals(r.MarkerText, marker.Text, StringComparison.Ordinal));

        if (matchingRow is not null)
        {
            await EditMarkerAtRowAsync(matchingRow);
            return;
        }

        var dlg = new MarkerInputDialog(marker.Text, isEditing: true)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dlg.ShowDialog() != true)
            return;

        string newText = dlg.MarkerText;
        if (string.Equals(marker.Text, newText, StringComparison.Ordinal))
            return;

        int idx = SessionMarkers.IndexOf(marker);
        if (idx >= 0)
        {
            SessionMarkers[idx] = new SessionMarker(marker.TimestampUtc, newText);
        }

        await _sessionManager.UpdateMarkerAsync(marker.TimestampUtc, marker.Text, newText);

        _logger.LogInformation("Marcador '{OldText}' atualizado para '{NewText}' ({Timestamp:HH:mm:ss.fff}).",
            marker.Text, newText, marker.TimestampUtc.ToLocalTime());
    }

    public async Task RemoveSessionMarkerAsync(SessionMarker marker)
    {
        IsPlacingMarker = false;
        var matchingRow = SpreadsheetRows.FirstOrDefault(r =>
            r.IsMarker &&
            Math.Abs((r.TimestampUtc - marker.TimestampUtc).TotalMilliseconds) < 10.0 &&
            string.Equals(r.MarkerText, marker.Text, StringComparison.Ordinal));

        if (matchingRow is not null)
        {
            await RemoveMarkerAtRowAsync(matchingRow);
            return;
        }

        SessionMarkers.Remove(marker);
        await _sessionManager.RemoveMarkerAsync(marker.TimestampUtc, marker.Text);

        _logger.LogInformation("Marcador '{Text}' excluído ({Timestamp:HH:mm:ss.fff}).",
            marker.Text, marker.TimestampUtc.ToLocalTime());
    }

    public async Task CommitMarkerAtAsync(DateTime timestampUtc, string? defaultComment = null)
    {
        IsPlacingMarker = false;
        string defaultText = defaultComment ?? $"Marcador #{_markerCounter}";
        var dlg = new MarkerInputDialog(defaultText)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dlg.ShowDialog() != true)
            return;

        string markerText = dlg.MarkerText;
        _markerCounter++;

        var marker = await _sessionManager.AddMarkerAsync(markerText, timestampUtc);
        if (marker is not null)
        {
            SessionMarkers.Add(marker);
        }

        var markerRow = _sessionManager.Snapshot.GetAllRows().LastOrDefault(r => r.IsMarker && r.MarkerText == markerText)
            ?? _sessionManager.Snapshot.InsertMarker(markerText, timestampUtc);

        if (!SpreadsheetRows.Contains(markerRow))
        {
            int insertIdx = SpreadsheetRows.Count;
            for (int i = SpreadsheetRows.Count - 1; i >= 0; i--)
            {
                if (SpreadsheetRows[i].TimestampUtc <= timestampUtc)
                {
                    insertIdx = i + 1;
                    break;
                }
            }
            if (insertIdx >= SpreadsheetRows.Count)
                SpreadsheetRows.Add(markerRow);
            else
                SpreadsheetRows.Insert(insertIdx, markerRow);

            if (SpreadsheetRows.Count > 10_000)
                SpreadsheetRows.RemoveAt(0);
        }

        _logger.LogInformation("Marcador '{Marker}' inserido em {Timestamp:HH:mm:ss.fff}.",
            markerText, timestampUtc.ToLocalTime());
    }

    [RelayCommand]
    private async Task ExportSpreadsheetAsync()
    {
        if (SpreadsheetRows.Count == 0 || MonitoredTags.Count == 0)
        {
            _logger.LogWarning("Não há linhas ou tags na planilha para exportar.");
            return;
        }

        var sfd = new SaveFileDialog
        {
            Title = Loc.Instance["Toolbar_Export"],
            FileName = $"{_project.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx|CSV Largo / Wide (*.csv)|*.csv|CSV Estreito / Narrow (*.csv)|*.csv|Texto Tabulado (*.txt)|*.txt"
        };
        if (sfd.ShowDialog() != true) return;

        try
        {
            var tags = MonitoredTags.Select(t => t.Config).ToList();
            var rows = SpreadsheetRows.ToList();

            switch (sfd.FilterIndex)
            {
                case 1:
                    await Task.Run(() => SpreadsheetExporter.ExportXlsx(sfd.FileName, tags, rows));
                    break;
                case 2:
                    await SpreadsheetExporter.ExportWideCsvAsync(sfd.FileName, tags, rows, ',');
                    break;
                case 3:
                    await SpreadsheetExporter.ExportNarrowCsvAsync(sfd.FileName, tags, rows, ',');
                    break;
                default:
                    await SpreadsheetExporter.ExportWideCsvAsync(sfd.FileName, tags, rows, '\t');
                    break;
            }

            _logger.LogInformation("Planilha exportada ({Rows:N0} linhas) para {Path}", rows.Count, sfd.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao exportar planilha");
        }
    }

    [RelayCommand]
    private void ChartZoomIn()
    {
        FitAllChartRange = false;
        int current = ChartWindowSeconds;
        int next = ChartWindowOptions.Reverse().FirstOrDefault(w => w < current);
        ChartWindowSeconds = next > 0 ? next : Math.Max(1, current / 2);
    }

    [RelayCommand]
    private void ChartZoomOut()
    {
        FitAllChartRange = false;
        int current = ChartWindowSeconds;
        int next = ChartWindowOptions.FirstOrDefault(w => w > current);
        ChartWindowSeconds = next > 0 ? next : Math.Min(7200, current * 2);
    }

    [RelayCommand]
    private void ChartFitAll()
    {
        AutoFollowChart = false;
        ChartTimeOffsetSeconds = 0;
        FitAllChartRange = true;
    }

    [RelayCommand]
    private void ChartPanLeft()
    {
        FitAllChartRange = false;
        AutoFollowChart = false;
        ChartTimeOffsetSeconds += Math.Max(1.0, ChartWindowSeconds * 0.25);
    }

    [RelayCommand]
    private void ChartPanRight()
    {
        FitAllChartRange = false;
        ChartTimeOffsetSeconds = Math.Max(0.0, ChartTimeOffsetSeconds - Math.Max(1.0, ChartWindowSeconds * 0.25));
        if (ChartTimeOffsetSeconds <= 0.01)
        {
            ChartTimeOffsetSeconds = 0;
        }
    }

    [RelayCommand]
    private void ChartJumpToStart()
    {
        if (SpreadsheetRows.Count < 2) return;
        FitAllChartRange = false;
        AutoFollowChart = false;
        double totalSpanSec = Math.Max(0, (SpreadsheetRows[^1].TimestampUtc - SpreadsheetRows[0].TimestampUtc).TotalSeconds);
        ChartTimeOffsetSeconds = Math.Max(0, totalSpanSec - ChartWindowSeconds);
    }

    [RelayCommand]
    private void ChartJumpToLive()
    {
        FitAllChartRange = false;
        ChartTimeOffsetSeconds = 0;
        AutoFollowChart = true;
    }

    [RelayCommand]
    private void SelectAllChartPens()
    {
        foreach (var tag in MonitoredTags)
            tag.IsPlotted = true;
    }

    [RelayCommand]
    private void ClearAllChartPens()
    {
        foreach (var tag in MonitoredTags)
            tag.IsPlotted = false;
    }

    [RelayCommand]
    private static void OpenHelp(string? topicId = null) =>
        HelpService.OpenHelp(topicId);

    [RelayCommand]
    private void ShowAbout() =>
        MessageBox.Show(Loc.Instance.Format("About_Text", AppVersion), Loc.Instance["Menu_About"],
            MessageBoxButton.OK, MessageBoxImage.Information);

    [RelayCommand]
    private static void Exit() => Application.Current.Shutdown();

    private async Task TransitionOnlineStateAsync(bool goOnline)
    {
        try
        {
            if (goOnline)
            {
                // Conecta automaticamente conexões habilitadas ainda desconectadas
                foreach (var conn in Connections.Where(c => !c.IsConnected && c.Config.Enabled))
                {
                    try
                    {
                        await conn.Driver.ConnectAsync();
                        conn.State = conn.Driver.State;
                        if (ReferenceEquals(SelectedConnection, conn) && BrowserRoots.Count == 0)
                        {
                            await ReloadBrowserForConnectionAsync(conn);
                        }
                    }
                    catch (Exception ex)
                    {
                        conn.State = conn.Driver.State;
                        _logger.LogWarning(ex, "Não foi possível conectar '{Name}' ao entrar On-line", conn.Name);
                    }
                }

                BuildProjectFromViewModels();
                string folder = _project.SessionsFolder
                                ?? _settings.Current.DefaultSessionsFolder
                                ?? SettingsService.DefaultSessionsFolder;

                // Preserva o histórico já existente na planilha e no gráfico ao alternar On-line/Off-line
                var drivers = Connections.Select(c => (IDriver)c.Driver).ToList();

                await _sessionManager.StartSessionAsync(_project, drivers, folder, AppVersion, resetSnapshot: false);
                _sessionClock.Restart();
                _logger.LogInformation("Sessão ON-LINE iniciada: {CsvFile}", _sessionManager.CurrentCsvPath);
            }
            else
            {
                string? csvFile = _sessionManager.CurrentCsvPath;
                await _sessionManager.StopSessionAsync();
                _sessionClock.Stop();
                _logger.LogInformation("Sessão OFF-LINE finalizada: {CsvFile}", csvFile);
            }

            SetOnlinePropertySilently(goOnline);
            RefreshStatusAndValues();
            NotifyConnectionStateChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao alternar estado On-line/Off-line");
            SetOnlinePropertySilently(_sessionManager.IsOnline);
            NotifyConnectionStateChanged();
        }
    }

    private void SetOnlinePropertySilently(bool value)
    {
        _suppressOnlineToggle = true;
        IsOnline = value;
        _suppressOnlineToggle = false;
    }

    private void BuildProjectFromViewModels()
    {
        _project.Connections = Connections.Select(c => c.Config).ToList();
        var existingGroups = _project.Groups.ToDictionary(g => g.ConnectionId);
        _project.Groups.Clear();

        var chosenMode = SelectedAcquisitionModeIndex == 1 ? AcquisitionMode.Polling : AcquisitionMode.Subscription;

        foreach (var byConn in MonitoredTags.GroupBy(t => t.Config.ConnectionId))
        {
            if (existingGroups.TryGetValue(byConn.Key, out var existing))
            {
                existing.Tags = byConn.Select(vm => vm.Config).ToList();
                existing.SamplingIntervalMs = SelectedSamplingIntervalMs > 0 ? SelectedSamplingIntervalMs : existing.SamplingIntervalMs;
                existing.PublishingIntervalMs = existing.SamplingIntervalMs;
                existing.Mode = chosenMode;
                _project.Groups.Add(existing);
            }
            else
            {
                _project.Groups.Add(new AcquisitionGroup
                {
                    Name = $"Group_Conn{byConn.Key}",
                    ConnectionId = byConn.Key,
                    Mode = chosenMode,
                    SamplingIntervalMs = SelectedSamplingIntervalMs > 0 ? SelectedSamplingIntervalMs : 100,
                    PublishingIntervalMs = SelectedSamplingIntervalMs > 0 ? SelectedSamplingIntervalMs : 100,
                    QueueSize = 10,
                    Tags = byConn.Select(vm => vm.Config).ToList()
                });
            }
        }
    }

    public async Task LoadProjectFromFileAsync(string filePath)
    {
        await NewProjectAsync();

        _project = await ProjectFileStore.LoadAsync(filePath);
        _currentProjectPath = filePath;

        foreach (var connCfg in _project.Connections)
        {
            _nextConnectionId = Math.Max(_nextConnectionId, connCfg.Id + 1);
            RegisterConnectionViewModel(connCfg);
        }

        foreach (var tagCfg in _project.AllTags)
        {
            _nextTagId = Math.Max(_nextTagId, tagCfg.Id + 1);
            string connName = Connections.FirstOrDefault(c => c.Config.Id == tagCfg.ConnectionId)?.Name ?? $"#{tagCfg.ConnectionId}";
            MonitoredTags.Add(new MonitoredTagViewModel(tagCfg, connName)
            {
                IsPlotted = true
            });
        }

        _logger.LogInformation("Projeto '{Name}' carregado ({ConnCount} conexões, {TagCount} tags).",
            _project.Name, Connections.Count, MonitoredTags.Count);
    }

    private ConnectionItemViewModel RegisterConnectionViewModel(ConnectionConfig cfg)
    {
        var driver = new OpcUaDriver(cfg, _securityManager, _driverLogger);
        var itemVm = new ConnectionItemViewModel(cfg, driver);

        driver.StateChanged += (_, args) => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            itemVm.State = args.State;
            itemVm.StatusMessage = args.Reason;
            RefreshStatusAndValues();
            NotifyConnectionStateChanged();
        });

        Connections.Add(itemVm);
        SelectedConnection ??= itemVm;
        NotifyConnectionStateChanged();
        return itemVm;
    }

    private void OnSessionBatchProcessed(IReadOnlyList<WideSpreadsheetRow> newRows)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            foreach (var r in newRows)
            {
                SpreadsheetRows.Add(r);
                if (SpreadsheetRows.Count > 10_000)
                    SpreadsheetRows.RemoveAt(0);
            }
        }, DispatcherPriority.Background);
    }

    private async Task ReloadBrowserForConnectionAsync(ConnectionItemViewModel? item)
    {
        foreach (var n in SelectedBrowseNodes)
            n.IsMultiSelected = false;
        SelectedBrowseNodes.Clear();
        BrowserRoots.Clear();
        SelectedNodeAttributes = null;
        OnPropertyChanged(nameof(AddSelectedBrowserButtonText));

        if (item is not { IsConnected: true }) return;

        try
        {
            var roots = await item.Driver.BrowseAsync(null);
            foreach (var r in roots)
            {
                BrowserRoots.Add(new BrowseNodeViewModel(r, item.Driver, OnBrowseNodeSelected, OnBrowseNodeError));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao navegar raiz de '{Name}'", item.Name);
        }
    }

    private void OnBrowseNodeError(Exception ex, BrowseNode node)
    {
        _logger.LogError(ex, "Falha ao expandir nó '{Name}' ({NodeId})", node.DisplayName, node.Id);
    }

    public void SelectBrowseNodeFromUi(BrowseNodeViewModel nodeVm, bool ctrlPressed, bool shiftPressed)
    {
        if (ctrlPressed)
        {
            if (nodeVm.IsMultiSelected && SelectedBrowseNodes.Count > 1)
            {
                nodeVm.IsMultiSelected = false;
                SelectedBrowseNodes.Remove(nodeVm);
                SelectedBrowseNode = SelectedBrowseNodes.LastOrDefault();
            }
            else
            {
                nodeVm.IsMultiSelected = true;
                if (!SelectedBrowseNodes.Contains(nodeVm))
                    SelectedBrowseNodes.Add(nodeVm);
                SelectedBrowseNode = nodeVm;
            }
        }
        else if (shiftPressed && SelectedBrowseNode is not null)
        {
            var visibleNodes = new List<BrowseNodeViewModel>();
            CollectVisibleBrowseNodes(BrowserRoots, visibleNodes);

            int startIdx = visibleNodes.IndexOf(SelectedBrowseNode);
            int endIdx = visibleNodes.IndexOf(nodeVm);

            if (startIdx >= 0 && endIdx >= 0)
            {
                int min = Math.Min(startIdx, endIdx);
                int max = Math.Max(startIdx, endIdx);

                foreach (var n in SelectedBrowseNodes.ToList())
                {
                    n.IsMultiSelected = false;
                }
                SelectedBrowseNodes.Clear();

                for (int i = min; i <= max; i++)
                {
                    var n = visibleNodes[i];
                    n.IsMultiSelected = true;
                    SelectedBrowseNodes.Add(n);
                }
                SelectedBrowseNode = nodeVm;
            }
            else
            {
                SetSingleSelectedBrowseNode(nodeVm);
            }
        }
        else
        {
            SetSingleSelectedBrowseNode(nodeVm);
        }

        OnPropertyChanged(nameof(AddSelectedBrowserButtonText));
        if (SelectedBrowseNode is not null)
            _ = LoadNodeAttributesAsync(SelectedBrowseNode);
        else
            SelectedNodeAttributes = null;
    }

    private void SetSingleSelectedBrowseNode(BrowseNodeViewModel nodeVm)
    {
        foreach (var n in SelectedBrowseNodes.ToList())
        {
            n.IsMultiSelected = false;
        }
        SelectedBrowseNodes.Clear();

        nodeVm.IsMultiSelected = true;
        SelectedBrowseNodes.Add(nodeVm);
        SelectedBrowseNode = nodeVm;
    }

    private static void CollectVisibleBrowseNodes(IEnumerable<BrowseNodeViewModel> nodes, List<BrowseNodeViewModel> result)
    {
        foreach (var n in nodes)
        {
            if (n.Node.Id == "__dummy__") continue;
            result.Add(n);
            if (n.IsExpanded && n.Children.Count > 0)
            {
                CollectVisibleBrowseNodes(n.Children, result);
            }
        }
    }

    private void OnBrowseNodeSelected(BrowseNodeViewModel nodeVm)
    {
        if (!nodeVm.IsMultiSelected)
        {
            SetSingleSelectedBrowseNode(nodeVm);
            OnPropertyChanged(nameof(AddSelectedBrowserButtonText));
        }
        SelectedBrowseNode = nodeVm;
        _ = LoadNodeAttributesAsync(nodeVm);
    }

    private async Task LoadNodeAttributesAsync(BrowseNodeViewModel nodeVm)
    {
        try
        {
            SelectedNodeAttributes = await nodeVm.Driver.ReadNodeAttributesAsync(nodeVm.Node.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Não foi possível ler atributos do nó {NodeId}", nodeVm.Node.Id);
        }
    }

    public async Task AddBrowseNodeDoubleClickAsync(BrowseNodeViewModel? nodeVm)
    {
        if (nodeVm is null || !nodeVm.Node.IsVariable) return;
        await AddNodesOrChildrenAsTagsAsync([nodeVm], includeChildrenIfFolder: false);
    }

    private async Task AddNodeOrChildrenAsTagsAsync(BrowseNodeViewModel nodeVm, bool includeChildrenIfFolder) =>
        await AddNodesOrChildrenAsTagsAsync([nodeVm], includeChildrenIfFolder);

    private async Task AddNodesOrChildrenAsTagsAsync(IReadOnlyList<BrowseNodeViewModel> nodes, bool includeChildrenIfFolder)
    {
        if (SelectedConnection is null || nodes.Count == 0) return;
        var conn = SelectedConnection;
        var added = new List<MonitoredTagViewModel>();

        foreach (var nodeVm in nodes)
        {
            if (nodeVm.Node.IsVariable && !includeChildrenIfFolder)
            {
                var vm = TryRegisterTag(conn, nodeVm.Node);
                if (vm is not null) added.Add(vm);
            }
            else
            {
                bool allowSystemFolders = nodeVm.Node.DisplayName.StartsWith('_');
                await CollectFolderVariablesRecursiveAsync(conn, nodeVm, added, currentDepth: 0, maxDepth: 4, allowSystemFolders: allowSystemFolders);
            }
        }

        if (added.Count > 0)
        {
            _logger.LogInformation("{Count} tag(s) adicionada(s) da conexão '{Conn}'.", added.Count, conn.Name);
            await ActivateOrRefreshOnlineAfterAddingTagsAsync(conn, added);
        }
        else
        {
            _logger.LogWarning("Nenhuma tag variável nova encontrada nos nós selecionados.");
        }
    }

    private async Task ActivateOrRefreshOnlineAfterAddingTagsAsync(
        ConnectionItemViewModel conn,
        IReadOnlyList<MonitoredTagViewModel> added)
    {
        if (!conn.IsConnected || added.Count == 0)
            return;

        try
        {
            var samples = await conn.Driver.ReadAsync(added.Select(a => a.Config).ToList());
            DateTime sweepUtc = DateTime.UtcNow;
            var normalizedSamples = new List<Sample>(samples.Count);

            for (int i = 0; i < added.Count && i < samples.Count; i++)
            {
                var s = samples[i];
                added[i].UpdateFromSample(in s);
                normalizedSamples.Add(s with { TimestampUtc = sweepUtc });
            }

            if (!IsOnline)
            {
                // Inicia automaticamente a coleta On-line ao adicionar tags de uma conexão ativa
                await TransitionOnlineStateAsync(true);
            }
            else
            {
                BuildProjectFromViewModels();
                var drivers = Connections.Select(c => (IDriver)c.Driver).ToList();
                await _sessionManager.UpdateGroupsAsync(_project, drivers);

                foreach (var ns in normalizedSamples)
                {
                    _sessionManager.TryPublish(in ns);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha na leitura/ativação inicial das tags adicionadas");
        }
    }

    private async Task RunPeriodicSweepAsync()
    {
        if (_isPeriodicReading || !_sessionManager.IsOnline || !PeriodicSpreadsheetSampling || MonitoredTags.Count == 0)
            return;

        _isPeriodicReading = true;
        try
        {
            DateTime sweepUtc = DateTime.UtcNow;
            foreach (var conn in Connections.Where(c => c.IsConnected))
            {
                var connTags = MonitoredTags
                    .Where(t => t.Config.ConnectionId == conn.Config.Id)
                    .ToList();

                if (connTags.Count == 0)
                    continue;

                var samples = await conn.Driver.ReadAsync(connTags.Select(t => t.Config).ToList());
                for (int i = 0; i < connTags.Count && i < samples.Count; i++)
                {
                    var s = samples[i];
                    connTags[i].UpdateFromSample(in s);
                    var periodicSample = s with { TimestampUtc = sweepUtc, Origin = TimestampOrigin.Local };
                    _sessionManager.TryPublish(in periodicSample);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Aviso na leitura periódica da planilha");
        }
        finally
        {
            _isPeriodicReading = false;
        }
    }

    public async Task ShutdownAsync()
    {
        _statusTimer.Stop();
        _periodicReadTimer.Stop();

        try
        {
            if (_sessionManager.IsOnline)
                await _sessionManager.StopSessionAsync();
        }
        catch { }

        foreach (var conn in Connections.ToList())
        {
            try
            {
                await conn.Driver.DisposeAsync();
            }
            catch { }
        }
    }

    private async Task CollectFolderVariablesRecursiveAsync(
        ConnectionItemViewModel conn,
        BrowseNodeViewModel folderVm,
        List<MonitoredTagViewModel> added,
        int currentDepth,
        int maxDepth,
        bool allowSystemFolders)
    {
        if (currentDepth >= maxDepth || added.Count >= 500)
            return;

        await folderVm.LoadChildrenAsync();
        foreach (var child in folderVm.Children.ToList())
        {
            if (child.Node.Id == "__dummy__")
                continue;

            if (child.Node.IsVariable)
            {
                if (!allowSystemFolders && child.Node.DisplayName.StartsWith('_'))
                    continue;

                var vm = TryRegisterTag(conn, child.Node);
                if (vm is not null)
                    added.Add(vm);
            }
            else if (currentDepth + 1 < maxDepth)
            {
                if (!allowSystemFolders && (child.Node.DisplayName.StartsWith('_') ||
                    string.Equals(child.Node.DisplayName, "Server", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                await CollectFolderVariablesRecursiveAsync(conn, child, added, currentDepth + 1, maxDepth, allowSystemFolders);
            }
        }
    }

    private MonitoredTagViewModel? TryRegisterTag(ConnectionItemViewModel conn, BrowseNode node)
    {
        if (MonitoredTags.Any(t => t.Config.ConnectionId == conn.Config.Id && t.Address == node.Id))
            return null;

        var cfg = new TagConfig
        {
            Id = _nextTagId++,
            ConnectionId = conn.Config.Id,
            Address = node.Id,
            Alias = node.DisplayName,
            DataType = node.DataType
        };

        var vm = new MonitoredTagViewModel(cfg, conn.Name)
        {
            IsPlotted = true
        };
        MonitoredTags.Add(vm);
        return vm;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(OnlineButtonText));
        OnPropertyChanged(nameof(OnlineStateText));
        RefreshStatusAndValues();
    }

    private void RefreshStatusAndValues()
    {
        var loc = Loc.Instance;
        var stats = _sessionManager.GetStats();

        if (_sessionManager.IsOnline)
        {
            var latest = _sessionManager.Snapshot.GetLatestSamples();
            foreach (var tagVm in MonitoredTags)
            {
                if (latest.TryGetValue(tagVm.Config.Id, out var s))
                    tagVm.UpdateFromSample(in s);
            }
        }

        SessionTimeText = loc.Format("Status_SessionTime", _sessionClock.Elapsed.ToString(@"hh\:mm\:ss"));
        EventsPerSecondText = loc.Format("Status_EventsPerSecond", stats.EventsPerSecond);
        BufferText = loc.Format("Status_Buffer", stats.BufferUsagePercent);
        int active = Connections.Count(c => c.IsConnected);
        ConnectionsText = loc.Format("Status_Connections", active, Connections.Count);
    }
}
