using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

/// <summary>Persist completed header gestures, never programmatic DPI/layout changes.</summary>
internal sealed class RememberedListView : Forms.ListView
{
    private readonly Forms.ImageList rowHeightImages = new();
    private string? layoutRoot;
    private TableColumnLayout[] layout = [];
    private bool applying;
    private int logicalRowHeight;

    public RememberedListView() => DoubleBuffered = true;

    public bool FitColumnsToWidth { get; init; }

    public int LogicalRowHeight
    {
        get => logicalRowHeight;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            logicalRowHeight = value;
            ApplyRowHeight();
        }
    }

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
        ApplyRowHeight();
        ApplyLayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyRowHeight();
        ApplyLayout();
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        if (FitColumnsToWidth) ApplyLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) rowHeightImages.Dispose();
        base.Dispose(disposing);
    }

    private void ApplyRowHeight()
    {
        if (logicalRowHeight == 0 || IsDisposed) return;
        var size = new Size(1, Math.Max(1, (int)Math.Round(logicalRowHeight * DeviceDpi / 96d)));
        if (rowHeightImages.ImageSize == size && ReferenceEquals(SmallImageList, rowHeightImages)) return;
        SmallImageList = null;
        rowHeightImages.ImageSize = size;
        SmallImageList = rowHeightImages;
    }

    private void ApplyLayout()
    {
        if (layoutRoot == null || applying) return;
        applying = true;
        try
        {
            double scale = DeviceDpi / 96d;
            if (FitColumnsToWidth && ClientSize.Width > 0 && layout.Length > 0)
                scale = Math.Min(scale, ClientSize.Width / layout.Sum(column => column.Width));
            foreach (var column in layout.OrderBy(column => column.Order))
            {
                var header = Columns[column.Id]!;
                header.Width = (int)(FitColumnsToWidth ? Math.Floor(column.Width * scale) : Math.Round(column.Width * scale));
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
