using System.Drawing.Imaging;

namespace RemoteDebugger;

// Retain at most three spare frames; a displayed bitmap is never reused until released.
internal sealed class BitmapPool : IDisposable
{
    private readonly object gate = new();
    private readonly Stack<Bitmap> available = new();
    private bool disposed;
    public Bitmap Rent(int width, int height)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            while (available.TryPop(out var bitmap))
            {
                if (bitmap.Width == width && bitmap.Height == height) return bitmap;
                bitmap.Dispose();
            }
            return new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }
    }
    public void Return(Bitmap bitmap)
    {
        lock (gate)
        { if (!disposed && available.Count < 3) available.Push(bitmap); else bitmap.Dispose(); }
    }
    public void Dispose()
    {
        lock (gate) { disposed = true; while (available.TryPop(out var bitmap)) bitmap.Dispose(); }
    }
}

internal sealed class BitmapLease(Bitmap image, BitmapPool? pool = null) : IDisposable
{
    private Bitmap? image = image;
    public Bitmap Image => image ?? throw new ObjectDisposedException(nameof(BitmapLease));
    public void Dispose()
    {
        var released = Interlocked.Exchange(ref image, null);
        if (released == null) return;
        if (pool == null) released.Dispose(); else pool.Return(released);
    }
}
