using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

/// <summary>Windows virtual-file drag source. Explorer owns destination selection and replacement dialogs.</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class VirtualFileDrop : System.Runtime.InteropServices.ComTypes.IDataObject, VirtualFileDrop.IDataObjectAsyncCapability
{
    private readonly TransferEntry[] entries;
    private readonly Func<int, Task<string>> materialize;
    private readonly CancellationToken cancellation;
    private readonly short descriptor, contents, preferred;
    private readonly FORMATETC[] formats;
    private readonly TaskCompletionSource<(int Result, uint Effect)> extraction = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool asyncMode = true, extractionStarted;
    internal Exception? Error { get; private set; }
    internal HashSet<int> RequestedFiles { get; } = [];

    internal VirtualFileDrop(TransferEntry[] entries, Func<int, Task<string>> materialize, CancellationToken cancellation)
    {
        if (entries.Length is < 1 or > TransferTree.MaximumEntries) throw new ArgumentException("Invalid file list.");
        foreach (var entry in entries) TransferTree.ValidateRelativePath(entry.RelativePath);
        this.entries = entries; this.materialize = materialize; this.cancellation = cancellation;
        descriptor = unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW"));
        contents = unchecked((short)RegisterClipboardFormat("FileContents"));
        preferred = unchecked((short)RegisterClipboardFormat("Preferred DropEffect"));
        formats = [Format(descriptor, TYMED.TYMED_HGLOBAL), Format(contents, TYMED.TYMED_ISTREAM, 0), Format(preferred, TYMED.TYMED_HGLOBAL)];
    }

    internal async Task<bool> DragAsync()
    {
        int result = DoDragDrop(this, new DropSource(cancellation), 1 /* copy only */, out int effect);
        // Explorer returns from Drop before requesting slow virtual contents on
        // its copy worker. Keep the source alive and the UI free until it finishes.
        if (extractionStarted)
        {
            var completed = await extraction.Task.WaitAsync(cancellation);
            if (completed.Result < 0) Marshal.ThrowExceptionForHR(completed.Result);
            effect = (int)completed.Effect;
        }
        if (Error != null) throw new IOException("The file copy did not complete.", Error);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        return result == 0x00040100 && (effect & 1) != 0;
    }

    public void SetAsyncMode(bool enabled) => asyncMode = enabled;
    public void GetAsyncMode(out bool enabled) => enabled = asyncMode;
    public void StartOperation(IBindCtx? reserved) => extractionStarted = true;
    public void InOperation(out bool running) => running = extractionStarted && !extraction.Task.IsCompleted;
    public void EndOperation(int result, IBindCtx? reserved, uint effect) => extraction.TrySetResult((result, effect));

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        int supported = QueryGetData(ref format); if (supported != 0) Marshal.ThrowExceptionForHR(supported);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (format.cfFormat == descriptor) { medium = Global(DescriptorBytes(entries)); return; }
            if (format.cfFormat == preferred) { medium = Global(BitConverter.GetBytes(1)); return; }
            int index = format.lindex;
            if (entries[index].Directory) { medium = Global([]); return; }
            // IDataObject::GetData is synchronous. Pump the OLE source's STA while
            // the verified network copy runs so cancellation, painting and input remain live.
            string file = WaitWithMessages(materialize(index), cancellation);
            RequestedFiles.Add(index);
            int result = SHCreateStreamOnFileEx(file, 0x20 /* read, deny writes */, 0, false, IntPtr.Zero, out IntPtr stream);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = stream, pUnkForRelease = null };
        }
        catch (Exception ex) { Error = ex; throw; }
    }

    internal static T WaitWithMessages<T>(Task<T> task, CancellationToken ct)
    {
        while (!task.IsCompleted)
        {
            ct.ThrowIfCancellationRequested(); Forms.Application.DoEvents(); Thread.Sleep(10);
        }
        return task.GetAwaiter().GetResult();
    }

    internal static byte[] DescriptorBytes(TransferEntry[] entries)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Encoding.Unicode, true);
        writer.Write(entries.Length);
        foreach (var entry in entries)
        {
            TransferTree.ValidateRelativePath(entry.RelativePath);
            writer.Write(0x80004024u | (entry.Directory ? 0u : 0x40u)); // Unicode, progress UI, attributes, write time, file size.
            writer.Write(new byte[32]); // CLSID, SIZEL and POINTL.
            writer.Write(entry.Directory ? 0x10u : 0x80u);
            writer.Write(0L); writer.Write(0L); writer.Write(entry.ModifiedUtc.UtcDateTime.ToFileTimeUtc());
            writer.Write((uint)((ulong)entry.Size >> 32)); writer.Write((uint)entry.Size);
            var name = Encoding.Unicode.GetBytes(entry.RelativePath.Replace('/', '\\'));
            writer.Write(name); writer.Write(new byte[520 - name.Length]);
        }
        return memory.ToArray();
    }

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) return unchecked((int)0x8004006B);
        if ((format.cfFormat == descriptor || format.cfFormat == preferred) && (format.tymed & TYMED.TYMED_HGLOBAL) != 0) return 0;
        if (format.cfFormat == contents && format.lindex >= 0 && format.lindex < entries.Length &&
            (format.tymed & (entries[format.lindex].Directory ? TYMED.TYMED_HGLOBAL : TYMED.TYMED_ISTREAM)) != 0) return 0;
        return unchecked((int)0x80040064);
    }
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => direction == DATADIR.DATADIR_GET ? new FormatEnumerator(formats) : throw new COMException("Unsupported direction", unchecked((int)0x80004001));
    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("Unsupported medium", unchecked((int)0x80040069));
    public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = IntPtr.Zero; return 0x00040130; }
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) { if (release) ReleaseStgMedium(ref medium); }
    public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
    public void DUnadvise(int connection) => throw new COMException("Advisory connections are unsupported", unchecked((int)0x80040003));
    public int EnumDAdvise(out IEnumSTATDATA? connections) { connections = null; return unchecked((int)0x80040003); }

    private static FORMATETC Format(short id, TYMED medium, int index = -1) => new() { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = medium };
    private static STGMEDIUM Global(byte[] data)
    {
        IntPtr handle = GlobalAlloc(0x42, (UIntPtr)Math.Max(1, data.Length));
        if (handle == IntPtr.Zero) throw new OutOfMemoryException();
        IntPtr memory = GlobalLock(handle);
        if (memory == IntPtr.Zero) { GlobalFree(handle); throw new OutOfMemoryException(); }
        try { if (data.Length > 0) Marshal.Copy(data, 0, memory, data.Length); }
        finally { GlobalUnlock(handle); }
        return new() { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle, pUnkForRelease = null };
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class FormatEnumerator(FORMATETC[] formats, int position = 0) : IEnumFORMATETC
    {
        private int index = position;
        public int Next(int count, FORMATETC[] values, int[]? fetched)
        {
            int copied = 0; while (copied < count && copied < values.Length && index < formats.Length) values[copied++] = formats[index++];
            if (fetched is { Length: > 0 }) fetched[0] = copied;
            return copied == count ? 0 : 1;
        }
        public int Skip(int count) { int previous = index; index = Math.Min(formats.Length, index + count); return index - previous == count ? 0 : 1; }
        public int Reset() { index = 0; return 0; }
        public void Clone(out IEnumFORMATETC clone) => clone = new FormatEnumerator(formats, index);
    }

    [ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keys);
        [PreserveSig] int GiveFeedback(uint effect);
    }
    [ComVisible(true), Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDataObjectAsyncCapability
    {
        void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool enabled);
        void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool enabled);
        void StartOperation(IBindCtx? reserved);
        void InOperation([MarshalAs(UnmanagedType.Bool)] out bool running);
        void EndOperation(int result, IBindCtx? reserved, uint effect);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DropSource(CancellationToken ct) : IDropSource
    {
        public int QueryContinueDrag(bool escape, uint keys) => escape || ct.IsCancellationRequested ? 0x00040101 : (keys & 1) == 0 ? 0x00040100 : 0;
        public int GiveFeedback(uint effect) => 0x00040102;
    }
    [DllImport("ole32.dll")] private static extern int DoDragDrop([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject data, IDropSource source, int allowed, out int effect);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes, bool create, IntPtr template, out IntPtr stream);
}
