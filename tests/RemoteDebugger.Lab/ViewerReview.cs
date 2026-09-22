using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task ViewerReviewAsync(string controllerRoot)
    {
        AutomationElement Control(string id) => FindVisibleId(id) ?? throw new IOException("Missing viewer control: " + id);
        void Require(bool condition, string id, string requirement, object? evidence = null)
        {
            if (!condition) { Fail(id, requirement, evidence); throw new IOException(requirement); }
            Pass(id, requirement, evidence);
        }
        var economy = Control("relayEconomy");
        await WaitForUiAsync(() => Value(Control("resourceMiniCharts")).Contains("%", StringComparison.Ordinal), 15);
        Require(Value(Control("resourceMiniCharts")).Contains("%", StringComparison.Ordinal), "viewer.resource_charts", "Direct connections populate the resource charts", new { values = Value(Control("resourceMiniCharts")) });
        Require(!economy.Current.IsEnabled && ((TogglePattern)economy.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.Off,
            "viewer.economy_direct", "Direct screen streaming shows economy disabled and unchecked");
        var clipboard = Control("shareClipboard");
        Require(Math.Abs(clipboard.Current.BoundingRectangle.Top - Control("mouseKeyboard").Current.BoundingRectangle.Top) < 10,
            "viewer.clipboard_location", "Clipboard sharing sits beside the input toggle");
        Require(Control("streamStatus").Current.BoundingRectangle.Left < Control("inputStatus").Current.BoundingRectangle.Left,
            "viewer.stats_left", "Stream statistics occupy the left side of the status bar");
        foreach (string id in new[] { "mouseKeyboard", "shareClipboard" }) ((TogglePattern)Control(id).GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        await Task.Delay(200, stop.Token);
        string saved = Directory.GetFiles(Path.Combine(controllerRoot, "viewer-preferences"), "*.json").Single();
        Require(JsonSerializer.Deserialize<ViewerPreferences>(File.ReadAllText(saved)) == new ViewerPreferences(false, false),
            "viewer.saved_toggles", "The real viewer saves both toggles under the connected device identity");
        foreach (string id in new[] { "mouseKeyboard", "shareClipboard" }) ((TogglePattern)Control(id).GetCurrentPattern(TogglePattern.Pattern)).Toggle();

        // In a loopback guest both endpoints share the desktop. Sending text into
        // the foreground controller editor proves the real UI-to-agent path.
        var text = Control("remoteText");
        ((ValuePattern)text.GetCurrentPattern(ValuePattern.Pattern)).SetValue("viewer-text");
        text.SetFocus(); Native.FocusWindow(product!.Id); await ViewerChordAsync([0x23]); Native.Key(product.Id, "ENTER");
        await WaitForUiAsync(() => Value(text) == "viewer-textviewer-text", 8);
        Require(Value(text) == "viewer-textviewer-text", "viewer.enter_sends_text", "Enter in the text editor sends its contents through the remote input channel");
        // Clicking a controller button changes the agent's foreground control in
        // this single-desktop fixture; Enter above exercises their shared sender.
        Require(Value(Control("typeText")) == UiText.TypeText, "viewer.send_text_caption",
            "The text action has the explicit localized Send text caption", new { caption = Value(Control("typeText")) });
        ((ValuePattern)text.GetCurrentPattern(ValuePattern.Pattern)).SetValue("");

        ResizeProductWindow(1060, 720);
        await Task.Delay(350, stop.Token);
        Require(new[] { "monitor", "mouseKeyboard", "shareClipboard", "relayEconomy", "remoteText", "typeText", "enterKey", "secureAttention", "fullScreen", "pauseViewing" }
            .All(id => FitsVisibleAncestors(Control(id))), "viewer.minimum_layout", "All viewer controls fit the minimum window");
        CaptureDesktop("viewer-normal-minimum.png");
        for (int i = 0; i < 3; i++)
        {
            if (i == 0) InvokeElement(Control("fullScreen"));
            else
            {
                Control("remoteText").SetFocus();
                await ViewerChordAsync(i == 1 ? [0xA2, 0xA4, 0x7B] : [0xA3, 0xA1, 0x7B, 0x7B]);
            }
            await Task.Delay(350, stop.Token);
            var exit = Control("exitFullScreen"); var status = Control("fullScreenStatus");
            Require(Control("fullScreenResourceCharts").Current.BoundingRectangle.Right <= exit.Current.BoundingRectangle.Left,
                "viewer.fullscreen_charts_" + i, "Compact resource charts remain visible before the exit button");
            Require(status.Current.BoundingRectangle.Right <= exit.Current.BoundingRectangle.Left && Value(status).Contains("Direct LAN", StringComparison.Ordinal),
                "viewer.fullscreen_bar_" + i, "Full screen keeps device/statistics left and a visible exit button right", new { caption = Value(status) });
            if (i == 0) { CaptureDesktop("viewer-fullscreen.png"); InvokeElement(exit); }
            else
            {
                Control("remoteScreen").SetFocus();
                await ViewerChordAsync(i == 1 ? [0xA2, 0xA4, 0x7B] : [0xA3, 0xA1, 0x7B]);
            }
            await WaitForUiAsync(() => FindVisibleId("exitFullScreen") == null, 5);
            Require(FindVisibleId("exitFullScreen") == null, "viewer.fullscreen_exit_" + i, "The exit button and both requested shortcuts return to the normal viewer");
        }
        await NativeViewerKeyboardAsync();
        Native.FocusWindow(product.Id);
        CaptureDesktop("viewer-final.png");
    }

    private async Task NativeViewerKeyboardAsync()
    {
        using var canary = new Forms.Form { Text = "Keyboard capture canary", Width = 520, Height = 220 };
        using var editor = new Forms.TextBox { Multiline = true, Dock = Forms.DockStyle.Fill };
        canary.Controls.Add(editor); canary.Show(); canary.Activate(); editor.Focus();
        bool capturing = true;
        int escapes = 0;
        var events = new List<JsonElement>();
        using var capture = new RemoteKeyboardCapture(() => capturing && canary.ContainsFocus && Forms.Form.ActiveForm == canary,
            value => events.Add(Json.Element(value)), () => events.Add(Json.Element(new { kind = "release" })),
            () => { escapes++; capturing = false; });
        capture.Start(); capture.Refresh();
        var before = Process.GetProcessesByName("Taskmgr").Select(p => p.Id).ToHashSet();
        await ViewerChordAsync([0xA0, 0x24]);
        await ViewerChordAsync([0x5B]);
        await ViewerChordAsync([0xA4, 0x09]);
        await ViewerChordAsync([0xA2, 0xA0, 0x1B]);
        if (!canary.ContainsFocus || editor.Text.Length != 0 || Process.GetProcessesByName("Taskmgr").Any(p => !before.Contains(p.Id)))
            throw new IOException("A captured system shortcut escaped to the local desktop.");
        int[] expected = [0xA0, 0x24, 0x24, 0xA0, 0x5B, 0x5B, 0xA4, 0x09, 0x09, 0xA4, 0xA2, 0xA0, 0x1B, 0x1B, 0xA0, 0xA2];
        if (!events.Where(e => e.Str("kind") is "keyDown" or "keyUp").Select(e => e.Int("virtualKey")).SequenceEqual(expected))
            throw new IOException("The keyboard capture changed modifier order or dropped keys: " + Json.Text(events));
        Pass("viewer.native_shortcuts", "The real Windows hook captures complete Shift+Home, Win, Alt+Tab and Ctrl+Shift+Esc without local execution", events.ToArray());
        events.Clear();
        using (var local = new Forms.Form { Text = "Local input after focus change", Width = 400, Height = 150 })
        using (var localEditor = new Forms.TextBox { Dock = Forms.DockStyle.Fill })
        {
            local.Controls.Add(localEditor); local.Show(); local.Activate(); localEditor.Focus();
            await ViewerChordAsync([0x42]);
            if (!localEditor.Text.Equals("b", StringComparison.OrdinalIgnoreCase) || events.Any(e => e.Str("kind") is "keyDown" or "keyUp"))
                throw new IOException("Switching windows did not return keyboard input to the local desktop.");
            Pass("viewer.focus_release", "Switching to another window releases capture and allows local typing");
            local.Close();
        }
        canary.Activate(); editor.Focus();
        events.Clear();
        ViewerTestKey(0xA0, 0x2A, 0, UIntPtr.Zero);
        await Task.Delay(3600, stop.Token);
        ViewerTestKey(0xA0, 0x2A, 2, UIntPtr.Zero);
        await Task.Delay(100, stop.Token);
        if (!events.Any(e => e.Str("kind") == "keepAlive")) throw new IOException("Held modifiers did not keep the input watchdog alive.");
        Pass("viewer.held_modifier_keepalive", "Holding Shift without repeating sends watchdog keepalives");
        await ViewerChordAsync([0xA2, 0xA4, 0x7B]);
        if (escapes != 1 || !events.Any(e => e.Str("kind") == "release")) throw new IOException("Capture exit did not release keys.");
        await ViewerChordAsync([0x42]);
        if (!editor.Text.Equals("b", StringComparison.OrdinalIgnoreCase)) throw new IOException("Local keyboard input did not resume after capture exit.");
        Pass("viewer.capture_release", "The escape chord releases captured input and normal local typing resumes");

        editor.Text = "alpha beta"; editor.SelectionStart = editor.TextLength; editor.SelectionLength = 0;
        try
        {
            Native.HandleInput(Json.Element(new { kind = "keyDown", virtualKey = 0xA0, scanCode = 0x2A }));
            for (int i = 0; i < 5; i++) { await Task.Delay(750, stop.Token); Native.HandleInput(Json.Element(new { kind = "keepAlive" })); }
            Native.HandleInput(Json.Element(new { kind = "keyDown", virtualKey = 0x24, scanCode = 0x47, extended = true }));
            Native.HandleInput(Json.Element(new { kind = "keyUp", virtualKey = 0x24, scanCode = 0x47, extended = true }));
            Native.HandleInput(Json.Element(new { kind = "keyUp", virtualKey = 0xA0, scanCode = 0x2A }));
            await Task.Delay(150, stop.Token);
            if (editor.SelectedText != "alpha beta") throw new IOException("Remote Shift+Home lost Shift or extended Home semantics.");
            Pass("viewer.native_shift_home", "Native injection selects to line start after Shift is held longer than the watchdog interval");
        }
        finally { Native.ReleaseAllInput(); }
        canary.Close();
    }

    private async Task ViewerChordAsync(byte[] keys)
    {
        foreach (byte vk in keys) { ViewerTestKey(vk, (byte)ViewerMapKey(vk, 0), Native.IsExtendedKey(vk) ? 1u : 0u, UIntPtr.Zero); await Task.Delay(40, stop.Token); }
        foreach (byte vk in keys.Reverse()) { ViewerTestKey(vk, (byte)ViewerMapKey(vk, 0), (Native.IsExtendedKey(vk) ? 1u : 0u) | 2u, UIntPtr.Zero); await Task.Delay(40, stop.Token); }
        await Task.Delay(150, stop.Token);
    }

    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void ViewerTestKey(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")] private static extern uint ViewerMapKey(uint key, uint mapType);
}
