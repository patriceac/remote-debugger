using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
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
            PhysicalMove(own); await Task.Delay(80, stop.Token);
            await Send("pointer");
            var reply = await Send("move", other);
            Require(Forms.Cursor.Position == own && new Point(reply.Cursor.X, reply.Cursor.Y) == own && downs == 0,
                "cursor.independent_movement", "Remote movement updates its overlay without moving the native local cursor", new { own, actual = Forms.Cursor.Position, reply });

            var engine = ((Lazy<SharedMouse>)typeof(SharedMouse).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value;
            var overlay = (CursorOverlay)typeof(SharedMouse).GetField("overlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            var overlayEvidence = (JsonElement)overlay.Invoke(() => Json.Element(new { overlay.Visible, overlay.ExcludedFromCapture, pointer = overlay.Pointer.Position }));
            bool overlayValid = overlayEvidence.GetProperty("visible").GetBoolean() && overlayEvidence.GetProperty("excludedFromCapture").GetBoolean() &&
                overlayEvidence.GetProperty("pointer").Deserialize<CursorPosition>(Json.Options) is { Name: "Controller PC", Visible: true } p && p.X == other.X && p.Y == other.Y;
            int capturedPixel;
            using (var buffer = new CaptureBuffer())
            using (var capture = DesktopCapture.CaptureBitmap(0, null, buffer).Capture!)
            {
                capturedPixel = capture.Bitmap.GetPixel(other.X - capture.Geometry.X + 6, other.Y - capture.Geometry.Y + 12).ToArgb();
                capture.Bitmap.Save(Path.Combine(output, "shared-cursor-capture.png"), ImageFormat.Png);
            }
            long? foregroundAfter = Native.NativeWindows().FirstOrDefault(w => w.Foreground)?.Handle;
            var evidence = new { overlayEvidence, capturedPixel, expectedExcludedPixel = SharedCursor.Blue.ToArgb(), foregroundBefore, foregroundAfter };
            const string overlayRequirement = "The assisted PC shows the named peer overlay without activation or capture echo";
            if (overlayValid && capturedPixel != SharedCursor.Blue.ToArgb() && foregroundBefore == foregroundAfter) Pass("cursor.overlay", overlayRequirement, evidence);
            else Fail("cursor.overlay", overlayRequirement, evidence);

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
            while ((float)overlay.Invoke(() => overlay.Pointer.LabelOpacity(Environment.TickCount64)) > 0.65f)
            { await Task.Delay(40, stop.Token); await Send("pointer"); }
            bool fading = (bool)overlay.Invoke(() =>
            {
                using var bitmap = overlay.RenderSurface();
                bitmap.Save(Path.Combine(output, "shared-cursor-fading.png"), ImageFormat.Png);
                var tip = overlay.PointToClient(other);
                int alpha = bitmap.GetPixel(tip.X + 24, tip.Y + 33).A;
                return alpha is > 0 and < 255;
            });
            Require(fading, "cursor.fade", "An intermediate label frame has real partial alpha rather than disappearing abruptly");
            while ((bool)overlay.Invoke(() => overlay.Pointer.LabelVisible(Environment.TickCount64)))
            { await Task.Delay(70, stop.Token); await Send("pointer"); }
            Require((bool)overlay.Invoke(() => overlay.Visible && !overlay.Pointer.LabelVisible(Environment.TickCount64)),
                "cursor.idle", "The name label fades while the peer pointer remains visible");
            SaveOverlay("shared-cursor-idle.png");

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
        await FinishAsync();

        async Task<SharedPointerReply> Send(string kind, Point point = default)
        {
            var args = Json.Element(new { kind, x = point.X, y = point.Y, layoutId = layout, button = "left", sharedPointer = true, pointerName = "Controller PC" });
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
}
