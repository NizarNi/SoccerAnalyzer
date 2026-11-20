using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using OpenCvSharp;
using Window = Avalonia.Controls.Window;
using Size = Avalonia.Size;

namespace SoccerAnalyzer;

/// <summary>Renders the video with the drawings burned in and encodes it (H.264 + audio through ffmpeg when available).</summary>
public partial class ExportDialog : Window
{
    readonly Project _project;
    readonly string _videoPath;
    readonly int _w, _h, _frames;
    readonly double _fps;
    CancellationTokenSource? _cts;
    readonly string? _ffmpeg;

    public ExportDialog() { InitializeComponent(); _project = new(); _videoPath = ""; }

    public ExportDialog(Project project, string videoPath, int w, int h, int frames, double fps)
    {
        InitializeComponent();
        (_project, _videoPath, _w, _h, _frames, _fps) = (project, videoPath, w, h, frames, fps);
        _ffmpeg = ToolLocator.FindFfmpeg();
        EncoderInfo.Text = _ffmpeg != null
            ? "Encoder: H.264 via ffmpeg (" + _ffmpeg + ")."
            : "ffmpeg was not found, so export uses a compatible MPEG-4 encoder WITHOUT audio. For H.264 and audio, install ffmpeg (winget install Gyan.FFmpeg) or place ffmpeg.exe next to SoccerAnalyzer.exe, then reopen this dialog.";
        if (_ffmpeg == null) AudioChk.IsChecked = false;

        var shapes = project.Shapes;
        SetRange(shapes.Count > 0 ? shapes.Min(s => s.StartFrame) : 0, shapes.Count > 0 ? shapes.Max(s => s.EndFrame) : frames - 1);
        OutBox.Text = Path.Combine(Path.GetDirectoryName(videoPath) ?? "", Path.GetFileNameWithoutExtension(videoPath) + "_annotated.mp4");

        AnnotatedBtn.Click += (_, _) => SetRange(shapes.Count > 0 ? shapes.Min(s => s.StartFrame) : 0, shapes.Count > 0 ? shapes.Max(s => s.EndFrame) : frames - 1);
        WholeBtn.Click += (_, _) => SetRange(0, frames - 1);
        BrowseBtn.Click += async (_, _) =>
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export video", DefaultExtension = "mp4", SuggestedFileName = Path.GetFileName(OutBox.Text),
                FileTypeChoices = new[] { new FilePickerFileType("MP4 video") { Patterns = new[] { "*.mp4" } } },
            });
            var p = f?.TryGetLocalPath();
            if (p != null) OutBox.Text = p;
        };
        StartBtn.Click += async (_, _) => await StartOrCancelAsync();
        CloseBtn.Click += (_, _) => { _cts?.Cancel(); Close(); };
        Closing += (_, _) => _cts?.Cancel();
    }

    void SetRange(int startFrame, int endFrame)
    {
        StartBox.Text = Format(Math.Clamp(startFrame, 0, _frames - 1) / _fps);
        EndBox.Text = Format(Math.Clamp(endFrame, 0, _frames - 1) / _fps);
    }

    static string Format(double sec) => TimeSpan.FromSeconds(sec).ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture);

    static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }

    async Task StartOrCancelAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        if (!TryParseTime(StartBox.Text, out var t0) || !TryParseTime(EndBox.Text, out var t1) || t1 <= t0)
        { StatusText.Text = "Enter a valid time range (From must be before To)."; return; }
        int startFrame = Math.Clamp((int)Math.Round(t0 * _fps), 0, _frames - 1);
        int endFrame = Math.Clamp((int)Math.Round(t1 * _fps), startFrame, _frames - 1);
        string outPath = OutBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(outPath)) { StatusText.Text = "Choose an output file."; return; }
        if (string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(_videoPath), StringComparison.OrdinalIgnoreCase))
        { StatusText.Text = "The output must be a different file from the source video."; return; }

        _cts = new CancellationTokenSource();
        StartBtn.Content = "Cancel";
        string? error = null; bool cancelled = false;
        try { await ExportAsync(startFrame, endFrame, outPath, AudioChk.IsChecked == true && _ffmpeg != null, _cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { error = ex.Message; }
        _cts.Dispose(); _cts = null;
        StartBtn.Content = "Export";
        if (cancelled) { TryDelete(outPath); StatusText.Text = "Export cancelled."; }
        else if (error != null) { TryDelete(outPath); StatusText.Text = "Export failed: " + error; }
        else StatusText.Text = "Exported " + outPath;
    }

    /// <summary>Headless export of a time range; returns an error message, or null on success.</summary>
    public async Task<string?> ExportRangeAsync(double startSec, double endSec, string outPath, bool audio, CancellationToken ct = default)
    {
        int a = Math.Clamp((int)Math.Round(startSec * _fps), 0, _frames - 1);
        int b = Math.Clamp((int)Math.Round(endSec * _fps), a, _frames - 1);
        try { await ExportAsync(a, b, outPath, audio && _ffmpeg != null, ct); return null; }
        catch (Exception ex) { TryDelete(outPath); return ex.Message; }
    }

    public string EncoderDescription => _ffmpeg != null ? "ffmpeg H.264" : "OpenCV mp4v (no audio)";

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ } }

    async Task ExportAsync(int startFrame, int endFrame, string outPath, bool withAudio, CancellationToken ct)
    {
        int total = endFrame - startFrame + 1;
        using var src = new VideoSource(_videoPath);
        var bmp = new WriteableBitmap(new PixelSize(_w, _h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        var surface = new VideoSurface
        {
            Bitmap = bmp, VideoW = _w, VideoH = _h, Fps = _fps, Project = _project, ShowEditingAids = false,
        };
        surface.Measure(new Size(_w, _h));
        surface.Arrange(new Avalonia.Rect(0, 0, _w, _h));
        var rt = new RenderTargetBitmap(new PixelSize(_w, _h), new Vector(96, 96));

        Process? ff = null;
        Stream? ffIn = null;
        VideoWriter? writer = null;
        var errLines = new Queue<string>();
        if (_ffmpeg != null)
        {
            var psi = new ProcessStartInfo(_ffmpeg)
            {
                RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            void A(params string[] a) { foreach (var x in a) psi.ArgumentList.Add(x); }
            A("-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{_w}x{_h}",
              "-r", _fps.ToString("F4", CultureInfo.InvariantCulture), "-i", "-");
            if (withAudio)
                A("-ss", (startFrame / _fps).ToString("F3", CultureInfo.InvariantCulture), "-t", (total / _fps).ToString("F3", CultureInfo.InvariantCulture), "-i", _videoPath, "-map", "0:v", "-map", "1:a?");
            A("-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
              "-c:a", "aac", "-b:a", "160k", "-shortest", "-movflags", "+faststart", outPath);
            ff = Process.Start(psi) ?? throw new IOException("Cannot start ffmpeg.");
            ff.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errLines) { errLines.Enqueue(e.Data); if (errLines.Count > 8) errLines.Dequeue(); } };
            ff.BeginErrorReadLine();
            ffIn = ff.StandardInput.BaseStream;
        }
        else
        {
            writer = new VideoWriter(outPath, FourCC.MP4V, _fps, new OpenCvSharp.Size(_w, _h));
            if (!writer.IsOpened()) throw new IOException("Cannot create the output video.");
        }

        try
        {
            var buf = new byte[_w * _h * 4];
            using var mat = new Mat();
            using var bgrOut = new Mat(_h, _w, MatType.CV_8UC3);
            using var bgraOut = new Mat(_h, _w, MatType.CV_8UC4);
            src.Seek(startFrame);
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!src.Read(mat)) break;
                FrameBlit.Copy(mat, bmp);
                surface.Frame = startFrame + i;
                rt.Render(surface);
                var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try { rt.CopyPixels(new PixelRect(0, 0, _w, _h), handle.AddrOfPinnedObject(), buf.Length, _w * 4); }
                finally { handle.Free(); }

                if (ffIn != null) await ffIn.WriteAsync(buf, 0, buf.Length, ct);
                else
                {
                    Marshal.Copy(buf, 0, bgraOut.Data, buf.Length);
                    Cv2.CvtColor(bgraOut, bgrOut, ColorConversionCodes.BGRA2BGR);
                    writer!.Write(bgrOut);
                }
                if (i % 4 == 0)
                {
                    Progress.Value = (i + 1) / (double)total;
                    StatusText.Text = $"Rendering frame {i + 1} / {total}";
                    await Task.Yield();
                }
            }
            if (ff != null)
            {
                ffIn!.Close();
                await ff.WaitForExitAsync(ct);
                if (ff.ExitCode != 0)
                    throw new IOException("ffmpeg failed: " + string.Join(" | ", errLines.ToArray()));
            }
            Progress.Value = 1;
        }
        finally
        {
            writer?.Dispose();
            if (ff is { HasExited: false }) { try { ff.Kill(true); } catch { /* already gone */ } }
            ff?.Dispose();
        }
    }
}
