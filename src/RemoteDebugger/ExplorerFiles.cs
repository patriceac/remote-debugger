using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal sealed record ExplorerFileContext(string Directory, string[] Paths);

/// <summary>Resolve real Explorer items on the interactive desktop without clipboard shortcuts.</summary>
internal static class ExplorerFiles
{
    private static readonly SemaphoreSlim queries = new(2, 2);
    internal static async Task<ExplorerFileContext> QueryAsync(string operation, int x, int y, CancellationToken ct)
    {
        await queries.WaitAsync(ct);
        var completion = new TaskCompletionSource<ExplorerFileContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { ct.ThrowIfCancellationRequested(); completion.TrySetResult(Query(operation, x, y)); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { queries.Release(); }
        }) { IsBackground = true, Name = "Explorer file context" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        return await completion.Task.WaitAsync(ct);
    }

    private static ExplorerFileContext Query(string operation, int x, int y)
    {
        DesktopCapture.RequireDesktop();
        if (!System.Windows.Forms.Screen.AllScreens.Any(screen => screen.Bounds.Contains(x, y))) throw new ArgumentException("Drop point is outside the desktop.");
        IntPtr window = GetAncestor(WindowFromPoint(new(x, y)), 2);
        string itemName = ItemAt(x, y, out var itemElement);
        using var objects = new ComObjects();
        dynamic shell = objects.Own(Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!);
        dynamic windows = objects.Own(shell.Windows());
        for (int i = 0; i < (int)windows.Count; i++)
        {
            dynamic browser = objects.Own(windows.Item(i));
            if (new IntPtr((long)browser.HWND) != window) continue;
            dynamic document = objects.Own(browser.Document);
            dynamic folder = objects.Own(document.Folder);
            dynamic self = objects.Own(folder.Self);
            string directory = (string)self.Path;
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new InvalidOperationException("Open a filesystem folder in Explorer first.");
            if (operation == "shell.dropTarget") return new ExplorerFileContext(FolderAt(folder, itemName, directory, objects), Array.Empty<string>());
            if (itemElement == null) return new(directory, []); // A window title or background drag is not a file drag.
            dynamic selected = objects.Own(document.SelectedItems());
            string[] paths = ReadItems(selected, objects);
            // SendInput has completed, but Explorer may not yet have dispatched
            // the selection notification. Never export an unrelated old selection.
            dynamic items = objects.Own(folder.Items());
            bool hitFound = false;
            for (int j = 0; j < (int)items.Count; j++)
            {
                dynamic item = objects.Own(items.Item(j));
                if ((string)item.Name != itemName || !(bool)item.IsFileSystem) continue;
                string hitPath = (string)item.Path;
                if (!paths.Contains(hitPath, StringComparer.OrdinalIgnoreCase)) paths = [hitPath];
                hitFound = true;
                break;
            }
            if (hitFound) return new(directory, paths);
        }
        var className = new StringBuilder(256); GetClassName(window, className, className.Capacity);
        if (className.ToString() is not ("Progman" or "WorkerW")) throw new InvalidOperationException("Drop over an Explorer folder or the Windows desktop.");
        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        dynamic desktop = objects.Own(shell.NameSpace(0));
        if (operation == "shell.dropTarget") return new ExplorerFileContext(FolderAt(desktop, itemName, desktopPath, objects), Array.Empty<string>());
        if (itemElement == null) return new(desktopPath, []);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (itemElement.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectedPattern))
        {
            var container = ((SelectionItemPattern)selectedPattern).Current.SelectionContainer;
            if (container?.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern) == true)
                foreach (var selected in ((SelectionPattern)pattern).Current.GetSelection()) names.Add(selected.Current.Name);
        }
        if (!names.Contains(itemName)) { names.Clear(); names.Add(itemName); }
        dynamic desktopItems = objects.Own(desktop.Items());
        var selectedPaths = new List<string>();
        for (int i = 0; i < (int)desktopItems.Count; i++)
        {
            dynamic item = objects.Own(desktopItems.Item(i));
            if (names.Contains((string)item.Name) && (bool)item.IsFileSystem) selectedPaths.Add((string)item.Path);
        }
        return new(desktopPath, selectedPaths.ToArray());
    }

    private static string FolderAt(dynamic folder, string name, string fallback, ComObjects objects)
    {
        if (name.Length == 0) return fallback;
        dynamic items = objects.Own(folder.Items());
        for (int i = 0; i < (int)items.Count; i++)
        {
            dynamic item = objects.Own(items.Item(i));
            if ((string)item.Name == name && (bool)item.IsFileSystem && (bool)item.IsFolder) return (string)item.Path;
        }
        return fallback;
    }
    private static string[] ReadItems(dynamic items, ComObjects objects)
    {
        var paths = new List<string>();
        for (int i = 0; i < (int)items.Count; i++)
        {
            dynamic item = objects.Own(items.Item(i));
            if ((bool)item.IsFileSystem) paths.Add((string)item.Path);
        }
        return paths.ToArray();
    }
    private static string ItemAt(int x, int y, out AutomationElement? item)
    {
        item = null;
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            for (int i = 0; element != null && i < 8; i++, element = TreeWalker.ControlViewWalker.GetParent(element))
            {
                if (element.Current.ControlType is var type && (type == ControlType.ListItem || type == ControlType.DataItem))
                { item = element; return element.Current.Name; }
                if (element.Current.ControlType == ControlType.Window) break;
            }
        }
        catch (ElementNotAvailableException) { }
        return "";
    }
    private sealed class ComObjects : IDisposable
    {
        private readonly List<object> owned = [];
        public object Own(object value) { owned.Add(value); return value; }
        public void Dispose() { for (int i = owned.Count - 1; i >= 0; i--) if (Marshal.IsComObject(owned[i])) Marshal.ReleaseComObject(owned[i]); }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);
}
