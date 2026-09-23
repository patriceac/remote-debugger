using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private sealed record ThemeChoice(string Code)
    {
        public override string ToString() => Code switch { "light" => UiText.Get("ThemeLight"), "dark" => UiText.Get("ThemeDark"), _ => UiText.SystemDefault };
    }
    private readonly LocalizedComboBox themeSelector = new ViewerComboBox
    {
        Name = "themeSelector", Dock = Forms.DockStyle.Fill, DropDownStyle = Forms.ComboBoxStyle.DropDownList,
        Font = new Font("Segoe UI", 10.5F), ItemHeight = 26, Margin = new Forms.Padding(0, 0, 0, 10)
    };

    private void InitializeTheme()
    {
        themeSelector.AccessibleName = UiText.Get("Theme");
        themeSelector.Items.AddRange([new ThemeChoice("system"), new ThemeChoice("light"), new ThemeChoice("dark")]);
        string preference = ThemePreference.Load(root);
        themeSelector.SelectedItem = themeSelector.Items.Cast<ThemeChoice>().Single(item => item.Code == preference);
        AppTheme.SetPreference(preference);
        AppTheme.Apply(this);
        themeSelector.SelectedIndexChanged += (_, _) =>
        {
            if (themeSelector.SelectedItem is not ThemeChoice choice) return;
            AppTheme.SetPreference(choice.Code);
            try { ThemePreference.Save(root, choice.Code); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { SetFooterMessage(() => UiText.Get("ThemeSaveFailed")); footerDetail = ex.Message; RefreshFooter(); }
        };
    }

    protected override void WndProc(ref Forms.Message message)
    {
        base.WndProc(ref message);
        if (message.Msg is 0x001a or 0x031a or 0x0015 && IsHandleCreated && !IsDisposed)
            BeginInvoke(AppTheme.RefreshSystem);
    }
}
