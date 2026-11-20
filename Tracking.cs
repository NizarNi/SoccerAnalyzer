using OpenCvSharp;
using OpenCvSharp.Tracking;

namespace SoccerAnalyzer;

/// <param name="Uncertain">The tracker stopped because confidence dropped, not because the range ended.</param>
public sealed record TrackResult(List<TrackSample> Samples, int LastTrackedFrame, bool Uncertain, string? Error);

/// <summary>
/// Follows the image patch under a drawing (a CSRT tracker) and returns per-frame displacements. Which patch is
/// followed is decided by the drawing kind (<see cref="TargetRegion"/>), not by the drawing's bounding box.
/// Tracking stops when the patch no longer resembles the target so the user can fix the position by hand.
/// </summary>
public static class Tracking
{
    // Stop after this many consecutive low-similarity frames or on a single implausible jump.
    const double MinSimilarityToPrevious = 0.20;
    const double MinSimilarityToInitial = -0.05;
    const int LowFramesToStop = 4;
    const double MaxJumpFractionOfWidth = 0.06;

    /// <summary>The patch that identifies the tracked object, in video pixels.</summary>
    public static Rect TargetRegion(Shape s, int frame, int vw, int vh)
    {
        var (dx, dy) = s.OffsetAt(frame);
        var p = s.Points.Select(q => new Point2d((q.X + dx) * vw, (q.Y + dy) * vh)).ToList();
        if (p.Count == 0) return default;
        double cx, cy, w, h;
        switch (s.Kind)
        {
            case ShapeKind.Pole when p.Count >= 2:
            {
                double height = ShapeGeometry.Dist(p[0].X, p[0].Y, p[1].X, p[1].Y);
                double rx = ShapeGeometry.PoleHalfWidth(height) * 1.25; // ring radius = body width reference
                (cx, cy, w, h) = (p[0].X, p[0].Y - 0.7 * rx, 1.3 * rx, 1.6 * rx); // the player standing in the ring
                break;
            }
            case ShapeKind.Ring when p.Count >= 2:
            {
                double rx = ShapeGeometry.RingRadius(p[0].X, p[0].Y, p[1].X, p[1].Y);
                (cx, cy, w, h) = (p[0].X, p[0].Y - 0.7 * rx, 1.3 * rx, 1.6 * rx);
                break;
            }
            case ShapeKind.Line or ShapeKind.Arrow or ShapeKind.DashedArrow:
                (cx, cy, w, h) = (p[0].X, p[0].Y, 40, 40); // the tail, which usually sits on the player
                break;
            default: // zones and freehand: the whole footprint
                double x1 = p.Min(q => q.X), x2 = p.Max(q => q.X), y1 = p.Min(q => q.Y), y2 = p.Max(q => q.Y);
                (cx, cy, w, h) = ((x1 + x2) / 2, (y1 + y2) / 2, x2 - x1, y2 - y1);
                break;
        }
        w = Math.Max(24, w); h = Math.Max(24, h);
        return new Rect((int)(cx - w / 2), (int)(cy - h / 2), (int)w, (int)h);
    }

    /// <summary>Frames from <paramref name="start"/> toward <paramref name="end"/> (inclusive); the Mat is reused.</summary>
    static IEnumerable<(int Frame, Mat Image)> Frames(VideoSource v, int start, int end, int dir)
    {
        if (dir > 0)
        {
            using var m = new Mat();
            v.Seek(start);
            for (int f = start; f <= end; f++)
            {
                if (!v.Read(m)) yield break;
                yield return (f, m);
            }
            yield break;
        }
        // Backward: decode forward in blocks, then walk each block in reverse.
        const int Block = 40;
        for (int hi = start; hi >= end; hi -= Block)
        {
            int lo = Math.Max(end, hi - Block + 1);
            var block = new List<Mat>();
            try
            {
                v.Seek(lo);
                for (int f = lo; f <= hi; f++)
                {
                    var m = new Mat();
                    if (!v.Read(m)) { m.Dispose(); break; }
                    block.Add(m);
                }
                for (int i = block.Count - 1; i >= 0; i--) yield return (lo + i, block[i]);
                if (block.Count < hi - lo + 1) yield break;
            }
            finally { foreach (var m in block) m.Dispose(); }
        }
    }

