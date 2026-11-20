using System.Text.Json;

namespace SoccerAnalyzer;

public enum ShapeKind { Line, Arrow, DashedArrow, Ring, Zone, Freehand, Pole }

/// <summary>Point in normalized video coordinates (0..1 on both axes).</summary>
public struct PointD
{
    public double X { get; set; }
    public double Y { get; set; }
    public PointD(double x, double y) { X = x; Y = y; }
}

/// <summary>Displacement of a shape from its base geometry at one frame (set by auto-tracking).</summary>
public class TrackSample
{
    public int Frame { get; set; }
    public double Dx { get; set; }
    public double Dy { get; set; }
    /// <summary>True when the user placed this position by hand (an anchor); false for auto-tracked samples.</summary>
    public bool IsKey { get; set; }
}

public class Shape
{
    public ShapeKind Kind { get; set; }
    public List<PointD> Points { get; set; } = new();
    public string Color { get; set; } = "#FFFFFF";
    public double Thickness { get; set; } = 3;
    public int StartFrame { get; set; }
    public int EndFrame { get; set; }
    public List<TrackSample> Track { get; set; } = new();
    /// <summary>Frame where auto-tracking stopped because confidence dropped; -1 when tracking ran to completion.</summary>
    public int TrackStop { get; set; } = -1;

    public bool VisibleAt(int frame) => frame >= StartFrame && frame <= EndFrame;

    public void InsertSample(TrackSample t)
    {
        Track.RemoveAll(x => x.Frame == t.Frame);
        int i = Track.FindIndex(x => x.Frame > t.Frame);
        if (i < 0) Track.Add(t); else Track.Insert(i, t);
    }

    /// <summary>Tracked displacement at a frame: linear interpolation, clamped at both ends, zero without a track.</summary>
    public (double dx, double dy) OffsetAt(int frame)
    {
        if (Track.Count == 0) return (0, 0);
        if (frame <= Track[0].Frame) return (Track[0].Dx, Track[0].Dy);
        var last = Track[^1];
        if (frame >= last.Frame) return (last.Dx, last.Dy);
        int lo = 0, hi = Track.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (Track[mid].Frame <= frame) lo = mid; else hi = mid;
        }
        var a = Track[lo]; var b = Track[hi];
        double t = (frame - a.Frame) / (double)(b.Frame - a.Frame);
        return (a.Dx + (b.Dx - a.Dx) * t, a.Dy + (b.Dy - a.Dy) * t);
    }
}

public class Project
{
    public string? VideoPath { get; set; }
    public List<Shape> Shapes { get; set; } = new();

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    public static Project Load(string path) =>
        JsonSerializer.Deserialize<Project>(File.ReadAllText(path), Options) ?? new Project();
}

/// <summary>Drawing proportions shared by the renderer and the tracker. All values are in video pixels.</summary>
public static class ShapeGeometry
{
    public static double Dist(double ax, double ay, double bx, double by) =>
        Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));

    /// <summary>Half-width of the spotlight beam, derived from its height.</summary>
    public static double PoleHalfWidth(double height) => Math.Max(6, height * 0.12);

    /// <summary>Horizontal radius of a ring defined by center and edge points.</summary>
    public static double RingRadius(double cx, double cy, double ex, double ey) =>
        Math.Max(2, Math.Abs(ex - cx) + Math.Abs(ey - cy) * 0.5);

    public const double RingFlatten = 0.4;
}

/// <summary>Snapshot-based undo/redo over the project's shape list.</summary>
public sealed class History
{
    sealed record Snap(string Json, int Selected);
    readonly List<Snap> _undo = new(), _redo = new();
    readonly Project _project;
    readonly Func<int> _getSelected;
    readonly Action<int> _setSelected;
    const int Limit = 100;

    public History(Project project, Func<int> getSelected, Action<int> setSelected)
    {
        _project = project; _getSelected = getSelected; _setSelected = setSelected;
    }

    Snap Take() => new(JsonSerializer.Serialize(_project.Shapes), _getSelected());

    void Restore(Snap s)
    {
        var list = JsonSerializer.Deserialize<List<Shape>>(s.Json) ?? new();
        _project.Shapes.Clear();
        _project.Shapes.AddRange(list);
        _setSelected(s.Selected);
    }

    /// <summary>Call immediately before changing the shapes.</summary>
    public void Checkpoint()
    {
        _undo.Add(Take());
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Add(Take());
        var s = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        Restore(s);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Add(Take());
        var s = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        Restore(s);
        return true;
    }
}
