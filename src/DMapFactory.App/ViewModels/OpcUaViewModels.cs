using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DMapFactory.Core.Models;
using DMapFactory.Drivers.OpcUa;

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
    public string ConnectActionText => IsConnected ? "Desconectar" : "Conectar";
    public string ConnectActionToolTip => IsConnected
        ? "Desconectar deste servidor OPC UA"
        : "Conectar a este servidor OPC UA";

    partial void OnStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsConnected));
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
        Node = new BrowseNode("__dummy__", "Carregando…", false, false);
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
                return Node.DisplayName;

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
    }

    public TagConfig Config { get; }
    public string ConnectionName { get; }
    public string Address => Config.Address;

    [ObservableProperty] private string _alias;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBoolean))]
    private string _dataType;
    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private QualityClass _quality = QualityClass.Good;
    [ObservableProperty] private DateTime? _timestampUtc;
    [ObservableProperty] private TimestampOrigin _origin = TimestampOrigin.Source;
    [ObservableProperty] private bool _isPlotted = true;
    [ObservableProperty] private Color _penColor;
    [ObservableProperty] private Brush _penBrush;
    [ObservableProperty] private string _minValueText = "—";
    [ObservableProperty] private string _maxValueText = "—";

    public bool IsBoolean =>
        string.Equals(DataType, "Boolean", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(DataType, "Bool", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(DataType, "Bit", StringComparison.OrdinalIgnoreCase);

    partial void OnAliasChanged(string value) => Config.Alias = value;

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

    public ChartInspectorRowViewModel(string cursorLabel, Brush accentBrush)
    {
        CursorLabel = cursorLabel;
        AccentBrush = accentBrush;
    }

    public string CursorLabel { get; }
    public Brush AccentBrush { get; }
    public string TimestampText { get; private set; } = "—";

    public string GetTagValueText(int tagId) =>
        _valuesByTagId.TryGetValue(tagId, out var val) ? val : "—";

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
        OnPropertyChanged(string.Empty);
    }
}


