using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed record ScreenFrame(long Sequence, DateTimeOffset CapturedUtc, DesktopGeometry Geometry, int EncodedWidth, int EncodedHeight, double CaptureEncodeMs, string Mime, string Data, double CopyMs = 0, double JpegMs = 0);
internal sealed record StreamCaptureResult(ScreenFrame? Frame, string Fingerprint);
internal sealed record CapturedDesktop(Bitmap Bitmap, DateTimeOffset CapturedUtc, DesktopGeometry Geometry, string Fingerprint, double CopyMs, bool OwnsBitmap = true, double CaptureMs = 0, string CaptureMethod = "GDI") : IDisposable
{
    public double TotalCaptureMs => CaptureMs > 0 ? CaptureMs : CopyMs;
    public void Dispose() { if (OwnsBitmap) Bitmap.Dispose(); }
}
internal sealed class CaptureBuffer : IDisposable
{
    private Bitmap? bitmap;
    private DxgiCapture? gpu;
    private bool gpuAttempted;
    public bool UsesGpu => gpu != null;
    public Bitmap Get(Size size)
    {
        if (bitmap?.Size != size) { bitmap?.Dispose(); bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb); }
        return bitmap;
    }
    public string? CaptureGpu(string device, Bitmap target)
    {
        try
        {
            if (!gpuAttempted) { gpuAttempted = true; gpu = DxgiCapture.Create(device, target.Size); }
            return gpu?.Capture(target);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { gpu?.Dispose(); gpu = null; return null; }
    }
    public void Dispose() { gpu?.Dispose(); bitmap?.Dispose(); }
}
internal sealed record BitmapCaptureResult(CapturedDesktop? Capture, string Fingerprint);
internal sealed record EncodedJpeg(ScreenFrame Frame, byte[] Bytes);
public static class DesktopCapture
{
    private static readonly object layoutGate = new();
    private static (string Device, Rectangle Bounds, bool Primary)[] previousLayout = [];
    private static string previousLayoutId = "";
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
    private static string DesktopName(IntPtr desktop) { var name = new StringBuilder(256); return desktop != IntPtr.Zero && GetUserObjectInformation(desktop, 2, name, 512, out _) ? name.ToString() : "<unavailable>"; }
    private static (string Input, string Thread, bool Available) ReadDesktopState()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 1); string inputName = DesktopName(desktop), threadName = DesktopName(GetThreadDesktop(GetCurrentThreadId())); if (desktop != IntPtr.Zero) CloseDesktop(desktop);
        return (inputName, threadName, inputName != "<unavailable>" && inputName == threadName);
    }
    public static object State() { var state = ReadDesktopState(); return new { inputDesktop = state.Input, threadDesktop = state.Thread, available = state.Available }; }
    public static void RequireDesktop()
    {
        if (!ReadDesktopState().Available) throw new InvalidOperationException("Interactive desktop unavailable or secure desktop active; unlock or handle the Windows prompt locally.");
    }
    public static object Monitors() => Forms.Screen.AllScreens.Select((s, i) => new { index = i, device = s.DeviceName, primary = s.Primary, x = s.Bounds.X, y = s.Bounds.Y, width = s.Bounds.Width, height = s.Bounds.Height }).ToArray();
    public static string LayoutId()
    {
        lock (layoutGate)
        {
            var screens = Forms.Screen.AllScreens;
            if (screens.Length == previousLayout.Length && screens.Select((s, i) => previousLayout[i] == (s.DeviceName, s.Bounds, s.Primary)).All(equal => equal)) return previousLayoutId;
            previousLayout = screens.Select(s => (s.DeviceName, s.Bounds, s.Primary)).ToArray();
            previousLayoutId = Safety.Hash(Json.Text(screens.Select((s, i) => new { index = i, device = s.DeviceName, primary = s.Primary, x = s.Bounds.X, y = s.Bounds.Y, width = s.Bounds.Width, height = s.Bounds.Height })));
            return previousLayoutId;
        }
    }
    public static ScreenFrame Capture(int monitor = 0, int maxWidth = 0, int quality = 75, long sequence = 0)
    {
        var result = CaptureBitmap(monitor, previousFingerprint: null);
        using var capture = result.Capture ?? throw new InvalidOperationException("Desktop capture returned no frame.");
        return EncodeJpeg(capture, maxWidth, quality, sequence).Frame;
    }

    internal static StreamCaptureResult CaptureStream(int monitor, int maxWidth, int quality, long sequence, string? previousFingerprint, CaptureBuffer? buffer = null)
    {
        var result = CaptureBitmap(monitor, previousFingerprint, buffer);
        if (result.Capture is not { } capture) return new StreamCaptureResult(null, result.Fingerprint);
        using (capture) return new StreamCaptureResult(EncodeJpeg(capture, maxWidth, quality, sequence).Frame, result.Fingerprint);
    }

    internal static BitmapCaptureResult CaptureBitmap(int monitor, string? previousFingerprint, CaptureBuffer? buffer = null)
    {
        IntPtr old = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            var sw = Stopwatch.StartNew(); RequireDesktop(); var screens = Forms.Screen.AllScreens;
            if (monitor < -1 || monitor >= screens.Length) throw new ArgumentException("Monitor index unavailable. Refresh monitors.");
            var r = monitor == -1 ? Forms.SystemInformation.VirtualScreen : screens[monitor].Bounds;
            string layoutId = LayoutId(); DateTimeOffset captured = DateTimeOffset.UtcNow;
            double copyStart = sw.Elapsed.TotalMilliseconds;
            var bmp = buffer?.Get(r.Size) ?? new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            string? fingerprint = monitor >= 0 ? buffer?.CaptureGpu(screens[monitor].DeviceName, bmp) : null;
            if (fingerprint == null) using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            double copyMs = sw.Elapsed.TotalMilliseconds - copyStart;
            fingerprint = fingerprint == null ? Fingerprint(bmp, r, layoutId) : layoutId + "|" + fingerprint;
            if (previousFingerprint != null && string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
            {
                if (buffer == null) bmp.Dispose();
                return new BitmapCaptureResult(null, fingerprint);
            }
            return new BitmapCaptureResult(new CapturedDesktop(bmp, captured, new DesktopGeometry(r.X, r.Y, r.Width, r.Height, layoutId), fingerprint, copyMs, buffer == null, sw.Elapsed.TotalMilliseconds, buffer?.UsesGpu == true ? "DXGI" : "GDI"), fingerprint);
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }

    internal static EncodedJpeg EncodeJpeg(CapturedDesktop capture, int maxWidth, int quality, long sequence, bool includeBase64 = true)
    {
        var sw = Stopwatch.StartNew();
        int width = maxWidth <= 0 ? capture.Bitmap.Width : Math.Min(capture.Bitmap.Width, Math.Clamp(maxWidth, 320, 3840));
        int height = (int)Math.Round(capture.Bitmap.Height * (double)width / capture.Bitmap.Width);
        using var resized = width != capture.Bitmap.Width ? new Bitmap(width, height, PixelFormat.Format24bppRgb) : null;
        if (resized != null) using (var g = Graphics.FromImage(resized)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(capture.Bitmap, new Rectangle(0, 0, width, height)); }
        using var ms = new MemoryStream(); using var parameters = new EncoderParameters(1); parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 30, 95));
        double jpegStart = sw.Elapsed.TotalMilliseconds;
        (resized ?? capture.Bitmap).Save(ms, ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid), parameters);
        double jpegMs = sw.Elapsed.TotalMilliseconds - jpegStart;
        if (ms.Length > 2 * 1024 * 1024) throw new IOException("Encoded image exceeds 2 MiB; lower maxWidth or quality.");
        byte[] bytes = ms.ToArray();
        return new EncodedJpeg(new ScreenFrame(sequence, capture.CapturedUtc, capture.Geometry, width, height, capture.TotalCaptureMs + sw.Elapsed.TotalMilliseconds, "image/jpeg", includeBase64 ? Convert.ToBase64String(bytes) : "", capture.CopyMs, jpegMs), bytes);
    }

    internal static unsafe string Fingerprint(Bitmap bitmap, Rectangle bounds, string layoutId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{layoutId}|{bounds.X}|{bounds.Y}|{bounds.Width}|{bounds.Height}"));
        var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr address = data.Stride >= 0
                    ? IntPtr.Add(data.Scan0, y * data.Stride)
                    : IntPtr.Add(data.Scan0, (bitmap.Height - 1 - y) * data.Stride);
                hash.AppendData(new ReadOnlySpan<byte>(address.ToPointer(), bitmap.Width * 4));
            }
        }
        finally { bitmap.UnlockBits(data); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
