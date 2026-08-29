using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ZeroAccel;

public sealed class CurveView : FrameworkElement
{
    private readonly DrawingVisual marker = new();
    private Rect plot = Rect.Empty;
    private double? lastMoveSpeed;
    internal double XMaximum { get; private set; } = 80;
    internal double YMinimum { get; private set; }
    internal double YMaximum { get; private set; } = 2;
    internal Rect PlotBounds => plot;
    internal bool ShowLastMove { get; set; }
    internal Point? LastMovePoint { get; private set; }
    public CurveView() => AddVisualChild(marker);
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? marker : throw new ArgumentOutOfRangeException(nameof(index));

    internal void SetLastMove(double speed)
    {
        if (!ShowLastMove || !double.IsFinite(speed) || speed < 0) return;
        lastMoveSpeed = speed;
        DrawMarker(); // The overlay never recalculates the curve or rescales its axes.
    }

    internal void ClearLastMove() { lastMoveSpeed = null; DrawMarker(); }
    internal CurveConfig Config { get; set; } = CurveConfig.From(new Settings());
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 120 || h < 100) { plot = Rect.Empty; DrawMarker(); return; }
        var text = (Brush)FindResource("TextBrush");
        var muted = (Brush)FindResource("SubtleBrush");
        var line = new Pen((Brush)((Brush)FindResource("BorderBrush")).GetCurrentValueAsFrozen(), 1); line.Freeze();
        double left = 52, right = w - 18, top = 18, bottom = h - 39;
        // Settings, not the live mouse speed, determine the viewport. Leave room
        // beyond a high offset/input cap without hiding small FPS multipliers.
        double extent = Config.Mode == 0 ? 80 : Math.Max(80, Config.Offset + 20);
        if (Config.Mode is 1 or 2 && Config.CapType is 1 or 2)
            extent = Math.Max(extent, Config.CapInput * 11 / 10);
        XMaximum = Math.Ceiling(extent / 20) * 20;
        const int points = 240;
        Span<double> gains = stackalloc double[points + 1];
        double low = double.MaxValue, high = 0;
        for (int i = 0; i <= points; i++)
        {
            gains[i] = EvaluateGain(i * XMaximum / points);
            low = Math.Min(low, gains[i]);
            high = Math.Max(high, gains[i]);
        }
        double pad = Math.Max(low * .05, (high - low) * .1);
        double step = NiceStep((high - low + 2 * pad) / 5);
        YMinimum = Math.Max(0, Math.Floor((low - pad) / step) * step);
        YMaximum = Math.Ceiling((high + pad) / step) * step;
        plot = new Rect(left, top, right - left, bottom - top);
        int intervals = (int)Math.Round((YMaximum - YMinimum) / step);
        for (int i = 0; i <= intervals; i++)
        {
            double value = YMinimum + i * step;
            double y = bottom - plot.Height * i / intervals;
            dc.DrawLine(line, new Point(left, y), new Point(right, y));
            DrawText(dc, AxisNumber(value) + "×", 0, y - 6, muted, 10);
        }
        for (int i = 0; i <= 4; i++)
        {
            double x = left + plot.Width * i / 4;
            DrawText(dc, AxisNumber(XMaximum * i / 4), x, bottom + 9, muted, 10, true);
        }
        DrawText(dc, (string)FindResource("S_CurveAxis"), 0, 0, muted, 10);
        DrawText(dc, (string)FindResource("S_CurveAxisX"), left, h - 12, muted, 10);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (int i = 0; i <= points; i++)
            {
                var p = MapPoint(i * XMaximum / points, gains[i]);
                if (i == 0) context.BeginFigure(p, false, false); else context.LineTo(p, true, false);
            }
        }
        geometry.Freeze();
        var pen = new Pen((Brush)text.GetCurrentValueAsFrozen(), 2); pen.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(left - 2, top - 2, plot.Width + 4, plot.Height + 4)));
        dc.DrawGeometry(null, pen, geometry);
        dc.Pop();
        DrawMarker();
    }

    private static double NiceStep(double value)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double scaled = value / magnitude;
        return magnitude * (scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 2.5 ? 2.5 : scaled <= 5 ? 5 : 10);
    }

    private static string AxisNumber(double value) =>
        value.ToString(value >= 10000 ? "0.#E+0" : "0.####", CultureInfo.InvariantCulture);

    // The curve and marker share both the native evaluator and coordinate map.
    internal double EvaluateGain(double speed)
    {
        double gain=RawAccelProtocol.Response(Config,speed);
        // Incomplete edits never feed NaN into WPF geometry.
        return double.IsFinite(gain) && gain>=0 ? gain : Config.Sensitivity;
    }
    private Point MapPoint(double speed, double gain) => new(
        plot.Left + plot.Width * speed / XMaximum,
        plot.Bottom - Math.Clamp((gain - YMinimum) / (YMaximum - YMinimum), 0, 1) * plot.Height);

    private void DrawMarker()
    {
        using var dc = marker.RenderOpen();
        LastMovePoint = null;
        if (!ShowLastMove || lastMoveSpeed is not double speed || plot.IsEmpty) return;
        speed = Math.Min(speed, XMaximum);
        var point = MapPoint(speed, EvaluateGain(speed));
        // Freeze snapshots, never the shared mutable theme resources: freezing
        // a Pen also freezes its Brush and would break retained text updates.
        var guide = new Pen((Brush)((Brush)FindResource("BorderBrush")).GetCurrentValueAsFrozen(), 1) { DashStyle = DashStyles.Dot };
        guide.Freeze();
        dc.DrawLine(guide, point, new Point(point.X, plot.Bottom));
        var outline = new Pen((Brush)((Brush)FindResource("BackgroundBrush")).GetCurrentValueAsFrozen(), 2); outline.Freeze();
        dc.DrawEllipse((Brush)FindResource("TextBrush"), outline, point, 4.5, 4.5);
        LastMovePoint = point;
    }

    private void DrawText(DrawingContext dc, string value, double x, double y, Brush brush, double size, bool centered = false)
    {
        var label = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new Point(centered ? x - label.Width / 2 : x, y));
    }
}
