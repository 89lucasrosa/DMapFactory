namespace DMapFactory.Core.Tests;

public sealed class TrendChartScaleTests
{
    private static (double YMin, double YMax) CalculateScale(
        IReadOnlyList<double> values,
        double yPanOffset = 0.0,
        double yZoomFactor = 1.0)
    {
        double yMin = 0, yMax = 100;
        double min = double.MaxValue;
        double max = double.MinValue;
        bool hasValid = false;

        for (int i = 0; i < values.Count; i++)
        {
            double v = values[i];
            if (!double.IsNaN(v) && !double.IsInfinity(v))
            {
                if (v < min) min = v;
                if (v > max) max = v;
                hasValid = true;
            }
        }

        if (hasValid)
        {
            yMin = min;
            yMax = max;

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

            double center = (yMin + yMax) / 2.0 + yPanOffset * (yMax - yMin);
            double halfSpan = (yMax - yMin) / (2.0 * yZoomFactor);
            yMin = center - halfSpan;
            yMax = center + halfSpan;
        }

        return (yMin, yMax);
    }

    [Fact]
    public void FlatLine_AddsSymmetricMargin()
    {
        var (yMin, yMax) = CalculateScale([50.0, 50.0, 50.0]);
        Assert.True(yMin < 50.0);
        Assert.True(yMax > 50.0);
        Assert.Equal(50.0, (yMin + yMax) / 2.0, precision: 4);
    }

    [Fact]
    public void IgnoresNaNsAndInfinities()
    {
        var (yMin, yMax) = CalculateScale([double.NaN, 10.0, double.PositiveInfinity, 20.0, double.NegativeInfinity]);
        Assert.True(yMin < 10.0);
        Assert.True(yMax > 20.0);
    }

    [Fact]
    public void StandardRange_HasTenPercentPadding()
    {
        var (yMin, yMax) = CalculateScale([0.0, 100.0]);
        // Span = 100, pad = 10 -> [-10, 110]
        Assert.Equal(-10.0, yMin, precision: 4);
        Assert.Equal(110.0, yMax, precision: 4);
    }
}
