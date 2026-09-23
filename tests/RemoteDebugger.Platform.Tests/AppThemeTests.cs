using System.Drawing;
using RemoteDebugger;
using Xunit;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Platform.Tests;

[CollectionDefinition("Theme", DisableParallelization = true)]
public sealed class ThemeCollection;

[Collection("Theme")]
public sealed class AppThemeTests
{
    [Fact]
    public void SwitchingRestoresLightColorsAndPreservesContentAndLayout()
    {
        string original = AppTheme.Preference;
        using var form = new Forms.Form { BackColor = Color.FromArgb(245, 250, 252), ForeColor = Color.FromArgb(9, 18, 38) };
        using var image = new Bitmap(2, 2);
        var panel = new Forms.Panel { Bounds = new(10, 10, 200, 100) };
        var editor = new Forms.TextBox { Text = "keep my input", BackColor = Color.White, Bounds = new(5, 5, 150, 24) };
        var screen = new Forms.PictureBox { Image = image };
        panel.Controls.Add(editor); form.Controls.Add(panel); form.Controls.Add(screen);
        var bounds = editor.Bounds;
        try
        {
            AppTheme.SetPreference("light"); AppTheme.Apply(form);
            AppTheme.SetPreference("dark"); AppTheme.Apply(form);
            Assert.Equal(AppTheme.Canvas, form.BackColor);
            Assert.Equal(form.BackColor, panel.BackColor);
            Assert.Equal(AppTheme.Surface, editor.BackColor);
            Assert.Equal("keep my input", editor.Text); Assert.Equal(bounds, editor.Bounds);
            Assert.Same(image, screen.Image);
            // Status updates still assign light semantic colors while dark mode is active.
            editor.BackColor = Color.FromArgb(255, 240, 241);
            Assert.Equal(Color.FromArgb(66, 35, 43), editor.BackColor);
            AppTheme.SetPreference("light"); AppTheme.Apply(form);
            Assert.Equal(Color.FromArgb(245, 250, 252), form.BackColor);
            Assert.Equal(form.BackColor, panel.BackColor);
            Assert.Equal(Color.FromArgb(255, 240, 241), editor.BackColor);
            Assert.Equal(Color.FromArgb(9, 18, 38), form.ForeColor);
        }
        finally { AppTheme.SetPreference(original); }
    }

    [Fact]
    public void ControlsAddedInDarkModeRestoreTheirOriginalColors()
    {
        string original = AppTheme.Preference;
        using var form = new Forms.Form { BackColor = Color.White, ForeColor = Color.FromArgb(9, 18, 38) };
        try
        {
            AppTheme.SetPreference("dark"); AppTheme.Apply(form);
            var panel = new Forms.Panel();
            var editor = new Forms.TextBox { BackColor = Color.White };
            Color originalInk = editor.ForeColor;
            panel.Controls.Add(editor); form.Controls.Add(panel);
            Assert.Equal(AppTheme.Surface, editor.BackColor);
            Assert.Equal(AppTheme.Text, editor.ForeColor);
            AppTheme.SetPreference("light"); AppTheme.Apply(form);
            Assert.Equal(Color.White, editor.BackColor);
            Assert.Equal(Color.White, panel.BackColor);
            Assert.Equal(originalInk, editor.ForeColor);
        }
        finally { AppTheme.SetPreference(original); }
    }
}
