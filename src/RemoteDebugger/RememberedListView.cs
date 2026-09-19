using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

/// <summary>Persist completed header gestures, never programmatic DPI/layout changes.</summary>
internal sealed class RememberedListView : Forms.ListView
{
    private string? layoutRoot;
    private TableColumnLayout[] layout = [];
    private bool applying;

    public RememberedListView() => DoubleBuffered = true;

    public void RememberLayout(string root, params string[] columnIds)
    {
        if (columnIds.Length != Columns.Count) throw new ArgumentException("Every column needs a stable ID.", nameof(columnIds));
        layoutRoot = root;
        for (int i = 0; i < columnIds.Length; i++) Columns[i].Name = columnIds[i];
        layout = TableLayoutStore.Load(root, Name, Columns.Cast<Forms.ColumnHeader>()
            .Select((column, i) => new TableColumnLayout(column.Name!, column.Width, i)).ToArray());
        AllowColumnReorder = true;
        ShowItemToolTips = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyLayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        if (layoutRoot == null || applying) return;
        applying = true;
        try
        {
            foreach (var column in layout.OrderBy(column => column.Order))
            {
                var header = Columns[column.Id]!;
                header.Width = (int)Math.Round(column.Width * DeviceDpi / 96d);
                header.DisplayIndex = column.Order;
            }
        }
        finally { applying = false; }
    }

    private void SaveLayout()
    {
        if (applying || layoutRoot == null || IsDisposed) return;
        layout = Columns.Cast<Forms.ColumnHeader>().Select(column =>
            new TableColumnLayout(column.Name!, Math.Max(32, column.Width * 96d / DeviceDpi), column.DisplayIndex)).ToArray();
        TableLayoutStore.Save(layoutRoot, Name, layout);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyHeader { public IntPtr Window; public UIntPtr Id; public int Code; }

    protected override void WndProc(ref Forms.Message message)
    {
        // HDN_ENDTRACK A/W, HDN_ENDDRAG, HDN_DIVIDERDBLCLICK A/W. Save after
        // native processing commits the new width/order, including auto-fit.
        bool completed = message.Msg == 0x004E && message.LParam != IntPtr.Zero &&
            Marshal.PtrToStructure<NotifyHeader>(message.LParam).Code is -307 or -327 or -311 or -305 or -325;
        base.WndProc(ref message);
        if (completed && !applying && IsHandleCreated && !IsDisposed) BeginInvoke(SaveLayout);
    }
}
