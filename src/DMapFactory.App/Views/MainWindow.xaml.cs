using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DMapFactory.App.Controls;
using DMapFactory.App.Services;
using DMapFactory.App.ViewModels;
using DMapFactory.Core.Models;
using Microsoft.Win32;

namespace DMapFactory.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _uiRefreshTimer;
    private readonly List<TextBlock> _verticalHeaderTextBlocks = [];
    private readonly IReadOnlyList<ChartInspectorRowViewModel> _chartInspectorRows =
    [
        new ChartInspectorRowViewModel("Cursor A", CreateFrozenBrush("#2B6CB0")),
        new ChartInspectorRowViewModel("Cursor B", CreateFrozenBrush("#C53030")),
        new ChartInspectorRowViewModel("Mira", CreateFrozenBrush("#68707A"))
    ];
    private long _lastRenderedRowNumber = -1;
    private int _lastRenderedRowCount = -1;

    private bool _isResizingHeaderHeight;
    private double _headerResizeStartY;
    private double _headerResizeStartHeight;

    private static readonly SolidColorBrush BoolTrueBrush = CreateFrozenBrush("#3E9B60");
    private static readonly SolidColorBrush BoolTrueChangedBrush = CreateFrozenBrush("#2B7D49");
    private static readonly SolidColorBrush BoolFalseBrush = CreateFrozenBrush("#DCE2EA");
    private static readonly SolidColorBrush BoolFalseChangedBrush = CreateFrozenBrush("#C7D0DC");

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        ChartInspectorGrid.ItemsSource = _chartInspectorRows;

        TrendPlot.CursorDeltaChanged += text => _vm.CursorDeltaText = text;
        TrendPlot.ReadoutsChanged += RefreshChartInspectorValues;
        TrendPlot.IsVisibleChanged += (_, _) =>
        {
            if (TrendPlot.IsVisible)
                RenderChart(force: true);
        };
        TrendPlot.SizeChanged += (_, _) => RenderChart(force: true);
        TrendPlot.Loaded += (_, _) => RenderChart(force: true);
        TrendPlot.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (_vm.IsPlacingMarker)
            {
                Point pt = e.GetPosition(TrendPlot);
                var ts = TrendPlot.GetTimestampAtPixel(pt);
                if (ts.HasValue)
                {
                    e.Handled = true;
                    _ = _vm.CommitMarkerAtAsync(ts.Value);
                }
            }
        };
        TrendPlot.MouseWheelZoomRequested += dir =>
        {
            if (dir > 0)
                _vm.ChartZoomInCommand.Execute(null);
            else
                _vm.ChartZoomOutCommand.Execute(null);
        };
        TrendPlot.HorizontalPanRequested += deltaRatio =>
        {
            _vm.FitAllChartRange = false;
            _vm.AutoFollowChart = false;
            double deltaSec = deltaRatio * _vm.ChartWindowSeconds;
            _vm.ChartTimeOffsetSeconds = Math.Max(0, _vm.ChartTimeOffsetSeconds - deltaSec);
        };
        TrendPlot.PlotRightClicked += OnTrendPlotRightClicked;

        SpreadsheetGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(SpreadsheetGrid_ScrollChanged));

        PreviewKeyDown += MainWindow_PreviewKeyDown;

        RebuildSpreadsheetColumns();
        RebuildChartInspectorColumns();

        foreach (var existingTag in _vm.MonitoredTags)
        {
            AttachTagPropertyListener(existingTag);
        }

        _vm.MonitoredTags.CollectionChanged += OnMonitoredTagsChanged;
        _vm.SessionMarkers.CollectionChanged += (_, _) => RenderChart(force: true);
        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.ChartWindowSeconds):
                case nameof(MainViewModel.ShowChartCursors):
                case nameof(MainViewModel.AutoFollowChart):
                case nameof(MainViewModel.FitAllChartRange):
                case nameof(MainViewModel.ChartTimeOffsetSeconds):
                    RenderChart(force: true);
                    break;
                case nameof(MainViewModel.ChartStepInterpolation):
                    TrendPlot.StepInterpolation = _vm.ChartStepInterpolation;
                    break;
                case nameof(MainViewModel.ShowChartSamplePoints):
                    TrendPlot.ShowSamplePoints = _vm.ShowChartSamplePoints;
                    break;
                case nameof(MainViewModel.ShowChartGrid):
                    TrendPlot.ShowGrid = _vm.ShowChartGrid;
                    break;
                case nameof(MainViewModel.ShowChartCrosshair):
                    TrendPlot.ShowCrosshair = _vm.ShowChartCrosshair;
                    break;
                case nameof(MainViewModel.VerticalColumnHeaders):
                    _vm.SpreadsheetHeaderHeight = 0;
                    RebuildSpreadsheetColumns();
                    break;
                case nameof(MainViewModel.ShowMarkerColumn):
                    if (SpreadsheetGrid.Columns.Count > 3)
                    {
                        SpreadsheetGrid.Columns[3].Visibility = _vm.ShowMarkerColumn
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                    break;
                case nameof(MainViewModel.SpreadsheetHeaderHeight):
                    if (!_isResizingHeaderHeight)
                        ApplySpreadsheetHeaderHeight();
                    break;
                case nameof(MainViewModel.IsPlacingMarker):
                    var markerCursor = _vm.IsPlacingMarker ? Cursors.Cross : null;
                    SpreadsheetGrid.Cursor = markerCursor;
                    TrendPlot.Cursor = markerCursor;
                    break;
            }
        };

        _uiRefreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Render,
            (_, _) => RefreshSpreadsheetAndChart(),
            Dispatcher);
        _uiRefreshTimer.Start();
    }

    private void OnMonitoredTagsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (MonitoredTagViewModel item in e.NewItems)
            {
                AttachTagPropertyListener(item);
            }
        }

        RebuildSpreadsheetColumns();
        RebuildChartInspectorColumns();
        RenderChart(force: true);
    }

    private void AttachTagPropertyListener(MonitoredTagViewModel item)
    {
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MonitoredTagViewModel.Alias))
            {
                RebuildSpreadsheetColumns();
                RebuildChartInspectorColumns();
                RenderChart(force: true);
            }
            else if (args.PropertyName == nameof(MonitoredTagViewModel.IsPlotted) ||
                     args.PropertyName == nameof(MonitoredTagViewModel.IsBoolean))
            {
                RebuildChartInspectorColumns();
                RenderChart(force: true);
            }
        };
    }

    private void BrowserTreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        // Se clicou no ToggleButton (seta de expandir/recolher pasta), permite comportamento padrão do WPF
        var current = source;
        while (current is not null && current != BrowserTreeView)
        {
            if (current is ToggleButton)
                return;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        // Procura o TreeViewItem clicado
        current = source;
        TreeViewItem? treeViewItem = null;
        while (current is not null && current != BrowserTreeView)
        {
            if (current is TreeViewItem tvi)
            {
                treeViewItem = tvi;
                break;
            }
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        if (treeViewItem?.DataContext is BrowseNodeViewModel nodeVm && nodeVm.Node.Id != "__dummy__")
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            _vm.SelectBrowseNodeFromUi(nodeVm, ctrl, shift);
            treeViewItem.Focus();

            if (ctrl || shift)
            {
                e.Handled = true;
            }
        }
    }

    private async void BrowserTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl) return;

        var target = _vm.SelectedBrowseNode ?? (BrowserTreeView.SelectedItem as BrowseNodeViewModel);
        if (target is not null && target.Node.IsVariable)
        {
            e.Handled = true;
            await _vm.AddBrowseNodeDoubleClickAsync(target);
        }
    }

    private void ChartZoomYIn_Click(object sender, RoutedEventArgs e) => TrendPlot.ZoomY(1.4);

    private void ChartZoomYOut_Click(object sender, RoutedEventArgs e) => TrendPlot.ZoomY(1.0 / 1.4);

    private void ChartResetY_Click(object sender, RoutedEventArgs e) => TrendPlot.ResetYScale();

    private void ChartCenterCursors_Click(object sender, RoutedEventArgs e) => TrendPlot.CenterCursors();

    private void ExportChartPng_Click(object sender, RoutedEventArgs e)
    {
        int width = Math.Max(800, (int)TrendPlot.ActualWidth);
        int height = Math.Max(450, (int)TrendPlot.ActualHeight);

        var sfd = new SaveFileDialog
        {
            Title = "Salvar Imagem do Gráfico (PNG)",
            FileName = $"Grafico_DMapFactory_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            Filter = "Imagem PNG (*.png)|*.png"
        };

        if (sfd.ShowDialog() != true)
            return;

        try
        {
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(TrendPlot);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            using var fs = File.Create(sfd.FileName);
            encoder.Save(fs);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível salvar a imagem PNG:\n{ex.Message}", "DMapFactory", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DecreaseHeaderHeight_Click(object sender, RoutedEventArgs e)
    {
        double current = GetActualOrEffectiveHeaderHeight();
        double next = Math.Clamp(Math.Round((current - 20) / 10.0) * 10.0, 32, 600);
        _vm.SpreadsheetHeaderHeight = next;
        ApplySpreadsheetHeaderHeight(next);
    }

    private void IncreaseHeaderHeight_Click(object sender, RoutedEventArgs e)
    {
        double current = GetActualOrEffectiveHeaderHeight();
        double next = Math.Clamp(Math.Round((current + 20) / 10.0) * 10.0, 32, 600);
        _vm.SpreadsheetHeaderHeight = next;
        ApplySpreadsheetHeaderHeight(next);
    }

    private void AutoFitHeaderHeight_Click(object sender, RoutedEventArgs e)
    {
        _vm.SpreadsheetHeaderHeight = 0;
        ApplySpreadsheetHeaderHeight();
    }

    private void AutoFitColumnWidths_Click(object sender, RoutedEventArgs e)
    {
        AutoFitSpreadsheetColumns();
    }

    private void OnConnectionItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: ConnectionItemViewModel connVm })
        {
            _ = _vm.EditConnectionCommand.ExecuteAsync(connVm);
        }
    }

    private void SpreadsheetGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isResizingHeaderHeight)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishHeaderResize();
                return;
            }

            double deltaY = e.GetPosition(SpreadsheetGrid).Y - _headerResizeStartY;
            double newHeight = Math.Clamp(Math.Round(_headerResizeStartHeight + deltaY), 32, 600);
            _vm.SpreadsheetHeaderHeight = newHeight;
            ApplySpreadsheetHeaderHeight(newHeight);
            e.Handled = true;
            return;
        }

        if (_vm.IsPlacingMarker)
        {
            SpreadsheetGrid.Cursor = Cursors.Cross;
            return;
        }

        if (IsInHeaderBottomResizeZone(e.OriginalSource as DependencyObject, e, out _))
        {
            SpreadsheetGrid.Cursor = Cursors.SizeNS;
        }
        else if (SpreadsheetGrid.Cursor == Cursors.SizeNS)
        {
            SpreadsheetGrid.Cursor = null;
        }
    }

    private void SpreadsheetGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;

        if (_vm.IsPlacingMarker)
        {
            var row = FindVisualParent<DataGridRow>(dep);
            if (row?.Item is WideSpreadsheetRow clickedRow)
            {
                e.Handled = true;
                _ = _vm.CommitMarkerAtRowAsync(clickedRow);
                return;
            }
        }

        if (IsInHeaderBottomResizeZone(dep, e, out _))
        {
            if (e.ClickCount == 2)
            {
                _vm.SpreadsheetHeaderHeight = 0;
                ApplySpreadsheetHeaderHeight();
                e.Handled = true;
                return;
            }

            _isResizingHeaderHeight = true;
            _headerResizeStartY = e.GetPosition(SpreadsheetGrid).Y;
            _headerResizeStartHeight = GetActualOrEffectiveHeaderHeight();
            SpreadsheetGrid.CaptureMouse();
            SpreadsheetGrid.Cursor = Cursors.SizeNS;
            e.Handled = true;
            return;
        }

        // Se o clique foi no cabeçalho ou célula de uma coluna de tag, seleciona a tag no ViewModel
        if (FindVisualParent<DataGridColumnHeader>(dep) is { Column: { } headerCol })
        {
            int colIdx = SpreadsheetGrid.Columns.IndexOf(headerCol);
            if (colIdx >= 4 && colIdx - 4 < _vm.MonitoredTags.Count)
            {
                _vm.SelectedMonitoredTag = _vm.MonitoredTags[colIdx - 4];
            }
        }
        else if (FindVisualParent<DataGridCell>(dep) is { Column: { } cellCol })
        {
            int colIdx = SpreadsheetGrid.Columns.IndexOf(cellCol);
            if (colIdx == 3 && e.ClickCount == 2)
            {
                var row = FindVisualParent<DataGridRow>(dep);
                if (row?.Item is WideSpreadsheetRow clickedRow)
                {
                    e.Handled = true;
                    _ = _vm.CommitMarkerAtRowAsync(clickedRow);
                    return;
                }
            }
            if (colIdx >= 4 && colIdx - 4 < _vm.MonitoredTags.Count)
            {
                _vm.SelectedMonitoredTag = _vm.MonitoredTags[colIdx - 4];
            }
        }
    }

    private void SpreadsheetGrid_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        var header = FindVisualParent<DataGridColumnHeader>(dep);
        if (header?.Column is DataGridColumn col)
        {
            int colIdx = SpreadsheetGrid.Columns.IndexOf(col);
            MonitoredTagViewModel? tagVm = null;
            if (colIdx >= 4 && colIdx - 4 < _vm.MonitoredTags.Count)
            {
                tagVm = _vm.MonitoredTags[colIdx - 4];
                _vm.SelectedMonitoredTag = tagVm;
            }

            var ctxMenu = BuildColumnContextMenu(colIdx, tagVm);
            ctxMenu.PlacementTarget = header;
            ctxMenu.Placement = PlacementMode.MousePoint;
            ctxMenu.IsOpen = true;
            e.Handled = true;
            return;
        }

        var rowElement = FindVisualParent<DataGridRow>(dep);
        if (rowElement?.Item is WideSpreadsheetRow clickedRow)
        {
            MonitoredTagViewModel? cellTagVm = null;
            var cell = FindVisualParent<DataGridCell>(dep);
            if (cell?.Column is { } cellCol)
            {
                int colIdx = SpreadsheetGrid.Columns.IndexOf(cellCol);
                if (colIdx >= 4 && colIdx - 4 < _vm.MonitoredTags.Count)
                {
                    cellTagVm = _vm.MonitoredTags[colIdx - 4];
                    _vm.SelectedMonitoredTag = cellTagVm;
                }
            }

            var ctxMenu = BuildSpreadsheetRowContextMenu(clickedRow, cellTagVm);
            ctxMenu.PlacementTarget = rowElement;
            ctxMenu.Placement = PlacementMode.MousePoint;
            ctxMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    private async void SpreadsheetGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _vm.SelectedMonitoredTag is not null)
        {
            e.Handled = true;
            await _vm.RemoveTagAsync(_vm.SelectedMonitoredTag);
        }
    }

    private async void ChartPensListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _vm.SelectedMonitoredTag is not null)
        {
            e.Handled = true;
            await _vm.RemoveTagAsync(_vm.SelectedMonitoredTag);
        }
    }

    private async void RemovePenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            var tagVm = menuItem.DataContext as MonitoredTagViewModel
                ?? ((menuItem.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as MonitoredTagViewModel
                ?? _vm.SelectedMonitoredTag;

            if (tagVm is not null)
            {
                await _vm.RemoveTagAsync(tagVm);
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        var curr = source;
        while (curr is not null)
        {
            if (curr is T match)
                return match;

            curr = curr is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(curr)
                : LogicalTreeHelper.GetParent(curr);
        }
        return null;
    }

    private void SpreadsheetGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isResizingHeaderHeight)
        {
            FinishHeaderResize();
            e.Handled = true;
        }
    }

    private void SpreadsheetGrid_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isResizingHeaderHeight && SpreadsheetGrid.Cursor == Cursors.SizeNS)
        {
            SpreadsheetGrid.Cursor = null;
        }
    }

    private void FinishHeaderResize()
    {
        _isResizingHeaderHeight = false;
        if (SpreadsheetGrid.IsMouseCaptured)
            SpreadsheetGrid.ReleaseMouseCapture();
        SpreadsheetGrid.Cursor = _vm.IsPlacingMarker ? Cursors.Cross : null;
    }

    private bool IsInHeaderBottomResizeZone(
        DependencyObject? source,
        MouseEventArgs e,
        out DataGridColumnHeader? header)
    {
        header = null;
        if (_vm.IsPlacingMarker)
            return false;

        double headerH = GetActualOrEffectiveHeaderHeight();
        Point gridPt = e.GetPosition(SpreadsheetGrid);

        if (gridPt.X >= 0 && gridPt.X <= SpreadsheetGrid.ActualWidth &&
            Math.Abs(gridPt.Y - headerH) <= 7)
        {
            if (source is DependencyObject dep)
            {
                if (FindVisualParent<ButtonBase>(dep) is not null)
                    return false;
                header = FindVisualParent<DataGridColumnHeader>(dep);
            }
            return true;
        }

        return false;
    }

    private double GetActualOrEffectiveHeaderHeight()
    {
        if (!double.IsNaN(SpreadsheetGrid.ColumnHeaderHeight) && SpreadsheetGrid.ColumnHeaderHeight > 0)
            return SpreadsheetGrid.ColumnHeaderHeight;

        return GetEffectiveHeaderHeight();
    }

    private double GetEffectiveHeaderHeight()
    {
        if (_vm.SpreadsheetHeaderHeight > 0)
            return Math.Clamp(_vm.SpreadsheetHeaderHeight, 32, 600);

        if (!_vm.VerticalColumnHeaders)
            return 32;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var headerFont = (FontFamily)TryFindResource("UiFont") ?? SystemFonts.MessageFontFamily;
        double maxTextWidth = MeasureTextWidth("Marcador", headerFont, 13, FontWeights.SemiBold, dpi);
        double cycleW = MeasureTextWidth("Cycle", headerFont, 13, FontWeights.SemiBold, dpi);
        if (cycleW > maxTextWidth) maxTextWidth = cycleW;

        foreach (var tag in _vm.MonitoredTags)
        {
            double w = MeasureTextWidth(tag.Alias, headerFont, 13, FontWeights.SemiBold, dpi);
            if (w > maxTextWidth)
                maxTextWidth = w;
        }

        // 70 px de folga para acomodar o botão '×' no topo + padding + bordas do cabeçalho sem cortar nenhuma letra
        return Math.Clamp(Math.Ceiling(maxTextWidth + 70), 95, 480);
    }

    private void ApplySpreadsheetHeaderHeight(double? overrideHeight = null)
    {
        double effectiveHeight = overrideHeight ?? GetEffectiveHeaderHeight();
        SpreadsheetGrid.ColumnHeaderHeight = effectiveHeight;

        bool isAuto = _vm.SpreadsheetHeaderHeight <= 0;
        double maxVerticalTextLength = Math.Max(30, effectiveHeight - 55);
        foreach (var tb in _verticalHeaderTextBlocks)
        {
            if (isAuto)
            {
                tb.MaxWidth = double.PositiveInfinity;
                tb.TextTrimming = TextTrimming.None;
            }
            else
            {
                tb.MaxWidth = maxVerticalTextLength;
                tb.TextTrimming = TextTrimming.CharacterEllipsis;
            }
        }
    }

    private void RebuildSpreadsheetColumns()
    {
        SpreadsheetGrid.Columns.Clear();
        _verticalHeaderTextBlocks.Clear();

        bool isVertical = _vm.VerticalColumnHeaders;
        double effectiveHeaderHeight = GetEffectiveHeaderHeight();
        bool isAuto = _vm.SpreadsheetHeaderHeight <= 0;
        double maxVerticalTextLength = Math.Max(30, effectiveHeaderHeight - 55);

        SpreadsheetGrid.Columns.Add(new DataGridTextColumn
        {
            Header = CreateFixedHeader("#", rotateVertical: false, columnIndex: 0),
            Width = new DataGridLength(CalculateColumnAutoWidth(0)),
            MinWidth = 36,
            Binding = new Binding(nameof(WideSpreadsheetRow.RowNumber))
        });

        SpreadsheetGrid.Columns.Add(new DataGridTextColumn
        {
            Header = CreateFixedHeader("Timestamp (UTC)", rotateVertical: false, columnIndex: 1),
            Width = new DataGridLength(CalculateColumnAutoWidth(1)),
            MinWidth = 110,
            Binding = new Binding(nameof(WideSpreadsheetRow.TimestampUtc))
            {
                StringFormat = "yyyy-MM-dd HH:mm:ss.fff"
            }
        });

        SpreadsheetGrid.Columns.Add(new DataGridTextColumn
        {
            Header = CreateFixedHeader("Cycle", rotateVertical: isVertical, columnIndex: 2),
            Width = new DataGridLength(CalculateColumnAutoWidth(2)),
            MinWidth = 38,
            Binding = new Binding(nameof(WideSpreadsheetRow.CycleDisplay))
        });

        SpreadsheetGrid.Columns.Add(new DataGridTextColumn
        {
            Header = CreateFixedHeader("Marcador", rotateVertical: isVertical, columnIndex: 3),
            Width = new DataGridLength(CalculateColumnAutoWidth(3)),
            MinWidth = 42,
            Visibility = _vm.ShowMarkerColumn ? Visibility.Visible : Visibility.Collapsed,
            Binding = new Binding(nameof(WideSpreadsheetRow.MarkerDisplay))
        });

        var changedBrush = (Brush)FindResource("ChangedBrush");
        var baseCellStyle = TryFindResource(typeof(DataGridCell)) as Style;

        for (int i = 0; i < _vm.MonitoredTags.Count; i++)
        {
            var capturedTag = _vm.MonitoredTags[i];
            int tagId = capturedTag.Config.Id;
            int colIdx = i + 4;

            var cellStyle = new Style(typeof(DataGridCell), baseCellStyle);
            cellStyle.Setters.Add(new Setter(
                Control.BackgroundProperty,
                new Binding(".")
                {
                    Converter = new SpreadsheetCellBrushConverter(tagId, capturedTag, changedBrush)
                }));
            cellStyle.Setters.Add(new Setter(
                Control.FontWeightProperty,
                new Binding(".")
                {
                    Converter = new ChangedCellFontWeightConverter(tagId)
                }));

            FrameworkElement headerElement;
            if (isVertical)
            {
                var dockPanel = new DockPanel
                {
                    LastChildFill = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = Brushes.Transparent,
                    ToolTip = $"{capturedTag.Alias}\n{capturedTag.Address}\n(Clique com botão direito para deletar/ajustar ou no ×)"
                };

                var removeBtn = new Button
                {
                    Content = "×",
                    FontWeight = FontWeights.Bold,
                    FontSize = 10,
                    Padding = new Thickness(3, 0, 3, 0),
                    Margin = new Thickness(0, 2, 0, 4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    ToolTip = $"Excluir coluna '{capturedTag.Alias}' da planilha"
                };
                removeBtn.Click += async (_, args) =>
                {
                    args.Handled = true;
                    await _vm.RemoveTagAsync(capturedTag);
                };
                DockPanel.SetDock(removeBtn, Dock.Top);
                dockPanel.Children.Add(removeBtn);

                var textBlock = new TextBlock
                {
                    Text = capturedTag.Alias,
                    MaxWidth = isAuto ? double.PositiveInfinity : maxVerticalTextLength,
                    TextTrimming = isAuto ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 2, 0, 4),
                    LayoutTransform = new RotateTransform(-90)
                };
                _verticalHeaderTextBlocks.Add(textBlock);
                dockPanel.Children.Add(textBlock);

                headerElement = dockPanel;
            }
            else
            {
                var headerPanel = new DockPanel
                {
                    LastChildFill = true,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Background = Brushes.Transparent,
                    ToolTip = $"{capturedTag.Alias}\n{capturedTag.Address}\n(Clique com botão direito para deletar/ajustar ou no ×)"
                };

                var removeBtn = new Button
                {
                    Content = "×",
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Padding = new Thickness(4, 0, 4, 0),
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = $"Excluir coluna '{capturedTag.Alias}' da planilha"
                };
                removeBtn.Click += async (_, args) =>
                {
                    args.Handled = true;
                    await _vm.RemoveTagAsync(capturedTag);
                };
                DockPanel.SetDock(removeBtn, Dock.Right);
                headerPanel.Children.Add(removeBtn);

                var tb = new TextBlock
                {
                    Text = capturedTag.Alias,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                headerPanel.Children.Add(tb);

                headerElement = headerPanel;
            }

            headerElement.ContextMenu = BuildColumnContextMenu(colIdx, capturedTag);

            SpreadsheetGrid.Columns.Add(new DataGridTextColumn
            {
                Header = headerElement,
                Width = new DataGridLength(CalculateColumnAutoWidth(colIdx)),
                MinWidth = 36,
                Binding = new Binding(".")
                {
                    Converter = new SpreadsheetCellTextConverter(tagId, capturedTag)
                },
                CellStyle = cellStyle
            });
        }

        ApplySpreadsheetHeaderHeight(effectiveHeaderHeight);
    }

    private FrameworkElement CreateFixedHeader(string text, bool rotateVertical, int columnIndex)
    {
        var tb = new TextBlock
        {
            Text = text,
            HorizontalAlignment = rotateVertical ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 2, 0, 4),
            ToolTip = $"{text}\n(Arraste a borda inferior do cabeçalho para ajustar a altura ou clique com botão direito)"
        };

        if (rotateVertical)
        {
            if (_vm.SpreadsheetHeaderHeight > 0)
            {
                tb.MaxWidth = Math.Max(30, GetActualOrEffectiveHeaderHeight() - 55);
                tb.TextTrimming = TextTrimming.CharacterEllipsis;
            }
            else
            {
                tb.MaxWidth = double.PositiveInfinity;
                tb.TextTrimming = TextTrimming.None;
            }
            tb.LayoutTransform = new RotateTransform(-90);
            _verticalHeaderTextBlocks.Add(tb);
        }

        var container = new Grid
        {
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ContextMenu = BuildColumnContextMenu(columnIndex, tagVm: null)
        };
        container.Children.Add(tb);
        return container;
    }

    private ContextMenu BuildColumnContextMenu(int columnIndex, MonitoredTagViewModel? tagVm)
    {
        var ctxMenu = new ContextMenu();

        if (tagVm is not null)
        {
            var removeMenuItem = new MenuItem
            {
                Header = $"🗑 Deletar tag '{tagVm.Alias}' da coleta (planilha e gráfico)",
                FontWeight = FontWeights.SemiBold
            };
            removeMenuItem.Click += async (_, _) => await _vm.RemoveTagAsync(tagVm);
            ctxMenu.Items.Add(removeMenuItem);
            ctxMenu.Items.Add(new Separator());
        }

        var autoFitThisItem = new MenuItem
        {
            Header = "↔ Auto-ajustar largura desta coluna"
        };
        autoFitThisItem.Click += (_, _) => AutoFitSingleColumn(columnIndex);
        ctxMenu.Items.Add(autoFitThisItem);

        var autoFitAllItem = new MenuItem
        {
            Header = "↔ Auto-ajustar largura de todas as colunas"
        };
        autoFitAllItem.Click += (_, _) => AutoFitSpreadsheetColumns();
        ctxMenu.Items.Add(autoFitAllItem);

        var autoFitHeaderHeightItem = new MenuItem
        {
            Header = "↕ Auto-ajustar altura da linha de título"
        };
        autoFitHeaderHeightItem.Click += (_, _) =>
        {
            _vm.SpreadsheetHeaderHeight = 0;
            ApplySpreadsheetHeaderHeight();
        };
        ctxMenu.Items.Add(autoFitHeaderHeightItem);

        return ctxMenu;
    }

    private void AutoFitSpreadsheetColumns()
    {
        for (int i = 0; i < SpreadsheetGrid.Columns.Count; i++)
        {
            AutoFitSingleColumn(i);
        }
    }

    private void AutoFitSingleColumn(int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= SpreadsheetGrid.Columns.Count)
            return;

        double optimalWidth = CalculateColumnAutoWidth(columnIndex);
        SpreadsheetGrid.Columns[columnIndex].Width = new DataGridLength(optimalWidth);
    }

    private double CalculateColumnAutoWidth(int columnIndex)
    {
        bool isVertical = _vm.VerticalColumnHeaders;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FontFamily headerFont = SystemFonts.MessageFontFamily;
        FontFamily cellFont = SpreadsheetGrid.FontFamily ?? SystemFonts.MessageFontFamily;
        var rows = _vm.SpreadsheetRows;

        if (columnIndex == 0)
        {
            // Coluna '#' (RowNumber)
            double headerWidth = isVertical
                ? 36
                : MeasureTextWidth("#", headerFont, 12, FontWeights.SemiBold, dpi) + 24;

            string maxRowStr = rows.Count > 0
                ? rows[^1].RowNumber.ToString(CultureInfo.InvariantCulture)
                : "1";
            double cellWidth = MeasureTextWidth(maxRowStr, cellFont, 12, FontWeights.Normal, dpi) + 22;

            return Math.Clamp(Math.Ceiling(Math.Max(headerWidth, cellWidth)), 36, 140);
        }

        if (columnIndex == 1)
        {
            // Coluna 'Timestamp (UTC)'
            double headerWidth = MeasureTextWidth("Timestamp (UTC)", headerFont, 12, FontWeights.SemiBold, dpi) + 24;
            double cellWidth = MeasureTextWidth("2026-10-24 23:59:59.999", cellFont, 12, FontWeights.Normal, dpi) + 22;
            return Math.Ceiling(Math.Max(headerWidth, cellWidth));
        }

        if (columnIndex == 2)
        {
            // Coluna 'Cycle' (diferença de tempo para a linha anterior)
            double headerWidth = isVertical
                ? 38
                : MeasureTextWidth("Cycle", headerFont, 12, FontWeights.SemiBold, dpi) + 24;

            double cellWidth = MeasureTextWidth("+00:00:00.000", cellFont, 12, FontWeights.Normal, dpi) + 24;
            return Math.Clamp(Math.Ceiling(Math.Max(headerWidth, cellWidth)), 38, 140);
        }

        if (columnIndex == 3)
        {
            // Coluna 'Marcador'
            double headerWidth = isVertical
                ? 38
                : MeasureTextWidth("Marcador", headerFont, 12, FontWeights.SemiBold, dpi) + 24;

            string longestMarker = "🚩 Marcador";
            if (rows.Count > 0)
            {
                int step = Math.Max(1, rows.Count / 200);
                for (int r = 0; r < rows.Count; r += step)
                {
                    if (rows[r].IsMarker && rows[r].MarkerDisplay.Length > longestMarker.Length)
                        longestMarker = rows[r].MarkerDisplay;
                }
                if (rows[^1].IsMarker && rows[^1].MarkerDisplay.Length > longestMarker.Length)
                    longestMarker = rows[^1].MarkerDisplay;
            }

            double cellWidth = MeasureTextWidth(longestMarker, cellFont, 12, FontWeights.Normal, dpi) + 24;
            return Math.Clamp(Math.Ceiling(Math.Max(headerWidth, cellWidth)), 42, 350);
        }

        int tagIndex = columnIndex - 4;
        if (tagIndex < 0 || tagIndex >= _vm.MonitoredTags.Count)
            return isVertical ? 60 : 120;

        var tag = _vm.MonitoredTags[tagIndex];
        int tagId = tag.Config.Id;

        // 1. Largura exigida pelo título da coluna (respeitando se está na vertical ou na horizontal)
        double tagHeaderWidth = isVertical
            ? 38.0
            : MeasureTextWidth(tag.Alias, headerFont, 12, FontWeights.SemiBold, dpi) + 52.0;

        // 2. Largura exigida pelas informações contidas nas células da coluna
        string widestCellText = "";

        if (!tag.IsBoolean && !string.IsNullOrWhiteSpace(tag.ValueText) && tag.ValueText != "—")
        {
            if (!TryExtractBoolean(tag.ValueText, tag.IsBoolean, out _))
                widestCellText = tag.ValueText;
        }

        int rowCount = rows.Count;
        int startIdx = Math.Max(0, rowCount - 500);
        for (int r = startIdx; r < rowCount; r++)
        {
            if (rows[r].ValuesByTagId.TryGetValue(tagId, out var raw) && raw is not null)
            {
                if (TryExtractBoolean(raw, tag.IsBoolean, out _))
                    continue; // Células booleanas são exibidas apenas com preenchimento de cor

                string formatted = WideSpreadsheetRow.FormatCell(raw);
                if (formatted.Length > widestCellText.Length)
                    widestCellText = formatted;
            }
        }

        double tagCellWidth = widestCellText.Length > 0
            ? MeasureTextWidth(widestCellText, cellFont, 12, FontWeights.SemiBold, dpi) + 22.0
            : 38.0;

        return Math.Clamp(Math.Ceiling(Math.Max(tagHeaderWidth, tagCellWidth)), 38, 650);
    }

    private static double MeasureTextWidth(
        string text,
        FontFamily fontFamily,
        double fontSize,
        FontWeight fontWeight,
        double pixelsPerDip)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(fontFamily, FontStyles.Normal, fontWeight, FontStretches.Normal),
            fontSize,
            Brushes.Black,
            pixelsPerDip > 0 ? pixelsPerDip : 1.0);

        return ft.WidthIncludingTrailingWhitespace;
    }

    private void RefreshSpreadsheetAndChart()
    {
        int count = _vm.SpreadsheetRows.Count;
        long lastRowNum = count > 0 ? _vm.SpreadsheetRows[^1].RowNumber : -1;

        bool rowsChanged = count != _lastRenderedRowCount || lastRowNum != _lastRenderedRowNumber;
        if (rowsChanged)
        {
            _lastRenderedRowCount = count;
            _lastRenderedRowNumber = lastRowNum;

            if (_vm.AutoScrollSpreadsheet && count > 0 && SpreadsheetGrid.IsVisible)
            {
                SpreadsheetGrid.ScrollIntoView(_vm.SpreadsheetRows[^1]);
            }
        }

        if (rowsChanged || TrendPlot.IsVisible)
        {
            RenderChart(force: false);
        }
    }

    private void RenderChart(bool force)
    {
        if (!force && !TrendPlot.IsVisible)
            return;

        var rows = _vm.SpreadsheetRows;
        var plottedTags = _vm.MonitoredTags.Where(t => t.IsPlotted).ToList();

        if (plottedTags.Count == 0)
        {
            TrendPlot.UpdateChart([], DateTime.UtcNow.AddSeconds(-_vm.ChartWindowSeconds), DateTime.UtcNow, _vm.ShowChartCursors);
            return;
        }

        if (rows.Count == 0)
        {
            // Se ainda não há linhas na planilha, mas alguma tag já possui valor lido, plota o ponto atual
            DateTime nowUtc = DateTime.UtcNow;
            DateTime fallbackMinUtc = nowUtc.AddSeconds(-Math.Max(5, _vm.ChartWindowSeconds));
            var liveSeries = new List<ChartSeriesData>();

            foreach (var tagVm in plottedTags)
            {
                if (tagVm.TimestampUtc.HasValue && TryToDouble(tagVm.ValueText, out double v))
                {
                    ChartSeriesPoint[] pts =
                    [
                        new ChartSeriesPoint(fallbackMinUtc, v),
                        new ChartSeriesPoint(nowUtc, v)
                    ];
                    tagVm.MinValueText = v.ToString("G5", CultureInfo.InvariantCulture);
                    tagVm.MaxValueText = v.ToString("G5", CultureInfo.InvariantCulture);
                    liveSeries.Add(new ChartSeriesData(tagVm.Config.Id, tagVm.Alias, tagVm.IsBoolean, tagVm.PenColor, pts));
                }
            }

            TrendPlot.UpdateChart(liveSeries, fallbackMinUtc, nowUtc, _vm.ShowChartCursors, _vm.SessionMarkers);
            return;
        }

        DateTime latestDataUtc = rows[^1].TimestampUtc;
        DateTime earliestDataUtc = rows[0].TimestampUtc;

        DateTime maxWindowUtc;
        DateTime minWindowUtc;

        if (_vm.FitAllChartRange)
        {
            minWindowUtc = earliestDataUtc;
            maxWindowUtc = latestDataUtc > earliestDataUtc ? latestDataUtc : earliestDataUtc.AddSeconds(5);
        }
        else if (_vm.AutoFollowChart)
        {
            maxWindowUtc = latestDataUtc;
            minWindowUtc = maxWindowUtc.AddSeconds(-Math.Max(1, _vm.ChartWindowSeconds));
        }
        else
        {
            double winSec = Math.Max(1, _vm.ChartWindowSeconds);
            maxWindowUtc = latestDataUtc.AddSeconds(-_vm.ChartTimeOffsetSeconds);
            if (maxWindowUtc < earliestDataUtc.AddSeconds(winSec * 0.5))
                maxWindowUtc = earliestDataUtc.AddSeconds(winSec * 0.5);
            minWindowUtc = maxWindowUtc.AddSeconds(-winSec);
        }

        // PF-01: Localiza o índice inicial da janela temporal em O(log N) via busca binária
        int startIdx = FindStartIndexByTimestamp(rows, minWindowUtc);

        var seriesList = new List<ChartSeriesData>(plottedTags.Count);

        for (int p = 0; p < plottedTags.Count; p++)
        {
            var tagVm = plottedTags[p];
            int tagId = tagVm.Config.Id;
            bool isBoolTag = tagVm.IsBoolean;

            var points = new List<ChartSeriesPoint>();
            double lastVal = 0;
            bool hasVal = false;
            double minVal = double.MaxValue;
            double maxVal = double.MinValue;

            // Busca o valor anterior mais recente antes de minWindowUtc
            for (int k = startIdx - 1; k >= 0; k--)
            {
                if (rows[k].ValuesByTagId.TryGetValue(tagId, out var prevRaw) && prevRaw is not null)
                {
                    if (prevRaw is bool && !isBoolTag)
                        isBoolTag = true;

                    if (TryToDouble(prevRaw, out double pv))
                    {
                        lastVal = pv;
                        hasVal = true;
                        break;
                    }
                }
            }

            // Itera apenas sobre as linhas contidas na janela visível atual
            for (int i = startIdx; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.TimestampUtc > maxWindowUtc)
                    break;

                if (r.ValuesByTagId.TryGetValue(tagId, out var raw) && raw is not null)
                {
                    if (raw is bool && !isBoolTag)
                    {
                        isBoolTag = true;
                    }

                    if (TryToDouble(raw, out double v))
                    {
                        lastVal = v;
                        hasVal = true;
                    }
                }

                if (r.TimestampUtc >= minWindowUtc && hasVal)
                {
                    if (points.Count == 0 && r.TimestampUtc > minWindowUtc)
                    {
                        // Propaga o valor anterior no início da janela (Sample-and-Hold)
                        points.Add(new ChartSeriesPoint(minWindowUtc, lastVal));
                    }

                    points.Add(new ChartSeriesPoint(r.TimestampUtc, lastVal));
                    if (lastVal < minVal) minVal = lastVal;
                    if (lastVal > maxVal) maxVal = lastVal;
                }
            }

            DateTime holdUntilUtc = maxWindowUtc < latestDataUtc ? maxWindowUtc : latestDataUtc;

            // Se a tag tinha valor antes de minWindowUtc e não teve novas linhas dentro da janela, mantém linha constante
            if (points.Count == 0 && hasVal && holdUntilUtc >= minWindowUtc)
            {
                points.Add(new ChartSeriesPoint(minWindowUtc, lastVal));
                points.Add(new ChartSeriesPoint(holdUntilUtc, lastVal));
                minVal = lastVal;
                maxVal = lastVal;
            }
            else if (points.Count > 0 && points[^1].TimestampUtc < holdUntilUtc)
            {
                // Estende o último valor conhecido até a borda direita dos dados na janela (Sample-and-Hold)
                points.Add(new ChartSeriesPoint(holdUntilUtc, lastVal));
            }

            if (points.Count > 0)
            {
                tagVm.MinValueText = isBoolTag ? (minVal != 0 ? "1" : "0") : minVal.ToString("G5", CultureInfo.InvariantCulture);
                tagVm.MaxValueText = isBoolTag ? (maxVal != 0 ? "1" : "0") : maxVal.ToString("G5", CultureInfo.InvariantCulture);
            }
            else
            {
                tagVm.MinValueText = "—";
                tagVm.MaxValueText = "—";
            }

            // PF-01: Reduz densidade excessiva para manter o DrawingContext suave em janelas longas
            var renderedPoints = DownsampleSeries(points, 3000);
            seriesList.Add(new ChartSeriesData(tagId, tagVm.Alias, isBoolTag, tagVm.PenColor, renderedPoints));
        }

        TrendPlot.UpdateChart(seriesList, minWindowUtc, maxWindowUtc, _vm.ShowChartCursors, _vm.SessionMarkers);
    }

    private static int FindStartIndexByTimestamp(IReadOnlyList<WideSpreadsheetRow> rows, DateTime targetUtc)
    {
        int low = 0;
        int high = rows.Count - 1;
        int result = rows.Count;

        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            if (rows[mid].TimestampUtc >= targetUtc)
            {
                result = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return result;
    }

    private static IReadOnlyList<ChartSeriesPoint> DownsampleSeries(List<ChartSeriesPoint> points, int targetMaxPoints)
    {
        if (points.Count <= targetMaxPoints)
            return points;

        var result = new List<ChartSeriesPoint>(targetMaxPoints + 2)
        {
            points[0]
        };

        int step = Math.Max(2, points.Count / (targetMaxPoints / 2));
        for (int i = 1; i < points.Count - 1; i += step)
        {
            int end = Math.Min(i + step, points.Count - 1);
            int minIdx = i;
            int maxIdx = i;
            for (int k = i; k < end; k++)
            {
                if (points[k].Value < points[minIdx].Value) minIdx = k;
                if (points[k].Value > points[maxIdx].Value) maxIdx = k;
            }

            if (minIdx <= maxIdx)
            {
                result.Add(points[minIdx]);
                if (minIdx != maxIdx) result.Add(points[maxIdx]);
            }
            else
            {
                result.Add(points[maxIdx]);
                result.Add(points[minIdx]);
            }
        }

        result.Add(points[^1]);
        return result;
    }

    private static bool TryToDouble(object? raw, out double value)
    {
        switch (raw)
        {
            case null:
                value = 0;
                return false;
            case bool b:
                value = b ? 1.0 : 0.0;
                return true;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                value = d;
                return true;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f):
                value = f;
                return true;
            case int i:
                value = i;
                return true;
            case uint ui:
                value = ui;
                return true;
            case long l:
                value = l;
                return true;
            case ulong ul:
                value = ul;
                return true;
            case short s:
                value = s;
                return true;
            case ushort us:
                value = us;
                return true;
            case byte by:
                value = by;
                return true;
            case sbyte sb:
                value = sb;
                return true;
            case decimal dec:
                value = (double)dec;
                return true;
            case string str:
                if (str.StartsWith('['))
                {
                    value = 0;
                    return false;
                }
                if (double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedInv) ||
                    double.TryParse(str, NumberStyles.Float, CultureInfo.CurrentCulture, out parsedInv))
                {
                    value = parsedInv;
                    return true;
                }
                if (bool.TryParse(str, out bool pb))
                {
                    value = pb ? 1.0 : 0.0;
                    return true;
                }
                value = 0;
                return false;
            case IConvertible conv:
                try
                {
                    value = conv.ToDouble(CultureInfo.InvariantCulture);
                    return !double.IsNaN(value) && !double.IsInfinity(value);
                }
                catch
                {
                    value = 0;
                    return false;
                }
            default:
                value = 0;
                return false;
        }
    }

    private static bool TryExtractBoolean(object? raw, bool tagIsBoolean, out bool boolValue)
    {
        if (raw is bool b)
        {
            boolValue = b;
            return true;
        }

        if (!tagIsBoolean || raw is null)
        {
            boolValue = false;
            return false;
        }

        switch (raw)
        {
            case byte by:
                boolValue = by != 0;
                return true;
            case sbyte sb:
                boolValue = sb != 0;
                return true;
            case short s:
                boolValue = s != 0;
                return true;
            case ushort us:
                boolValue = us != 0;
                return true;
            case int i:
                boolValue = i != 0;
                return true;
            case uint ui:
                boolValue = ui != 0;
                return true;
            case long l:
                boolValue = l != 0;
                return true;
            case string str when str is "1" or "0":
                boolValue = str == "1";
                return true;
            case string str when bool.TryParse(str, out bool parsed):
                boolValue = parsed;
                return true;
            default:
                boolValue = false;
                return false;
        }
    }

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(hex)!;
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Formata o texto da célula na planilha: para sinais booleanos, oculta o texto "0"/"1"
    /// pois o estado é representado pelo preenchimento em cor da célula.
    /// </summary>
    private sealed class SpreadsheetCellTextConverter(int tagId, MonitoredTagViewModel tagVm) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not WideSpreadsheetRow row || !row.ValuesByTagId.TryGetValue(tagId, out var raw) || raw is null)
                return "";

            if (TryExtractBoolean(raw, tagVm.IsBoolean, out _))
                return "";

            return WideSpreadsheetRow.FormatCell(raw);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Define a cor de fundo da célula na planilha:
    /// - Para sinais booleanos: célula preenchida em Verde quando 1 (True) e Cinza-Azulado suave quando 0 (False).
    /// - Para sinais numéricos/analógicos: destaca em amarelo suave quando o valor mudou naquela linha.
    /// </summary>
    private sealed class SpreadsheetCellBrushConverter(int tagId, MonitoredTagViewModel tagVm, Brush analogChangedBrush) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not WideSpreadsheetRow row)
                return Brushes.Transparent;

            bool changed = row.IsTagChanged(tagId);

            if (row.ValuesByTagId.TryGetValue(tagId, out var raw) && raw is not null &&
                TryExtractBoolean(raw, tagVm.IsBoolean, out bool boolVal))
            {
                if (boolVal)
                    return changed ? BoolTrueChangedBrush : BoolTrueBrush;

                return changed ? BoolFalseChangedBrush : BoolFalseBrush;
            }

            return changed ? analogChangedBrush : Brushes.Transparent;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class ChangedCellFontWeightConverter(int tagId) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is WideSpreadsheetRow row && row.IsTagChanged(tagId)
                ? FontWeights.SemiBold
                : FontWeights.Normal;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class ChartInspectorCellConverter(int tagId) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is ChartInspectorRowViewModel row ? row.GetTagValueText(tagId) : "—";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private void RebuildChartInspectorColumns()
    {
        ChartInspectorGrid.Columns.Clear();

        var cursorHeaderStyle = new Style(typeof(DataGridCell));
        cursorHeaderStyle.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        cursorHeaderStyle.Setters.Add(new Setter(ForegroundProperty, new Binding(nameof(ChartInspectorRowViewModel.AccentBrush))));

        ChartInspectorGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Cursor / Mira",
            Width = 105,
            Binding = new Binding(nameof(ChartInspectorRowViewModel.CursorLabel)),
            CellStyle = cursorHeaderStyle
        });

        ChartInspectorGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Timestamp (UTC)",
            Width = 175,
            Binding = new Binding(nameof(ChartInspectorRowViewModel.TimestampText))
        });

        var plottedTags = _vm.MonitoredTags.Where(t => t.IsPlotted).ToList();
        foreach (var tag in plottedTags)
        {
            int tagId = tag.Config.Id;

            var headerStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"{tag.Alias}\n{tag.Address}"
            };

            headerStack.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 9,
                Height = 9,
                RadiusX = 2,
                RadiusY = 2,
                Fill = tag.PenBrush,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            headerStack.Children.Add(new TextBlock
            {
                Text = tag.Alias,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            ChartInspectorGrid.Columns.Add(new DataGridTextColumn
            {
                Header = headerStack,
                MinWidth = 90,
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                Binding = new Binding(".")
                {
                    Converter = new ChartInspectorCellConverter(tagId)
                }
            });
        }

        RefreshChartInspectorValues();
    }

    private void RefreshChartInspectorValues()
    {
        if (_chartInspectorRows.Count < 3)
            return;

        var plottedTags = _vm.MonitoredTags.Where(t => t.IsPlotted).ToList();

        UpdateSingleInspectorRow(_chartInspectorRows[0], TrendPlot.CursorATimeUtc, plottedTags);
        UpdateSingleInspectorRow(_chartInspectorRows[1], TrendPlot.CursorBTimeUtc, plottedTags);
        UpdateSingleInspectorRow(_chartInspectorRows[2], TrendPlot.HoverTimeUtc, plottedTags);
    }

    private void UpdateSingleInspectorRow(
        ChartInspectorRowViewModel rowVm,
        DateTime? timestampUtc,
        IReadOnlyList<MonitoredTagViewModel> plottedTags)
    {
        string tsText = timestampUtc.HasValue
            ? timestampUtc.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
            : "—";

        var values = new Dictionary<int, string>(plottedTags.Count);
        foreach (var tag in plottedTags)
        {
            values[tag.Config.Id] = TrendPlot.FormatValueAt(tag.Config.Id, timestampUtc);
        }

        rowVm.UpdateReadout(tsText, values);
    }

    private void SpreadsheetGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) < 0.01) return;

        // FN-04: Pausa automática de auto-scroll ao rolar para cima para inspecionar histórico
        if (e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 2.0)
        {
            if (_vm.AutoScrollSpreadsheet)
            {
                _vm.AutoScrollSpreadsheet = false;
            }
        }
        else if (e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 1.0)
        {
            if (!_vm.AutoScrollSpreadsheet && _vm.IsOnline)
            {
                _vm.AutoScrollSpreadsheet = true;
            }
        }
    }

    private void OnTrendPlotRightClicked(Point mousePoint, DateTime timestampUtc)
    {
        var ctxMenu = new ContextMenu();

        var nearMarker = TrendPlot.GetMarkerNearPixel(mousePoint);
        if (nearMarker is not null)
        {
            var editNearItem = new MenuItem
            {
                Header = $"✏️ Editar Marcador ('{nearMarker.Text}')...",
                FontWeight = FontWeights.SemiBold
            };
            editNearItem.Click += async (_, _) => await _vm.EditSessionMarkerAsync(nearMarker);
            ctxMenu.Items.Add(editNearItem);

            var deleteNearItem = new MenuItem
            {
                Header = $"🗑️ Excluir Marcador ('{nearMarker.Text}')"
            };
            deleteNearItem.Click += async (_, _) => await _vm.RemoveSessionMarkerAsync(nearMarker);
            ctxMenu.Items.Add(deleteNearItem);

            ctxMenu.Items.Add(new Separator());
        }

        var locateRowItem = new MenuItem
        {
            Header = "📋 Localizar na Planilha",
            FontWeight = nearMarker is null ? FontWeights.SemiBold : FontWeights.Normal
        };
        locateRowItem.Click += (_, _) =>
        {
            HighlightSpreadsheetRowNearTimestamp(timestampUtc);
            MainTabControl.SelectedIndex = 0;
        };
        ctxMenu.Items.Add(locateRowItem);

        var markerItem = new MenuItem
        {
            Header = "📍 Inserir Marcador neste ponto..."
        };
        markerItem.Click += async (_, _) =>
        {
            await _vm.CommitMarkerAtAsync(timestampUtc);
        };
        ctxMenu.Items.Add(markerItem);

        if (_vm.SessionMarkers.Count > 0)
        {
            var markersSubMenu = new MenuItem
            {
                Header = $"🚩 Marcadores da Sessão ({_vm.SessionMarkers.Count})"
            };

            foreach (var m in _vm.SessionMarkers)
            {
                var capturedMarker = m;
                var mItem = new MenuItem
                {
                    Header = $"{capturedMarker.TimestampUtc:HH:mm:ss.fff} — {capturedMarker.Text}"
                };

                var focusMarkerItem = new MenuItem { Header = "📈 Centralizar no Gráfico (Cursor A)" };
                focusMarkerItem.Click += (_, _) => FocusTimestampOnChartWithCursorA(capturedMarker.TimestampUtc);
                mItem.Items.Add(focusMarkerItem);

                var editMItem = new MenuItem { Header = "✏️ Editar Marcador..." };
                editMItem.Click += async (_, _) => await _vm.EditSessionMarkerAsync(capturedMarker);
                mItem.Items.Add(editMItem);

                var delMItem = new MenuItem { Header = "🗑️ Excluir Marcador" };
                delMItem.Click += async (_, _) => await _vm.RemoveSessionMarkerAsync(capturedMarker);
                mItem.Items.Add(delMItem);

                markersSubMenu.Items.Add(mItem);
            }

            ctxMenu.Items.Add(markersSubMenu);
        }

        ctxMenu.Items.Add(new Separator());

        var cursorAItem = new MenuItem { Header = "Posicionar Cursor A aqui" };
        cursorAItem.Click += (_, _) => TrendPlot.SetCursorAPosition(timestampUtc);
        ctxMenu.Items.Add(cursorAItem);

        var cursorBItem = new MenuItem { Header = "Posicionar Cursor B aqui" };
        cursorBItem.Click += (_, _) => TrendPlot.SetCursorBPosition(timestampUtc);
        ctxMenu.Items.Add(cursorBItem);

        var centerCursorsItem = new MenuItem { Header = "Centralizar Cursores" };
        centerCursorsItem.Click += (_, _) => TrendPlot.CenterCursors();
        ctxMenu.Items.Add(centerCursorsItem);

        ctxMenu.Items.Add(new Separator());

        var exportPngItem = new MenuItem { Header = "Salvar Gráfico em Imagem (PNG)..." };
        exportPngItem.Click += ExportChartPng_Click;
        ctxMenu.Items.Add(exportPngItem);

        ctxMenu.PlacementTarget = TrendPlot;
        ctxMenu.Placement = PlacementMode.MousePoint;
        ctxMenu.IsOpen = true;
    }

    private void FocusTimestampOnChartWithCursorA(DateTime targetTimestampUtc)
    {
        _vm.ShowChartCursors = true;
        _vm.FitAllChartRange = false;
        _vm.AutoFollowChart = false;

        double halfWindowSec = Math.Max(1.0, _vm.ChartWindowSeconds) / 2.0;
        if (_vm.SpreadsheetRows.Count > 0)
        {
            DateTime latestDataUtc = _vm.SpreadsheetRows[^1].TimestampUtc;
            _vm.ChartTimeOffsetSeconds = (latestDataUtc - targetTimestampUtc).TotalSeconds - halfWindowSec;
        }

        MainTabControl.SelectedIndex = 1;
        RenderChart(force: true);
        TrendPlot.SetCursorAPosition(targetTimestampUtc);
    }

    private void HighlightSpreadsheetRowNearTimestamp(DateTime targetUtc)
    {
        var rows = _vm.SpreadsheetRows;
        if (rows.Count == 0) return;

        int closestIdx = 0;
        double minDiff = Math.Abs((rows[0].TimestampUtc - targetUtc).TotalMilliseconds);

        int low = 0, high = rows.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            double diff = Math.Abs((rows[mid].TimestampUtc - targetUtc).TotalMilliseconds);
            if (diff < minDiff)
            {
                minDiff = diff;
                closestIdx = mid;
            }

            if (rows[mid].TimestampUtc < targetUtc)
                low = mid + 1;
            else
                high = mid - 1;
        }

        var targetRow = rows[closestIdx];
        _vm.AutoScrollSpreadsheet = false;
        SpreadsheetGrid.SelectedItem = targetRow;
        SpreadsheetGrid.ScrollIntoView(targetRow);
    }

    private ContextMenu BuildSpreadsheetRowContextMenu(WideSpreadsheetRow row, MonitoredTagViewModel? tagVm)
    {
        var ctxMenu = new ContextMenu();

        if (row.IsMarker)
        {
            var editMarkerItem = new MenuItem
            {
                Header = $"✏️ Editar Marcador ('{row.MarkerText}')...",
                FontWeight = FontWeights.SemiBold
            };
            editMarkerItem.Click += async (_, _) => await _vm.EditMarkerAtRowAsync(row);
            ctxMenu.Items.Add(editMarkerItem);

            var deleteMarkerItem = new MenuItem
            {
                Header = $"🗑️ Excluir Marcador ('{row.MarkerText}')"
            };
            deleteMarkerItem.Click += async (_, _) => await _vm.RemoveMarkerAtRowAsync(row);
            ctxMenu.Items.Add(deleteMarkerItem);
        }
        else
        {
            var markerItem = new MenuItem
            {
                Header = $"📍 Inserir Marcador na Linha #{row.RowNumber}...",
                FontWeight = FontWeights.SemiBold
            };
            markerItem.Click += async (_, _) => await _vm.CommitMarkerAtRowAsync(row);
            ctxMenu.Items.Add(markerItem);
        }

        var focusChartItem = new MenuItem
        {
            Header = "📈 Focar instante no Gráfico (Cursor A)"
        };
        focusChartItem.Click += (_, _) =>
        {
            FocusTimestampOnChartWithCursorA(row.TimestampUtc);
        };
        ctxMenu.Items.Add(focusChartItem);

        if (tagVm is not null)
        {
            ctxMenu.Items.Add(new Separator());

            if (tagVm.IsBoolean)
            {
                var cycleTriggerItem = new MenuItem
                {
                    Header = $"🏷 Definir '{tagVm.Alias}' como Gatilho de Ciclo"
                };
                cycleTriggerItem.Click += (_, _) => _vm.SetAsCycleTrigger(tagVm);
                ctxMenu.Items.Add(cycleTriggerItem);
            }

            if (row.ValuesByTagId.TryGetValue(tagVm.Config.Id, out var cellVal) && cellVal is not null)
            {
                var copyCellItem = new MenuItem
                {
                    Header = $"Copiar valor de '{tagVm.Alias}' ({WideSpreadsheetRow.FormatCell(cellVal)})"
                };
                copyCellItem.Click += (_, _) => Clipboard.SetText(WideSpreadsheetRow.FormatCell(cellVal));
                ctxMenu.Items.Add(copyCellItem);
            }
        }

        ctxMenu.Items.Add(new Separator());

        var copyRowItem = new MenuItem
        {
            Header = "📋 Copiar Linha Inteira (TSV)"
        };
        copyRowItem.Click += (_, _) => Clipboard.SetText(FormatRowToClipboard(row));
        ctxMenu.Items.Add(copyRowItem);

        var copyTimestampItem = new MenuItem
        {
            Header = $"Copiar Horário ({row.TimestampUtc:yyyy-MM-dd HH:mm:ss.fff})"
        };
        copyTimestampItem.Click += (_, _) => Clipboard.SetText(row.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        ctxMenu.Items.Add(copyTimestampItem);

        return ctxMenu;
    }

    private string FormatRowToClipboard(WideSpreadsheetRow row)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(row.RowNumber).Append('\t')
          .Append(row.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append('\t')
          .Append(row.CycleDisplay).Append('\t')
          .Append(row.CycleNumber.HasValue ? row.CycleNumber.Value.ToString() : "");

        foreach (var tag in _vm.MonitoredTags)
        {
            sb.Append('\t');
            if (row.ValuesByTagId.TryGetValue(tag.Config.Id, out var val) && val is not null)
            {
                sb.Append(WideSpreadsheetRow.FormatCell(val));
            }
        }

        return sb.ToString();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            e.Handled = true;
            OpenContextSensitiveHelp();
            return;
        }

        if (e.Key == Key.Escape && _vm.IsPlacingMarker)
        {
            _vm.IsPlacingMarker = false;
            e.Handled = true;
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            if (e.Key == Key.D1 || e.Key == Key.NumPad1)
            {
                MainTabControl.SelectedIndex = 0;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.D2 || e.Key == Key.NumPad2)
            {
                MainTabControl.SelectedIndex = 1;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.D3 || e.Key == Key.NumPad3)
            {
                MainTabControl.SelectedIndex = 2;
                e.Handled = true;
                return;
            }
        }

        // Espaço para alternar auto-scroll / auto-follow quando não estiver editando texto
        if (e.Key == Key.Space && !(Keyboard.FocusedElement is TextBox or PasswordBox))
        {
            if (MainTabControl.SelectedIndex == 0)
            {
                _vm.AutoScrollSpreadsheet = !_vm.AutoScrollSpreadsheet;
                e.Handled = true;
            }
            else if (MainTabControl.SelectedIndex == 1)
            {
                _vm.AutoFollowChart = !_vm.AutoFollowChart;
                e.Handled = true;
            }
        }
    }

    private void OpenContextSensitiveHelp()
    {
        var focused = Keyboard.FocusedElement as DependencyObject
                      ?? FocusManager.GetFocusedElement(this) as DependencyObject
                      ?? Mouse.DirectlyOver as DependencyObject;

        var topicId = HelpProvider.FindTopicId(focused);

        if (string.IsNullOrEmpty(topicId))
        {
            topicId = MainTabControl.SelectedIndex switch
            {
                0 => HelpTopicIds.Spreadsheet,
                1 => HelpTopicIds.Chart,
                2 => HelpTopicIds.TagBrowser,
                _ => HelpTopicIds.Overview
            };
        }

        HelpService.OpenHelp(topicId);
    }

    private async void ConnectOrDisconnectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var connVm = (sender as MenuItem)?.DataContext as ConnectionItemViewModel
            ?? (((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as ConnectionItemViewModel
            ?? _vm.SelectedConnection;

        if (connVm is not null)
        {
            await _vm.ConnectOrDisconnectSelectedAsync(connVm);
        }
    }

    private async void EditConnectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var connVm = (sender as MenuItem)?.DataContext as ConnectionItemViewModel
            ?? (((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as ConnectionItemViewModel
            ?? _vm.SelectedConnection;

        if (connVm is not null)
        {
            await _vm.EditConnectionCommand.ExecuteAsync(connVm);
        }
    }

    private async void RemoveConnectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var connVm = (sender as MenuItem)?.DataContext as ConnectionItemViewModel
            ?? (((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as ConnectionItemViewModel
            ?? _vm.SelectedConnection;

        if (connVm is not null)
        {
            await _vm.RemoveConnectionCommand.ExecuteAsync(connVm);
        }
    }

    private async void AddSelectedNodeAsTagMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            var nodeVm = menuItem.DataContext as BrowseNodeViewModel
                ?? ((menuItem.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as BrowseNodeViewModel;
            if (nodeVm is not null && !_vm.SelectedBrowseNodes.Contains(nodeVm))
            {
                _vm.SelectBrowseNodeFromUi(nodeVm, ctrlPressed: false, shiftPressed: false);
            }
        }

        await _vm.AddSelectedNodeAsTagCommand.ExecuteAsync(null);
    }

    private async void AddFolderVariablesAsTagsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            var nodeVm = menuItem.DataContext as BrowseNodeViewModel
                ?? ((menuItem.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as BrowseNodeViewModel;
            if (nodeVm is not null && !_vm.SelectedBrowseNodes.Contains(nodeVm))
            {
                _vm.SelectBrowseNodeFromUi(nodeVm, ctrlPressed: false, shiftPressed: false);
            }
        }

        await _vm.AddFolderVariablesAsTagsCommand.ExecuteAsync(null);
    }

    private void SetPenAsCycleTriggerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            var tagVm = menuItem.DataContext as MonitoredTagViewModel
                ?? ((menuItem.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as MonitoredTagViewModel
                ?? _vm.SelectedMonitoredTag;
            if (tagVm is not null)
            {
                _vm.SetAsCycleTrigger(tagVm);
            }
        }
    }

    private void CopyNodeId_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            var nodeVm = menuItem.DataContext as BrowseNodeViewModel
                ?? ((menuItem.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as BrowseNodeViewModel
                ?? _vm.SelectedBrowseNode;

            if (nodeVm is not null && !string.IsNullOrWhiteSpace(nodeVm.Node.Id))
            {
                Clipboard.SetText(nodeVm.Node.Id);
            }
        }
    }

    private void CopyPenAddress_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            var tagVm = menuItem.DataContext as MonitoredTagViewModel
                ?? ((menuItem.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as MonitoredTagViewModel
                ?? _vm.SelectedMonitoredTag;

            if (tagVm is not null)
            {
                Clipboard.SetText(tagVm.Address);
            }
        }
    }

    private void CopySelectedLog_Click(object sender, RoutedEventArgs e)
    {
        if (LogDataGrid.SelectedItem is DMapFactory.App.Services.LogEntry entry)
        {
            Clipboard.SetText($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level}] {entry.Message}");
        }
    }

    private void CopyAllLogs_Click(object sender, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var item in _vm.FilteredLogEntries)
        {
            if (item is DMapFactory.App.Services.LogEntry entry)
            {
                sb.AppendLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level}] {entry.Message}");
            }
        }
        if (sb.Length > 0)
        {
            Clipboard.SetText(sb.ToString());
        }
    }

    private async void EditSelectedMarkerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SpreadsheetGrid.SelectedItem is WideSpreadsheetRow selectedRow && selectedRow.IsMarker)
        {
            await _vm.EditMarkerAtRowAsync(selectedRow);
            return;
        }

        var targetMarker = GetActiveOrLatestSessionMarker();
        if (targetMarker is not null)
        {
            await _vm.EditSessionMarkerAsync(targetMarker);
            return;
        }

        var lastMarkerRow = _vm.SpreadsheetRows.LastOrDefault(r => r.IsMarker);
        if (lastMarkerRow is not null)
        {
            await _vm.EditMarkerAtRowAsync(lastMarkerRow);
            return;
        }

        MessageBox.Show(
            this,
            "Nenhum marcador foi inserido na sessão atual para editar.\nSelecione uma linha com marcador na planilha ou insira um marcador primeiro.",
            "Editar Marcador",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void DeleteSelectedMarkerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SpreadsheetGrid.SelectedItem is WideSpreadsheetRow selectedRow && selectedRow.IsMarker)
        {
            await _vm.RemoveMarkerAtRowAsync(selectedRow);
            return;
        }

        var targetMarker = GetActiveOrLatestSessionMarker();
        if (targetMarker is not null)
        {
            await _vm.RemoveSessionMarkerAsync(targetMarker);
            return;
        }

        var lastMarkerRow = _vm.SpreadsheetRows.LastOrDefault(r => r.IsMarker);
        if (lastMarkerRow is not null)
        {
            await _vm.RemoveMarkerAtRowAsync(lastMarkerRow);
            return;
        }

        MessageBox.Show(
            this,
            "Nenhum marcador foi inserido na sessão atual para excluir.\nSelecione uma linha com marcador na planilha ou clique com o botão direito sobre o marcador no gráfico.",
            "Excluir Marcador",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private SessionMarker? GetActiveOrLatestSessionMarker()
    {
        if (_vm.SessionMarkers.Count == 0)
            return null;

        if (MainTabControl.SelectedIndex == 1 && TrendPlot.CursorATimeUtc is { } cursorAUtc)
        {
            return _vm.SessionMarkers
                .OrderBy(m => Math.Abs((m.TimestampUtc - cursorAUtc).TotalMilliseconds))
                .FirstOrDefault();
        }

        return _vm.SessionMarkers[^1];
    }

    private void ShowSpreadsheetTabMenuItem_Click(object sender, RoutedEventArgs e) => MainTabControl.SelectedIndex = 0;
    private void ShowChartTabMenuItem_Click(object sender, RoutedEventArgs e) => MainTabControl.SelectedIndex = 1;
}

