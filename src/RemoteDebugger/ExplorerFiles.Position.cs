using System.Runtime.InteropServices;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal static partial class ExplorerFiles
{
    internal static Task<object> PositionDesktopAsync(string directory, string[] names, int x, int y, CancellationToken ct)
    {
        string[] paths = DesktopItems(directory, names);
        return RunAsync<object>(() =>
        {
            DesktopCapture.RequireDesktop();
            if (!Forms.Screen.AllScreens.Any(screen => screen.Bounds.Contains(x, y))) throw new ArgumentException("Drop point is outside the desktop.");
            using var objects = new ComObjects();
            dynamic shell = objects.Own(Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!);
            dynamic windows = objects.Own(shell.Windows());
            object location = 0, root = 0; int window;
            object desktop = objects.Own(windows.FindWindowSW(ref location, ref root, 8 /* SWC_DESKTOP */, out window, 1));
            var services = (IShellServiceProvider)desktop;
            Guid service = new("4c96be40-915c-11cf-99d3-00aa004ae837"), browserId = typeof(IShellBrowser).GUID;
            services.QueryService(ref service, ref browserId, out var browserObject);
            var browser = (IShellBrowser)objects.Own(browserObject);
            browser.QueryActiveShellView(out var viewObject);
            var view = (IFolderView)objects.Own(viewObject);
            view.GetSpacing(out var spacing);
            var bounds = Forms.Screen.FromPoint(new(x, y)).WorkingArea;
            int columnCount = Math.Max(1, (bounds.Right - x) / Math.Max(1, spacing.X));
            var pidls = new List<IntPtr>();
            try
            {
                foreach (string path in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _));
                    pidls.Add(pidl);
                }
                var children = pidls.Select(ILFindLastID).ToArray();
                // Upload commit is atomic, but Explorer observes it asynchronously.
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (children.Any(pidl => view.GetItemPosition(pidl, out _) < 0))
                {
                    ct.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow >= deadline) throw new IOException("Explorer has not displayed the copied items yet.");
                    ct.WaitHandle.WaitOne(100);
                }
                var points = Enumerable.Range(0, paths.Length).Select(i => new NativePoint(
                    x + i % columnCount * Math.Max(1, spacing.X),
                    Math.Min(bounds.Bottom - Math.Max(1, spacing.Y), y + i / columnCount * Math.Max(1, spacing.Y)))).ToArray();
                ct.ThrowIfCancellationRequested();
                // Supported shell API; TRANSLATEPT preserves virtual-desktop coordinates and DPI.
                view.SelectAndPositionItems((uint)children.Length, children, points, 0x400000a1 /* SELECT | TRANSLATEPT | POSITIONITEM | NOTAKEFOCUS */);
                return new { positioned = true };
            }
            finally { foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl); }
        }, ct);
    }

    internal static string[] DesktopItems(string directory, string[] names)
    {
        string desktop = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), Path.TrimEndingDirectorySeparator(desktop), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only copied desktop items can be positioned.");
        if (names.Length is < 1 or > 256) throw new ArgumentException("Select between 1 and 256 desktop items.");
        return names.Select(name =>
        {
            TransferTree.ValidateRelativePath(name);
            if (name.Contains('\\') || name.Contains('/')) throw new ArgumentException("Only top-level desktop items can be positioned.");
            return TransferTree.Destination(desktop, name);
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellServiceProvider
    {
        void QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport, Guid("000214e2-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(); void ContextSensitiveHelp(); void InsertMenusSB(); void SetMenuSB(); void RemoveMenusSB();
        void SetStatusTextSB(); void EnableModelessSB(); void TranslateAcceleratorSB(); void BrowseObject();
        void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
        void QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object view);
    }

    [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode(); void SetCurrentViewMode(); void GetFolder(); void Item(); void ItemCount();
        void Items(); void GetSelectionMarkedItem(); void GetFocusedItem();
        [PreserveSig] int GetItemPosition(IntPtr pidl, out NativePoint point);
        void GetSpacing(out NativePoint point);
        void GetDefaultSpacing(); void GetAutoArrange(); void SelectItem();
        void SelectAndPositionItems(uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] pidls,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] NativePoint[] points, uint flags);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr pidl, uint mask, out uint attributes);
    [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr pidl);
}
