using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DMapFactory.Core.Models;

namespace DMapFactory.App.Controls;

public sealed record ChartSeriesPoint(DateTime TimestampUtc, double Value);

public sealed record ChartSeriesData(
    int TagId,
    string Alias,
    bool IsBoolean,
    Color Color,
    IReadOnlyList<ChartSeriesPoint> Points);

/// <summary>
/// Controle nativo WPF de alto desempenho (DrawingContext) para gráfico de tendência industrial (RF-CHT-01..04):
/// - Curvas analógicas em degrau (StepHorizontal) ou interpolação linear
/// - Faixas digitais empilhadas ([D]) preenchidas estilo analisador lógico para tags booleanas
/// - Cursores duplos A/B arrastáveis com cálculo de Δt, frequência (Hz) e valores A/B
/// - Inspetor Crosshair ao passar o mouse, Zoom X/Y, Pan com mouse e exportação PNG
/// Zero dependência de OpenGL / SkiaSharp nativo, 100% compatível com VMware/RDP/Hyper-V.
/// </summary>
public sealed class TrendChartControl : FrameworkElement
{
    private static readonly Typeface MonoTypeface = new(new FontFamily("Consolas, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Brush FigureBgBrush = CreateFrozenBrush("#F3F4F5");
    private static readonly Brush PlotBgBrush = CreateFrozenBrush("#ECEDEF");
    private static readonly Brush DigitalBandBgBrush = CreateFrozenBrush("#E5E8EC");
    private static readonly Pen BorderPen = CreateFrozenPen("#D3D6DA", 1.0);
    private static readonly Pen GridPen = CreateFrozenPen("#D8DBDF", 1.0);
    private static readonly Pen CrosshairPen = CreateFrozenDashedPen("#68707A", 1.0);
    private static readonly Brush TextBrush = CreateFrozenBrush("#3C4046");
    private static readonly Brush SecondaryTextBrush = CreateFrozenBrush("#727780");
    private static readonly Brush LegendBgBrush = CreateFrozenBrush("#F2F3F4F5");
    private static readonly Brush TooltipBgBrush = CreateFrozenBrush("#F426292E");
    private static readonly Brush TooltipTextBrush = CreateFrozenBrush("#F8F9FA");

    private static readonly Pen CursorAPen = CreateFrozenDashedPen("#2B6CB0", 1.6);
    private static readonly Pen CursorBPen = CreateFrozenDashedPen("#C53030", 1.6);
    private static readonly Pen MarkerLinePen = CreateFrozenDashedPen("#D97706", 1.6);
    private static readonly Brush MarkerBadgeBgBrush = CreateFrozenBrush("#FFFBEB");
    private static readonly Pen MarkerBadgeBorderPen = CreateFrozenPen("#D97706", 1.2);
    private static readonly Brush MarkerTextBrush = CreateFrozenBrush("#92400E");

    private IReadOnlyList<ChartSeriesData> _series = [];
    private IReadOnlyList<SessionMarker> _markers = [];
    private DateTime _minTimeUtc = DateTime.UtcNow.AddSeconds(-60);
    private DateTime _maxTimeUtc = DateTime.UtcNow;
    private bool _showCursors = true;
    private bool _stepInterpolation = true;
    private bool _showSamplePoints;
    private bool _showGrid = true;
    private bool _showCrosshair = true;
    private double _yZoomFactor = 1.0;
    private double _yPanOffset;

    private double _cursorARatio = 0.30;
    private double _cursorBRatio = 0.70;
    private int _draggingCursor; // 0 = none, 1 = A, 2 = B
    private bool _nextClickMovesB;

    private bool _isPanning;
    private Point _panStartPoint;
    private Point? _hoverPoint;

    public event Action<string>? CursorDeltaChanged;
    public event Action? ReadoutsChanged;
    public event Action<int>? MouseWheelZoomRequested;
    public event Action<double>? HorizontalPanRequested;
    public event Action<Point, DateTime>? PlotRightClicked;

    public TrendChartControl()
    {
        ClipToBounds = true;
        Focusable = false;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonDown += OnMouseRightButtonDown;
        MouseRightButtonUp += OnMouseRightButtonUp;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        MouseWheel += OnMouseWheel;
    }

    public bool StepInterpolation
    {
        get => _stepInterpolation;
        set { _stepInterpolation = value; InvalidateVisual(); }
    }

    public bool ShowSamplePoints
    {
        get => _showSamplePoints;
        set { _showSamplePoints = value; InvalidateVisual(); }
    }

    public bool ShowGrid
    {
        get => _showGrid;
        set { _showGrid = value; InvalidateVisual(); }
    }

    public bool ShowCrosshair
    {
        get => _showCrosshair;
        set
        {
            _showCrosshair = value;
            ReadoutsChanged?.Invoke();
            InvalidateVisual();
        }
    }

    public DateTime? CursorATimeUtc =>
        _showCursors
            ? _minTimeUtc.AddTicks((long)(_cursorARatio * Math.Max(1.0, (_maxTimeUtc - _minTimeUtc).Ticks)))
            : null;

    public DateTime? CursorBTimeUtc =>
        _showCursors
            ? _minTimeUtc.AddTicks((long)(_cursorBRatio * Math.Max(1.0, (_maxTimeUtc - _minTimeUtc).Ticks)))
            : null;

    public DateTime? HoverTimeUtc
    {
        get
        {
            if (!_showCrosshair || !_hoverPoint.HasValue)
                return null;
            Rect plotRect = GetPlotRect(ActualWidth, ActualHeight);
            if (!plotRect.Contains(_hoverPoint.Value) || plotRect.Width <= 10)
                return null;
            double ratio = Math.Clamp((_hoverPoint.Value.X - plotRect.Left) / plotRect.Width, 0.0, 1.0);
            double spanTicks = Math.Max(1.0, (_maxTimeUtc - _minTimeUtc).Ticks);
            return _minTimeUtc.AddTicks((long)(ratio * spanTicks));
        }
    }

    public string FormatValueAt(int tagId, DateTime? timestampUtc)
    {
        if (!timestampUtc.HasValue)
            return "—";

        var s = _series.FirstOrDefault(x => x.TagId == tagId);
        if (s is null || s.Points.Count == 0)
            return "—";

        double val = SampleValueAt(s.Points, timestampUtc.Value);
        return s.IsBoolean
            ? (val != 0 ? "1 (ON)" : "0 (OFF)")
            : val.ToString("G6", CultureInfo.InvariantCulture);
    }

    public void ZoomY(double factor)
    {
        _yZoomFactor = Math.Clamp(_yZoomFactor * factor, 0.1, 50.0);
        InvalidateVisual();
    }

    public void PanY(double fraction)
    {
        _yPanOffset += fraction / _yZoomFactor;
        InvalidateVisual();
    }

    public void ResetYScale()
    {
        _yZoomFactor = 1.0;
        _yPanOffset = 0.0;
        InvalidateVisual();
    }

    public void CenterCursors()
    {
        _cursorARatio = 0.30;
        _cursorBRatio = 0.70;
        PublishCursorDelta();
        ReadoutsChanged?.Invoke();
        InvalidateVisual();
    }

    public void SetCursorAPosition(DateTime ts)
    {
        double totalSpan = (_maxTimeUtc - _minTimeUtc).TotalSeconds;
        if (totalSpan <= 0) return;
        _cursorARatio = Math.Clamp((ts - _minTimeUtc).TotalSeconds / totalSpan, 0.0, 1.0);
        PublishCursorDelta();
        ReadoutsChanged?.Invoke();
        InvalidateVisual();
    }

    public void SetCursorBPosition(DateTime ts)
    {
        double totalSpan = (_maxTimeUtc - _minTimeUtc).TotalSeconds;
        if (totalSpan <= 0) return;
        _cursorBRatio = Math.Clamp((ts - _minTimeUtc).TotalSeconds / totalSpan, 0.0, 1.0);
        PublishCursorDelta();
        ReadoutsChanged?.Invoke();
        InvalidateVisual();
    }

    public DateTime? GetTimestampAtPixel(Point pt)
    {
        Rect plotRect = GetPlotRect(ActualWidth, ActualHeight);
        if (plotRect.Width <= 10 || pt.X < plotRect.Left || pt.X > plotRect.Right)
            return null;

        double ratio = Math.Clamp((pt.X - plotRect.Left) / plotRect.Width, 0.0, 1.0);
        double totalSeconds = (_maxTimeUtc - _minTimeUtc).TotalSeconds;
        return _minTimeUtc.AddSeconds(ratio * totalSeconds);
    }

    public SessionMarker? GetMarkerNearPixel(Point pt, double maxPixelDistance = 24.0)
    {
        if (_markers.Count == 0)
            return null;

        Rect plotRect = GetPlotRect(ActualWidth, ActualHeight);
        if (plotRect.Width <= 10 || pt.X < plotRect.Left || pt.X > plotRect.Right)
            return null;

        double totalSpan = Math.Max(0.001, (_maxTimeUtc - _minTimeUtc).TotalSeconds);
        SessionMarker? best = null;
        double bestDist = maxPixelDistance;

        foreach (var marker in _markers)
        {
            double xRatio = (marker.TimestampUtc - _minTimeUtc).TotalSeconds / totalSpan;
            if (xRatio < 0.0 || xRatio > 1.0)
                continue;

            double x = plotRect.Left + xRatio * plotRect.Width;
            double dist = Math.Abs(pt.X - x);
            if (dist <= bestDist)
            {
                bestDist = dist;
                best = marker;
            }
        }

        return best;
    }

    public void UpdateChart(
        IReadOnlyList<ChartSeriesData> series,
        DateTime minTimeUtc,
        DateTime maxTimeUtc,
        bool showCursors,
        IReadOnlyList<SessionMarker>? markers = null)
    {
        _series = series;
        _minTimeUtc = minTimeUtc;
        _maxTimeUtc = maxTimeUtc > minTimeUtc ? maxTimeUtc : minTimeUtc.AddSeconds(1);
        _showCursors = showCursors;
        _markers = markers ?? [];

        PublishCursorDelta();
        ReadoutsChanged?.Invoke();
        InvalidateVisual();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_showCursors) return;

        Rect plotRect = GetPlotRect(ActualWidth, ActualHeight);
        if (plotRect.Width <= 10) return;

        Point pt = e.GetPosition(this);
        if (pt.X < plotRect.Left || pt.X > plotRect.Right) return;

        double ratio = Math.Clamp((pt.X - plotRect.Left) / plotRect.Width, 0.0, 1.0);
        double xA = plotRect.Left + _cursorARatio * plotRect.Width;
        double xB = plotRect.Left + _cursorBRatio * plotRect.Width;

        if (Math.Abs(pt.X - xA) <= 10)
        {
            _draggingCursor = 1;
            _cursorARatio = ratio;
        }
        else if (Math.Abs(pt.X - xB) <= 10)
        {
            _draggingCursor = 2;
            _cursorBRatio = ratio;
        }
        else
        {
            if (!_nextClickMovesB)
            {
                _cursorARatio = ratio;
                _draggingCursor = 1;
            }
            else
            {
                _cursorBRatio = ratio;
                _draggingCursor = 2;
            }
            _nextClickMovesB = !_nextClickMovesB;
        }

        CaptureMouse();
        PublishCursorDelta();
        ReadoutsChanged?.Invoke();
        InvalidateVisual();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingCursor != 0)
        {
            _draggingCursor = 0;
            ReleaseMouseCapture();
        }
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        Rect plotRect = GetPlotRect(ActualWidth, ActualHeight);
        Point pt = e.GetPosition(this);
        if (!plotRect.Contains(pt)) return;

        _isPanning = true;
        _panStartPoint = pt;
        CaptureMouse();
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            ReleaseMouseCapture();

            Point pt = e.GetPosition(this);
            if ((pt - _panStartPoint).Length < 4)
            {
                var ts = GetTimestampAtPixel(pt);
                if (ts.HasValue)
                {
                    PlotRightClicked?.Invoke(pt, ts.Value);
                }
            }
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Rect plotRect = GetPlotRect(ActualWidth, ActualHeight);
        if (plotRect.Width <= 10) return;

        Point pt = e.GetPosition(this);

        if (_draggingCursor != 0 && e.LeftButton == MouseButtonState.Pressed)
        {
            double ratio = Math.Clamp((pt.X - plotRect.Left) / plotRect.Width, 0.0, 1.0);
            if (_draggingCursor == 1)
                _cursorARatio = ratio;
            else if (_draggingCursor == 2)
                _cursorBRatio = ratio;

            PublishCursorDelta();
            ReadoutsChanged?.Invoke();
            InvalidateVisual();
            return;
        }

        if (_isPanning && e.RightButton == MouseButtonState.Pressed)
        {
            double dx = pt.X - _panStartPoint.X;
            if (Math.Abs(dx) > 2)
            {
                double deltaRatio = -dx / plotRect.Width;
                _panStartPoint = pt;
                HorizontalPanRequested?.Invoke(deltaRatio);
            }
            return;
        }

        if (plotRect.Contains(pt))
        {
            _hoverPoint = pt;
            ReadoutsChanged?.Invoke();
            if (_showCrosshair)
                InvalidateVisual();
        }
        else if (_hoverPoint.HasValue)
        {
            _hoverPoint = null;
            ReadoutsChanged?.Invoke();
            if (_showCrosshair)
                InvalidateVisual();
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoverPoint.HasValue)
        {
            _hoverPoint = null;
            ReadoutsChanged?.Invoke();
            InvalidateVisual();
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta != 0)
        {
            MouseWheelZoomRequested?.Invoke(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        }
    }

    private void PublishCursorDelta()
    {
        if (!_showCursors)
        {
            CursorDeltaChanged?.Invoke("Cursores ocultos");
            return;
        }

        double totalMs = (_maxTimeUtc - _minTimeUtc).TotalMilliseconds;
        double dtMs = Math.Abs(_cursorBRatio - _cursorARatio) * totalMs;
        double freqHz = dtMs > 1e-3 ? 1000.0 / dtMs : 0.0;
        string dtText = dtMs < 10_000
            ? $"{dtMs:N1} ms"
            : $"{dtMs / 1000.0:N2} s";
        string freqText = freqHz > 0 ? $" ({freqHz:N2} Hz)" : "";
        CursorDeltaChanged?.Invoke($"Δt = {dtText}{freqText}");
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 40 || h < 40) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRectangle(FigureBgBrush, null, new Rect(0, 0, w, h));

        Rect plotRect = GetPlotRect(w, h);
        dc.DrawRectangle(PlotBgBrush, BorderPen, plotRect);

        var analogSeries = _series.Where(s => !s.IsBoolean && s.Points.Count > 0).ToList();
        var digitalSeries = _series.Where(s => s.IsBoolean && s.Points.Count > 0).ToList();

        double digitalBandHeight = 0.0;
        if (digitalSeries.Count > 0)
        {
            if (analogSeries.Count == 0)
            {
                // Se só há sinais booleanos, usa toda a área do gráfico para as faixas digitais
                digitalBandHeight = plotRect.Height;
            }
            else
            {
                digitalBandHeight = Math.Min(plotRect.Height * 0.42, digitalSeries.Count * 28.0 + 10.0);
            }
        }

        Rect analogRect = new(
            plotRect.Left,
            plotRect.Top,
            plotRect.Width,
            Math.Max(0, plotRect.Height - digitalBandHeight));

        Rect digitalRect = new(
            plotRect.Left,
            analogRect.Bottom,
            plotRect.Width,
            digitalBandHeight);

        if (digitalBandHeight > 0)
        {
            dc.DrawRectangle(DigitalBandBgBrush, BorderPen, digitalRect);
        }

        // Cálculo de escala Y analógica com suporte a Zoom Y e Pan Y
        double yMin = 0, yMax = 100;
        if (analogSeries.Count > 0)
        {
            yMin = analogSeries.SelectMany(s => s.Points).Min(p => p.Value);
            yMax = analogSeries.SelectMany(s => s.Points).Max(p => p.Value);
            if (Math.Abs(yMax - yMin) < 1e-6)
            {
                double baseDelta = Math.Max(1.0, Math.Abs(yMax) * 0.1);
                yMin -= baseDelta;
                yMax += baseDelta;
            }
            else
            {
                double pad = (yMax - yMin) * 0.10;
                yMin -= pad;
                yMax += pad;
            }

            double center = (yMin + yMax) / 2.0 + _yPanOffset * (yMax - yMin);
            double halfSpan = (yMax - yMin) / (2.0 * _yZoomFactor);
            yMin = center - halfSpan;
            yMax = center + halfSpan;
        }

        // Grade horizontal + rótulos Y (apenas quando há área analógica visível)
        if (analogRect.Height >= 30)
        {
            const int yTicks = 5;
            for (int i = 0; i <= yTicks; i++)
            {
                double frac = i / (double)yTicks;
                double y = analogRect.Bottom - frac * analogRect.Height;
                if (_showGrid)
                {
                    dc.DrawLine(GridPen, new Point(analogRect.Left, y), new Point(analogRect.Right, y));
                }

                double val = yMin + frac * (yMax - yMin);
                var ft = CreateText(val.ToString("G5", CultureInfo.InvariantCulture), 11, SecondaryTextBrush, dpi);
                dc.DrawText(ft, new Point(Math.Max(2, plotRect.Left - ft.Width - 6), y - ft.Height / 2));
            }
        }

        // Grade vertical + rótulos de tempo (UTC)
        const int xTicks = 5;
        double spanTicks = Math.Max(1.0, (_maxTimeUtc - _minTimeUtc).Ticks);
        for (int i = 0; i <= xTicks; i++)
        {
            double frac = i / (double)xTicks;
            double x = plotRect.Left + frac * plotRect.Width;
            if (_showGrid)
            {
                dc.DrawLine(GridPen, new Point(x, plotRect.Top), new Point(x, plotRect.Bottom));
            }

            DateTime t = _minTimeUtc.AddTicks((long)(frac * spanTicks));
            var ft = CreateText(t.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), 11, SecondaryTextBrush, dpi);
            dc.DrawText(ft, new Point(x - ft.Width / 2, plotRect.Bottom + 6));
        }

