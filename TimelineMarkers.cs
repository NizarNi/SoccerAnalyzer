using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SoccerAnalyzer;

/// <summary>Strip above the seek bar showing the selected drawing's visible range, tracked range, keyframes and stop point.</summary>
public sealed class TimelineMarkers : Control
{
    public Shape? Shape { get; set; }
    public int FrameCount { get; set; } = 1;
    const double Inset = 9; // matches the slider thumb inset

    double X(int frame) => Inset + Math.Clamp(frame / (double)Math.Max(1, FrameCount - 1), 0, 1) * Math.Max(1, Bounds.Width - 2 * Inset);

    public override void Render(DrawingContext ctx)
    {
        var s = Shape;
        if (s == null) return;
        double h = Bounds.Height;
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 120, 170, 255)), null, new Rect(X(s.StartFrame), h - 6, Math.Max(2, X(s.EndFrame) - X(s.StartFrame)), 4));
        if (s.Track.Count > 0)
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(70, 200, 90)), null, new Rect(X(s.Track[0].Frame), h - 6, Math.Max(2, X(s.Track[^1].Frame) - X(s.Track[0].Frame)), 4));
        foreach (var k in s.Track.Where(t => t.IsKey))
        {
            double x = X(k.Frame), y = h / 2 - 1;
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(new Point(x, y - 5), true); c.LineTo(new Point(x + 5, y)); c.LineTo(new Point(x, y + 5)); c.LineTo(new Point(x - 5, y)); c.EndFigure(true); }
            ctx.DrawGeometry(Brushes.Yellow, new Pen(Brushes.Black, 1), g);
        }
        if (s.TrackStop >= 0)
            ctx.DrawLine(new Pen(Brushes.Red, 2), new Point(X(s.TrackStop), 0), new Point(X(s.TrackStop), h));
    }
}
