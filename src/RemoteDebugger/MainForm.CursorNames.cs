using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private readonly Forms.TextBox cursorNickname = TextBox("cursorNickname");
    private readonly Forms.Button saveCursorNickname = Button(() => UiText.SaveWanAddress, "saveCursorNickname", 104);
    private string cursorNicknameFingerprint = "";
    private DeviceCursorNames? cursorNames;
    private DeviceCursorNames CursorNames => cursorNames ??= new(root);

    private Forms.Control BuildCursorNicknameFields()
    {
        var panel = ConnectionStack("cursorNicknameSettings");
        ConnectionRow(panel, ConnectionLabel("cursorNicknameLabel", 10.5f).WithText(() => UiText.Get("CursorNickname")));
        var row = new Forms.Panel { Dock = Forms.DockStyle.Top, Height = 38, Margin = Forms.Padding.Empty };
        cursorNickname.BorderStyle = Forms.BorderStyle.None; cursorNickname.MaxLength = 28;
        saveCursorNickname.AutoSize = false; saveCursorNickname.MinimumSize = new(98, 38); saveCursorNickname.Margin = Forms.Padding.Empty;
        var field = ConnectionField.Wrap(cursorNickname, 38); field.Dock = Forms.DockStyle.None;
        row.Controls.Add(field); row.Controls.Add(saveCursorNickname);
        row.SizeChanged += (_, _) =>
        {
            int saveWidth = HeaderPixels(98); saveCursorNickname.SetBounds(Math.Max(0, row.Width - saveWidth), 0, saveWidth, row.Height);
            field.SetBounds(0, 0, Math.Max(0, row.Width - saveWidth - HeaderPixels(8)), row.Height);
        };
        ConnectionRow(panel, row, 4);
        var help = ConnectionLabel("cursorNicknameHelp", 10.5f).WithText(() => UiText.Get("CursorNicknameHelp"));
        panel.SizeChanged += (_, _) => help.MaximumSize = new(Math.Max(120, panel.ClientSize.Width - panel.Padding.Horizontal), 0);
        ConnectionRow(panel, help, 4);
        saveCursorNickname.Click += (_, _) =>
        {
            if (!saveCursorNickname.Enabled || selectedPeer?.Fingerprint != cursorNicknameFingerprint) return;
            try
            {
                CursorNames.Save(cursorNicknameFingerprint, cursorNickname.Text);
                cursorNickname.SetText(CursorNames.Get(cursorNicknameFingerprint));
                connectionState.SetText(() => UiText.Get("CursorNicknameSaved"));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
            { connectionState.SetText(ex.Message); cursorNickname.Focus(); }
        };
        RefreshCursorNickname(); return panel;
    }

    private void RefreshCursorNickname()
    {
        string fingerprint = selectedPeer?.Fingerprint ?? "";
        if (fingerprint != cursorNicknameFingerprint)
        { cursorNickname.SetText(CursorNames.Get(fingerprint)); cursorNicknameFingerprint = fingerprint; }
        cursorNickname.PlaceholderText = UiText.Get("CursorNicknameDefault");
        cursorNickname.Enabled = saveCursorNickname.Enabled = PairingExchange.ValidHash(fingerprint) && !pairingBusy && !FleetBusy && !terminating;
    }
}
