using System.Drawing;
using System.IO;
using System.Windows.Automation;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task DesktopDropReviewAsync()
    {
        var controllerWindow = (WindowPattern)Root().GetCurrentPattern(WindowPattern.Pattern);
        controllerWindow.SetWindowVisualState(WindowVisualState.Minimized);
        product = loopbackAgent;
        var agentWindow = (WindowPattern)Root().GetCurrentPattern(WindowPattern.Pattern);
        agentWindow.SetWindowVisualState(WindowVisualState.Minimized);
        WindowState = Forms.FormWindowState.Minimized;
        await Task.Delay(400, stop.Token);
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        var point = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        var remote = new RemoteClient(RemoteClient.Load().Connection, await HashFileAsync(application));
        string name = "rd-drop-" + Guid.NewGuid().ToString("N") + ".txt";
        string source = Path.Combine(output, name), destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);
        try
        {
            var target = RemoteClient.Require(await remote.CallAsync("shell.dropTarget", new { x = point.X, y = point.Y }, stop.Token));
            if (!target.GetProperty("desktop").GetBoolean()) throw new IOException("The drop target was not the desktop background.");
            await File.WriteAllTextAsync(source, "desktop drop position", stop.Token);
            await remote.UploadAsync(source, destination, stop.Token);
            RemoteClient.Require(await remote.CallAsync("shell.positionDesktop", new { directory = target.Str("directory"), names = new[] { name }, x = point.X, y = point.Y }, stop.Token));
            AutomationElement? item = null;
            await WaitForUiAsync(() =>
            {
                item = AutomationElement.RootElement.FindFirst(TreeScope.Descendants,
                    new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new OrCondition(new PropertyCondition(AutomationElement.NameProperty, name), new PropertyCondition(AutomationElement.NameProperty, Path.GetFileNameWithoutExtension(name)))));
                return item != null && Math.Abs(item.Current.BoundingRectangle.Left - point.X) < 140 && Math.Abs(item.Current.BoundingRectangle.Top - point.Y) < 140;
            }, 10);
            if (await File.ReadAllTextAsync(destination, stop.Token) != "desktop drop position") throw new IOException("Copied desktop content differs.");
            Pass("desktop.drop_position", "The verified remote copy is positioned at the saved drop point, within the desktop icon grid", new { point, bounds = item!.Current.BoundingRectangle });
            CaptureDesktop("desktop-drop-position.png");
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
            agentWindow.SetWindowVisualState(WindowVisualState.Normal);
            product = loopbackController;
            controllerWindow.SetWindowVisualState(WindowVisualState.Normal);
            Native.FocusWindow(product!.Id);
        }
    }
}