        if (_series.Count == 0 || (analogSeries.Count == 0 && digitalSeries.Count == 0))
        {
            string msg = _series.Count == 0
                ? "Marque as penas à direita e inicie a sessão On-line (F5) para visualizar o gráfico."
                : "Aguardando amostras das penas selecionadas...";
            var emptyText = CreateText(msg, 13, SecondaryTextBrush, dpi);
            dc.DrawText(emptyText, new Point(
                plotRect.Left + Math.Max(10, (plotRect.Width - emptyText.Width) / 2),
                plotRect.Top + (plotRect.Height - emptyText.Height) / 2));
            return;
        }

        dc.PushClip(new RectangleGeometry(plotRect));

        // Desenha séries analógicas
        if (analogRect.Height >= 10)
        {
            foreach (var s in analogSeries)
            {
                var pen = CreateFrozenPen(s.Color, 2.0);
                var pointBrush = new SolidColorBrush(s.Color);
                pointBrush.Freeze();

                var geom = new StreamGeometry();
                using (var ctx = geom.Open())
                {
                    bool started = false;
                    Point firstPt = default;
                    Point lastPt = default;
                    double prevY = 0;

                    foreach (var pt in s.Points)
                    {
                        double x = TimeToX(pt.TimestampUtc, plotRect, spanTicks);
                        double y = ValueToY(pt.Value, yMin, yMax, analogRect);

                        if (!started)
                        {
                            firstPt = new Point(x, y);
                            ctx.BeginFigure(firstPt, false, false);
                            started = true;
                        }
                        else
                        {
                            if (_stepInterpolation)
                            {
                                ctx.LineTo(new Point(x, prevY), true, false);
                            }
                            ctx.LineTo(new Point(x, y), true, false);
                        }
                        prevY = y;
                        lastPt = new Point(x, y);
                    }

                    // Garante traço visível mesmo se houver apenas 1 amostra ou timestamps idênticos
                    if (started && Math.Abs(lastPt.X - firstPt.X) < 1.5)
                    {
                        ctx.LineTo(new Point(plotRect.Right, lastPt.Y), true, false);
                    }
                }
                geom.Freeze();
                dc.DrawGeometry(null, pen, geom);

                if (_showSamplePoints || s.Points.Count <= 15)
                {
                    int step = Math.Max(1, s.Points.Count / 150);
                    for (int i = 0; i < s.Points.Count; i += step)
                    {
                        var pt = s.Points[i];
                        double x = TimeToX(pt.TimestampUtc, plotRect, spanTicks);
                        double y = ValueToY(pt.Value, yMin, yMax, analogRect);
                        dc.DrawEllipse(pointBrush, null, new Point(x, y), 3.0, 3.0);
                    }
                }
            }
        }

