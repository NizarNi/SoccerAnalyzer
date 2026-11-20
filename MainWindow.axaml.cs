using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenCvSharp;
using System.Text.Json;
using Color = Avalonia.Media.Color;
using Rect = OpenCvSharp.Rect;
using Size = Avalonia.Size;
using Window = Avalonia.Controls.Window;

namespace SoccerAnalyzer;

public partial class MainWindow : Window
{
    static readonly (string Name, Color Color)[] Palette =
    {
        ("White", Colors.White), ("Yellow", Colors.Yellow), ("Red", Colors.Red), ("Cyan", Colors.Cyan),
        ("Lime", Colors.Lime), ("Orange", Colors.Orange), ("Magenta", Colors.Magenta),
        ("Blue", Colors.DodgerBlue), ("Black", Colors.Black),
    };
    static readonly double[] Speeds = { 0.25, 0.5, 1, 2 };

    readonly Project _project = new();
    readonly DispatcherTimer _timer = new();
    readonly Mat _frame = new();
    readonly History _history;
    VideoSource? _video;
    WriteableBitmap? _bitmap;
    bool _playing;
    bool _updatingSeek;
    CancellationTokenSource? _trackCts;
    string? _clipboard;

    public MainWindow() : this(Array.Empty<string>()) { }

    /// <summary>Usage: SoccerAnalyzer.exe [video] [project.json] [--frame N]</summary>
    public MainWindow(string[] args)
    {
        InitializeComponent();
        Surface.Project = _project;
        _history = new History(_project,
            () => Surface.Selected == null ? -1 : _project.Shapes.IndexOf(Surface.Selected),
            i => Surface.Select(i >= 0 && i < _project.Shapes.Count ? _project.Shapes[i] : null));

        foreach (var p in Palette) ColorBox.Items.Add(p.Name);
        ColorBox.SelectedIndex = 0;
        foreach (var s in Speeds) SpeedBox.Items.Add($"{s}x");
        SpeedBox.SelectedIndex = 2;
        SetColor(Colors.White);

        OpenBtn.Click += async (_, _) => await OpenVideoAsync();
        LinkBtn.Click += async (_, _) => await OpenFromLinkAsync(null, false);
        LoadBtn.Click += async (_, _) => await LoadProjectAsync();
        SaveBtn.Click += async (_, _) => await SaveProjectAsync();
        PlayBtn.Click += (_, _) => TogglePlay();
        PrevBtn.Click += (_, _) => Step(-1);
        NextBtn.Click += (_, _) => Step(1);
        UndoBtn.Click += (_, _) => Undo();
        RedoBtn.Click += (_, _) => Redo();
        TrackBtn.Click += (_, _) => TrackSelected(+1);
        TrackBackBtn.Click += (_, _) => TrackSelected(-1);
        ClearTrackBtn.Click += (_, _) => ClearTrack();
        DeleteBtn.Click += (_, _) => Surface.DeleteSelected();
        SnapBtn.Click += async (_, _) => await SaveSnapshotAsync();
        ExportBtn.Click += async (_, _) => await ExportAsync();
        KeyChk.IsCheckedChanged += (_, _) => Surface.KeyframeMode = KeyChk.IsChecked == true;
        ColorBox.SelectionChanged += (_, _) =>
        {
            if (ColorBox.SelectedIndex < 0) return;
            var c = Palette[ColorBox.SelectedIndex].Color;
            SetColor(c);
            ApplyToSelected(s => s.Color = VideoSurface.ToHex(c));
        };
        WidthBox.ValueChanged += (_, _) =>
        {
            double w = (double)(WidthBox.Value ?? 3);
            Surface.Thickness = w;
            ApplyToSelected(s => s.Thickness = w);
        };
        DurBox.ValueChanged += (_, _) =>
        {
            double d = (double)(DurBox.Value ?? 5);
            Surface.DurationSeconds = d;
            ApplyToSelected(s => s.EndFrame = s.StartFrame + Math.Max(1, (int)(d * (_video?.Fps ?? 25))));
        };
        Seek.ValueChanged += (_, e) => { if (!_updatingSeek && _video != null) ShowFrame((int)e.NewValue); };
        _timer.Tick += (_, _) => Tick();

        foreach (var rb in this.GetLogicalDescendants<RadioButton>())
            rb.IsCheckedChanged += (_, _) =>
            {
                if (rb.IsChecked == true && Enum.TryParse<Tool>((string?)rb.Tag, out var t)) Surface.Tool = t;
            };

        Surface.SamplePixel = SamplePixel;
        Surface.ColorPicked += c => { SetColor(c); ColorBox.SelectedIndex = -1; SetStatus($"Picked color #{c.R:X2}{c.G:X2}{c.B:X2}"); };
        Surface.SelectionChanged += s => { UpdateMarkers(); SetStatus(DescribeSelection(s)); };
        Surface.ProjectChanged += () => { Surface.InvalidateVisual(); UpdateMarkers(); };
        Surface.BeforeChange += () => _history.Checkpoint();
        Surface.TrackCorrected += (s, f, hadAuto) =>
        {
            if (RetrackChk.IsChecked == true && hadAuto) StartTracking(s, f, +1, checkpoint: false);
            else SetStatus($"Keyframe set at frame {f}.");
        };

        AddHandler(KeyDownEvent, OnKeyTunnel, RoutingStrategies.Tunnel);
        Closing += (_, _) => { _trackCts?.Cancel(); _video?.Dispose(); };
        Opened += (_, _) => ApplyArgs(args);
    }

