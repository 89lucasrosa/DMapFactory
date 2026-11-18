using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DMapFactory.Core.Models;
using DMapFactory.Drivers.OpcUa;
using DMapFactory.Localization;

namespace DMapFactory.App.ViewModels;

/// <summary>Representa uma conexão configurada na lista lateral, com estado ao vivo e driver associado.</summary>
public sealed partial class ConnectionItemViewModel : ObservableObject
{
    public ConnectionItemViewModel(ConnectionConfig config, OpcUaDriver driver)
    {
        Config = config;
        Driver = driver;
        _name = config.Name;
        _endpointUrl = config.EndpointUrl;
        _state = driver.State;
        _securitySummary = $"{OpcUaSecurityManager.GetPolicyShortName(config.SecurityPolicyUri)}/{config.SecurityMode}";
    }

    public ConnectionConfig Config { get; private set; }
    public OpcUaDriver Driver { get; private set; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _endpointUrl;
    [ObservableProperty] private string _securitySummary;
    [ObservableProperty] private ConnectionState _state;
    [ObservableProperty] private string? _statusMessage;

    public bool IsConnected => Driver.State == ConnectionState.Connected || State == ConnectionState.Connected;
    public string ConnectActionText => IsConnected ? Loc.Instance["Toolbar_Disconnect"] : Loc.Instance["Toolbar_Connect"];
    public string ConnectActionToolTip => IsConnected
        ? Loc.Instance["Toolbar_Disconnect_Tip"]
        : Loc.Instance["Toolbar_Connect_Tip"];

    partial void OnStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectActionText));
        OnPropertyChanged(nameof(ConnectActionToolTip));
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(ConnectActionText));
        OnPropertyChanged(nameof(ConnectActionToolTip));
    }

    public void Update(ConnectionConfig newConfig, OpcUaDriver newDriver)
    {
        Config = newConfig;
        Driver = newDriver;
        Name = newConfig.Name;
        EndpointUrl = newConfig.EndpointUrl;
        State = newDriver.State;
        SecuritySummary = $"{OpcUaSecurityManager.GetPolicyShortName(newConfig.SecurityPolicyUri)}/{newConfig.SecurityMode}";
        OnPropertyChanged(nameof(Config));
        OnPropertyChanged(nameof(Driver));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectActionText));
        OnPropertyChanged(nameof(ConnectActionToolTip));
    }
}

/// <summary>Nó da árvore de navegação OPC UA com carregamento sob demanda (lazy loading — RF-TAG-01).</summary>
public sealed partial class BrowseNodeViewModel : ObservableObject
{
    private readonly OpcUaDriver _driver;
    private readonly Action<BrowseNodeViewModel> _onSelected;
    private readonly Action<Exception, BrowseNode>? _onError;
    private bool _loaded;

    public BrowseNodeViewModel(
        BrowseNode node,
        OpcUaDriver driver,
        Action<BrowseNodeViewModel> onSelected,
        Action<Exception, BrowseNode>? onError = null)
    {
        Node = node;
        _driver = driver;
        _onSelected = onSelected;
        _onError = onError;

        if (node.HasChildren)
        {
            // Item sentinela para exibir a seta de expansão antes de consultar o servidor
            Children.Add(CreateDummy(driver, onSelected));
        }
    }

    private BrowseNodeViewModel(OpcUaDriver driver, Action<BrowseNodeViewModel> onSelected)
    {
        Node = new BrowseNode("__dummy__", Loc.Instance["Browser_Loading"], false, false);
        _driver = driver;
        _onSelected = onSelected;
        _loaded = true;
    }

    public BrowseNode Node { get; }
    public OpcUaDriver Driver => _driver;

    public string DisplayText
    {
        get
        {
            if (Node.Id == "__dummy__")
                return Loc.Instance["Browser_Loading"];

            string prefix = Node.IsVariable ? "🏷 " : "📁 ";
            return string.IsNullOrEmpty(Node.DataType)
                ? $"{prefix}{Node.DisplayName}"
                : $"{prefix}{Node.DisplayName}  ({Node.DataType})";
        }
    }

