using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OpenCvSharp;

namespace SoccerAnalyzer;

/// <summary>
/// Opens paths the video decoder cannot read directly. OpenCV's decoder fails on Windows file names that are not plain
/// ASCII (accents, full-width punctuation, emoji: common in downloaded titles), so such files are opened through an
/// ASCII alias: the 8.3 short path when available, otherwise a hard link (no extra disk space) in the app's own folder.
/// </summary>
public static class AsciiPath
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint cch);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr securityAttributes);

    public static bool IsAscii(string s) => s.All(c => c < 128);

    static string LinkDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoccerAnalyzer", "links");

    /// <summary>Candidate ASCII paths for the same file, cheapest first. Empty when the path is already ASCII.</summary>
    public static IEnumerable<string> Aliases(string path)
    {
        if (IsAscii(path) || !File.Exists(path)) yield break;

        var sb = new StringBuilder(520);
        if (GetShortPathNameW(path, sb, (uint)sb.Capacity) is > 0 and < 520 && IsAscii(sb.ToString()) && File.Exists(sb.ToString()))
            yield return sb.ToString();

        string? link = TryHardLink(path);
        if (link != null) yield return link;
    }

    static string? TryHardLink(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..12];
            string link = Path.Combine(LinkDir, id + Path.GetExtension(full));
            Directory.CreateDirectory(LinkDir);
            if (File.Exists(link))
            {
                var a = new FileInfo(full); var b = new FileInfo(link);
                if (a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc) return link; // still the same file
                File.Delete(link); // the original was replaced: make a fresh link
            }
            return CreateHardLinkW(link, full, IntPtr.Zero) ? link : null; // fails across drives: caller reports it
        }
        catch { return null; }
    }
}

/// <summary>Frame-indexed video decoding. The same decoder backs display and tracking so frame numbers always agree.</summary>
public sealed class VideoSource : IDisposable
{
    readonly VideoCapture _cap;
    public int Width { get; }
    public int Height { get; }
    public int FrameCount { get; }
    public double Fps { get; }
    /// <summary>The path the caller asked for (not the alias used internally).</summary>
    public string Path { get; }
    /// <summary>Index of the most recently read frame, or -1.</summary>
    public int Index { get; private set; } = -1;

    public VideoSource(string path)
    {
        Path = path;
        _cap = OpenCapture(path);
        Width = (int)_cap.Get(VideoCaptureProperties.FrameWidth);
        Height = (int)_cap.Get(VideoCaptureProperties.FrameHeight);
        FrameCount = (int)_cap.Get(VideoCaptureProperties.FrameCount);
        var fps = _cap.Get(VideoCaptureProperties.Fps);
        Fps = fps > 1 ? fps : 25;
    }

    static VideoCapture OpenCapture(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The video file does not exist: " + path);
        var cap = new VideoCapture(path);
        if (cap.IsOpened()) return cap;
        cap.Dispose();
        foreach (var alias in AsciiPath.Aliases(path))
        {
            var c = new VideoCapture(alias);
            if (c.IsOpened()) return c;
            c.Dispose();
        }
        string hint = AsciiPath.IsAscii(path)
            ? "The file may be damaged or use a codec this decoder does not support (H.264 MP4 is the safest)."
            : "The file name contains special characters the decoder cannot read, and a safe alias could not be created (is the file on another drive?). Rename it using plain letters and digits.";
        throw new IOException("Cannot open video: " + path + "\n" + hint);
    }

    public void Seek(int frame) => _cap.Set(VideoCaptureProperties.PosFrames, Math.Max(0, frame));

    public bool Read(Mat dst)
    {
        if (!_cap.Read(dst) || dst.Empty()) return false;
        Index = (int)_cap.Get(VideoCaptureProperties.PosFrames) - 1;
        return true;
    }

    public bool ReadAt(int frame, Mat dst)
    {
        Seek(frame);
        return Read(dst);
    }

    public void Dispose() => _cap.Dispose();
}
