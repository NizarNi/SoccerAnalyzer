using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SoccerAnalyzer;

public enum Tool { Select, Line, Arrow, DashedArrow, Ring, Zone, Freehand, Pole, Eyedrop }

/// <summary>
/// Draws the current video frame and the overlay, and edits drawings with the mouse. Shapes are drawn in
/// video-pixel space, so the same code produces identical output on screen and in exported video.
/// </summary>
public sealed class VideoSurface : Control
{
    public WriteableBitmap? Bitmap { get; set; }
    public int VideoW { get; set; }
    public int VideoH { get; set; }
    public int Frame { get; set; }
    public double Fps { get; set; } = 25;
    public Project Project { get; set; } = new();
    public Tool Tool { get; set; } = Tool.Select;
    public Color DrawColor { get; set; } = Colors.White;
    public double Thickness { get; set; } = 3;
    public double DurationSeconds { get; set; } = 5;
    /// <summary>When on, dragging a drawing sets keyframes at the current frame instead of editing its geometry.</summary>
    public bool KeyframeMode { get; set; }
    /// <summary>Selection outlines, handles and tracking markers. Off for exports and snapshots.</summary>
    public bool ShowEditingAids { get; set; } = true;
    public Shape? Selected { get; private set; }
    public Func<int, int, Color?>? SamplePixel { get; set; }

    public event Action<Shape?>? SelectionChanged;
    public event Action<Color>? ColorPicked;
    public event Action? ProjectChanged;
    /// <summary>Raised once, just before a user edit changes the drawings (for undo).</summary>
    public event Action? BeforeChange;
    /// <summary>A tracked drawing was moved by hand at a frame. Args: shape, frame, whether it had auto-tracked samples.</summary>
    public event Action<Shape, int, bool>? TrackCorrected;

    Shape? _draft;
    bool _dragging, _moved, _dragKey;
    int _handle = -1;
    PointD _lastDrag;
    TrackSample? _key;

    public VideoSurface() { Focusable = true; ClipToBounds = true; }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public void Select(Shape? s)
    {
        Selected = s;
        SelectionChanged?.Invoke(s);
        InvalidateVisual();
    }

    Rect VideoRect()
    {
        if (VideoW <= 0 || VideoH <= 0) return default;
        double s = Math.Min(Bounds.Width / VideoW, Bounds.Height / VideoH);
        double w = VideoW * s, h = VideoH * s;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    PointD ToNorm(Point p, Rect r) => new((p.X - r.X) / r.Width, (p.Y - r.Y) / r.Height);
    Point ToScreen(PointD p, Rect r, double dx, double dy) => new(r.X + (p.X + dx) * r.Width, r.Y + (p.Y + dy) * r.Height);

    // ---------------------------------------------------------------- rendering

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        var r = VideoRect();
        if (r.Width <= 0) return;
        if (Bitmap != null) ctx.DrawImage(Bitmap, new Rect(0, 0, VideoW, VideoH), r);
        double k = r.Width / VideoW;
        using (ctx.PushTransform(Matrix.CreateScale(k, k) * Matrix.CreateTranslation(r.X, r.Y)))
        {
            foreach (var s in Project.Shapes)
                if (s.VisibleAt(Frame)) DrawShape(ctx, s);
            if (_draft != null) DrawShape(ctx, _draft);
        }
        if (!ShowEditingAids) return;

        foreach (var s in Project.Shapes)
        {
            if (!s.VisibleAt(Frame)) continue;
            var (dx, dy) = s.OffsetAt(Frame);
            if (s.Track.Count > 0 && s.Points.Count > 0)
            {
                bool key = s.Track.Any(t => t.Frame == Frame && t.IsKey);
                ctx.DrawEllipse(key ? Brushes.Yellow : Brushes.Orange, new Pen(Brushes.Black, 1), ToScreen(s.Points[0], r, dx, dy), 4, 4);
            }
        }
        if (Selected is { } sel && sel.VisibleAt(Frame))
        {
            var nb = ShapeBoundsNorm(sel);
            if (nb != null)
            {
                var box = new Rect(r.X + nb.Value.X * r.Width - 4, r.Y + nb.Value.Y * r.Height - 4, nb.Value.Width * r.Width + 8, nb.Value.Height * r.Height + 8);
                ctx.DrawRectangle(null, new Pen(Brushes.DeepSkyBlue, 1, new DashStyle(new double[] { 4, 3 }, 0)), box);
            }
            var (sdx, sdy) = sel.OffsetAt(Frame);
            foreach (int i in HandleIndices(sel))
            {
                var c = ToScreen(sel.Points[i], r, sdx, sdy);
                var pen = new Pen(Brushes.DeepSkyBlue, 1.5);
                if (i == 0 && sel.Kind is ShapeKind.Pole or ShapeKind.Ring) ctx.DrawEllipse(Brushes.White, pen, c, 6, 6); // move grip
                else ctx.DrawRectangle(Brushes.White, pen, new Rect(c - new Vector(5, 5), new Size(10, 10)));       // resize/edit handle
            }
        }
    }