    public ObservableCollection<BrowseNodeViewModel> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isMultiSelected;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
        {
            _ = LoadChildrenAsync();
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && Node.Id != "__dummy__")
        {
            _onSelected(this);
        }
    }

    public async Task LoadChildrenAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var items = await _driver.BrowseAsync(Node.Id);
            Children.Clear();
            foreach (var item in items)
            {
                Children.Add(new BrowseNodeViewModel(item, _driver, _onSelected, _onError));
            }
        }
        catch (Exception ex)
        {
            _loaded = false;
            Children.Clear();
            Children.Add(CreateDummy(_driver, _onSelected));
            _onError?.Invoke(ex, Node);
        }
    }

    private static BrowseNodeViewModel CreateDummy(OpcUaDriver driver, Action<BrowseNodeViewModel> onSelected) =>
        new(driver, onSelected);
}

/// <summary>Linha de tag monitorada na aba "Tags e Valores Atuais" e seletor de penas do Gráfico (RF-TAG-04 / RF-GRD-05).</summary>
public sealed partial class MonitoredTagViewModel : ObservableObject
{
    public static readonly Color[] DefaultPalette =
    [
        (Color)ColorConverter.ConvertFromString("#2B6CB0"),
        (Color)ColorConverter.ConvertFromString("#2F855A"),
        (Color)ColorConverter.ConvertFromString("#C05621"),
        (Color)ColorConverter.ConvertFromString("#805AD5"),
        (Color)ColorConverter.ConvertFromString("#00838F"),
        (Color)ColorConverter.ConvertFromString("#C53030"),
        (Color)ColorConverter.ConvertFromString("#B7791F"),
        (Color)ColorConverter.ConvertFromString("#4A5568"),
        (Color)ColorConverter.ConvertFromString("#319795"),
        (Color)ColorConverter.ConvertFromString("#D53F8C"),
        (Color)ColorConverter.ConvertFromString("#38A169"),
        (Color)ColorConverter.ConvertFromString("#DD6B20")
    ];

    public MonitoredTagViewModel(TagConfig config, string connectionName)
    {
        Config = config;
        ConnectionName = connectionName;
        _alias = config.Alias;
        _dataType = config.DataType ?? "";
        _penColor = DefaultPalette[Math.Abs(config.Id - 1) % DefaultPalette.Length];
        var brush = new SolidColorBrush(_penColor);
        brush.Freeze();
        _penBrush = brush;
        _isPlotted = CanPlotOnChart;
    }

    public TagConfig Config { get; }
    public string ConnectionName { get; }
    public string Address => Config.Address;
    public Action<string, string>? AliasRenamedCallback { get; set; }

