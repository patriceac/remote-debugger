using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed record CursorPosition(int X, int Y, string Name, bool Visible, long Activity, long Click);

// One renderer for the viewer and the click-through overlay on the assisted PC.
internal sealed class SharedCursor
{
    internal static readonly Color Blue = Color.FromArgb(37, 99, 235);
    private CursorPosition? position;
    private long received, moved, clicked = long.MinValue / 2;
    internal CursorPosition? Position => position;
    internal bool LabelVisible(long now) => now - moved < 2000;
    internal float LabelOpacity(long now) => Math.Clamp((2000 - (now - moved)) / 500f, 0, 1);
    internal float Pulse(long now) => Math.Clamp((now - clicked) / 450f, 0, 1);

    internal void Update(CursorPosition? value, long now)
    {
        if (value != null)
        {
            if (position == null || value.Activity != position.Activity || !position.Visible) moved = now;
            if (position != null && value.Click != 0 && value.Click != position.Click) clicked = now;
        }
        position = value; received = now;
    }

    internal bool Visible(long now) => position is { Visible: true } && now - received < 3000;

    internal void Paint(Graphics graphics, PointF tip, float scale, Rectangle clip, long now)
    {
        if (!Visible(now)) return;
        var saved = graphics.Save();
        graphics.SetClip(clip); graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float pulse = Pulse(now);
        if (pulse < 1)
        {
            float radius = (8 + 17 * pulse) * scale;
            using var ring = new Pen(Color.FromArgb((int)(150 * (1 - pulse)), Blue), 2 * scale);
            graphics.DrawEllipse(ring, tip.X - radius, tip.Y - radius, radius * 2, radius * 2);
        }
        PointF[] arrow = [new(0, 0), new(0, 27), new(7, 21), new(12, 31), new(17, 28), new(12, 18), new(22, 18)];
        using var path = new GraphicsPath();
        path.AddPolygon(arrow.Select(p => new PointF(tip.X + p.X * scale, tip.Y + p.Y * scale)).ToArray());
        using var shadow = new Pen(Color.FromArgb(100, Color.Black), 4 * scale) { LineJoin = LineJoin.Round };
        using var outline = new Pen(Color.White, 2.5f * scale) { LineJoin = LineJoin.Round };
        using var blue = new SolidBrush(Blue);
        graphics.DrawPath(shadow, path); graphics.FillPath(blue, path); graphics.DrawPath(outline, path);
        if (LabelVisible(now) && position is { } cursor)
        {
            using var font = new Font("Segoe UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            string name = cursor.Name.Length > 28 ? cursor.Name[..27] + "…" : cursor.Name;
            SizeF text = graphics.MeasureString(name, font);
            float w = text.Width + 12 * scale, h = text.Height + 5 * scale;
            float x = Math.Clamp(tip.X + 18 * scale, clip.Left, Math.Max(clip.Left, clip.Right - w - 2));
            float y = tip.Y + 27 * scale;
            if (y + h > clip.Bottom) y = tip.Y - h - 3 * scale;
            using var tag = new GraphicsPath();
            float r = 4 * scale;
            tag.AddArc(x, y, r * 2, r * 2, 180, 90); tag.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            tag.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90); tag.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90); tag.CloseFigure();
            int alpha = (int)(255 * LabelOpacity(now));
            using var tagFill = new SolidBrush(Color.FromArgb(alpha, Blue));
            using var textFill = new SolidBrush(Color.FromArgb(alpha, Color.White));
            graphics.FillPath(tagFill, tag);
            graphics.DrawString(name, font, textFill, x + 6 * scale, y + 2 * scale);
        }
        graphics.Restore(saved);
    }
}

internal sealed class CursorOverlay : Forms.Form
{
    internal readonly SharedCursor Pointer = new();
    internal Point Tip;
    internal Point? LocalTip;
    internal bool ExcludedFromCapture { get; private set; }
    internal CursorOverlay()
    {
        FormBorderStyle = Forms.FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = Forms.FormStartPosition.Manual;
        DoubleBuffered = true;
    }
    protected override bool ShowWithoutActivation => true;
    protected override Forms.CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x080800A0; return p; } // No activate, layered, transparent, tool window.
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ExcludedFromCapture = SetWindowDisplayAffinity(Handle, 0x11);
    }
    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        PaintCursor(e.Graphics);
    }
    private void PaintCursor(Graphics graphics)
    {
        Pointer.Paint(graphics, PointToClient(Tip), DeviceDpi / 96f, ClientRectangle, Environment.TickCount64);
        if (LocalTip is { } local) Forms.Cursors.Default.Draw(graphics, new Rectangle(PointToClient(local), Forms.Cursors.Default.Size));
    }
    internal Bitmap RenderSurface()
    {
        var bitmap = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap); PaintCursor(graphics); return bitmap;
    }
    internal void Redraw()
    {
        using var bitmap = RenderSurface();
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), pixels = bitmap.GetHbitmap(Color.FromArgb(0)), previous = SelectObject(dc, pixels);
        try
        {
            var destination = Location; var source = Point.Empty; var size = Size;
            var blend = new Blend { Alpha = 255, Format = 1 };
            if (!UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, dc, ref source, 0, ref blend, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { SelectObject(dc, previous); DeleteObject(pixels); DeleteDC(dc); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blend { public byte Operation, Flags, Alpha, Format; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref Point destination, ref Size size, IntPtr sourceDc, ref Point source, uint key, ref Blend blend, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
