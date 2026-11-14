using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DMapFactory.App.ViewModels;
using DMapFactory.Core.Models;
using DMapFactory.Localization;

namespace DMapFactory.App.Views;

public partial class FindMarkerDialog : Window
{
    private readonly MainWindow _mainWindow;
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<MarkerListItem> _filteredMarkers = [];

    public sealed record MarkerListItem(SessionMarker Marker, long? RowNumber)
    {
        public DateTime TimestampUtc => Marker.TimestampUtc;
        public string TimestampDisplay => TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        public string RowDisplay => RowNumber.HasValue ? RowNumber.Value.ToString() : "—";
        public string Text => Marker.Text;
    }

    public FindMarkerDialog(MainWindow mainWindow, MainViewModel vm)
    {
        InitializeComponent();
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));

        ColTimestamp.Header = Loc.Instance["FindMarker_Col_Timestamp"];
        ColComment.Header = Loc.Instance["FindMarker_Col_Comment"];
        MarkersGrid.ItemsSource = _filteredMarkers;

        Loaded += (_, _) =>
        {
            ReloadMarkers();
            SearchTextBox.Focus();
        };

        _vm.SessionMarkers.CollectionChanged += (_, _) => ReloadMarkers();
    }

    public void FocusSearchInput()
    {
        SearchTextBox.Focus();
        SearchTextBox.SelectAll();
    }

    public void ReloadMarkers()
    {
        string query = SearchTextBox.Text?.Trim() ?? string.Empty;
        var selectedItem = MarkersGrid.SelectedItem as MarkerListItem;

        _filteredMarkers.Clear();

        var rows = _vm.SpreadsheetRows;
        foreach (var m in _vm.SessionMarkers)
        {
            if (!string.IsNullOrEmpty(query) &&
                !m.Text.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !m.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long? rowNum = null;
            var matchingRow = rows.FirstOrDefault(r =>
                r.IsMarker &&
                Math.Abs((r.TimestampUtc - m.TimestampUtc).TotalMilliseconds) < 15.0);
            if (matchingRow is not null)
            {
                rowNum = matchingRow.RowNumber;
            }

            _filteredMarkers.Add(new MarkerListItem(m, rowNum));
        }

        StatusTextBlock.Text = Loc.Instance.Format("FindMarker_StatusFormat", _filteredMarkers.Count, _vm.SessionMarkers.Count);

        if (selectedItem is not null)
        {
            var keep = _filteredMarkers.FirstOrDefault(x =>
                Math.Abs((x.TimestampUtc - selectedItem.TimestampUtc).TotalMilliseconds) < 1.0 &&
                x.Text == selectedItem.Text);
            if (keep is not null)
                MarkersGrid.SelectedItem = keep;
        }

        if (MarkersGrid.SelectedItem is null && _filteredMarkers.Count > 0)
        {
            MarkersGrid.SelectedIndex = 0;
        }

        UpdateButtonsState();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ReloadMarkers();
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        SearchTextBox.Focus();
    }

    private void MarkersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtonsState();
    }

    private void UpdateButtonsState()
    {
        bool hasSelection = MarkersGrid.SelectedItem is MarkerListItem;
        FocusChartButton.IsEnabled = hasSelection;
        LocateSpreadsheetButton.IsEnabled = hasSelection;
        EditMarkerButton.IsEnabled = hasSelection;
        DeleteMarkerButton.IsEnabled = hasSelection;
    }

    private void MarkersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MarkersGrid.SelectedItem is MarkerListItem item)
        {
            _mainWindow.FocusTimestampOnChartWithCursorA(item.TimestampUtc, switchTab: true);
        }
    }

    private void FocusChartButton_Click(object sender, RoutedEventArgs e)
    {
        if (MarkersGrid.SelectedItem is MarkerListItem item)
        {
            _mainWindow.FocusTimestampOnChartWithCursorA(item.TimestampUtc, switchTab: true);
        }
    }

    private void LocateSpreadsheetButton_Click(object sender, RoutedEventArgs e)
    {
        if (MarkersGrid.SelectedItem is MarkerListItem item)
        {
            _mainWindow.HighlightSpreadsheetRowNearTimestamp(item.TimestampUtc);
            _mainWindow.SetActiveTabIndex(0);
        }
    }

    private async void EditMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (MarkersGrid.SelectedItem is MarkerListItem item)
        {
            await _vm.EditSessionMarkerAsync(item.Marker);
            ReloadMarkers();
        }
    }

    private async void DeleteMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (MarkersGrid.SelectedItem is MarkerListItem item)
        {
            var res = MessageBox.Show(
                this,
                Loc.Instance.Format("FindMarker_DeleteConfirmMsg", item.Text, item.TimestampDisplay),
                Loc.Instance["FindMarker_DeleteConfirmTitle"],
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                await _vm.RemoveSessionMarkerAsync(item.Marker);
                ReloadMarkers();
            }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
