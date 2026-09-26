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
    [Theory]
    [InlineData("light", 195, 228, 253, -16777216)]
    [InlineData("dark", 12, 107, 225, -1)]
    public void SelectedTrayRowRendersReadableThemeColors(string theme, int red, int green, int blue, int ink)
    {
        string original = AppTheme.Preference;
        using var menu = new TestMenu();
        var item = new Forms.ToolStripMenuItem("End support"); menu.Items.Add(item);
        try
        {
            AppTheme.SetPreference(theme); AppTheme.ConfigureMenu(menu); menu.Prepare(); item.Select();
            Assert.True(item.Selected);
            using var bitmap = new Bitmap(item.Width, item.Height);
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(menu.BackColor);
            menu.Renderer.DrawMenuItemBackground(new Forms.ToolStripItemRenderEventArgs(graphics, item));
            Assert.Equal(Color.FromArgb(red, green, blue).ToArgb(), bitmap.GetPixel(8, item.Height / 2).ToArgb());
            Assert.Equal(menu.BackColor.ToArgb(), bitmap.GetPixel(2, 0).ToArgb());
            var text = new Forms.ToolStripItemTextRenderEventArgs(graphics, item, item.Text,
                item.ContentRectangle, Color.Magenta, item.Font, Forms.TextFormatFlags.Left);
            menu.Renderer.DrawItemText(text);
            Assert.Equal(ink, text.TextColor.ToArgb());
            Assert.NotNull(menu.Region); Assert.False(menu.Region.IsVisible(0, 0));
        }
        finally { AppTheme.SetPreference(original); }
    }

    private sealed class TestMenu : Forms.ContextMenuStrip
    {
        internal void Prepare() => OnOpening(new System.ComponentModel.CancelEventArgs());
    }

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
            Assert.Equal(Color.FromArgb(30, 30, 30), AppTheme.Canvas);
            Assert.Equal(Color.FromArgb(37, 37, 37), AppTheme.Surface);
            Assert.Equal(Color.FromArgb(32, 32, 32), AppTheme.Background(Color.FromArgb(23, 40, 51)));
            Assert.Equal(Color.FromArgb(53, 53, 53), AppTheme.Background(Color.FromArgb(227, 246, 248)));
            Assert.Equal(AppTheme.Input, editor.BackColor);
            Assert.Equal("keep my input", editor.Text); Assert.Equal(bounds, editor.Bounds);
            Assert.Same(image, screen.Image);
            // Status updates still assign light semantic colors while dark mode is active.
            editor.BackColor = Color.FromArgb(255, 240, 241);
            Assert.Equal(AppTheme.Input, editor.BackColor);
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
            Assert.Equal(AppTheme.Input, editor.BackColor);
            Assert.Equal(AppTheme.Text, editor.ForeColor);
            AppTheme.SetPreference("light"); AppTheme.Apply(form);
            Assert.Equal(Color.White, editor.BackColor);
            Assert.Equal(Color.White, panel.BackColor);
            Assert.Equal(originalInk, editor.ForeColor);
        }
        finally { AppTheme.SetPreference(original); }
    }
}
