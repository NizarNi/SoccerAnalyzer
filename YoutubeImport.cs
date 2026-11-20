using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SoccerAnalyzer;

public sealed record DownloadResult(string? FilePath, string? Error);

/// <summary>
/// Downloads a video from a link by running the user's own yt-dlp. The app never installs or updates yt-dlp,
/// starts it only on an explicit request, and passes the link as a single argument (no shell).
/// </summary>
public static class YoutubeImport
{
    public const string InstallHint = "Install yt-dlp with:  winget install yt-dlp.yt-dlp   (or put yt-dlp.exe next to SoccerAnalyzer.exe), then try again.";
    const string PathMarker = "SA_PATH:";
    static readonly Regex ProgressRx = new(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
    static readonly Regex MergeRx = new(@"\[Merger\] Merging formats into ""(.+)""", RegexOptions.Compiled);
    static readonly Regex DestRx = new(@"\[download\] Destination: (.+)$", RegexOptions.Compiled);
    static readonly Regex ExistsRx = new(@"\[download\] (.+) has already been downloaded", RegexOptions.Compiled);

    public static bool TryValidateUrl(string? text, out string url, out string error)
    {
        url = ""; error = "";
        var t = (text ?? "").Trim();
        if (t.Length == 0) { error = "Paste a video link first."; return false; }
        if (t.Any(char.IsControl) || t.Any(char.IsWhiteSpace)) { error = "The link must be a single line without spaces."; return false; }
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host))
        { error = "Enter a full http(s) link, for example https://www.youtube.com/watch?v=..."; return false; }
        url = uri.AbsoluteUri;
        return true;
    }

    /// <summary>yt-dlp arguments. MP4/H.264 is requested because the decoder used here reads it reliably.</summary>
    public static List<string> BuildArgs(string url, string folder, int maxHeight, bool haveFfmpeg)
    {
        string h = maxHeight.ToString();
        string format = haveFfmpeg
            ? $"bv*[vcodec^=avc1][height<={h}]+ba[ext=m4a]/b[vcodec^=avc1][height<={h}]/b[ext=mp4][height<={h}]/b[height<={h}]/b"
            : $"b[ext=mp4][height<={h}]/b[height<={h}]/b"; // single file: no merging, so no ffmpeg needed
        var a = new List<string>
        {
            "--no-playlist", "--no-mtime", "--restrict-filenames", "--newline", "--no-quiet", "--encoding", "utf-8",
            "-P", folder, "-o", "%(title).80s [%(id)s].%(ext)s", "-f", format,
        };
        if (haveFfmpeg) { a.Add("--merge-output-format"); a.Add("mp4"); }
        a.Add("--print"); a.Add("after_move:" + PathMarker + "%(filepath)s");
        a.Add("--"); a.Add(url);
        return a;
    }

    public static double? ParseProgress(string line)
    {
        var m = ProgressRx.Match(line);
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    /// <summary>Extracts the saved file path from one line of yt-dlp output, or null.</summary>
    public static string? ParsePath(string line)
    {
        if (line.StartsWith(PathMarker, StringComparison.Ordinal)) return line[PathMarker.Length..].Trim();
        foreach (var rx in new[] { MergeRx, ExistsRx, DestRx })
        {
            var m = rx.Match(line);
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    public static async Task<DownloadResult> RunAsync(
        string ytdlpPath, string url, string folder, int maxHeight, bool haveFfmpeg,
        IProgress<(double Percent, string Message)>? progress, CancellationToken ct)
    {
        if (!TryValidateUrl(url, out var clean, out var err)) return new(null, err);
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) { return new(null, "Cannot use that folder: " + ex.Message); }

        var started = DateTime.UtcNow;
        var psi = new ProcessStartInfo(ytdlpPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in BuildArgs(clean, folder, maxHeight, haveFfmpeg)) psi.ArgumentList.Add(a);

        string? path = null, lastError = null;
        var tail = new Queue<string>();
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (tail) { tail.Enqueue(line); if (tail.Count > 6) tail.Dequeue(); }
            if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) lastError = line;
            var p = ParsePath(line);
            if (p != null && (path == null || line.StartsWith(PathMarker, StringComparison.Ordinal))) path = p;
            var pct = ParseProgress(line);
            if (pct != null) progress?.Report((pct.Value, $"Downloading... {pct.Value:0}%"));
            else if (line.StartsWith("[Merger]")) progress?.Report((100, "Merging video and audio..."));
        }

        Process proc;
        try { proc = Process.Start(psi) ?? throw new IOException("Could not start yt-dlp."); }
        catch (Exception ex) { return new(null, "Could not start yt-dlp: " + ex.Message); }
        using (proc)
        {
            proc.OutputDataReceived += (_, e) => OnLine(e.Data);
            proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            using var reg = ct.Register(() => { try { proc.Kill(true); } catch { /* already exited */ } });
            await proc.WaitForExitAsync(CancellationToken.None);
            // Let the async readers drain before judging the result.
            proc.WaitForExit();

            if (ct.IsCancellationRequested)
            {
                CleanPartials(folder, started);
                return new(null, "Cancelled.");
            }
            if (proc.ExitCode != 0)
            {
                CleanPartials(folder, started);
                string msg = lastError ?? string.Join(" | ", tail);
                if (msg.Contains("Unable to extract", StringComparison.OrdinalIgnoreCase) || msg.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase))
                    msg += "  (yt-dlp may be out of date: run  yt-dlp -U  or  winget upgrade yt-dlp.yt-dlp)";
                return new(null, string.IsNullOrWhiteSpace(msg) ? $"yt-dlp failed (exit code {proc.ExitCode})." : msg);
            }
        }
        if (path == null || !File.Exists(path)) return new(null, "yt-dlp finished but the saved file could not be found.");
        return new(path, null);
    }

    /// <summary>Removes incomplete download files created by this run (only .part / .ytdl files newer than the start time).</summary>
    static void CleanPartials(string folder, DateTime startedUtc)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder))
            {
                var ext = Path.GetExtension(f);
                if ((ext == ".part" || ext == ".ytdl" || f.EndsWith(".part-Frag0", StringComparison.Ordinal)) && File.GetLastWriteTimeUtc(f) >= startedUtc.AddSeconds(-2))
                    File.Delete(f);
            }
        }
        catch { /* best effort */ }
    }
}
