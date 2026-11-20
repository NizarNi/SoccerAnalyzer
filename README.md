# SoccerAnalyzer

<img src="Assets/logo.png" width="96" align="right" alt="SoccerAnalyzer logo">

A Windows desktop app for analysing soccer video: draw on the footage, make the drawings follow players
automatically, and export the annotated clip.

Written in C# with [Avalonia](https://avaloniaui.net/) (UI), [OpenCvSharp](https://github.com/shimat/opencvsharp)
(decoding and tracking) and optional `yt-dlp` / `ffmpeg` integrations. Status: early, usable, actively changing.

## Features
- **Playback** with frame-accurate stepping in both directions, speed control, a timeline and keyboard shortcuts.
- **Drawing tools:** line, arrow, dashed arrow, ring, striped zone, freehand, and a **spotlight** (beam + ground ring);
  colors with an eyedropper that samples the video frame; line width; per-drawing visibility time.
- **Auto-tracking for any drawing.** The tracker follows the target under the drawing (for a spotlight, the player
  standing in the ring). Track forward or backward from the current frame. If it loses confidence it stops and marks
  the frame on the timeline instead of drifting silently.
- **Manual correction:** drag a tracked drawing onto the target to set a keyframe; tracking restarts from your fix.
  *Keyframe mode* animates a drawing by hand with no tracker.
- **Undo / redo** (Ctrl+Z, Ctrl+Y), copy / paste / duplicate, and editable endpoints.
- **Export** the annotated video (MP4, with the original audio when ffmpeg is installed) and PNG snapshots.
- **Open from a link:** download a video with your own `yt-dlp` and open it straight away.
- Projects are saved as JSON.

## Requirements
- Windows 10/11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source.
- Optional: [`ffmpeg`](https://ffmpeg.org/) (H.264 export with audio, best download quality) and
  [`yt-dlp`](https://github.com/yt-dlp/yt-dlp) (Open from link). Both are found automatically (app folder, `tools\`,
  PATH, WinGet); the app never installs them.
  `winget install yt-dlp.yt-dlp` and `winget install Gyan.FFmpeg`

## Build and run
```powershell
git clone https://github.com/NizarNi/SoccerAnalyzer.git
cd SoccerAnalyzer
dotnet run -c Release
# self-contained build:
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```
Command line: `SoccerAnalyzer.exe [video] [project.json] [--frame N] [--url <link>] [--export out.mp4 startSec endSec]`

## Notes
- Video is decoded by OpenCV, so H.264 MP4 is the safest format. File names with special characters are handled.
- Downloading from a site is subject to that site's terms and the video owner's rights; use it for your own analysis.
- Not done yet: automatic player detection and team colors, pitch calibration, a text-label tool, zoom and pan.

## License
MIT, see [LICENSE](LICENSE).
