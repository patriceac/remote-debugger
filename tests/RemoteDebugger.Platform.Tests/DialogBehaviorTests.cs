using RemoteDebugger;
using System.Windows.Forms;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class DialogBehaviorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PowerConfirmationFitsWithoutScrollingAndDefaultsToCancel(bool restart)
    {
        using var form = new PowerConfirmationForm(new PowerPreflight("PC", @"PC\yolande", true, ""), restart);
        form.PerformLayout();
        Assert.False(form.AutoScroll);
        Assert.Contains("PC", form.Controls.Find("powerQuestion", true).Single().Text);
        Assert.Same(form.CancelButton, form.AcceptButton);
        Assert.False(form.OneTimeLogin);
        Assert.All(form.Controls.Cast<Control>().Where(control => control.Width > 0 && control.Height > 0),
            control => Assert.True(form.ClientRectangle.Contains(control.Bounds)));
    }

    [Fact]
    public void DisablingOneTimeSignInClearsThePasswordAndCollapsesTheDialog()
    {
        using var form = new PowerConfirmationForm(new PowerPreflight("PC", @"PC\yolande", true, ""), true);
        var choice = (CheckBox)form.Controls.Find("oneTimeLogin", true).Single();
        var password = (TextBox)form.Controls.Find("oneTimePassword", true).Single();
        int collapsed = form.Height;
        choice.Checked = true; password.Text = "disposable-test-value";
        Assert.True(form.OneTimeLogin); Assert.True(password.Enabled); Assert.True(password.UseSystemPasswordChar);
        Assert.True(form.Height > collapsed);
        choice.Checked = false;
        Assert.False(form.OneTimeLogin); Assert.False(password.Enabled); Assert.Empty(password.Text);
        Assert.Empty(form.Password); Assert.Equal(collapsed, form.Height);
    }

    [Fact]
    public void SuccessfulTransferClosesWithoutCancelling()
    {
        int cancellations = 0;
        using var form = new TransferQueueForm(() => cancellations++, () => Task.CompletedTask);
        form.SetItems(["file.txt"]);
        form.SetState("verified", complete: true);
        _ = form.Handle;

        form.CloseAfterSuccess();

        Assert.True(form.IsDisposed);
        Assert.Equal(0, cancellations);
    }
}
