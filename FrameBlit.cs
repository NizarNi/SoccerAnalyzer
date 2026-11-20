using Avalonia.Media.Imaging;
using OpenCvSharp;

namespace SoccerAnalyzer;

public static class FrameBlit
{
    /// <summary>Copies a BGR video frame into a Bgra8888 bitmap of the same size.</summary>
    public static void Copy(Mat bgr, WriteableBitmap dst)
    {
        using var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        using var fb = dst.Lock();
        int rowBytes = Math.Min(fb.RowBytes, (int)bgra.Step());
        int rows = Math.Min(bgra.Rows, fb.Size.Height);
        unsafe
        {
            for (int y = 0; y < rows; y++)
                Buffer.MemoryCopy((void*)bgra.Ptr(y), (void*)(fb.Address + y * fb.RowBytes), fb.RowBytes, rowBytes);
        }
    }
}
