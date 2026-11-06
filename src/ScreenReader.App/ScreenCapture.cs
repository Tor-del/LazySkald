using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ScreenReader.App;

public sealed record CapturedScreen(Bitmap Image, Rectangle Bounds, string Backend) : IDisposable
{
    public void Dispose() => Image.Dispose();
}
public interface IScreenCapture
{
    CapturedScreen Capture(CaptureMode mode);
    CapturedScreen CaptureMonitor(Rectangle bounds,CaptureMode mode);
}
public sealed class ScreenCapture : IScreenCapture
{
    public CapturedScreen Capture(CaptureMode mode)
    {
        var screen = Screen.FromPoint(Cursor.Position);
        return CaptureMonitor(screen.Bounds,mode);
    }
    public CapturedScreen CaptureMonitor(Rectangle bounds,CaptureMode mode)
    {
        if (!Screen.AllScreens.Any(screen=>screen.Bounds==bounds))
            throw new InvalidOperationException(UiText.T("capture.noMonitor"));
        if (mode != CaptureMode.Gdi)
        {
            try { return new(CaptureDxgi(bounds),bounds,"DXGI Desktop Duplication"); }
            catch (Exception e) when (mode == CaptureMode.Auto) { AppLog.Write("DXGI недоступен; переход на GDI.",e); }
        }
        var bitmap = new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy);
            return new(bitmap,bounds,"GDI");
        }
        catch { bitmap.Dispose(); throw; }
    }
    private static unsafe Bitmap CaptureDxgi(Rectangle bounds)
    {
        long requestedAt=System.Diagnostics.Stopwatch.GetTimestamp();
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();
        for (uint ai = 0; factory.EnumAdapters1(ai,out var adapter).Success; ai++)
        {
            using (adapter)
            for (uint oi = 0; adapter.EnumOutputs(oi,out var output).Success; oi++)
            {
                using (output)
                {
                    var desc = output.Description;
                    if (!desc.AttachedToDesktop || desc.DesktopCoordinates.Left != bounds.Left || desc.DesktopCoordinates.Top != bounds.Top) continue;
                    D3D11CreateDevice(adapter,DriverType.Unknown,DeviceCreationFlags.BgraSupport,[FeatureLevel.Level_11_0],out ID3D11Device device,out ID3D11DeviceContext context).CheckError();
                    using (device)
                    using (context)
                    using (var output1 = output.QueryInterface<IDXGIOutput1>())
                    using (var duplication = output1.DuplicateOutput(device))
                    {
                        // A new duplication can initially return an older desktop image.
                        // Retain it for static desktops, but wait briefly for a presentation
                        // at or after this request. LastPresentTime uses the QPC clock.
                        var acquisition=System.Diagnostics.Stopwatch.StartNew();
                        Bitmap? latest=null;
                        try
                        {
                            while (acquisition.ElapsedMilliseconds<800)
                            {
                                uint remaining=(uint)Math.Max(1,800-acquisition.ElapsedMilliseconds);
                                if (latest is not null) remaining=Math.Min(remaining,100u);
                                var result=duplication.AcquireNextFrame(remaining,out var frameInfo,out var resource);
                                if (result.Code==unchecked((int)0x887A0027) && latest is not null) break; // DXGI_ERROR_WAIT_TIMEOUT
                                result.CheckError();
                                try
                                {
                                    using (resource)
                                    {
                                        // Pointer-only notifications have no desktop image.
                                        if (frameInfo.LastPresentTime==0) continue;
                                        var next=ReadDesktopFrame(device,context,resource,desc.Rotation,bounds);
                                        latest?.Dispose(); latest=next;
                                    }
                                }
                                finally { duplication.ReleaseFrame(); }
                                if (frameInfo.LastPresentTime>=requestedAt) break;
                            }
                            if (latest is null) throw new TimeoutException(UiText.T("capture.noFrame"));
                            var captured=latest; latest=null;
                            return captured;
                        }
                        finally { latest?.Dispose(); }
                    }
                }
            }
        }
        throw new InvalidOperationException(UiText.T("capture.noMonitor"));
    }
    private static unsafe Bitmap ReadDesktopFrame(ID3D11Device device,ID3D11DeviceContext context,
        IDXGIResource resource,ModeRotation rotation,Rectangle bounds)
    {
        using (var texture = resource.QueryInterface<ID3D11Texture2D>())
        {
            var textureDesc = texture.Description;
            textureDesc.Usage = ResourceUsage.Staging;
            textureDesc.BindFlags = BindFlags.None;
            textureDesc.CPUAccessFlags = CpuAccessFlags.Read;
            textureDesc.MiscFlags = ResourceOptionFlags.None;
            using var staging = device.CreateTexture2D(textureDesc);
            context.CopyResource(staging,texture);
            var mapped = context.Map(staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
            var bitmap = new Bitmap((int)textureDesc.Width,(int)textureDesc.Height,PixelFormat.Format32bppArgb);
            try
            {
                var locked = bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
                try
                {
                    for (int y=0; y<bitmap.Height; y++)
                        Buffer.MemoryCopy((byte*)mapped.DataPointer+y*mapped.RowPitch,(byte*)locked.Scan0+y*locked.Stride,locked.Stride,bitmap.Width*4);
                }
                finally { bitmap.UnlockBits(locked); }
                switch (rotation)
                {
                    case ModeRotation.Rotate90: bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                    case ModeRotation.Rotate180: bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                    case ModeRotation.Rotate270: bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                }
                if (bitmap.Size != bounds.Size) throw new InvalidOperationException(UiText.T("capture.size")+$" ({bitmap.Width}×{bitmap.Height}; {bounds.Width}×{bounds.Height})");
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
            finally { context.Unmap(staging,0); }
        }
    }
}