        // Desenha faixas digitais empilhadas ([D]) com preenchimento colorido no nível alto (1)
        if (digitalSeries.Count > 0 && digitalRect.Height >= 10)
        {
            double stripHeight = digitalRect.Height / digitalSeries.Count;
            for (int d = 0; d < digitalSeries.Count; d++)
            {
                var s = digitalSeries[d];
                double rowTop = digitalRect.Top + d * stripHeight;
                double rowBottom = rowTop + stripHeight;
                double stripTop = rowTop + Math.Max(4, stripHeight * 0.18);
                double stripBottom = rowBottom - Math.Max(4, stripHeight * 0.14);

                if (d > 0)
                {
                    dc.DrawLine(GridPen, new Point(digitalRect.Left, rowTop), new Point(digitalRect.Right, rowTop));
                }

                var pen = CreateFrozenPen(s.Color, 2.0);
                var fillBrush = new SolidColorBrush(Color.FromArgb(65, s.Color.R, s.Color.G, s.Color.B));
                fillBrush.Freeze();

                // Desenha blocos preenchidos onde o sinal booleano é 1 (True)
                for (int i = 0; i < s.Points.Count; i++)
                {
                    var pt = s.Points[i];
                    if (pt.Value == 0) continue;

                    double x1 = TimeToX(pt.TimestampUtc, plotRect, spanTicks);
                    double x2 = (i + 1 < s.Points.Count)
                        ? TimeToX(s.Points[i + 1].TimestampUtc, plotRect, spanTicks)
                        : plotRect.Right;

                    if (x2 > x1)
                    {
                        dc.DrawRectangle(fillBrush, null, new Rect(new Point(x1, stripTop), new Point(x2, stripBottom)));
                    }
                }

                var geom = new StreamGeometry();
                using (var ctx = geom.Open())
                {
                    bool started = false;
                    Point firstPt = default;
                    Point lastPt = default;
                    double prevY = stripBottom;

                    foreach (var pt in s.Points)
                    {
                        double x = TimeToX(pt.TimestampUtc, plotRect, spanTicks);
                        double y = pt.Value != 0 ? stripTop : stripBottom;
                        if (!started)
                        {
                            firstPt = new Point(x, y);
                            ctx.BeginFigure(firstPt, false, false);
                            started = true;
                        }
                        else
                        {
                            ctx.LineTo(new Point(x, prevY), true, false);
                            ctx.LineTo(new Point(x, y), true, false);
                        }
                        prevY = y;
                        lastPt = new Point(x, y);
                    }

                    if (started && Math.Abs(lastPt.X - firstPt.X) < 1.5)
                    {
                        ctx.LineTo(new Point(plotRect.Right, lastPt.Y), true, false);
                    }
                }
                geom.Freeze();
                dc.DrawGeometry(null, pen, geom);

                var lblBrush = new SolidColorBrush(s.Color);
                lblBrush.Freeze();
                var lbl = CreateBoldText($"{s.Alias} [D]", 11, lblBrush, dpi);
                Rect lblBg = new(digitalRect.Left + 6, rowTop + 2, lbl.Width + 8, lbl.Height + 2);
                dc.DrawRectangle(LegendBgBrush, null, lblBg);
                dc.DrawText(lbl, new Point(digitalRect.Left + 10, rowTop + 3));
            }
        }

