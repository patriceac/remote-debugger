using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Win32;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task LoopbackThemeAsync()
    {
        using var personalization = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        object? original = personalization.GetValue("AppsUseLightTheme");
        string agentRoot = Path.Combine(output, "theme-agent"), controllerRoot = Path.Combine(output, "theme-controller");
        void Require(bool condition, string id, object? evidence = null)
        {
            if (!condition) throw new IOException(id + ": " + System.Text.Json.JsonSerializer.Serialize(evidence));
            Pass("theme." + id, id, evidence);
        }
        async Task SystemTheme(bool dark)
        {
            personalization.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            _ = ThemeSendMessage(new IntPtr(0xffff), 0x001a, IntPtr.Zero, "ImmersiveColorSet", 2, 1000, out _);
            await Task.Delay(400, stop.Token);
        }
        async Task CheckPalette(bool dark, string id)
        {
            Native.FocusWindow(product!.Id); await Task.Delay(300, stop.Token);
            var bounds = Element("headerTitle").Current.BoundingRectangle;
            using var pixel = new Bitmap(1, 1);
            using (var graphics = Graphics.FromImage(pixel)) graphics.CopyFromScreen((int)bounds.Right - 3, (int)bounds.Bottom - 3, 0, 0, new Size(1, 1));
            Color actual = pixel.GetPixel(0, 0), expected = dark ? Color.FromArgb(25, 42, 53) : Color.White;
            Require(actual.ToArgb() == expected.ToArgb(), id, new { expected = expected.ToArgb(), actual = actual.ToArgb() });
        }
        try
        {
            UiCulture.Apply(CultureInfo.GetCultureInfo("en"));
            await SystemTheme(false);
            if (File.Exists(RemoteClient.DefaultPath)) File.Delete(RemoteClient.DefaultPath);
            product = loopbackAgent = LaunchLoopbackProduct(true, agentRoot, "en");
            await WaitUiAsync(); string code = await WaitPairingCodeAsync();
            WindowState = Forms.FormWindowState.Minimized;
            await SelectThemeAsync("Dark"); CaptureDesktop("theme-dark-give-control.png");
            product = loopbackController = LaunchLoopbackProduct(false, controllerRoot, "en");
            await WaitUiAsync(); Native.FocusWindow(product.Id); ResizeProductWindow(1060, 720);
            Require(TryValue("themeSelector") == UiText.SystemDefault, "fresh_default_system");
            await CheckPalette(false, "system_initial_light");
            Set("host", "127.0.0.1"); Set("pairCode", "12");
            var layout = new[] { "themeSelector", "languageSelector", "headerTitle", "host" }
                .ToDictionary(id => id, id => Element(id).Current.BoundingRectangle);
            await SelectThemeAsync("Dark");
            await CheckPalette(true, "explicit_dark");
            Require(TryValue("host") == "127.0.0.1" && TryValue("pairCode") == "12" &&
                layout.All(entry => Element(entry.Key).Current.BoundingRectangle == entry.Value), "draft_and_geometry_preserved");
            CaptureDesktop("theme-dark-connection-minimum.png");
            await SelectThemeAsync("Light"); await SystemTheme(true);
            await CheckPalette(false, "light_ignores_system_dark");
            await SelectThemeAsync(UiText.SystemDefault);
            await CheckPalette(true, "system_follows_dark");
            await SystemTheme(false); await CheckPalette(false, "system_follows_light_live");
            CaptureDesktop("theme-light-connection-minimum.png");
            await ConnectNavigationSessionAsync(code, "theme.connected");
            Click("navScreen"); await WaitForTextAsync("liveBadge", value => value.Contains(UiText.Live), 30);
            int pid = product.Id; var connections = StreamConnections(); string token = RemoteClient.Load().Connection.Token;
            await SelectThemeAsync("Dark");
            var live = await WaitForLiveEvidenceAsync(15);
            Require(product.Id == pid && Value(Element("connectionStatus")) == UiText.Connected &&
                TryValue("headerTitle") == UiText.RemoteScreen && RemoteClient.Load().Connection.Token == token &&
                connections.Intersect(StreamConnections()).Any() && live.BadgeVisible && live.TelemetryVisible,
                "live_session_preserved", live);
            CaptureDesktop("theme-dark-live.png");
            foreach (string page in new[] { "navProcesses", "navFiles", "navDiagnostics" })
            {
                Click(page); await Task.Delay(1200, stop.Token); CaptureDesktop("theme-dark-" + page + ".png");
            }
            Set("arguments", "{\"kept\":true}");
            await SelectThemeAsync("Light"); await SelectThemeAsync("Dark");
            Require(TryValue("arguments") == "{\"kept\":true}" && TryValue("headerTitle") == UiText.Diagnostics, "editor_and_page_preserved");
            Click("terminateSession");
            await CheckControllerNavigationAsync("theme.disconnected", false, true, "Disconnected tools remain disabled in dark mode");
            CaptureDesktop("theme-dark-disconnected.png");
            loopbackController.Kill(entireProcessTree: true); await loopbackController.WaitForExitAsync(stop.Token);
            product = loopbackController = LaunchLoopbackProduct(false, controllerRoot, "en");
            await WaitUiAsync(); Require(TryValue("themeSelector") == "Dark", "saved_choice_restored");
            await CheckPalette(true, "saved_dark_palette");
            await SelectThemeAsync(UiText.SystemDefault);
            Require(ThemePreference.Load(controllerRoot) == "system", "system_choice_saved");

            // Render the production dialog in the guest, without changing a remote PC's wake settings.
            AppTheme.SetPreference("dark");
            using var dialog = new WakeSettingsForm("PC-YOLANDE", null);
            dialog.Show(); dialog.Activate(); await Task.Delay(200, stop.Token);
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(dialog.Location, Point.Empty, bitmap.Size);
            bitmap.Save(Path.Combine(output, "theme-dark-wake-dialog.png"));
            Require(dialog.BackColor == AppTheme.Surface && dialog.Controls.Find("wakeMac", true).Single().BackColor == AppTheme.Surface,
                "new_dialog_palette");
            dialog.Close();
            product = loopbackAgent; await QuitLocalizedProductAsync("theme_dark_tray"); loopbackAgent = null;
            product = loopbackController; await QuitLocalizedProductAsync("theme_system_tray"); loopbackController = null;
            await FinishAsync();
        }
        finally
        {
            await CleanupLoopbackProcessesAsync();
            if (original == null) personalization.DeleteValue("AppsUseLightTheme", false);
            else personalization.SetValue("AppsUseLightTheme", original);
        }
    }

    private async Task SelectThemeAsync(string caption)
    {
        Native.FocusWindow(product!.Id);
        var combo = Element("themeSelector");
        ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        await Task.Delay(150, stop.Token);
        var option = AutomationElement.RootElement.FindAll(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, product.Id), new PropertyCondition(AutomationElement.NameProperty, caption)))
            .Cast<AutomationElement>().First(item => item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _));
        ((SelectionItemPattern)option.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
        await Task.Delay(300, stop.Token);
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr ThemeSendMessage(IntPtr window, uint message, IntPtr wparam, string lparam, uint flags, uint timeout, out IntPtr result);
}
