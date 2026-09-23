using System.Globalization;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private sealed record LanguageChoice(string? Code, string Caption)
    {
        public override string ToString() => Caption;
    }

    private readonly Forms.ComboBox languageSelector = new ViewerComboBox()
    {
        Name = "languageSelector", DropDownStyle = Forms.ComboBoxStyle.DropDownList,
        Dock = Forms.DockStyle.Fill, Font = new Font("Segoe UI", 10.5F), ItemHeight = 26
    };
    private bool updatingLanguageSelector;
    private bool refreshingMonitorLabels;

    private Forms.Control BuildLanguageSelector(string? launchOverride)
    {
        string? choice = launchOverride == null ? LanguagePreference.Load(root)
            : UiCulture.Select(CultureInfo.CurrentUICulture, launchOverride).Name;
        PopulateLanguageChoices(choice);
        languageSelector.SelectedIndexChanged += (_, _) => ApplyLanguageChoice();
        var section = new Forms.TableLayoutPanel
        {
            Name = "languageSettings", Dock = Forms.DockStyle.Fill, AutoSize = true,
            ColumnCount = 1, RowCount = 4, Padding = new Forms.Padding(8, 4, 8, 22), Margin = Forms.Padding.Empty
        };
        section.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        section.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        section.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        section.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        section.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        section.Controls.Add(new Forms.Label { Name = "themeLabel", AutoSize = true, ForeColor = RailSecondary,
            Font = new Font("Segoe UI", 10.5F), Margin = new Forms.Padding(0, 0, 0, 4) }.WithText(() => UiText.Get("Theme")), 0, 0);
        section.Controls.Add(themeSelector, 0, 1);
        section.Controls.Add(new Forms.Label { Name = "languageLabel", AutoSize = true, ForeColor = RailSecondary,
            Font = new Font("Segoe UI", 10.5F), Margin = new Forms.Padding(0, 0, 0, 4) }.WithText(() => UiText.Language), 0, 2);
        languageSelector.Margin = Forms.Padding.Empty;
        section.Controls.Add(languageSelector, 0, 3);
        return section;
    }

    private void PopulateLanguageChoices(string? choice)
    {
        updatingLanguageSelector = true;
        try
        {
            languageSelector.AccessibleName = UiText.Language;
            languageSelector.Items.Clear();
            languageSelector.Items.AddRange(new object[]
            {
                new LanguageChoice(null, UiText.SystemDefault), new LanguageChoice("en", "English"),
                new LanguageChoice("fr", "Français"), new LanguageChoice("es", "Español")
            });
            languageSelector.SelectedItem = languageSelector.Items.Cast<LanguageChoice>().Single(item => item.Code == choice);
        }
        finally { updatingLanguageSelector = false; }
    }

    private void ApplyLanguageChoice()
    {
        if (updatingLanguageSelector || languageSelector.SelectedItem is not LanguageChoice choice) return;
        UiCulture.Apply(LanguagePreference.Resolve(UiCulture.SystemLanguage, choice.Code));
        SuspendLayout();
        try
        {
            LiveText.Refresh(this);
            foreach (Forms.ToolStripItem item in tray.ContextMenuStrip!.Items) LiveText.Refresh(item);
            PopulateLanguageChoices(choice.Code);
            themeSelector.RefreshLabels(); themeSelector.AccessibleName = UiText.Get("Theme");
            RefreshLocalizedDescriptions();
            RefreshLocalizedRows();
            RefreshUiState();
        }
        finally { ResumeLayout(true); }
        try { LanguagePreference.Save(root, choice.Code); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetFooterMessage(() => UiText.LanguageSaveFailed);
            footerDetail = ex.Message;
            RefreshFooter();
        }
    }

    // Only display cells change. Keep selection, scroll position, column order,
    // editors, in-flight operations and the remote session intact.
    private void RefreshLocalizedRows()
    {
        SetSortIndicator(processList, (int)processSort.Column, processSort.Descending);
        SetSortIndicator(fileList, (int)fileSort.Column, fileSort.Descending);
        foreach (Forms.ListViewItem item in peers.Items) item.SubItems[2].Text = UiText.Available;
        foreach (Forms.ListViewItem item in processList.Items)
            if (item.Tag is ProcessSortRow row) item.SubItems[4].Text = row.Responding is null ? "—" : row.Responding.Value ? UiText.Yes : UiText.NotResponding;
        foreach (Forms.ListViewItem item in fileList.Items)
            if (item.Tag is FileSortRow row)
            {
                item.SubItems[1].Text = row.IsDirectory ? UiText.Folder : UiText.File;
                item.SubItems[2].Text = row.SizeBytes is { } bytes ? FormatBytes(bytes) : "—";
            }
        refreshingMonitorLabels = true;
        try { monitor.RefreshLabels(); }
        finally { refreshingMonitorLabels = false; }
    }

    private Func<string> footerMessageText = () => UiText.ReadyForConnection;
    private Func<string> footerDetailText = () => UiText.CloseToTray;
    private string footerMessage { get => footerMessageText(); set => footerMessageText = () => value; }
    private string footerDetail { get => footerDetailText(); set => footerDetailText = () => value; }
    private void SetFooterMessage(Func<string> text) => footerMessageText = text;
    private void SetFooterDetail(Func<string> text) => footerDetailText = text;

    private static Forms.Button Button(Func<string> text, string name, int width = 0, bool primary = false, bool destructive = false) =>
        Button(text(), name, width, primary, destructive).WithText(text);
    private static Forms.Button RailButton(Func<string> text, string name) => RailButton(text(), name).WithText(text);
    private static Forms.Button RailSubButton(Func<string> text, string name) => RailSubButton(text(), name).WithText(text);
    private static Forms.Label RailCaption(Func<string> text) => RailCaption(text()).WithText(text);
    private static Forms.Label Eyebrow(Func<string> text) => Eyebrow(text()).WithText(text);
    private static Forms.Label Badge(Func<string> text, string name) => Badge(text(), name).WithText(() => "●  " + text());
    private static Forms.Label RowLabel(Func<string> text, string name) => RowLabel(text(), name).WithText(text);
}