        // Marcadores de Sessão (RF-STO-06: linhas verticais tracejadas âmbar com badge 🚩)
        if (_markers.Count > 0)
        {
            int markerIdx = 0;
            foreach (var marker in _markers)
            {
                if (marker.TimestampUtc < _minTimeUtc || marker.TimestampUtc > _maxTimeUtc)
                    continue;

                double x = TimeToX(marker.TimestampUtc, plotRect, spanTicks);

                // Linha vertical de ponta a ponta na área do gráfico
                dc.DrawLine(MarkerLinePen, new Point(x, plotRect.Top), new Point(x, plotRect.Bottom));

                // Badge no topo: 🚩 <Texto> com alternância de altura para evitar sobreposição se muito próximos
                string badgeLabel = $"🚩 {marker.Text}";
                var ft = CreateBoldText(badgeLabel, 10, MarkerTextBrush, dpi);
                double badgeW = ft.Width + 8;
                double badgeH = ft.Height + 4;
                double badgeX = Math.Clamp(x - badgeW / 2, plotRect.Left + 2, plotRect.Right - badgeW - 2);
                double badgeY = plotRect.Top + 4 + (markerIdx % 3) * 19;
                markerIdx++;

                Rect badgeRect = new(badgeX, badgeY, badgeW, badgeH);
                dc.DrawRoundedRectangle(MarkerBadgeBgBrush, MarkerBadgeBorderPen, badgeRect, 3, 3);
                dc.DrawText(ft, new Point(badgeX + 4, badgeY + 2));
            }
        }

