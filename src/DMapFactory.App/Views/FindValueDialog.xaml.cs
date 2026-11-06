using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DMapFactory.App.ViewModels;
using DMapFactory.Core.Models;

namespace DMapFactory.App.Views;

public partial class FindValueDialog : Window
{
    private readonly MainWindow _mainWindow;
    private readonly MainViewModel _vm;

    private static readonly SolidColorBrush StatusNormalBrush = CreateFrozenBrush("#3C4046");
    private static readonly SolidColorBrush StatusSuccessBrush = CreateFrozenBrush("#2F855A");
    private static readonly SolidColorBrush StatusWarningBrush = CreateFrozenBrush("#C05621");
    private static readonly SolidColorBrush StatusErrorBrush = CreateFrozenBrush("#C53030");

    public FindValueDialog(MainWindow mainWindow, MainViewModel vm)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _vm = vm;

        TagComboBox.ItemsSource = _vm.MonitoredTags;

        if (_vm.SelectedMonitoredTag is not null && _vm.MonitoredTags.Contains(_vm.SelectedMonitoredTag))
        {
            TagComboBox.SelectedItem = _vm.SelectedMonitoredTag;
        }
        else if (_vm.MonitoredTags.Count > 0)
        {
            TagComboBox.SelectedIndex = 0;
        }

        UpdateViewSwitchButton();

        Loaded += (_, _) =>
        {
            SearchValueTextBox.Focus();
            SearchValueTextBox.SelectAll();
        };