    async void ApplyArgs(string[] args)
    {
        string? video = null, proj = null, exportPath = null, url = null; int frame = 0; double exp0 = 0, exp1 = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--url" && i + 1 < args.Length) { url = args[++i]; continue; }
            if (args[i] == "--export" && i + 3 < args.Length)
            {
                exportPath = args[++i];
                double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out exp0);
                double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out exp1);
            }
            else if (args[i] == "--frame" && i + 1 < args.Length) int.TryParse(args[++i], out frame);
            else if (args[i].EndsWith(".json", StringComparison.OrdinalIgnoreCase)) proj = args[i];
            else video ??= args[i];
        }
        try
        {
            if (proj != null)
            {
                var loaded = Project.Load(proj);
                _project.Shapes.AddRange(loaded.Shapes);
                video ??= loaded.VideoPath;
            }
            if (video != null) LoadVideo(video);
            if (frame > 0) ShowFrame(frame);
            if (url != null) await OpenFromLinkAsync(url, true);
            if (exportPath != null && _video != null)
            {
                // Command-line batch export: SoccerAnalyzer.exe video.mp4 project.json --export out.mp4 startSec endSec
                var dlg = new ExportDialog(_project, _video.Path, _video.Width, _video.Height, _video.FrameCount, _video.Fps);
                var err = await dlg.ExportRangeAsync(exp0, exp1, exportPath, true);
                File.WriteAllText(exportPath + ".log", err == null ? $"OK {dlg.EncoderDescription}" : "ERROR " + err);
                Close();
            }
        }
        catch (Exception ex) { SetStatus("Error: " + ex.Message); }
    }

    void SetStatus(string text) => Status.Text = text;

    static string DescribeSelection(Shape? s)
    {
        if (s == null) return "No selection.";
        int keys = s.Track.Count(t => t.IsKey), autos = s.Track.Count - keys;
        return $"Selected {s.Kind}" + (s.Track.Count > 0 ? $" (tracked: {autos} auto frames, {keys} keyframes)" : "");
    }

    void SetColor(Color c)
    {
        Surface.DrawColor = c;
        Swatch.Background = new SolidColorBrush(c);
    }

    Color? SamplePixel(int x, int y)
    {
        if (_frame.Empty() || x < 0 || y < 0 || x >= _frame.Width || y >= _frame.Height) return null;
        var v = _frame.At<Vec3b>(y, x);
        return Color.FromRgb(v.Item2, v.Item1, v.Item0);
    }

    void UpdateMarkers()
    {
        Markers.Shape = Surface.Selected;
        Markers.FrameCount = _video?.FrameCount ?? 1;
        Markers.InvalidateVisual();
    }

    // ---- Undo / redo / clipboard / shortcuts ----
    bool Busy()
    {
        if (_trackCts == null) return false;
        SetStatus("Tracking in progress; wait or cancel it first.");
        return true;
    }

    void Undo()
    {
        if (Busy()) return;
        SetStatus(_history.Undo() ? "Undo." : "Nothing to undo.");
        AfterHistory();
    }

    void Redo()
    {
        if (Busy()) return;
        SetStatus(_history.Redo() ? "Redo." : "Nothing to redo.");
        AfterHistory();
    }

    void AfterHistory() { Surface.InvalidateVisual(); UpdateMarkers(); }

    void ApplyToSelected(Action<Shape> change)
    {
        if (Surface.Tool != Tool.Select || Surface.Selected == null) return;
        _history.Checkpoint();
        change(Surface.Selected);
        Surface.InvalidateVisual();
        UpdateMarkers();
    }

    Shape CloneShape(Shape s) => JsonSerializer.Deserialize<Shape>(JsonSerializer.Serialize(s))!;

    void CopySelected()
    {
        if (Surface.Selected == null) return;
        _clipboard = JsonSerializer.Serialize(Surface.Selected);
        SetStatus("Copied.");
    }

    void PasteClipboard(double offset)
    {
        if (_clipboard == null || _video == null) return;
        var s = JsonSerializer.Deserialize<Shape>(_clipboard)!;
        int len = Math.Max(1, s.EndFrame - s.StartFrame), oldStart = s.StartFrame, f = Surface.Frame;
        s.StartFrame = f; s.EndFrame = f + len;
        foreach (var t in s.Track) t.Frame += f - oldStart; // keep the track aligned with the new start
        for (int i = 0; i < s.Points.Count; i++) s.Points[i] = new PointD(s.Points[i].X + offset, s.Points[i].Y + offset);
        _history.Checkpoint();
        _project.Shapes.Add(s);
        Surface.Select(s);
        Surface.InvalidateVisual();
        UpdateMarkers();
    }

    void OnKeyTunnel(object? sender, KeyEventArgs e)
    {
        var focus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        bool typing = focus is TextBox || focus is NumericUpDown || focus is ComboBox;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (typing) return;
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z: if (shift) Redo(); else Undo(); break;
                case Key.Y: Redo(); break;
                case Key.C: CopySelected(); break;
                case Key.V: PasteClipboard(0.02); break;
                case Key.D: CopySelected(); PasteClipboard(0.02); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left when alt: JumpKey(-1); break;
            case Key.Right when alt: JumpKey(+1); break;
            case Key.Left: Step(shift ? -(int)Math.Round(_video?.Fps ?? 25) : -1); break;
            case Key.Right: Step(shift ? (int)Math.Round(_video?.Fps ?? 25) : 1); break;
            default: return;
        }
        e.Handled = true;
    }

    void JumpKey(int dir)
    {
        var s = Surface.Selected;
        if (s == null || _video == null) return;
        var keys = s.Track.Where(t => t.IsKey).Select(t => t.Frame).OrderBy(x => x).ToList();
        int target = dir > 0 ? keys.FirstOrDefault(k => k > Surface.Frame, -1) : keys.LastOrDefault(k => k < Surface.Frame, -1);
        if (target >= 0) { Pause(); ShowFrame(target); }
    }

    // ---- Files ----
    async Task OpenVideoAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open video",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Video") { Patterns = new[] { "*.mp4", "*.mov", "*.mkv", "*.avi", "*.m4v" } } },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) LoadVideo(path);
    }

    async Task OpenFromLinkAsync(string? url, bool autoStart)
    {
        Pause();
        var dlg = new LinkDialog(url, autoStart);
        var path = await dlg.ShowDialog<string?>(this);
        if (path != null) LoadVideo(path);
    }

    public void LoadVideo(string path)
    {
        try
        {
            Pause();
            _video?.Dispose();
            _video = new VideoSource(path);
            _project.VideoPath = path;
            _bitmap = new WriteableBitmap(new PixelSize(_video.Width, _video.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            Surface.Bitmap = _bitmap;
            Surface.VideoW = _video.Width;
            Surface.VideoH = _video.Height;
            Surface.Fps = _video.Fps;
            _updatingSeek = true;
            Seek.Maximum = Math.Max(1, _video.FrameCount - 1);
            _updatingSeek = false;
            ShowFrame(0);
            UpdateMarkers();
            SetStatus($"{System.IO.Path.GetFileName(path)}: {_video.Width}x{_video.Height}, {_video.Fps:0.##} fps, {_video.FrameCount} frames.");
        }
        catch (Exception ex) { SetStatus("Error: " + ex.Message); }
    }

    async Task SaveProjectAsync()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project", DefaultExtension = "json", SuggestedFileName = "analysis.json",
            FileTypeChoices = new[] { new FilePickerFileType("Project") { Patterns = new[] { "*.json" } } },
        });
        var path = f?.TryGetLocalPath();
        if (path == null) return;
        _project.Save(path);
        SetStatus("Saved " + path);
    }

    async Task LoadProjectAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load project", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Project") { Patterns = new[] { "*.json" } } },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            var loaded = Project.Load(path);
            _history.Checkpoint();
            _project.Shapes.Clear();
            _project.Shapes.AddRange(loaded.Shapes);
            Surface.Select(null);
            if (loaded.VideoPath != null && File.Exists(loaded.VideoPath)) LoadVideo(loaded.VideoPath);
            Surface.InvalidateVisual();
            UpdateMarkers();
            SetStatus($"Loaded {loaded.Shapes.Count} drawings from {path}");
        }
        catch (Exception ex) { SetStatus("Error loading project: " + ex.Message); }
    }

    async Task SaveSnapshotAsync()
    {
        if (_video == null) return;
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save snapshot", DefaultExtension = "png", SuggestedFileName = "snapshot.png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } } },
        });
        var path = f?.TryGetLocalPath();
        if (path == null) return;
        var rt = new RenderTargetBitmap(new PixelSize(_video.Width, _video.Height), new Vector(96, 96));
        var snap = new VideoSurface
        {
            Bitmap = _bitmap, VideoW = _video.Width, VideoH = _video.Height, Frame = Surface.Frame,
            Fps = _video.Fps, Project = _project, ShowEditingAids = false,
        };
        snap.Measure(new Size(_video.Width, _video.Height));
        snap.Arrange(new Avalonia.Rect(0, 0, _video.Width, _video.Height));
        rt.Render(snap);
        rt.Save(path);
        SetStatus("Saved snapshot " + path);
    }

    async Task ExportAsync()
    {
        if (_video == null) { SetStatus("Open a video first."); return; }
        Pause();
        var dlg = new ExportDialog(_project, _video.Path, _video.Width, _video.Height, _video.FrameCount, _video.Fps);
        await dlg.ShowDialog(this);
    }

    // ---- Playback ----
    void ShowFrame(int index)
    {
        if (_video == null || _bitmap == null) return;
        index = Math.Clamp(index, 0, Math.Max(0, _video.FrameCount - 1));
        if (!_video.ReadAt(index, _frame)) return;
        Present(_video.Index);
    }

    void Present(int frameIndex)
    {
        if (_video == null || _bitmap == null || _frame.Empty()) return;
        FrameBlit.Copy(_frame, _bitmap);
        Surface.Frame = frameIndex;
        Surface.InvalidateVisual();
        _updatingSeek = true;
        Seek.Value = frameIndex;
        _updatingSeek = false;
        var t = TimeSpan.FromSeconds(frameIndex / _video.Fps);
        TimeText.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}  (frame {frameIndex})";
    }

    void TogglePlay() { if (_playing) Pause(); else Play(); }

    void Play()
    {
        if (_video == null) return;
        _playing = true;
        PlayBtn.Content = "Pause";
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(5, 1000.0 / (_video.Fps * Speeds[Math.Max(0, SpeedBox.SelectedIndex)])));
        _timer.Start();
    }

    void Pause()
    {
        _playing = false;
        PlayBtn.Content = "Play";
        _timer.Stop();
    }

    void Tick()
    {
        if (_video == null) return;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(5, 1000.0 / (_video.Fps * Speeds[Math.Max(0, SpeedBox.SelectedIndex)])));
        if (!_video.Read(_frame)) { Pause(); return; }
        Present(_video.Index);
    }

    void Step(int delta)
    {
        if (_video == null) return;
        Pause();
        ShowFrame(Surface.Frame + delta);
    }

    // ---- Tracking ----
    void TrackSelected(int dir)
    {
        if (_trackCts != null) { _trackCts.Cancel(); return; }
        var s = Surface.Selected;
        if (_video == null || s == null) { SetStatus("Select a drawing and put it on the target at the current frame first."); return; }
        StartTracking(s, Surface.Frame, dir, checkpoint: true);
    }

    async void StartTracking(Shape s, int startFrame, int dir, bool checkpoint)
    {
        if (_video == null || _trackCts != null) return;
        Pause();
        var roi = Tracking.TargetRegion(s, startFrame, _video.Width, _video.Height);
        var (bdx, bdy) = s.OffsetAt(startFrame);
        string path = _video.Path;
        int nextKey = s.Track.Where(t => t.IsKey && t.Frame > startFrame).Select(t => t.Frame).DefaultIfEmpty(int.MaxValue).Min();
        int endFrame = dir > 0
            ? Math.Min(Math.Max(s.EndFrame, startFrame + 1), nextKey == int.MaxValue ? int.MaxValue : nextKey - 1)
            : Math.Max(0, startFrame - Math.Max(1, (int)(Surface.DurationSeconds * _video.Fps)));
        endFrame = Math.Min(endFrame, _video.FrameCount - 1);
        if ((dir > 0 && endFrame <= startFrame) || (dir < 0 && endFrame >= startFrame))
        { SetStatus("Nothing to track in that direction."); return; }

        _trackCts = new CancellationTokenSource();
        var ct = _trackCts.Token;
        var button = dir > 0 ? TrackBtn : TrackBackBtn;
        var label = button.Content;
        button.Content = "Cancel tracking";
        SetStatus("Tracking...");
        var progress = new Progress<int>(f => SetStatus($"Tracking... frame {f}"));
        var result = await Task.Run(() => Tracking.Run(path, startFrame, endFrame, dir, roi, bdx, bdy, progress, ct));
        _trackCts.Dispose();
        _trackCts = null;
        button.Content = label;

        if (!_project.Shapes.Contains(s)) return; // deleted while tracking
        if (result.Samples.Count > 1)
        {
            if (checkpoint) _history.Checkpoint();
            MergeTrack(s, result, startFrame, dir, nextKey);
            if (dir < 0) s.StartFrame = Math.Min(s.StartFrame, result.LastTrackedFrame);
            else s.EndFrame = Math.Max(s.EndFrame, result.LastTrackedFrame);
            SetStatus(result.Uncertain
                ? $"Tracker lost confidence at frame {result.LastTrackedFrame + (dir > 0 ? 1 : -1)} (red mark). Go there, drag the drawing onto the target to fix it; tracking continues from your fix."
                : $"Tracked {result.Samples.Count} frames.");
        }
        else
            SetStatus("Tracking failed: " + (result.Error ?? "the target could not be followed from this frame.") + " Try moving the drawing so it sits exactly on the target.");
        Surface.InvalidateVisual();
        UpdateMarkers();
    }

    static void MergeTrack(Shape s, TrackResult r, int start, int dir, int nextKey)
    {
        if (dir > 0)
        {
            s.Track.RemoveAll(t => !t.IsKey && t.Frame >= start && t.Frame < nextKey);
            s.TrackStop = r.Uncertain ? r.LastTrackedFrame + 1 : -1;
        }
        else
        {
            int lo = r.Samples.Min(t => t.Frame);
            s.Track.RemoveAll(t => !t.IsKey && t.Frame <= start && t.Frame >= lo);
        }
        foreach (var t in r.Samples)
        {
            var existing = s.Track.FirstOrDefault(x => x.Frame == t.Frame);
            if (existing is { IsKey: true }) continue; // never overwrite a hand-placed position
            s.InsertSample(t);
        }
    }

    void ClearTrack()
    {
        var s = Surface.Selected;
        if (s == null || Busy()) return;
        _history.Checkpoint();
        s.Track.Clear();
        s.TrackStop = -1;
        SetStatus("Track cleared.");
        Surface.InvalidateVisual();
        UpdateMarkers();
    }
}

static class VisualExt
{
    public static IEnumerable<T> GetLogicalDescendants<T>(this Avalonia.LogicalTree.ILogical root) where T : class
    {
        foreach (var c in Avalonia.LogicalTree.LogicalExtensions.GetLogicalChildren(root))
        {
            if (c is T t) yield return t;
            foreach (var d in c.GetLogicalDescendants<T>()) yield return d;
        }
    }
}
