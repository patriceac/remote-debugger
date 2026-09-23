using System.ComponentModel;
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
    internal static Color Canvas => Color.FromArgb(17, 28, 37);
    internal static Color Surface => Color.FromArgb(25, 42, 53);
    internal static Color Text => Color.FromArgb(230, 237, 243);
    internal static Color Muted => Color.FromArgb(157, 175, 191);
    internal static Color Border => Color.FromArgb(49, 70, 83);

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
            (23, 40, 51) => Color.FromArgb(20, 35, 45),
            (31, 72, 85) or (227, 246, 248) => Color.FromArgb(21, 61, 71),
            (227, 243, 233) => Color.FromArgb(24, 58, 44),
            (255, 242, 219) or (255, 244, 222) => Color.FromArgb(65, 51, 27),
            (255, 240, 241) => Color.FromArgb(66, 35, 43),
            (0, 153, 165) => Color.FromArgb(0, 169, 181),
            (255, 255, 255) or (253, 253, 254) or (248, 253, 253) => Surface,
            _ when light.R > 195 && light.G > 195 && light.B > 195 => Color.FromArgb(30, 48, 60),
            _ => light
        };
    }

    internal static Color Ink(Color light)
    {
        if (!Dark || light.IsEmpty || light.A != 255) return light;
        return (light.R, light.G, light.B) switch
        {
            (255, 255, 255) => light,
            (32, 107, 69) or (50, 137, 91) => Color.FromArgb(139, 215, 174),
            (139, 94, 18) => Color.FromArgb(231, 188, 106),
            (184, 61, 73) or (222, 41, 59) or (178, 34, 34) => Color.FromArgb(244, 138, 151),
            (0, 153, 165) => Color.FromArgb(0, 169, 181),
            (103, 124, 137) => light, // Disabled navigation stays clearly muted.
            _ when light.R < 60 && light.G < 85 && light.B < 100 => Text,
            _ when Math.Max(light.R, Math.Max(light.G, light.B)) < 195 => Muted,
            _ => light
        };
    }
    internal static Color Line(Color light) => Dark && light.R > 150 && light.G > 150 && light.B > 150 ? Border : Ink(light);

    internal static void ConfigureMenu(Forms.ContextMenuStrip menu)
    {
        var original = menu.Renderer;
        var themed = new Forms.ToolStripProfessionalRenderer(new MenuColors());
        menu.Opening += (_, _) =>
        {
            menu.Renderer = Dark ? themed : original;
            menu.BackColor = Dark ? Surface : SystemColors.Menu;
            menu.ForeColor = Dark ? Text : SystemColors.MenuText;
        };
    }

    private sealed class MenuColors : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuItemSelected => Color.FromArgb(21, 61, 71);
        public override Color MenuItemBorder => Border;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
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
        control.Invalidate();
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
                    using var fill = new SolidBrush(Surface); e.Graphics.FillRectangle(fill, e.Bounds);
                    Forms.TextRenderer.DrawText(e.Graphics, e.Header?.Text, list.Font, Rectangle.Inflate(e.Bounds, -6, 0), Muted,
                        Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.EndEllipsis | Forms.TextFormatFlags.NoPrefix);
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
                if (!background.IsEmpty) control.BackColor = Background(background);
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
