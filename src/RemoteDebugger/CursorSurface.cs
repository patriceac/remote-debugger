using System.Drawing.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace RemoteDebugger;

// Keep the existing premultiplied cursor pixels, including fading labels, on a
// composition surface: legacy UpdateLayeredWindow defeats capture exclusion on Windows 10.
internal sealed class CursorSurface : IDisposable
{
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private IDCompositionDevice? composition;
    private IDCompositionTarget? target;
    private IDCompositionVisual? visual;
    private IDXGISwapChain1? swapChain;
    private Size size;

    internal CursorSurface(IntPtr window)
    {
        try
        {
            var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out device, out context);
            if (result.Failure)
                D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0], out device, out context).CheckError();
            using var dxgi = device!.QueryInterface<IDXGIDevice>();
            composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgi);
            composition.CreateTargetForHwnd(window, true, out target).CheckError();
            visual = composition.CreateVisual();
            target.SetRoot(visual).CheckError();
        }
        catch { Dispose(); throw; }
    }

    internal void Present(Bitmap bitmap)
    {
        if (swapChain == null)
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
            swapChain = factory.CreateSwapChainForComposition(device!, new SwapChainDescription1
            {
                Width = (uint)bitmap.Width, Height = (uint)bitmap.Height, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
                Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Premultiplied
            });
            visual!.SetContent(swapChain).CheckError();
            composition!.Commit().CheckError();
            size = bitmap.Size;
        }
        else if (size != bitmap.Size)
        {
            swapChain.ResizeBuffers(2, (uint)bitmap.Width, (uint)bitmap.Height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
            size = bitmap.Size;
        }
        using var buffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        var pixels = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try { context!.UpdateSubresource(buffer, 0, null, pixels.Scan0, (uint)pixels.Stride, 0); }
        finally { bitmap.UnlockBits(pixels); }
        swapChain.Present(0, PresentFlags.None).CheckError();
    }

    public void Dispose()
    {
        swapChain?.Dispose(); visual?.Dispose(); target?.Dispose(); composition?.Dispose();
        context?.Dispose(); device?.Dispose();
    }
}
