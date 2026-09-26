using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal static class AppTheme
{
    private static readonly ConditionalWeakTable<Forms.Control, Colors> controls = new();
    internal static string Preference { get; private set; } = "system";
    internal static bool Dark { get; private set; }
    internal static Color Canvas => Color.FromArgb(30, 30, 30);
    internal static Color Surface => Color.FromArgb(37, 37, 37);
    internal static Color Input => Color.FromArgb(43, 43, 43);
    internal static Color Rail => Color.FromArgb(32, 32, 32);
    internal static Color Selected => Color.FromArgb(53, 53, 53);
    internal static Color Text => Color.FromArgb(240, 240, 240);
    internal static Color Muted => Color.FromArgb(173, 173, 173);
    internal static Color Border => Color.FromArgb(68, 68, 68);
    internal static Color Accent => Color.FromArgb(0, 153, 165);

    internal static void SetPreference(string value)
    {
        Preference = ThemePreference.Normalize(value);
        RefreshSystem();
    }

    internal static bool SystemDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    internal static void RefreshSystem()
    {
        bool dark = !Forms.SystemInformation.HighContrast && ThemePreference.IsDark(Preference, SystemDark());
        if (dark == Dark) return;
        Dark = dark;
        foreach (Forms.Form form in Forms.Application.OpenForms) Apply(form);
    }

    // Preserve the exact existing light colors; these functions are also used by owner-drawn controls.
    internal static Color Background(Color light)
    {
        if (!Dark || light.IsEmpty || light.A != 255) return light;
        return (light.R, light.G, light.B) switch
        {
            (245, 250, 252) => Canvas,
            (218, 230, 237) or (228, 234, 236) => Border,
            (23, 40, 51) => Rail,
            (31, 72, 85) or (227, 246, 248) => Selected,
            (227, 243, 233) or (255, 242, 219) or (255, 244, 222) or (255, 240, 241) => Input,
            (0, 153, 165) => Accent,
            (255, 255, 255) => Surface,
            (253, 253, 254) or (248, 253, 253) => Input,
            _ when light.R > 195 && light.G > 195 && light.B > 195 => Input,
            _ => light
        };
    }

    internal static Color Ink(Color light)
    {
        if (!Dark || light.IsEmpty || light.A != 255) return light;
        return (light.R, light.G, light.B) switch
        {
            (255, 255, 255) => light,
            (32, 107, 69) or (50, 137, 91) => Accent,
            (139, 94, 18) => Color.FromArgb(231, 188, 106),
            (184, 61, 73) or (222, 41, 59) or (178, 34, 34) => Color.FromArgb(244, 138, 151),
            (0, 153, 165) => Accent,
            (103, 124, 137) => Color.FromArgb(112, 112, 112),
            _ when light.R < 60 && light.G < 85 && light.B < 100 => Text,
            _ when Math.Max(light.R, Math.Max(light.G, light.B)) < 195 => Muted,
            _ when light.R > 195 && light.G > 195 && light.B > 195 => Text,
            _ => light
        };
    }
    internal static Color Line(Color light) => Dark && light.R > 150 && light.G > 150 && light.B > 150 ? Border : Ink(light);

    internal static void ConfigureMenu(Forms.ContextMenuStrip menu)
    {
        var original = menu.Renderer;
        var themed = new MenuRenderer();
        Size roundedSize = Size.Empty;
        menu.SizeChanged += (_, _) => RoundMenu();
        menu.Opening += (_, _) =>
        {
            RefreshSystem();
            bool highContrast = Forms.SystemInformation.HighContrast;
            menu.Renderer = highContrast ? original : themed;
            menu.BackColor = highContrast ? SystemColors.Menu : MenuColors.Background;
            menu.ForeColor = highContrast ? SystemColors.MenuText : Dark ? Text : Color.Black;
            menu.MinimumSize = new Size((int)Math.Ceiling(menu.PreferredSize.Height * 1.92), 0);
            RoundMenu();
        };

        void RoundMenu()
        {
            if (!Forms.SystemInformation.HighContrast)
                ControlRegions.ApplyRounded(menu, ref roundedSize, Math.Max(1, 4 * menu.DeviceDpi / 96));
            else { var previous = menu.Region; menu.Region = null; previous?.Dispose(); }
        }
    }

    private sealed class MenuColors : Forms.ProfessionalColorTable
    {
        internal static Color Background => Dark ? Color.FromArgb(41, 42, 44) : Color.FromArgb(246, 249, 252);
        internal static Color Highlight => Dark ? Color.FromArgb(7, 108, 112) : Color.FromArgb(227, 246, 248);
        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Dark ? Color.FromArgb(85, 86, 87) : Color.FromArgb(201, 207, 213);
        public override Color SeparatorDark => Dark ? Border : Color.FromArgb(224, 229, 234);
        public override Color SeparatorLight => SeparatorDark;
    }

    private sealed class MenuRenderer() : Forms.ToolStripProfessionalRenderer(new MenuColors())
    {
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            float scale = e.ToolStrip!.DeviceDpi / 96f;
            using var path = Rounded(new RectangleF(2 * scale, 0, e.Item.Width - 3 * scale, e.Item.Height - 1), 2 * scale);
            var smoothing = e.Graphics.SmoothingMode; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(MenuColors.Highlight); e.Graphics.FillPath(brush, path);
            e.Graphics.SmoothingMode = smoothing;
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? (Dark ? Muted : SystemColors.GrayText)
                : Dark ? (e.Item.Selected ? Color.White : Text) : Color.Black;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            float scale = e.ToolStrip.DeviceDpi / 96f;
            using var path = Rounded(new RectangleF(.5f, .5f, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 4 * scale);
            var smoothing = e.Graphics.SmoothingMode; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(ColorTable.MenuBorder, scale); e.Graphics.DrawPath(pen, path);
            e.Graphics.SmoothingMode = smoothing;
        }

        private static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
    }

    internal static void Apply(Forms.Control control)
    {
        // Register children before recoloring their parent so inherited colors stay inherited.
        var colors = controls.GetValue(control, c => new Colors(c));
        foreach (Forms.Control child in control.Controls) Apply(child);
        colors.Apply();
        if (control is Forms.Form form && form.IsHandleCreated)
        {
            int dark = Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(form.Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        }
        control.Invalidate(true);
    }

    private sealed class Colors
    {
        private readonly Forms.Control control;
        private Color background, foreground, border;
        private readonly Forms.FlatStyle? buttonStyle;
        private readonly bool visualButton;
        private bool applying;

        internal Colors(Forms.Control control)
        {
            this.control = control;
            bool native = control is Forms.Form or Forms.TextBoxBase or Forms.ListView or Forms.ComboBox or Forms.UpDownBase;
            background = native || HasOwnColor(nameof(control.BackColor)) ? control.BackColor : Color.Empty;
            foreground = native || HasOwnColor(nameof(control.ForeColor)) ? control.ForeColor : Color.Empty;
            if (control is Forms.Button button)
            {
                border = button.FlatAppearance.BorderColor;
                buttonStyle = button.FlatStyle; visualButton = button.UseVisualStyleBackColor;
            }
            control.BackColorChanged += (_, _) => Changed(backgroundChanged: true);
            control.ForeColorChanged += (_, _) => Changed(backgroundChanged: false);
            control.ControlAdded += (_, e) => { if (e.Control != null) AppTheme.Apply(e.Control); };
            control.HandleCreated += (_, _) => AppTheme.Apply(control);
            if (control is Forms.ListView { OwnerDraw: false } list)
            {
                list.OwnerDraw = true;
                list.DrawColumnHeader += (_, e) =>
                {
                    if (!Dark) { e.DrawDefault = true; return; }
                    using var fill = new SolidBrush(Input); e.Graphics.FillRectangle(fill, e.Bounds);
                    var alignment = e.Header?.TextAlign switch { Forms.HorizontalAlignment.Right => Forms.TextFormatFlags.Right,
                        Forms.HorizontalAlignment.Center => Forms.TextFormatFlags.HorizontalCenter, _ => Forms.TextFormatFlags.Left };
                    int inset = (int)Math.Round(4 * list.DeviceDpi / 96f);
                    Forms.TextRenderer.DrawText(e.Graphics, e.Header?.Text, list.Font, Rectangle.Inflate(e.Bounds, -inset, 0), Muted,
                        alignment | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.EndEllipsis | Forms.TextFormatFlags.NoPrefix |
                        Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.SingleLine);
                };
                list.DrawItem += (_, e) => e.DrawDefault = list.View != Forms.View.Details;
                list.DrawSubItem += (_, e) => e.DrawDefault = true;
            }
        }
        private bool HasOwnColor(string property) => TypeDescriptor.GetProperties(control)[property]!.ShouldSerializeValue(control);
        private void Changed(bool backgroundChanged)
        {
            if (applying) return;
            if (backgroundChanged) background = HasOwnColor(nameof(control.BackColor)) ? control.BackColor : Color.Empty;
            else foreground = HasOwnColor(nameof(control.ForeColor)) ? control.ForeColor : Color.Empty;
            Apply();
        }
        internal void Apply()
        {
            if (applying || control.IsDisposed) return;
            applying = true;
            try
            {
                if (!background.IsEmpty)
                {
                    Color themed = Background(background);
                    if (Dark && background.ToArgb() == Color.White.ToArgb() &&
                        (control is Forms.TextBoxBase or Forms.ComboBox or Forms.UpDownBase)) themed = Input;
                    control.BackColor = themed;
                }
                if (!foreground.IsEmpty) control.ForeColor = Ink(foreground);
                if (control is Forms.Button button)
                {
                    button.FlatAppearance.BorderColor = Line(border);
                    if (button is not WorkspaceButton)
                    {
                        button.FlatStyle = Dark ? Forms.FlatStyle.Flat : buttonStyle!.Value;
                        button.UseVisualStyleBackColor = !Dark && visualButton;
                    }
                }
            }
            finally { applying = false; }
        }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