    static Mat Patch(Mat frame, Rect box, Size size)
    {
        var r = new Rect(Math.Clamp(box.X, 0, frame.Width - 2), Math.Clamp(box.Y, 0, frame.Height - 2), 0, 0);
        r.Width = Math.Max(2, Math.Min(box.Width, frame.Width - r.X));
        r.Height = Math.Max(2, Math.Min(box.Height, frame.Height - r.Y));
        using var crop = new Mat(frame, r);
        var resized = new Mat();
        Cv2.Resize(crop, resized, size);
        return resized;
    }

    static double Similarity(Mat a, Mat b)
    {
        using var res = new Mat();
        Cv2.MatchTemplate(a, b, res, TemplateMatchModes.CCoeffNormed);
        var v = res.At<float>(0, 0);
        return double.IsNaN(v) ? 0 : v;
    }

    /// <summary>
    /// Track from <paramref name="startFrame"/> in direction <paramref name="dir"/> (+1 / -1) up to <paramref name="endFrame"/>.
    /// The first sample is the starting position and is marked as a user anchor.
    /// </summary>
    public static TrackResult Run(
        string videoPath, int startFrame, int endFrame, int dir, Rect roi,
        double baseDx, double baseDy, IProgress<int>? progress, CancellationToken ct, List<double>? diagnostics = null)
    {
        var samples = new List<TrackSample>();
        int lastGood = startFrame;
        try
        {
            using var video = new VideoSource(videoPath);
            roi = new Rect(Math.Max(0, roi.X), Math.Max(0, roi.Y),
                Math.Min(roi.Width, video.Width - Math.Max(0, roi.X)), Math.Min(roi.Height, video.Height - Math.Max(0, roi.Y)));
            if (roi.Width < 8 || roi.Height < 8) return new(samples, startFrame, false, "The drawing is outside the frame or too small to track.");

            var patchSize = new Size(Math.Max(8, roi.Width), Math.Max(8, roi.Height));
            using var tracker = TrackerCSRT.Create();
            Mat? initial = null, previous = null;
            double cx0 = roi.X + roi.Width / 2.0, cy0 = roi.Y + roi.Height / 2.0, pcx = cx0, pcy = cy0;
            int low = 0;
            bool first = true;

            foreach (var (f, frame) in Frames(video, startFrame, endFrame, dir))
            {
                ct.ThrowIfCancellationRequested();
                if (first)
                {
                    tracker.Init(frame, roi);
                    initial = Patch(frame, roi, patchSize);
                    previous = initial.Clone();
                    samples.Add(new TrackSample { Frame = f, Dx = baseDx, Dy = baseDy, IsKey = true });
                    first = false;
                    continue;
                }
                var box = roi;
                if (!tracker.Update(frame, ref box)) return Finish(samples, lastGood, true, initial, previous);
                double cx = box.X + box.Width / 2.0, cy = box.Y + box.Height / 2.0;

                using var cur = Patch(frame, box, patchSize);
                double simPrev = Similarity(cur, previous!), simInit = Similarity(cur, initial!);
                diagnostics?.Add(simPrev);
                double jump = ShapeGeometry.Dist(pcx, pcy, cx, cy);
                low = simPrev < MinSimilarityToPrevious || simInit < MinSimilarityToInitial ? low + 1 : 0;
                if (low >= LowFramesToStop || jump > MaxJumpFractionOfWidth * video.Width)
                    return Finish(samples, lastGood, true, initial, previous);

                samples.Add(new TrackSample { Frame = f, Dx = baseDx + (cx - cx0) / video.Width, Dy = baseDy + (cy - cy0) / video.Height });
                lastGood = f;
                previous.Dispose(); previous = cur.Clone();
                (pcx, pcy) = (cx, cy);
                if (Math.Abs(f - startFrame) % 10 == 0) progress?.Report(f);
            }
            return Finish(samples, lastGood, false, initial, previous);
        }
        catch (OperationCanceledException) { return new(samples, lastGood, false, "Cancelled."); }
        catch (Exception ex) { return new(samples, lastGood, false, ex.Message); }
    }

    static TrackResult Finish(List<TrackSample> samples, int lastGood, bool uncertain, Mat? a, Mat? b)
    {
        a?.Dispose(); b?.Dispose();
        return new TrackResult(samples, lastGood, uncertain, null);
    }
}
