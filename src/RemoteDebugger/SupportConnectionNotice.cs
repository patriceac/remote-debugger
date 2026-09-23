using System.Diagnostics;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed class SupportConnectionNotice : Forms.Form
{
    private readonly Forms.Timer timer = new() { Interval = 33 };
    private readonly Forms.Panel progress = new() { BackColor = Color.FromArgb(8, 127, 131), Bounds = new Rectangle(4, 218, 436, 4) };
    private readonly Stopwatch lifetime = new();

    protected override bool ShowWithoutActivation => true;

    public event EventHandler? ViewSessionRequested;
    public event EventHandler? EndSupportRequested;

    public SupportConnectionNotice()
    {
        SuspendLayout();
        Name = "supportConnectionNotice";
        Text = "Remote Debugger";
        FormBorderStyle = Forms.FormBorderStyle.None;
        StartPosition = Forms.FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.White;
        ClientSize = new Size(440, 222);
        AutoScaleMode = Forms.AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);

        var accent = new Forms.Panel { BackColor = Color.FromArgb(8, 127, 131), Bounds = new Rectangle(0, 0, 4, 222) };
        var icon = new Forms.Label { Text = "RD", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White,
            BackColor = Color.FromArgb(8, 127, 131), Font = new Font("Segoe UI", 11, FontStyle.Bold), Bounds = new Rectangle(23, 21, 35, 35) };
        var brand = Label("REMOTE DEBUGGER", 70, 25, 270, 20, 9, Color.FromArgb(92, 115, 124), FontStyle.Bold);
        var title = Label(UiText.SupportConnectedNoticeTitle, 23, 69, 385, 32, 17, Color.FromArgb(24, 48, 57), FontStyle.Bold);
        var body = Label(UiText.SupportConnectedNoticeBody, 23, 106, 394, 40, 10, Color.FromArgb(90, 112, 121));
        body.AutoEllipsis = false;
        var divider = new Forms.Panel { BackColor = Color.FromArgb(228, 234, 236), Bounds = new Rectangle(23, 148, 394, 1) };
        var close = Button("×", 393, 18, 27, 27, Color.FromArgb(99, 119, 128), Color.White);
        var view = Button(UiText.ViewSession, 23, 163, 150, 40, Color.FromArgb(8, 127, 131), Color.White);
        var end = Button(UiText.EndSupport, 217, 163, 200, 40, Color.FromArgb(184, 61, 73), Color.FromArgb(255, 240, 241));
        close.AccessibleName = UiText.CloseNotice;
        view.Name = "viewSupportSession";
        end.Name = "endSupportFromNotice";
        close.Click += (_, _) => Close();
        view.Click += (_, _) => { Close(); ViewSessionRequested?.Invoke(this, EventArgs.Empty); };
        end.Click += (_, _) => { Close(); EndSupportRequested?.Invoke(this, EventArgs.Empty); };

        Controls.AddRange([accent, icon, brand, title, body, divider, close, view, end, progress]);
        Paint += (_, e) => { using var pen = new Pen(AppTheme.Line(Color.LightGray)); e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1); };
        Shown += (_, _) =>
        {
            var area = (Forms.Screen.PrimaryScreen ?? Forms.Screen.FromControl(this)).WorkingArea;
            Location = new Point(area.Right - Width - 24, area.Bottom - Height - 24);
            lifetime.Restart();
            timer.Start();
        };
        timer.Tick += (_, _) =>
        {
            double remaining = Math.Max(0, 1 - lifetime.Elapsed.TotalSeconds / 10);
            progress.Width = (int)Math.Round((ClientSize.Width - progress.Left) * remaining);
            if (remaining == 0) Close();
        };
        FormClosed += (_, _) => timer.Dispose();
        ResumeLayout(true);
        AppTheme.Apply(this);
    }

    private static Forms.Label Label(string text, int x, int y, int width, int height, float size, Color color,
        FontStyle style = FontStyle.Regular) => new()
        {
            Text = text, Bounds = new Rectangle(x, y, width, height), AutoEllipsis = true,
            ForeColor = color, BackColor = Color.White, Font = new Font("Segoe UI", size, style)
        };

    private static Forms.Button Button(string text, int x, int y, int width, int height, Color foreground, Color background)
    {
        var button = new Forms.Button { Text = text, Bounds = new Rectangle(x, y, width, height),
            ForeColor = foreground, BackColor = background, Cursor = Forms.Cursors.Hand,
            FlatStyle = Forms.FlatStyle.Flat, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}
