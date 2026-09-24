using RemoteDebugger;
using System.Windows.Forms;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class DialogBehaviorTests
{
    [Fact]
    public void RestartOptionsShowEveryControlWithoutScrolling()
    {
        using var form = new RestartOptionsForm(new PowerPreflight("PC", @"PC\yolande", true, ""));
        form.PerformLayout();
        var layout = Assert.IsType<FlowLayoutPanel>(Assert.Single(form.Controls));
        layout.PerformLayout();

        Assert.False(layout.AutoScroll);
        Assert.All(layout.Controls.Cast<Control>(), control => Assert.True(layout.ClientRectangle.Contains(control.Bounds)));
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
