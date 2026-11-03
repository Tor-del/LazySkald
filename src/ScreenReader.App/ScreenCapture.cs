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
                        // The first notification can contain only cursor metadata. Its texture
                        // is not a desktop image (LastPresentTime == 0) and can be all zeroes.
                        var acquisition=System.Diagnostics.Stopwatch.StartNew();
                        IDXGIResource resource;
                        while (true)
                        {
                            uint remaining=(uint)Math.Max(1,800-acquisition.ElapsedMilliseconds);
                            duplication.AcquireNextFrame(remaining,out var frameInfo,out resource).CheckError();
                            if (frameInfo.LastPresentTime!=0) break;
                            resource.Dispose(); duplication.ReleaseFrame();
                            if (acquisition.ElapsedMilliseconds>=800) throw new TimeoutException(UiText.T("capture.noFrame"));
                        }
                        try
                        {
                            using (resource)
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
                                    switch (desc.Rotation)
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
                        finally { duplication.ReleaseFrame(); }
                    }
                }
            }
        }
        throw new InvalidOperationException(UiText.T("capture.noMonitor"));
    }
}
