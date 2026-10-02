using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task SharedCursorReviewAsync()
    {
        Text = "Shared cursors — native interaction review"; Size = new(1080, 620);
        StartPosition = Forms.FormStartPosition.Manual; Location = new(20, 20); log.Visible = false;
        using var target = new Forms.Panel { Location = new(15, 45), Size = new(490, 460), BackColor = Color.White, BorderStyle = Forms.BorderStyle.FixedSingle };
        using var viewer = new RemoteScreenView { Location = new(525, 45), Size = new(490, 460), SizeMode = Forms.PictureBoxSizeMode.Zoom };
        Controls.Add(target); Controls.Add(viewer);
        Controls.Add(new Forms.Label { Text = "Assisted desktop", Location = new(15, 15), AutoSize = true });
        Controls.Add(new Forms.Label { Text = "Viewer (same desktop coordinates)", Location = new(525, 15), AutoSize = true });
        int downs = 0, ups = 0; Point lastDrag = default, lastDown = default;
        target.Paint += (_, e) =>
        {
            using var font = new Font("Segoe UI", 13);
            e.Graphics.DrawString("Documents", font, Brushes.Black, 18, 18);
            e.Graphics.DrawString("Project notes.txt\n\nPhotos\n\nReport.pdf", font, Brushes.DimGray, 30, 70);
            e.Graphics.DrawString($"Mouse presses: {downs}   Releases: {ups}", Font, Brushes.Gray, 18, 410);
        };
        target.MouseDown += (_, e) => { downs++; lastDown = e.Location; target.Invalidate(); };
        target.MouseUp += (_, _) => { ups++; target.Invalidate(); };
        target.MouseMove += (_, e) => { if (e.Button == Forms.MouseButtons.Left) lastDrag = e.Location; };
        Native.FocusWindow(Environment.ProcessId); target.Focus(); await Task.Delay(150, stop.Token);
        long foregroundBefore = Native.NativeWindows().Single(w => w.Foreground).Handle;
        string layout = DesktopCapture.LayoutId();
        Point own = target.PointToScreen(new(85, 155)), other = target.PointToScreen(new(285, 245));
        try
        {
            // Keep the live capture open before the overlay appears, as a real viewer does.
            using var buffer = new CaptureBuffer();
            using (DesktopCapture.CaptureBitmap(0, null, buffer).Capture!) { }
            PhysicalMove(own); await Task.Delay(80, stop.Token);
            await Send("pointer");
            var reply = await Send("move", other);
            Require(Forms.Cursor.Position == own && new Point(reply.Cursor.X, reply.Cursor.Y) == own && downs == 0,
                "cursor.independent_movement", "Remote movement updates its overlay without moving the native local cursor", new { own, actual = Forms.Cursor.Position, reply });

            var engine = ((Lazy<SharedMouse>)typeof(SharedMouse).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value;
            var overlay = (CursorOverlay)typeof(SharedMouse).GetField("overlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            var overlayEvidence = (JsonElement)overlay.Invoke(() => Json.Element(new { overlay.Visible, overlay.ExcludedFromCapture, overlay.ExcludedFromDuplication, pointer = overlay.Pointer.Position }));
            bool overlayValid = overlayEvidence.GetProperty("visible").GetBoolean() && overlayEvidence.GetProperty("excludedFromCapture").GetBoolean() && overlayEvidence.GetProperty("excludedFromDuplication").GetBoolean() &&
                overlayEvidence.GetProperty("pointer").Deserialize<CursorPosition>(Json.Options) is { Name: "Alex", Visible: true } p && p.X == other.X && p.Y == other.Y;
            var captureEvidence = new List<object>();
            bool cleanCapture = true;
            foreach (var captureBuffer in new CaptureBuffer?[] { buffer, null })
            {
                using var capture = DesktopCapture.CaptureBitmap(0, null, captureBuffer).Capture!;
                int arrowPixel = capture.Bitmap.GetPixel(other.X - capture.Geometry.X + 6, other.Y - capture.Geometry.Y + 12).ToArgb();
                int tagPixel = capture.Bitmap.GetPixel(other.X - capture.Geometry.X + 24, other.Y - capture.Geometry.Y + 33).ToArgb();
                cleanCapture &= arrowPixel == Color.White.ToArgb() && tagPixel == Color.White.ToArgb();
                captureEvidence.Add(new { capture.CaptureMethod, arrowPixel, tagPixel });
                capture.Bitmap.Save(Path.Combine(output, captureBuffer == null ? "shared-cursor-capture-gdi.png" : "shared-cursor-capture.png"), ImageFormat.Png);
            }
            long? foregroundAfter = Native.NativeWindows().FirstOrDefault(w => w.Foreground)?.Handle;
            var evidence = new { overlayEvidence, captures = captureEvidence, expectedBackground = Color.White.ToArgb(), foregroundBefore, foregroundAfter };
            const string overlayRequirement = "The assisted PC shows the named peer overlay without activation or capture echo";
            if (overlayValid && cleanCapture && foregroundBefore == foregroundAfter) Pass("cursor.overlay", overlayRequirement, evidence);
            else Fail("cursor.overlay", overlayRequirement, evidence);

            // Verify the actual composed window too: an invisible/broken surface must not pass exclusion checks.
            overlay.Invoke(() => overlay.SetCaptureExclusion(false));
            try
            {
                await Send("move", other);
                await Task.Delay(150, stop.Token);
                using var visible = DesktopCapture.CaptureBitmap(0, null).Capture!;
                int blue = visible.Bitmap.GetPixel(other.X - visible.Geometry.X + 6, other.Y - visible.Geometry.Y + 12).ToArgb();
                int clear = visible.Bitmap.GetPixel(other.X - visible.Geometry.X - 15, other.Y - visible.Geometry.Y - 20).ToArgb();
                visible.Bitmap.Save(Path.Combine(output, "shared-cursor-composed.png"), ImageFormat.Png);
                Require(blue == SharedCursor.Blue.ToArgb() && clear == Color.White.ToArgb(), "cursor.composed_surface", "The actual desktop shows the blue cursor over a transparent background", new { blue, clear });
            }
            finally { overlay.Invoke(() => overlay.SetCaptureExclusion(true)); }

            using (var foreground = new Forms.Form { Text = "Window under the shared cursor", StartPosition = Forms.FormStartPosition.Manual, Bounds = new(other.X - 40, other.Y - 60, 230, 160), BackColor = Color.LemonChiffon })
            {
                int clicked = 0;
                foreground.MouseDown += (_, _) => clicked++;
                foreground.Show(); foreground.Activate();
                // Reproduce a demoted overlay even on workers where Windows does not demote it spontaneously.
                overlay.Invoke(() => CursorWindowPos(overlay.Handle, new IntPtr(1), 0, 0, 0, 0, 0x0213));
                await Send("move", other);
                bool above = true;
                for (IntPtr window = CursorWindowAbove(overlay.Handle, 3); window != IntPtr.Zero; window = CursorWindowAbove(window, 3))
                    if (window == foreground.Handle) above = false;
                Require(above && foreground.ClientRectangle.Contains(foreground.PointToClient(other)) && Native.NativeWindows().Any(w => w.Handle == foreground.Handle.ToInt64() && w.Foreground),
                    "cursor.foreground_window", "The cursor stays above an open active window without taking focus", new { foreground.Bounds, other });
                overlay.Invoke(() => overlay.SetCaptureExclusion(false));
                try
                {
                    await Task.Delay(150, stop.Token);
                    using var displayed = DesktopCapture.CaptureBitmap(0, null).Capture!;
                    displayed.Bitmap.Save(Path.Combine(output, "shared-cursor-foreground-window.png"), ImageFormat.Png);
                    Require(displayed.Bitmap.GetPixel(other.X - displayed.Geometry.X + 6, other.Y - displayed.Geometry.Y + 12).ToArgb() == SharedCursor.Blue.ToArgb(),
                        "cursor.foreground_pixels", "The composed cursor is visible over the open window");
                }
                finally { overlay.Invoke(() => overlay.SetCaptureExclusion(true)); }
                await Send("down", other); await Send("up", other);
                Require(clicked == 1 && Forms.Cursor.Position == own, "cursor.foreground_click", "Clicks pass through the cursor overlay to the open window", new { clicked, actual = Forms.Cursor.Position, own });
                foreground.Close();
            }
            Activate(); target.Focus();

            using var frame = new Bitmap(target.Width, target.Height);
            target.DrawToBitmap(frame, target.ClientRectangle); viewer.Image = frame;
            var origin = target.PointToScreen(Point.Empty);
            viewer.UpdateCursor(reply.Cursor, new DesktopGeometry(origin.X, origin.Y, target.Width, target.Height, layout));
            using (var rendered = new Bitmap(viewer.Width, viewer.Height))
            {
                viewer.DrawToBitmap(rendered, viewer.ClientRectangle);
                rendered.Save(Path.Combine(output, "shared-cursor-viewer.png"), ImageFormat.Png);
                Require(HasBlue(rendered), "cursor.viewer_render", "The viewer paints the assisted person's cursor at a separate mapped position");
            }
            SaveOverlay("shared-cursor-moving.png");
            long idleUntil = Environment.TickCount64 + 2200;
            while (Environment.TickCount64 < idleUntil)
            { await Task.Delay(70, stop.Token); await Send("pointer"); }
            bool namedWhileIdle = (bool)overlay.Invoke(() =>
            {
                using var bitmap = overlay.RenderSurface();
                bitmap.Save(Path.Combine(output, "shared-cursor-idle.png"), ImageFormat.Png);
                var tip = overlay.PointToClient(other);
                int alpha = bitmap.GetPixel(tip.X + 24, tip.Y + 33).A;
                return overlay.Visible && overlay.Pointer.LabelVisible(Environment.TickCount64) && alpha == 255;
            });
            Require(namedWhileIdle, "cursor.idle", "The name label stays fully visible while the peer pointer is stationary");

            await Send("down", other); SaveOverlay("shared-cursor-click.png");
            await Send("up", other);
            Require(downs == 1 && ups == 1 && lastDown == target.PointToClient(other) && Forms.Cursor.Position == own,
                "cursor.click_restore", "A remote click reaches its target and restores the local person's position", new { downs, ups, lastDown, expected = target.PointToClient(other), actual = Forms.Cursor.Position });

            await Send("down", other);
            mouse_event(1, 12, 8, 0, UIntPtr.Zero); await Task.Delay(60, stop.Token);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            Point remoteEnd = new(other.X + 60, other.Y + 30);
            await Send("move", remoteEnd);
            Require(downs == 2 && ups == 1 && lastDrag == target.PointToClient(remoteEnd),
                "cursor.remote_drag", "Local pointer motion and a contending click do not disrupt a remote drag", new { downs, ups, lastDrag, expected = target.PointToClient(remoteEnd) });
            reply = await Send("up", remoteEnd);
            Require(ups == 2 && Forms.Cursor.Position == new Point(reply.Cursor.X, reply.Cursor.Y) && Forms.Cursor.Position != own,
                "cursor.local_motion_during_drag", "The local person's independent movement is retained when the remote drag releases", reply);

            PhysicalMove(own); await Task.Delay(60, stop.Token);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(60, stop.Token);
            reply = await Send("down", other);
            await Send("move", remoteEnd); await Send("up", remoteEnd);
            Require(!reply.Applied && Forms.Cursor.Position == own && downs == 3 && ups == 2,
                "cursor.local_drag", "An active local drag has equal ownership; competing remote clicks are not applied", new { downs, ups, reply });
            var localEnd = new Point(own.X + 45, own.Y + 25);
            PhysicalMove(localEnd); await Task.Delay(60, stop.Token);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(60, stop.Token);
            Require(lastDrag == target.PointToClient(localEnd) && ups == 3 && Forms.Cursor.Position == localEnd,
                "cursor.local_drag_motion", "The local person's drag moves and releases at its own destination", new { lastDrag, ups, actual = Forms.Cursor.Position });

            await Send("pointerLeave");
            Require(!(bool)overlay.Invoke(() => overlay.Visible), "cursor.leave", "The controller's cursor disappears when it leaves the shared surface");
            await Send("move", other);
            await Send("down", other); await Send("move", remoteEnd);
            Require(SharedMouse.Dragging && downs == 4 && ups == 3, "cursor.held_before_release", "The teardown check starts with a real remote button still held");
            await Task.Run(() => Native.ReleaseAllInput());
            await Task.Delay(60, stop.Token);
            Require(!(bool)overlay.Invoke(() => overlay.Visible) && !SharedMouse.Dragging && ups == 4 && Forms.Cursor.Position == localEnd,
                "cursor.release", "Session release removes the pointer and releases gesture ownership");
            viewer.UpdateCursor(null, null);
            viewer.Image = null;

            void SaveOverlay(string file) => overlay.Invoke(() =>
            {
                using var bitmap = overlay.RenderSurface(); bitmap.Save(Path.Combine(output, file), ImageFormat.Png);
            });
        }
        finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Run(() => Native.ReleaseAllInput()); }
        await NativeViewerKeyboardAsync();
        await CursorNicknameReviewAsync();
        await FinishAsync();

        async Task<SharedPointerReply> Send(string kind, Point point = default)
        {
            var args = Json.Element(new { kind, x = point.X, y = point.Y, layoutId = layout, button = "left", sharedPointer = true, pointerName = "Alex" });
            var result = Json.Element(await Task.Run(() => Native.HandleSharedInput(args)));
            await Task.Delay(60, stop.Token);
            return result.Deserialize<SharedPointerReply>(Json.Options)!;
        }
        void Require(bool ok, string id, string requirement, object? evidence = null)
        {
            if (!ok) throw new IOException(id + ": " + requirement + "; " + Json.Text(evidence));
            Pass(id, requirement, evidence);
        }
        static bool HasBlue(Bitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y).ToArgb() == SharedCursor.Blue.ToArgb()) return true;
            return false;
        }
        static void PhysicalMove(Point point)
        {
            var bounds = Forms.SystemInformation.VirtualScreen;
            mouse_event(0xC001, (uint)(((point.X - bounds.Left) * 65536L + 32768) / bounds.Width),
                (uint)(((point.Y - bounds.Top) * 65536L + 32768) / bounds.Height), 0, UIntPtr.Zero);
        }
    }
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool CursorWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindow")] private static extern IntPtr CursorWindowAbove(IntPtr window, uint command);
}
