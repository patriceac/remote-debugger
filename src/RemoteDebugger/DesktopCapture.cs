using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed record ScreenFrame(long Sequence, DateTimeOffset CapturedUtc, DesktopGeometry Geometry, int EncodedWidth, int EncodedHeight, double CaptureEncodeMs, string Mime, string Data, double CopyMs = 0, double JpegMs = 0);
public static class DesktopCapture
{
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
    private static string DesktopName(IntPtr desktop) { var name = new StringBuilder(256); return desktop != IntPtr.Zero && GetUserObjectInformation(desktop, 2, name, 512, out _) ? name.ToString() : "<unavailable>"; }
    public static object State()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 1); string inputName = DesktopName(desktop), threadName = DesktopName(GetThreadDesktop(GetCurrentThreadId())); if (desktop != IntPtr.Zero) CloseDesktop(desktop);
        return new { inputDesktop = inputName, threadDesktop = threadName, available = inputName != "<unavailable>" && inputName == threadName };
    }
    public static void RequireDesktop()
    {
        var state = Json.Element(State());
        if (!state.GetProperty("available").GetBoolean()) throw new InvalidOperationException("Interactive desktop unavailable or secure desktop active; unlock or handle the Windows prompt locally.");
    }
    public static object Monitors() => Forms.Screen.AllScreens.Select((s, i) => new { index = i, device = s.DeviceName, primary = s.Primary, x = s.Bounds.X, y = s.Bounds.Y, width = s.Bounds.Width, height = s.Bounds.Height }).ToArray();
    public static string LayoutId() => Safety.Hash(Json.Text(Monitors()));
    public static ScreenFrame Capture(int monitor = 0, int maxWidth = 0, int quality = 75, long sequence = 0)
    {
        IntPtr old = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            RequireDesktop(); var sw = Stopwatch.StartNew(); var screens = Forms.Screen.AllScreens;
            if (monitor < -1 || monitor >= screens.Length) throw new ArgumentException("Monitor index unavailable. Refresh monitors.");
            var r = monitor == -1 ? Forms.SystemInformation.VirtualScreen : screens[monitor].Bounds;
            string layoutId = LayoutId(); DateTimeOffset captured = DateTimeOffset.UtcNow;
            double copyStart = sw.Elapsed.TotalMilliseconds;
            using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            double copyMs = sw.Elapsed.TotalMilliseconds - copyStart;
            int width = maxWidth <= 0 ? r.Width : Math.Min(r.Width, Math.Clamp(maxWidth, 320, 3840)); int height = (int)Math.Round(r.Height * (double)width / r.Width);
            using var resized = width != r.Width ? new Bitmap(width, height, PixelFormat.Format24bppRgb) : null;
            if (resized != null) using (var g = Graphics.FromImage(resized)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(bmp, new Rectangle(0, 0, width, height)); }
            using var ms = new MemoryStream(); using var parameters = new EncoderParameters(1); parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 30, 95));
            double jpegStart = sw.Elapsed.TotalMilliseconds;
            (resized ?? bmp).Save(ms, ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid), parameters);
            double jpegMs = sw.Elapsed.TotalMilliseconds - jpegStart;
            if (ms.Length > 2 * 1024 * 1024) throw new IOException("Encoded image exceeds 2 MiB; lower maxWidth or quality.");
            return new ScreenFrame(sequence, captured, new DesktopGeometry(r.X, r.Y, r.Width, r.Height, layoutId), width, height, sw.Elapsed.TotalMilliseconds, "image/jpeg", Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length), copyMs, jpegMs);
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }
}