    [ObservableProperty] private string _alias;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBoolean))]
    [NotifyPropertyChangedFor(nameof(IsString))]
    [NotifyPropertyChangedFor(nameof(CanPlotOnChart))]
    [NotifyPropertyChangedFor(nameof(PlotToolTip))]
    private string _dataType;
    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private QualityClass _quality = QualityClass.Good;
    [ObservableProperty] private DateTime? _timestampUtc;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginText))]
    private TimestampOrigin _origin = TimestampOrigin.Source;
    [ObservableProperty] private bool _isPlotted = true;
    [ObservableProperty] private Color _penColor;
    [ObservableProperty] private Brush _penBrush;
    [ObservableProperty] private string _minValueText = "—";
    [ObservableProperty] private string _maxValueText = "—";

    public string OriginText => Origin switch
    {
        TimestampOrigin.Source => Loc.Instance["Origin_Source"],
        TimestampOrigin.Server => Loc.Instance["Origin_Server"],
        _ => Loc.Instance["Origin_Local"]
    };

    public bool IsBoolean =>
        string.Equals(DataType, "Boolean", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(DataType, "Bool", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(DataType, "Bit", StringComparison.OrdinalIgnoreCase);

    public bool IsString =>
        string.Equals(DataType, "String", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(DataType, "ByteString", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(DataType, "Char", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(DataType, "LocalizedText", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(DataType, "QualifiedName", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(DataType, "XmlElement", StringComparison.OrdinalIgnoreCase);

    public bool CanPlotOnChart => !IsString;

    public string PlotToolTip => CanPlotOnChart
        ? Loc.Instance["Chart_PenPlot_Tip_Allowed"]
        : Loc.Instance["Chart_PenPlot_Tip_String"];

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(OriginText));
        OnPropertyChanged(nameof(PlotToolTip));
    }

    partial void OnDataTypeChanged(string value)
    {
        if (!CanPlotOnChart && IsPlotted)
        {
            IsPlotted = false;
        }
    }

    partial void OnIsPlottedChanged(bool value)
    {
        if (value && !CanPlotOnChart)
        {
            _isPlotted = false;
            OnPropertyChanged(nameof(IsPlotted));
        }
    }

    partial void OnAliasChanged(string? oldValue, string newValue)
    {
        string prev = oldValue ?? Config.Alias;
        Config.Alias = newValue;
        if (!string.Equals(prev, newValue, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(newValue))
        {
            AliasRenamedCallback?.Invoke(prev, newValue);
        }
    }

    public void UpdateFromSample(in Sample sample)
    {
        if (sample.Value is bool && !IsBoolean)
        {
            DataType = "Boolean";
            Config.DataType = "Boolean";
        }
        else if (sample.Value is not null && string.IsNullOrWhiteSpace(DataType))
        {
            DataType = sample.Value.GetType().Name;
            Config.DataType = DataType;
        }

        ValueText = FormatValue(sample.Value);
        Quality = sample.Quality;
        TimestampUtc = sample.TimestampUtc;
        Origin = sample.Origin;
    }

    public static string FormatValue(object? value) => value switch
    {
        null => "null",
        bool b => b ? "1" : "0",
        double d => d.ToString("G6"),
        float f => f.ToString("G6"),
        Array arr => $"[{arr.Length}] {string.Join(", ", arr.Cast<object?>().Take(6))}",
        _ => value.ToString() ?? ""
    };
}

/// <summary>Linha da tabela inspetora abaixo do gráfico correlacionando Cursor A, Cursor B e Mira (Crosshair) com o Timestamp e os valores das penas selecionadas.</summary>
public sealed partial class ChartInspectorRowViewModel : ObservableObject
{
    private readonly Dictionary<int, string> _valuesByTagId = [];
    private readonly string _labelResourceKey;

    public ChartInspectorRowViewModel(string labelResourceKey, Brush accentBrush)
    {
        _labelResourceKey = labelResourceKey;
        AccentBrush = accentBrush;
    }

    public string CursorLabel => Loc.Instance[_labelResourceKey];
    public Brush AccentBrush { get; }
    public string TimestampText { get; private set; } = "—";

    public void RefreshLocalization() => OnPropertyChanged(nameof(CursorLabel));

    public string GetTagValueText(int tagId) =>
        _valuesByTagId.TryGetValue(tagId, out var val) ? val : "—";

    public string this[int tagId] => GetTagValueText(tagId);

    public void UpdateReadout(string timestampText, IReadOnlyDictionary<int, string> valuesByTagId)
    {
        bool changed = !string.Equals(TimestampText, timestampText, StringComparison.Ordinal);
        TimestampText = timestampText;

        if (!changed && _valuesByTagId.Count == valuesByTagId.Count)
        {
            foreach (var kv in valuesByTagId)
            {
                if (!_valuesByTagId.TryGetValue(kv.Key, out var prev) || !string.Equals(prev, kv.Value, StringComparison.Ordinal))
                {
                    changed = true;
                    break;
                }
            }
        }
        else
        {
            changed = true;
        }

        if (!changed)
            return;

        _valuesByTagId.Clear();
        foreach (var kv in valuesByTagId)
        {
            _valuesByTagId[kv.Key] = kv.Value;
        }

        OnPropertyChanged(nameof(TimestampText));
        OnPropertyChanged(System.Windows.Data.Binding.IndexerName);
        OnPropertyChanged(string.Empty);
    }
}