        // Cursores A e B
        if (_showCursors)
        {
            double xA = plotRect.Left + _cursorARatio * plotRect.Width;
            double xB = plotRect.Left + _cursorBRatio * plotRect.Width;

            dc.DrawLine(CursorAPen, new Point(xA, plotRect.Top), new Point(xA, plotRect.Bottom));
            dc.DrawLine(CursorBPen, new Point(xB, plotRect.Top), new Point(xB, plotRect.Bottom));

            DateTime tA = _minTimeUtc.AddTicks((long)(_cursorARatio * spanTicks));
            DateTime tB = _minTimeUtc.AddTicks((long)(_cursorBRatio * spanTicks));

            var lblA = CreateBoldText($"A ({tA:HH:mm:ss.fff})", 11, CreateFrozenBrush("#2B6CB0"), dpi);
            var lblB = CreateBoldText($"B ({tB:HH:mm:ss.fff})", 11, CreateFrozenBrush("#C53030"), dpi);

            double boxAX = Math.Min(xA + 4, plotRect.Right - lblA.Width - 8);
            double boxBX = Math.Min(xB + 4, plotRect.Right - lblB.Width - 8);

            dc.DrawRectangle(LegendBgBrush, BorderPen, new Rect(boxAX - 2, plotRect.Top + 3, lblA.Width + 6, lblA.Height + 2));
            dc.DrawText(lblA, new Point(boxAX + 1, plotRect.Top + 4));

            dc.DrawRectangle(LegendBgBrush, BorderPen, new Rect(boxBX - 2, plotRect.Top + 22, lblB.Width + 6, lblB.Height + 2));
            dc.DrawText(lblB, new Point(boxBX + 1, plotRect.Top + 23));
        }

