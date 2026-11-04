namespace DMapFactory.Core.Models;

/// <summary>
/// Uma linha da planilha em formato largo (RF-GRD-01/02/03/10):
/// contém o timestamp da mudança, o snapshot do valor atual de todas as tags naquele instante,
/// o conjunto de tags que mudaram naquela linha e o número do ciclo de máquina ativo.
/// </summary>
public sealed class WideSpreadsheetRow : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public required long RowNumber { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required IReadOnlyDictionary<int, object?> ValuesByTagId { get; init; }
    public required IReadOnlySet<int> ChangedTagIds { get; init; }
    public int? CycleNumber { get; init; }
    public bool IsCycleStart { get; init; }
    public TimeSpan? DeltaTime { get; set; }
    public string? MarkerText { get; set; }
    public bool IsMarker => !string.IsNullOrEmpty(MarkerText);

    public void SetMarker(string? text)
    {
        MarkerText = text;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MarkerText)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsMarker)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MarkerDisplay)));
    }

    /// <summary>Texto exibido na coluna Cycle: diferença de tempo (sem data) para a linha anterior.</summary>
    public string CycleDisplay
    {
        get
        {
            if (!DeltaTime.HasValue)
                return "00:00:00.000";

            var d = DeltaTime.Value;
            if (d < TimeSpan.Zero) d = TimeSpan.Zero;
            return $"+{d:hh\\:mm\\:ss\\.fff}";
        }
    }

    /// <summary>Texto exibido na coluna exclusiva de Marcador.</summary>
    public string MarkerDisplay => IsMarker ? $"🚩 {MarkerText}" : "";

    /// <summary>Indexador por TagId para ligação direta das colunas dinâmicas no WPF DataGrid.</summary>
    public string this[int tagId] => ValuesByTagId.TryGetValue(tagId, out var val) ? FormatCell(val) : "";

    /// <summary>Lista textual dos aliases/IDs alterados na linha.</summary>
    public string ChangedCountText => ChangedTagIds.Count.ToString();

    public bool IsTagChanged(int tagId) => ChangedTagIds.Contains(tagId);

    public static string FormatCell(object? value) => value switch
    {
        null => "",
        bool b => b ? "1" : "0",
        double d => d.ToString("G6", System.Globalization.CultureInfo.InvariantCulture),
        float f => f.ToString("G6", System.Globalization.CultureInfo.InvariantCulture),
        Array arr => $"[{string.Join(", ", arr.Cast<object?>().Take(8).Select(FormatCell))}]",
        IFormattable fmt => fmt.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
