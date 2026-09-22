using System.Drawing;
using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class BitmapPoolTests
{
    [Fact]
    public void LeaseReturnsOnceAndPoolRetainsAtMostThreeSpareBitmaps()
    {
        using (var pool = new BitmapPool())
        {
            var leasedBitmap = new Bitmap(8, 8);
            using (var lease = new BitmapLease(leasedBitmap, pool))
            {
                Assert.Same(leasedBitmap, lease.Image);
                lease.Dispose();
                lease.Dispose();
            }

            Bitmap reused = pool.Rent(8, 8);
            Assert.Same(leasedBitmap, reused);
            pool.Return(reused);
        }

        using (var pool = new BitmapPool())
        {
            Bitmap[] returned = Enumerable.Range(0, 4).Select(_ => new Bitmap(4, 4)).ToArray();
            foreach (Bitmap bitmap in returned) pool.Return(bitmap);
            Bitmap[] rented = Enumerable.Range(0, 4).Select(_ => pool.Rent(4, 4)).ToArray();

            Assert.DoesNotContain(returned[3], rented);
            Assert.Equal(4, rented.Distinct().Count());
            foreach (Bitmap bitmap in rented) bitmap.Dispose();
        }
    }
}
