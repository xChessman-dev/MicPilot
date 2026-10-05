using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MicPilot.App.Controls;

public sealed class HistoryGraph : FrameworkElement
{
    private readonly Queue<double> _history = new();
    public bool IsLevel { get; set; }
    public void Add(double value) { _history.Enqueue(value); while (_history.Count > 120) _history.Dequeue(); InvalidateVisual(); }
    public void Clear() { _history.Clear(); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth; var h = ActualHeight;
        if (w < 40 || h < 30) return;
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(63, 72, 86)), 1); grid.Freeze();
        var textBrush = new SolidColorBrush(Color.FromRgb(178, 189, 205)); textBrush.Freeze();
        var right = w - 4; const double left = 38; var bottom = h - 21;
        for (var i = 0; i < 3; i++)
        {
            var y = 4 + i * (bottom - 4) / 2;
            dc.DrawLine(grid, new(left, y), new(right, y));
            var label = IsLevel ? (i * -30).ToString() : (10 - i * 5).ToString();
            dc.DrawText(Text(label, textBrush), new(0, y - 6));
        }
        dc.DrawText(Text(IsLevel ? "dBFS · последние 12 с" : "% CPU · последние 120 с", textBrush), new(left, h - 16));
        if (_history.Count == 0) { dc.DrawText(Text("Пока нет данных", textBrush), new(left + 8, bottom / 2)); return; }
        var samples = _history.ToArray(); var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < samples.Length; i++)
            {
                var x = left + (right - left) * (120 - samples.Length + i) / 119;
                var normalized = IsLevel ? (Math.Clamp(samples[i], -60, 0) + 60) / 60 : Math.Clamp(samples[i], 0, 10) / 10;
                var y = bottom - normalized * (bottom - 4);
                if (i == 0) context.BeginFigure(new(x, y), false, false); else context.LineTo(new(x, y), true, false);
            }
        }
        geometry.Freeze(); var pen = new Pen(new SolidColorBrush(IsLevel ? Color.FromRgb(246, 189, 96) : Color.FromRgb(137, 182, 255)), 2); pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
    private FormattedText Text(string value, Brush brush) => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
