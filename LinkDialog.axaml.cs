using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace SoccerAnalyzer;

/// <summary>Asks for a link, downloads it with the user's yt-dlp, and returns the saved file path on success.</summary>
public partial class LinkDialog : Window
{
    static readonly int[] Heights = { 720, 1080 };
    string? _ytdlp, _ffmpeg;
    CancellationTokenSource? _cts;

    public LinkDialog() : this(null, false) { }

    public LinkDialog(string? prefilledUrl, bool autoStart)
    {
        InitializeComponent();
        UrlBox.Text = prefilledUrl;
        FolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SoccerAnalyzer");
        foreach (var h in Heights) QualityBox.Items.Add($"{h}p");
        QualityBox.SelectedIndex = 1;
        RefreshTools();
        FindToolBtn.Click += async (_, _) =>
        {
            var f = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select yt-dlp.exe", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("yt-dlp") { Patterns = new[] { "yt-dlp*.exe" } } },
            });
            var p = f.FirstOrDefault()?.TryGetLocalPath();
            if (p != null) { ToolLocator.Remember("yt-dlp.exe", p); RefreshTools(); }
        };

        BrowseBtn.Click += async (_, _) =>
        {
            var f = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Save videos to", AllowMultiple = false });
            var p = f.FirstOrDefault()?.TryGetLocalPath();
            if (p != null) FolderBox.Text = p;
        };
        StartBtn.Click += async (_, _) => await StartOrCancelAsync();
        CloseBtn.Click += (_, _) => { _cts?.Cancel(); Close(null); };
        Closing += (_, _) => _cts?.Cancel();
        if (autoStart) Opened += async (_, _) => await StartOrCancelAsync();
    }

    void RefreshTools()
    {
        _ytdlp = ToolLocator.FindYtDlp();
        _ffmpeg = ToolLocator.FindFfmpeg();
        ToolInfo.Text = _ytdlp == null
            ? "yt-dlp was not found. " + YoutubeImport.InstallHint
            : "yt-dlp: " + _ytdlp + (_ffmpeg != null ? "\nffmpeg found: best quality, audio merged." : "\nffmpeg not found: a single combined file is downloaded (install ffmpeg for higher quality).");
        StartBtn.IsEnabled = _ytdlp != null;
        FindToolBtn.IsVisible = _ytdlp == null;
    }

    async Task StartOrCancelAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        if (_ytdlp == null) { StatusText.Text = YoutubeImport.InstallHint; return; }
        if (!YoutubeImport.TryValidateUrl(UrlBox.Text, out var url, out var err)) { StatusText.Text = err; return; }
        var folder = FolderBox.Text?.Trim();
        if (string.IsNullOrEmpty(folder)) { StatusText.Text = "Choose a folder to save to."; return; }

        _cts = new CancellationTokenSource();
        StartBtn.Content = "Cancel";
        Progress.Value = 0;
        StatusText.Text = "Starting...";
        var progress = new Progress<(double Percent, string Message)>(p => { Progress.Value = p.Percent; StatusText.Text = p.Message; });
        var result = await YoutubeImport.RunAsync(_ytdlp, url, folder, Heights[Math.Max(0, QualityBox.SelectedIndex)], _ffmpeg != null, progress, _cts.Token);
        _cts.Dispose(); _cts = null;
        StartBtn.Content = "Download and open";
        if (result.FilePath != null) Close(result.FilePath);
        else StatusText.Text = result.Error ?? "Download failed.";
    }
}