        // Legenda de penas no canto superior esquerdo (com valores nos cursores A/B quando ativos)
        double legX = plotRect.Left + 10;
        double legY = plotRect.Top + (_showCursors ? 44 : 8);
        DateTime cursorATime = _minTimeUtc.AddTicks((long)(_cursorARatio * spanTicks));
        DateTime cursorBTime = _minTimeUtc.AddTicks((long)(_cursorBRatio * spanTicks));

        foreach (var s in _series)
        {
            var itemBrush = new SolidColorBrush(s.Color);
            itemBrush.Freeze();

            string labelText = s.IsBoolean ? $"{s.Alias} [D]" : s.Alias;
            if (s.Points.Count > 0)
            {
                if (_showCursors)
                {
                    double vA = SampleValueAt(s.Points, cursorATime);
                    double vB = SampleValueAt(s.Points, cursorBTime);
                    labelText += s.IsBoolean
                        ? $"   A:{(vA != 0 ? 1 : 0)}  B:{(vB != 0 ? 1 : 0)}"
                        : $"   A:{vA:G5}  B:{vB:G5}  (Δ:{vB - vA:G4})";
                }
                else
                {
                    double latest = s.Points[^1].Value;
                    labelText += s.IsBoolean ? $" = {(latest != 0 ? 1 : 0)}" : $" = {latest:G5}";
                }
            }

            var ft = CreateBoldText(labelText, 11, TextBrush, dpi);
            Rect box = new(legX, legY, ft.Width + 24, ft.Height + 4);
            dc.DrawRectangle(LegendBgBrush, BorderPen, box);
            dc.DrawRectangle(itemBrush, null, new Rect(legX + 5, legY + 5, 10, 10));
            dc.DrawText(ft, new Point(legX + 19, legY + 2));
            legY += box.Height + 3;
        }

