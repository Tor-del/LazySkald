using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfPoint = System.Windows.Point;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using Brushes = System.Windows.Media.Brushes;

namespace ScreenReader.App;

public sealed class SelectionWindow : Window
{
    private readonly Canvas canvas = new();
    private readonly WpfRectangle selection = new() { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(25,0,170,255)), Visibility = Visibility.Collapsed };
    private readonly CapturedScreen screenshot;
    private WpfPoint? start;
    private Rectangle? selectedRegion;
    private readonly TaskCompletionSource<Rectangle?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Rectangle?> Completion => completion.Task;

    public SelectionWindow(CapturedScreen screenshot,IWindowFocusTarget? returnFocus=null)
    {
        this.screenshot = screenshot;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Background = Brushes.Black; Cursor = Cursors.Cross; Focusable = true;
        var image = new System.Windows.Controls.Image { Source = ToBitmapSource(screenshot.Image), Stretch = Stretch.Fill };
        var grid = new Grid(); grid.Children.Add(image); grid.Children.Add(canvas);
        canvas.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(25,0,0,0));
        canvas.Children.Add(selection);
        var hint = new TextBlock { Text=UiText.T("selection.hint"),Foreground=Brushes.White,Background=new SolidColorBrush(System.Windows.Media.Color.FromArgb(220,20,24,32)),Padding=new Thickness(14,9,14,9),FontSize=16,IsHitTestVisible=false };
        Canvas.SetLeft(hint,20); Canvas.SetTop(hint,20); canvas.Children.Add(hint);
        Content = grid;
        SourceInitialized += (_,_) => SetWindowPos(new WindowInteropHelper(this).Handle,new IntPtr(-1),screenshot.Bounds.X,screenshot.Bounds.Y,screenshot.Bounds.Width,screenshot.Bounds.Height,0x0040);
        Loaded += (_,_) => { Activate(); Focus(); };
        PreviewKeyDown += (_,e) => { if (e.Key==Key.Escape) { e.Handled=true; Close(); } };
        MouseLeftButtonDown += (_,e) => { start=e.GetPosition(canvas); selection.Visibility=Visibility.Visible; CaptureMouse(); Update(e.GetPosition(canvas)); };
        MouseMove += (_,e) => { if (start is not null) Update(e.GetPosition(canvas)); };
        MouseLeftButtonUp += (_,e) =>
        {
            if (start is not { } origin) return;
            ReleaseMouseCapture(); var end=e.GetPosition(canvas);
            var rectangle = SelectionMath.ToPixels(origin.X,origin.Y,end.X,end.Y,canvas.ActualWidth,canvas.ActualHeight,screenshot.Image.Width,screenshot.Image.Height);
            selectedRegion=rectangle.Width>=4 && rectangle.Height>=4 ? rectangle : null; Close();
        };
        Closed += async (_,_) =>
        {
            try { if (returnFocus is not null) await returnFocus.RestoreAsync(); }
            catch (Exception e) { AppLog.Write("Не удалось вернуть фокус после выделения.",e); }
            finally { completion.TrySetResult(selectedRegion); }
        };
    }
    private void Update(WpfPoint point)
    {
        if (start is not { } origin) return;
        double x=Math.Clamp(point.X,0,canvas.ActualWidth), y=Math.Clamp(point.Y,0,canvas.ActualHeight);
        Canvas.SetLeft(selection,Math.Min(x,origin.X)); Canvas.SetTop(selection,Math.Min(y,origin.Y));
        selection.Width=Math.Abs(x-origin.X); selection.Height=Math.Abs(y-origin.Y);
    }
    public static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        using var stream=new MemoryStream(); bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Bmp); stream.Position=0;
        var source = BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad); source.Freeze(); return source;
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
public static class SelectionMath
{
    public static Rectangle ToPixels(double x1,double y1,double x2,double y2,double viewWidth,double viewHeight,int pixelWidth,int pixelHeight)
    {
        if (viewWidth<=0 || viewHeight<=0) return Rectangle.Empty;
        int left=(int)Math.Floor(Math.Clamp(Math.Min(x1,x2)/viewWidth,0,1)*pixelWidth);
        int top=(int)Math.Floor(Math.Clamp(Math.Min(y1,y2)/viewHeight,0,1)*pixelHeight);
        int right=(int)Math.Ceiling(Math.Clamp(Math.Max(x1,x2)/viewWidth,0,1)*pixelWidth);
        int bottom=(int)Math.Ceiling(Math.Clamp(Math.Max(y1,y2)/viewHeight,0,1)*pixelHeight);
        return Rectangle.FromLTRB(left,top,right,bottom);
    }
}
