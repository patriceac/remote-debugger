using RemoteDebugger;
using Xunit;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Platform.Tests;

public sealed class LiveTextTests
{
    [Fact]
    public void LanguageRefreshChangesBoundCaptionsAndPreservesRawData()
    {
        string caption = "Name";
        using var panel = new Forms.Panel();
        using var label = new Forms.Label().WithText(() => caption);
        using var editor = new Forms.TextBox { Text = "Name" };
        panel.Controls.Add(label); panel.Controls.Add(editor);
        caption = "Nombre";
        LiveText.Refresh(panel);
        Assert.Equal("Nombre", label.Text);
        Assert.Equal("Name", editor.Text);
    }

    [Fact]
    public void RawResultReplacesPreviousTranslatedMessageEvenWhenTextMatches()
    {
        string message = "Ready";
        using var result = new Forms.TextBox().WithText(() => message);
        result.SetText("Ready");
        message = "Prêt";
        LiveText.Refresh(result);
        Assert.Equal("Ready", result.Text);
        result.SetText(() => message);
        result.SetText("");
        LiveText.Refresh(result);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public void ColumnTranslationPreservesWidthAndReturnsCaptionWithoutSortGlyph()
    {
        string caption = "Name";
        using var table = new Forms.ListView();
        var column = table.Columns.Add("", 237).WithText(() => caption);
        column.Text += "  ▲";
        caption = "Nom";
        LiveText.Refresh(table);
        Assert.Equal("Nom", LiveText.Caption(column));
        Assert.Equal(237, column.Width);
    }
}