        // Inspetor Crosshair ao passar o mouse
        if (_showCrosshair && _hoverPoint.HasValue && plotRect.Contains(_hoverPoint.Value))
        {
            double hx = _hoverPoint.Value.X;
            dc.DrawLine(CrosshairPen, new Point(hx, plotRect.Top), new Point(hx, plotRect.Bottom));

            double hoverRatio = Math.Clamp((hx - plotRect.Left) / plotRect.Width, 0.0, 1.0);
            DateTime hoverTime = _minTimeUtc.AddTicks((long)(hoverRatio * spanTicks));

            var lines = new List<string>(_series.Count + 1)
            {
                $"⏱ {hoverTime:HH:mm:ss.fff} UTC"
            };

            foreach (var s in _series)
            {
                if (s.Points.Count == 0) continue;
                double val = SampleValueAt(s.Points, hoverTime);
                string formatted = s.IsBoolean ? (val != 0 ? "1 (ON)" : "0 (OFF)") : val.ToString("G6", CultureInfo.InvariantCulture);
                lines.Add($"• {s.Alias}: {formatted}");
            }

            foreach (var m in _markers)
            {
                double markerX = TimeToX(m.TimestampUtc, plotRect, spanTicks);
                if (Math.Abs(markerX - hx) <= 12)
                {
                    lines.Add($"🚩 Marcador: {m.Text}");
                    break;
                }
            }

            string tooltipText = string.Join("\n", lines);
            var ft = CreateText(tooltipText, 11, TooltipTextBrush, dpi);
            double tipW = ft.Width + 14;
            double tipH = ft.Height + 10;
            double tipX = hx + 12 + tipW > plotRect.Right ? hx - tipW - 12 : hx + 12;
            double tipY = Math.Clamp(_hoverPoint.Value.Y - tipH / 2, plotRect.Top + 4, plotRect.Bottom - tipH - 4);

            dc.DrawRoundedRectangle(TooltipBgBrush, BorderPen, new Rect(tipX, tipY, tipW, tipH), 4, 4);
            dc.DrawText(ft, new Point(tipX + 7, tipY + 5));
        }

