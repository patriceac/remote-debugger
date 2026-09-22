using System.Drawing.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteDebugger;

// Desktop Duplication reports unchanged desktops without copying or hashing pixels.
// Unsupported/rotated outputs keep the existing GDI capture path.
internal sealed class DxgiCapture : IDisposable
{
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private IDXGIOutputDuplication? duplication;
    private ID3D11Texture2D? staging;
    private bool hasFrame;
    private long version;
    private readonly string identity = Guid.NewGuid().ToString("N");

    private DxgiCapture(IDXGIAdapter adapter, IDXGIOutput1 output, Size size)
    {
        try
        {
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out device, out context).CheckError();
            duplication = output.DuplicateOutput(device);
            staging = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)size.Width, Height = (uint)size.Height, Format = Format.B8G8R8A8_UNorm,
                ArraySize = 1, MipLevels = 1, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
            });
        }
        catch { Dispose(); throw; }
    }

    public static DxgiCapture? Create(string deviceName, Size size)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        using (adapter)
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            using (output)
            {
                var description = output.Description;
                if (description.DeviceName != deviceName) continue;
                if (description.Rotation != ModeRotation.Identity) return null;
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                return new DxgiCapture(adapter, output1, size);
            }
        return null;
    }

    public unsafe string Capture(Bitmap target)
    {
        var result = duplication!.AcquireNextFrame(hasFrame ? 0u : 100u, out var info, out var resource);
        if (result.Code == unchecked((int)0x887A0027) && hasFrame) return identity + ":" + version; // WAIT_TIMEOUT
        result.CheckError();
        try
        {
            using (resource)
            {
                if (hasFrame && info.LastPresentTime == 0) return identity + ":" + version;
                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                var description = texture.Description;
                if (description.Width != target.Width || description.Height != target.Height || description.Format != Format.B8G8R8A8_UNorm)
                    throw new InvalidOperationException("Desktop duplication geometry changed.");
                context!.CopyResource(staging!, texture);
                context.Map(staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
                try
                {
                    var pixels = target.LockBits(new Rectangle(Point.Empty, target.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        for (int y = 0; y < target.Height; y++)
                            Buffer.MemoryCopy((byte*)mapped.DataPointer + y * mapped.RowPitch,
                                (byte*)pixels.Scan0 + y * pixels.Stride, Math.Abs(pixels.Stride), target.Width * 4);
                    }
                    finally { target.UnlockBits(pixels); }
                }
                finally { context.Unmap(staging!, 0); }
                hasFrame = true; version++;
                return identity + ":" + version;
            }
        }
        finally { duplication.ReleaseFrame().CheckError(); }
    }

    public void Dispose()
    {
        staging?.Dispose(); staging = null; duplication?.Dispose(); duplication = null;
        context?.Dispose(); context = null; device?.Dispose(); device = null;
    }
}