        PreviewKeyDown += FindValueDialog_PreviewKeyDown;
    }

    public void FocusSearchInput()
    {
        SearchValueTextBox.Focus();
        SearchValueTextBox.SelectAll();
    }

    private void FindValueDialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F3)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            PerformSearch(shift ? SearchDirection.Previous : SearchDirection.Next);
            e.Handled = true;
        }
    }

    private void SearchValueTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            PerformSearch(shift ? SearchDirection.Previous : SearchDirection.Next);
            e.Handled = true;
        }
    }

    private void FindNextButton_Click(object sender, RoutedEventArgs e)
    {
        PerformSearch(SearchDirection.Next);
    }

    private void FindPrevButton_Click(object sender, RoutedEventArgs e)
    {
        PerformSearch(SearchDirection.Previous);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    public void UpdateViewSwitchButton()
    {
        if (_mainWindow.GetActiveTabIndex() == 1)
        {
            ViewSwitchButton.Content = "📋 Ver na Planilha";
            ViewSwitchButton.ToolTip = "Alterna a visualização para a Planilha";
        }
        else
        {
            ViewSwitchButton.Content = "📈 Ver no Gráfico";
            ViewSwitchButton.ToolTip = "Alterna a visualização para o Gráfico de Tendências";
        }
    }

    private void ViewSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow.GetActiveTabIndex() == 1)
            _mainWindow.SetActiveTabIndex(0);
        else
            _mainWindow.SetActiveTabIndex(1);

        UpdateViewSwitchButton();
    }

    private enum SearchDirection
    {
        Next,
        Previous
    }

    private void PerformSearch(SearchDirection direction)
    {
        UpdateViewSwitchButton();

        var rows = _vm.SpreadsheetRows;
        if (rows.Count == 0)
        {
            SetStatus("Nenhum registro na planilha para pesquisar.", StatusWarningBrush);
            return;
        }

        if (TagComboBox.SelectedItem is not MonitoredTagViewModel selectedTag)
        {
            SetStatus("Selecione uma tag no menu suspenso para pesquisar.", StatusWarningBrush);
            return;
        }

        string searchText = SearchValueTextBox.Text.Trim();
        if (string.IsNullOrEmpty(searchText))
        {
            SetStatus("Digite o valor a ser localizado.", StatusWarningBrush);
            return;
        }

        bool exactMatch = ExactMatchCheckBox.IsChecked == true;
        bool caseSensitive = CaseSensitiveCheckBox.IsChecked == true;
        bool onlyTransitions = OnlyTransitionsCheckBox.IsChecked == true;
        int tagId = selectedTag.Config.Id;

        int currentIdx = _mainWindow.GetSpreadsheetSelectedIndex();
        if (currentIdx < 0 && _mainWindow.GetActiveTabIndex() == 1)
        {
            var cursorATime = _mainWindow.GetChartCursorATimestamp();
            if (cursorATime.HasValue)
            {
                currentIdx = _mainWindow.FindRowIndexNearTimestamp(cursorATime.Value);
            }
        }

        int totalCount = rows.Count;
        int foundIdx = -1;
        bool wrapped = false;

        if (direction == SearchDirection.Next)
        {
            int start = currentIdx >= 0 ? currentIdx + 1 : 0;
            for (int i = start; i < totalCount; i++)
            {
                var prev = i > 0 ? rows[i - 1] : null;
                if (MatchesTagValue(rows[i], tagId, searchText, exactMatch, caseSensitive, onlyTransitions, prev))
                {
                    foundIdx = i;
                    break;
                }
            }

            if (foundIdx == -1 && start > 0)
            {
                wrapped = true;
                for (int i = 0; i <= Math.Min(currentIdx, totalCount - 1); i++)
                {
                    var prev = i > 0 ? rows[i - 1] : null;
                    if (MatchesTagValue(rows[i], tagId, searchText, exactMatch, caseSensitive, onlyTransitions, prev))
                    {
                        foundIdx = i;
                        break;
                    }
                }
            }
        }
        else // SearchDirection.Previous
        {
            int start = currentIdx >= 0 ? currentIdx - 1 : totalCount - 1;
            for (int i = start; i >= 0; i--)
            {
                var prev = i > 0 ? rows[i - 1] : null;
                if (MatchesTagValue(rows[i], tagId, searchText, exactMatch, caseSensitive, onlyTransitions, prev))
                {
                    foundIdx = i;
                    break;
                }
            }

            if (foundIdx == -1 && start < totalCount - 1)
            {
                wrapped = true;
                for (int i = totalCount - 1; i >= Math.Max(0, currentIdx); i--)
                {
                    var prev = i > 0 ? rows[i - 1] : null;
                    if (MatchesTagValue(rows[i], tagId, searchText, exactMatch, caseSensitive, onlyTransitions, prev))
                    {
                        foundIdx = i;
                        break;
                    }
                }
            }
        }

        if (foundIdx >= 0)
        {
            var matchingRow = rows[foundIdx];
            _mainWindow.NavigateToSpreadsheetRow(matchingRow, selectedTag);

            string timeStr = matchingRow.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
            string valStr = matchingRow[tagId];

            if (wrapped && foundIdx == currentIdx)
            {
                SetStatus($"✓ Única ocorrência encontrada na linha #{matchingRow.RowNumber} ({timeStr}) com valor '{valStr}'", StatusSuccessBrush);
            }
            else if (wrapped)
            {
                string edge = direction == SearchDirection.Next ? "Fim alcançado (reiniciado do início)" : "Início alcançado (reiniciado do fim)";
                SetStatus($"✓ {edge}: linha #{matchingRow.RowNumber} ({timeStr}) com valor '{valStr}'", StatusSuccessBrush);
            }
            else
            {
                SetStatus($"✓ Encontrado na linha #{matchingRow.RowNumber} ({timeStr}) com valor '{valStr}'", StatusSuccessBrush);
            }
        }
        else
        {
            SetStatus($"⚠ Valor '{searchText}' não encontrado para '{selectedTag.Alias}'.", StatusWarningBrush);
        }
    }

    private static bool MatchesTagValue(
        WideSpreadsheetRow row,
        int tagId,
        string targetText,
        bool exactMatch,
        bool caseSensitive,
        bool onlyTransitions,
        WideSpreadsheetRow? previousRow)
    {
        if (!row.ValuesByTagId.TryGetValue(tagId, out var rawVal) || rawVal is null)
            return false;

        if (onlyTransitions && previousRow is not null)
        {
            bool changed = row.IsTagChanged(tagId);
            if (!changed && previousRow.ValuesByTagId.TryGetValue(tagId, out var prevVal))
            {
                if (AreValuesEqual(rawVal, prevVal))
                    return false;
            }
            else if (!changed)
            {
                return false;
            }
        }

        string cellText = WideSpreadsheetRow.FormatCell(rawVal);
        string target = targetText.Trim();

        // 1. Text match
        var comp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (exactMatch)
        {
            if (string.Equals(cellText, target, comp))
                return true;
        }
        else
        {
            if (cellText.Contains(target, comp))
                return true;
        }

        // 2. Boolean match
        if (rawVal is bool b)
        {
            if (bool.TryParse(target, out bool targetBool) && b == targetBool)
                return true;

            if (target == "1" && b) return true;
            if (target == "0" && !b) return true;

            if ((target.Equals("sim", StringComparison.OrdinalIgnoreCase) ||
                 target.Equals("verdadeiro", StringComparison.OrdinalIgnoreCase) ||
                 target.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                 target.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                 target.Equals("ativo", StringComparison.OrdinalIgnoreCase)) && b)
                return true;

            if ((target.Equals("nao", StringComparison.OrdinalIgnoreCase) ||
                 target.Equals("não", StringComparison.OrdinalIgnoreCase) ||
                 target.Equals("falso", StringComparison.OrdinalIgnoreCase) ||
                 target.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                 target.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                 target.Equals("inativo", StringComparison.OrdinalIgnoreCase)) && !b)
                return true;
        }

        // 3. Numeric match
        if (TryGetNumericValue(rawVal, cellText, out double valNum) &&
            TryParseNumeric(target, out double targetNum))
        {
            if (exactMatch)
            {
                if (Math.Abs(valNum - targetNum) < 1e-6)
                    return true;
            }
            else
            {
                if (Math.Abs(valNum - targetNum) < 1e-6 || cellText.Contains(target, comp))
                    return true;
            }
        }

        return false;
    }

    private static bool TryParseNumeric(string text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            return true;
        if (double.TryParse(text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            return true;
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
            return true;
        value = 0;
        return false;
    }

    private static bool TryGetNumericValue(object? rawVal, string cellText, out double value)
    {
        if (rawVal is double d) { value = d; return true; }
        if (rawVal is float f) { value = f; return true; }
        if (rawVal is int i) { value = i; return true; }
        if (rawVal is long l) { value = l; return true; }
        if (rawVal is short s) { value = s; return true; }
        if (rawVal is byte b) { value = b; return true; }
        if (rawVal is decimal dec) { value = (double)dec; return true; }
        return TryParseNumeric(cellText, out value);
    }

    private static bool AreValuesEqual(object? v1, object? v2)
    {
        if (Equals(v1, v2)) return true;
        if (v1 is null || v2 is null) return false;
        if (TryGetNumericValue(v1, "", out double d1) && TryGetNumericValue(v2, "", out double d2))
            return Math.Abs(d1 - d2) < 1e-9;
        return string.Equals(WideSpreadsheetRow.FormatCell(v1), WideSpreadsheetRow.FormatCell(v2), StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string message, Brush brush)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = brush;
    }

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