    static IEnumerable<int> HandleIndices(Shape s) => s.Kind switch
    {
        ShapeKind.Freehand => Enumerable.Empty<int>(),
        ShapeKind.Zone => Enumerable.Range(0, s.Points.Count),
        _ => Enumerable.Range(0, Math.Min(2, s.Points.Count)),
    };

    void DrawShape(DrawingContext ctx, Shape s)
    {
        var (dx, dy) = s.OffsetAt(Frame);
        var color = Color.Parse(s.Color);
        var pen = new Pen(new SolidColorBrush(color), s.Thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var pts = s.Points.Select(p => new Point((p.X + dx) * VideoW, (p.Y + dy) * VideoH)).ToList();
        if (pts.Count == 0) return;

        switch (s.Kind)
        {
            case ShapeKind.Line when pts.Count >= 2:
                ctx.DrawLine(pen, pts[0], pts[1]);
                break;
            case ShapeKind.Arrow when pts.Count >= 2:
                ctx.DrawLine(pen, pts[0], pts[1]);
                DrawArrowHead(ctx, pts[0], pts[1], s.Thickness, color);
                break;
            case ShapeKind.DashedArrow when pts.Count >= 2:
            {
                var dashed = new Pen(new SolidColorBrush(color), s.Thickness * 1.8, new DashStyle(new double[] { 1.2, 1.0 }, 0), PenLineCap.Flat);
                var v = pts[1] - pts[0];
                double len = Math.Sqrt(v.X * v.X + v.Y * v.Y), head = 10 + s.Thickness * 2;
                var shaftEnd = len > head ? pts[1] - new Vector(v.X / len, v.Y / len) * head : pts[0];
                ctx.DrawLine(dashed, pts[0], shaftEnd);
                DrawArrowHead(ctx, pts[0], pts[1], s.Thickness, color);
                break;
            }
            case ShapeKind.Ring when pts.Count >= 2:
            {
                double rx = ShapeGeometry.RingRadius(pts[0].X, pts[0].Y, pts[1].X, pts[1].Y);
                ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B)), pen, pts[0], rx, rx * ShapeGeometry.RingFlatten);
                break;
            }
            case ShapeKind.Zone when pts.Count >= 2:
            {
                var g = Polygon(pts, true);
                ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B)), pen, g);
                var area = PointsBounds(pts, 0);
                using (ctx.PushGeometryClip(g))
                {
                    double band = Math.Max(10, area.Width / 8);
                    var light = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255));
                    for (double x = area.X; x < area.Right; x += band * 2)
                        ctx.DrawRectangle(light, null, new Rect(x, area.Y, band, area.Height));
                }
                break;
            }
            case ShapeKind.Freehand when pts.Count >= 2:
                ctx.DrawGeometry(null, pen, Polygon(pts, false));
                break;
            case ShapeKind.Pole when pts.Count >= 2:
                DrawPole(ctx, pts[0], pts[1], color, s.Thickness);
                break;
        }
    }

    static StreamGeometry Polygon(IReadOnlyList<Point> pts, bool closed)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(pts[0], closed);
        for (int i = 1; i < pts.Count; i++) c.LineTo(pts[i]);
        c.EndFigure(closed);
        return g;
    }

    static void DrawArrowHead(DrawingContext ctx, Point from, Point to, double thickness, Color color)
    {
        var v = to - from;
        double len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        if (len < 1) return;
        var u = new Vector(v.X / len, v.Y / len);
        var n = new Vector(-u.Y, u.X);
        double size = 10 + thickness * 2;
        var g = new StreamGeometry();
        using (var c = g.Open()) { c.BeginFigure(to, true); c.LineTo(to - u * size + n * (size * 0.5)); c.LineTo(to - u * size - n * (size * 0.5)); c.EndFigure(true); }
        ctx.DrawGeometry(new SolidColorBrush(color), null, g);
    }

    /// <summary>Spotlight: ring on the ground at the base point and a light beam fading upward to the second point.</summary>
    static void DrawPole(DrawingContext ctx, Point bottom, Point top, Color color, double thickness)
    {
        double hw = ShapeGeometry.PoleHalfWidth(ShapeGeometry.Dist(bottom.X, bottom.Y, top.X, top.Y));
        double ry = hw * ShapeGeometry.RingFlatten;
        var beam = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(150, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(70, color.R, color.G, color.B), 0.5),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };
        ctx.DrawRectangle(beam, null, new Rect(bottom.X - hw, Math.Min(top.Y, bottom.Y), hw * 2, Math.Abs(bottom.Y - top.Y)));
        var ring = new Pen(new SolidColorBrush(Color.FromArgb(230, color.R, color.G, color.B)), Math.Max(1.5, thickness * 0.6));
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)), ring, bottom, hw * 1.25, ry * 1.25);
        ctx.DrawEllipse(null, ring, bottom, hw * 0.8, ry * 0.8);
    }

    static Rect PointsBounds(IReadOnlyList<Point> pts, double pad)
    {
        double x1 = pts.Min(p => p.X), y1 = pts.Min(p => p.Y), x2 = pts.Max(p => p.X), y2 = pts.Max(p => p.Y);
        return new Rect(x1 - pad, y1 - pad, x2 - x1 + 2 * pad, y2 - y1 + 2 * pad);
    }

    /// <summary>Normalized bounds (0..1) of what the drawing covers at the current frame, including ring and beam extents.</summary>
    public Rect? ShapeBoundsNorm(Shape s)
    {
        if (s.Points.Count == 0 || VideoW <= 0) return null;
        var (dx, dy) = s.OffsetAt(Frame);
        double x1 = s.Points.Min(p => p.X) + dx, x2 = s.Points.Max(p => p.X) + dx;
        double y1 = s.Points.Min(p => p.Y) + dy, y2 = s.Points.Max(p => p.Y) + dy;
        if (s.Points.Count >= 2 && (s.Kind == ShapeKind.Pole || s.Kind == ShapeKind.Ring))
        {
            var a = s.Points[0]; var b = s.Points[1];
            double padX, padY;
            if (s.Kind == ShapeKind.Pole)
            {
                double hw = ShapeGeometry.PoleHalfWidth(ShapeGeometry.Dist(a.X * VideoW, a.Y * VideoH, b.X * VideoW, b.Y * VideoH));
                (padX, padY) = (hw * 1.25 / VideoW, hw * 0.5 / VideoH);
                x1 = a.X + dx - padX; x2 = a.X + dx + padX; y2 = Math.Max(y2, a.Y + dy + padY);
            }
            else
            {
                double rx = ShapeGeometry.RingRadius(a.X * VideoW, a.Y * VideoH, b.X * VideoW, b.Y * VideoH);
                (padX, padY) = (rx / VideoW, rx * ShapeGeometry.RingFlatten / VideoH);
                x1 = a.X + dx - padX; x2 = a.X + dx + padX; y1 = a.Y + dy - padY; y2 = a.Y + dy + padY;
            }
        }
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    // ---------------------------------------------------------------- interaction

    Shape? HitTest(Point p, Rect r)
    {
        for (int i = Project.Shapes.Count - 1; i >= 0; i--)
        {
            var s = Project.Shapes[i];
            if (!s.VisibleAt(Frame)) continue;
            var nb = ShapeBoundsNorm(s);
            if (nb == null) continue;
            var box = new Rect(r.X + nb.Value.X * r.Width - 8, r.Y + nb.Value.Y * r.Height - 8, nb.Value.Width * r.Width + 16, nb.Value.Height * r.Height + 16);
            if (box.Contains(p)) return s;
        }
        return null;
    }

    int HandleAt(Point pos, Rect r)
    {
        if (Selected is not { } s || !s.VisibleAt(Frame)) return -1;
        var (dx, dy) = s.OffsetAt(Frame);
        foreach (int i in HandleIndices(s))
        {
            var h = ToScreen(s.Points[i], r, dx, dy);
            if (Math.Abs(h.X - pos.X) <= 8 && Math.Abs(h.Y - pos.Y) <= 8) return i;
        }
        return -1;
    }

    TrackSample EnsureKey(Shape s, int frame)
    {
        var ex = s.Track.FirstOrDefault(t => t.Frame == frame);
        if (ex != null) { ex.IsKey = true; return ex; }
        var (dx, dy) = s.OffsetAt(frame);
        var k = new TrackSample { Frame = frame, Dx = dx, Dy = dy, IsKey = true };
        s.InsertSample(k);
        return k;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var r = VideoRect();
        if (r.Width <= 0) return;
        var pos = e.GetPosition(this);
        var n = ToNorm(pos, r);
        var props = e.GetCurrentPoint(this).Properties;

        if (Tool == Tool.Select)
        {
            _moved = false; _handle = -1; _dragging = false; _key = null;
            int hi = HandleAt(pos, r);
            // The base point of a spotlight or ring is a "move" grip: it drags the whole drawing (and sets a keyframe when tracked).
            bool moveGrip = hi == 0 && Selected is { Kind: ShapeKind.Pole or ShapeKind.Ring };
            if (hi >= 0 && !moveGrip) { _handle = hi; _lastDrag = n; e.Pointer.Capture(this); return; }
            if (moveGrip)
            {
                _dragging = true; _lastDrag = n;
                _dragKey = KeyframeMode || Selected!.Track.Count > 0;
                e.Pointer.Capture(this);
                return;
            }
            if (!r.Contains(pos)) return;
            Select(HitTest(pos, r));
            if (Selected != null)
            {
                _dragging = true; _lastDrag = n;
                _dragKey = KeyframeMode || Selected.Track.Count > 0;
                e.Pointer.Capture(this);
            }
            return;
        }
        if (!r.Contains(pos)) return;
        if (Tool == Tool.Eyedrop)
        {
            var c = SamplePixel?.Invoke((int)(n.X * VideoW), (int)(n.Y * VideoH));
            if (c != null) ColorPicked?.Invoke(c.Value);
            return;
        }
        if (Tool == Tool.Zone)
        {
            if (props.IsRightButtonPressed) { FinishZone(); return; }
            _draft ??= NewShape(ShapeKind.Zone);
            _draft.Points.Add(n);
            InvalidateVisual();
            return;
        }
        _draft = NewShape(ToKind(Tool));
        _draft.Points.Add(n);
        _draft.Points.Add(n);
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var r = VideoRect();
        if (r.Width <= 0) return;
        var n = ToNorm(e.GetPosition(this), r);

        if (Selected != null && (_dragging || _handle >= 0))
        {
            double dx = n.X - _lastDrag.X, dy = n.Y - _lastDrag.Y;
            if (!_moved)
            {
                _moved = true;
                BeforeChange?.Invoke();
                if (_handle < 0 && _dragKey) _key = EnsureKey(Selected, Frame);
            }
            if (_handle >= 0)
                Selected.Points[_handle] = new PointD(Selected.Points[_handle].X + dx, Selected.Points[_handle].Y + dy);
            else if (_key != null) { _key.Dx += dx; _key.Dy += dy; }
            else
                for (int i = 0; i < Selected.Points.Count; i++)
                    Selected.Points[i] = new PointD(Selected.Points[i].X + dx, Selected.Points[i].Y + dy);
            _lastDrag = n;
            InvalidateVisual();
            return;
        }
        if (_draft == null) return;
        if (_draft.Kind == ShapeKind.Freehand && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) _draft.Points.Add(n);
        else if (_draft.Kind != ShapeKind.Zone && _draft.Points.Count >= 2) _draft.Points[1] = n;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging || _handle >= 0)
        {
            e.Pointer.Capture(null);
            bool corrected = _moved && _handle < 0 && _key != null && Selected != null;
            var shape = Selected;
            _dragging = false; _handle = -1;
            if (corrected && shape != null)
            {
                int f = Frame;
                bool hadAuto = shape.Track.Any(t => !t.IsKey);
                int nextKey = shape.Track.Where(t => t.IsKey && t.Frame > f).Select(t => t.Frame).DefaultIfEmpty(int.MaxValue).Min();
                shape.Track.RemoveAll(t => !t.IsKey && t.Frame > f && t.Frame < nextKey); // stale after the fix
                shape.TrackStop = -1;
                TrackCorrected?.Invoke(shape, f, hadAuto);
            }
            if (_moved) ProjectChanged?.Invoke();
            _key = null;
            return;
        }
        if (_draft == null || _draft.Kind == ShapeKind.Zone) return;
        e.Pointer.Capture(null);
        var r = VideoRect();
        double dist = _draft.Points.Count >= 2
            ? Math.Sqrt(Math.Pow((_draft.Points[^1].X - _draft.Points[0].X) * r.Width, 2) + Math.Pow((_draft.Points[^1].Y - _draft.Points[0].Y) * r.Height, 2))
            : 0;
        if (dist >= 4) Commit(_draft);
        _draft = null;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Delete && Selected != null) DeleteSelected();
        else if (e.Key == Key.Enter) FinishZone();
        else if (e.Key == Key.Escape) { _draft = null; InvalidateVisual(); }
    }

    public void DeleteSelected()
    {
        if (Selected == null) return;
        BeforeChange?.Invoke();
        Project.Shapes.Remove(Selected);
        Select(null);
        ProjectChanged?.Invoke();
    }

    void FinishZone()
    {
        if (_draft is { Kind: ShapeKind.Zone } && _draft.Points.Count >= 3) Commit(_draft);
        _draft = null;
        InvalidateVisual();
    }

    void Commit(Shape s)
    {
        BeforeChange?.Invoke();
        Project.Shapes.Add(s);
        Select(s);
        ProjectChanged?.Invoke();
    }

    Shape NewShape(ShapeKind kind) => new()
    {
        Kind = kind,
        Color = ToHex(DrawColor),
        Thickness = Thickness,
        StartFrame = Frame,
        EndFrame = Frame + Math.Max(1, (int)(DurationSeconds * Fps)),
    };

    static ShapeKind ToKind(Tool t) => t switch
    {
        Tool.Line => ShapeKind.Line,
        Tool.Arrow => ShapeKind.Arrow,
        Tool.DashedArrow => ShapeKind.DashedArrow,
        Tool.Ring => ShapeKind.Ring,
        Tool.Freehand => ShapeKind.Freehand,
        Tool.Pole => ShapeKind.Pole,
        _ => ShapeKind.Line,
    };
}