        dc.Pop();
    }

    private static double SampleValueAt(IReadOnlyList<ChartSeriesPoint> points, DateTime targetUtc)
    {
        if (points.Count == 0) return 0;
        int low = 0;
        int high = points.Count - 1;
        int bestIdx = -1;

        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            if (points[mid].TimestampUtc <= targetUtc)
            {
                bestIdx = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return bestIdx >= 0 ? points[bestIdx].Value : points[0].Value;
    }

    private static Rect GetPlotRect(double width, double height)
    {
        const double leftMargin = 66;
        const double rightMargin = 16;
        const double topMargin = 12;
        const double bottomMargin = 28;
        return new Rect(
            leftMargin,
            topMargin,
            Math.Max(10, width - leftMargin - rightMargin),
            Math.Max(10, height - topMargin - bottomMargin));
    }

    private double TimeToX(DateTime t, Rect plotRect, double spanTicks)
    {
        double ratio = (t - _minTimeUtc).Ticks / spanTicks;
        return plotRect.Left + Math.Clamp(ratio, 0.0, 1.0) * plotRect.Width;
    }

    private static double ValueToY(double val, double yMin, double yMax, Rect analogRect)
    {
        double ratio = (val - yMin) / Math.Max(1e-9, yMax - yMin);
        return analogRect.Bottom - Math.Clamp(ratio, 0.0, 1.0) * analogRect.Height;
    }

    private static FormattedText CreateText(string text, double size, Brush brush, double dpi) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, size, brush, dpi);

    private static FormattedText CreateBoldText(string text, double size, Brush brush, double dpi) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, BoldTypeface, size, brush, dpi);

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(hex)!;
        b.Freeze();
        return b;
    }

    private static Pen CreateFrozenPen(string hex, double thickness)
    {
        var p = new Pen(CreateFrozenBrush(hex), thickness);
        p.Freeze();
        return p;
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        var p = new Pen(b, thickness);
        p.Freeze();
        return p;
    }

    private static Pen CreateFrozenDashedPen(string hex, double thickness)
    {
        var p = new Pen(CreateFrozenBrush(hex), thickness)
        {
            DashStyle = new DashStyle([4, 3], 0)
        };
        p.Freeze();
        return p;
    }
}

