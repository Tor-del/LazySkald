using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScreenReader.App;

// Future game renderers consume the same ReadingJob/PlaybackSession and commands.
public interface IReadingOverlay : IDisposable
{
    void ApplySettings(OverlaySettings settings);
    void SetInteractionHeld(bool held);
    OverlaySettings? ConfigureLayout(Window owner,OverlaySettings settings);
}

public partial class ReadingOverlayWindow : Window,IReadingOverlay
{
    private readonly ReadingController reader;
    private readonly DispatcherTimer timer;
    private OverlaySettings options=new();
    private bool configuration,configuring,interactive,interactionHeld,disposed;
    private long? displayedJob;
    private PlaybackSession? playback;
    private IWindowFocusTarget? previousFocus;
    private HwndSource? source;
    private System.Drawing.Rectangle? captureMonitor;
    private bool positioning;
    public ReadingOverlayWindow(ReadingController reader)
    {
        this.reader=reader;
        InitializeComponent();
        timer=new DispatcherTimer(TimeSpan.FromMilliseconds(100),DispatcherPriority.Background,(_,_)=>Refresh(),Dispatcher);
        timer.Stop();
        SourceInitialized+=(_,_)=>
        {
            source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(WindowMessage);
            UpdateActivation();
        };
        Deactivated+=(_,_)=> { interactionHeld=false; if (interactive) ExitInteraction(false); };
        SizeChanged+=(_,_)=> { if (IsVisible && !configuration) ClampToMonitor(); };
    }
    public void ApplySettings(OverlaySettings settings)
    {
        options=SettingsStore.NormalizeOverlay(settings);
        if (!interactive) Width=options.Width;
        if (options.Enabled) timer.Start(); else timer.Stop();
        Refresh();
    }
    private void Refresh()
    {
        if (configuration || disposed) return;
        var job=options.Enabled && !configuring ? reader.Queue.Snapshot.FirstOrDefault() : null;
        if (job is null)
        {
            displayedJob=null; playback=null;
            ExitInteraction(true);
            Hide(); return;
        }
        if (displayedJob!=job.Id) TranslationPanel.ScrollToTop();
        displayedJob=job.Id; playback=job.Playback;
        captureMonitor=job.CaptureMonitor;
        var progress=playback?.Progress;
        StageText.Text=progress?.Started==true ? UiText.T(progress.Paused ? "overlay.paused" : "overlay.playing") : UiText.T(job.Stage);
        bool playing=progress?.Started==true;
        PlaybackPanel.Visibility=PauseButton.Visibility=playing ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content=progress?.Paused==true ? "▶" : "Ⅱ";
        PauseButton.ToolTip=UiText.T(progress?.Paused==true ? "overlay.resume" : "overlay.pause");
        if (progress is { Started:true })
        {
            PlaybackBar.IsIndeterminate=progress.Duration is null;
            PlaybackBar.Value=progress.Duration is { TotalSeconds:>0 } duration ? Math.Clamp(progress.Position.TotalSeconds/duration.TotalSeconds,0,1) : 0;
            TimeText.Text=FormatTime(progress.Position)+(progress.Duration is { } total ? " / "+FormatTime(total) : "");
        }
        TranslationText.Text=options.ShowTranslation ? job.TranslationText ?? "" : "";
        TranslationPanel.Visibility=string.IsNullOrWhiteSpace(TranslationText.Text) ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.ToolTip=UiText.T(job.AwaitingClose ? "overlay.finish" : "overlay.close");
        UpdateActivation();
        if (!IsVisible) Show();
        ClampToMonitor();
        if (interactionHeld && !interactive) EnterInteraction();
    }
    private static string FormatTime(TimeSpan value)=>$"{(int)value.TotalMinutes}:{value.Seconds:00}";
    public void SetInteractionHeld(bool held)
    {
        interactionHeld=held;
        if (!held) { ExitInteraction(true); return; }
        Refresh();
    }
    private void EnterInteraction()
    {
        if (!IsVisible || configuring || interactive) return;
        previousFocus=new ForegroundWindowService().Capture();
        interactive=true; UpdateActivation();
        InteractionHint.Visibility=Visibility.Visible;
        Frame.BorderBrush=new SolidColorBrush(Color.FromRgb(113,181,255));
        Activate(); CloseButton.Focus();
    }
    private async void ExitInteraction(bool restore)
    {
        if (!interactive) return;
        bool wasActive=IsActive;
        interactive=false; UpdateActivation();
        InteractionHint.Visibility=Visibility.Collapsed;
        Frame.BorderBrush=new SolidColorBrush(Color.FromRgb(100,116,139));
        var target=previousFocus; previousFocus=null;
        if (restore && wasActive && target is not null) await target.RestoreAsync();
    }
    public OverlaySettings? ConfigureLayout(Window owner,OverlaySettings settings)
    {
        configuring=true; Refresh();
        using var preview=new ReadingOverlayWindow(reader) { configuration=true,Owner=owner,ShowActivated=true };
        var normalized=SettingsStore.NormalizeOverlay(settings);
        preview.options=normalized;
        preview.captureMonitor=reader.Queue.Snapshot.FirstOrDefault()?.CaptureMonitor ??
            System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle).Bounds;
        preview.Width=normalized.Width;
        preview.StageText.Text=UiText.T("overlay.preview");
        preview.TranslationText.Text=UiText.T("overlay.sample");
        preview.TranslationPanel.Visibility=Visibility.Visible;
        preview.ConfigurationPanel.Visibility=Visibility.Visible;
        preview.Loaded+=(_,_)=>preview.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,preview.ClampToMonitor);
        var result=normalized;
        preview.Closing+=(_,_)=>
        {
            if (preview.source is null || !GetWindowRect(preview.source.Handle,out var rect)) return;
            var bounds=System.Windows.Forms.Screen.FromHandle(preview.source.Handle).Bounds;
            result=normalized with
            {
                Width=preview.Width,
                CenterX=((rect.Left+rect.Right)/2.0-bounds.Left)/bounds.Width,
                CenterY=((rect.Top+rect.Bottom)/2.0-bounds.Top)/bounds.Height
            };
        };
        try
        {
            return preview.ShowDialog()==true ? result : null;
        }
        finally { configuring=false; Refresh(); }
    }
    private void CloseJob(object sender,RoutedEventArgs e)
    {
        if (configuration) { DialogResult=false; return; }
        if (displayedJob is { } id) reader.Queue.Close(id);
    }
    private void TogglePause(object sender,RoutedEventArgs e)=>playback?.TogglePause();
    private void DragHeader(object sender,MouseButtonEventArgs e)
    {
        if (configuration && e.OriginalSource is not System.Windows.Controls.Button &&
            !IsInsideButton(e.OriginalSource as DependencyObject)) { DragMove(); e.Handled=true; }
    }
    private static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Button) return true;
            element=VisualTreeHelper.GetParent(element);
        }
        return false;
    }
    private void ResizeWidget(object sender,DragDeltaEventArgs e)
    { Width=Math.Clamp(Width+e.HorizontalChange,MinWidth,MaxWidth); e.Handled=true; }
    private void SaveLayout(object sender,RoutedEventArgs e)=>DialogResult=true;
    private void CancelLayout(object sender,RoutedEventArgs e)=>DialogResult=false;
    private void OverlayKeyDown(object sender,KeyEventArgs e)
    {
        if (e.Key!=Key.Escape) return;
        e.Handled=true;
        interactionHeld=false;
        if (configuration) DialogResult=false; else ExitInteraction(true);
    }
    private IntPtr WindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if (message==0x21 && !interactive && !configuration) { handled=true; return new IntPtr(3); } // MA_NOACTIVATE
        if (message is 0x7E or 0x2E0 && IsVisible && !configuration) Dispatcher.BeginInvoke(ClampToMonitor); // display or DPI changed
        return IntPtr.Zero;
    }
    private void UpdateActivation()
    {
        if (source is null) return;
        int style=GetWindowLong(source.Handle,-20)|0x80; // WS_EX_TOOLWINDOW
        style=configuration || interactive ? style & ~0x08000000 : style | 0x08000000;
        // A visible, unrestricted cursor can use buttons without activating the window.
        // Captured/hidden game cursors pass through until the interaction key is held.
        bool passThrough=!configuration && !interactive && !HasFreeCursor();
        SetWindowLong(source.Handle,-20,passThrough ? style | 0x20 : style & ~0x20); // WS_EX_TRANSPARENT
    }
    private static bool HasFreeCursor()
    {
        var cursor=new CursorInfo { Size=Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || (cursor.Flags&1)==0) return false;
        if (!GetClipCursor(out var clip)) return false;
        var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;
        return clip.Left<=desktop.Left && clip.Top<=desktop.Top && clip.Right>=desktop.Right && clip.Bottom>=desktop.Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo
    { public int Size; public uint Flags; public IntPtr Cursor; public int X,Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out NativeRect rect);
    private void ClampToMonitor()
    {
        if (positioning || source is null || !GetWindowRect(source.Handle,out var rect)) return;
        var monitor=captureMonitor is { } bounds
            ? System.Windows.Forms.Screen.FromRectangle(bounds)
            : System.Windows.Forms.Screen.FromHandle(source.Handle);
        var position=OverlayPlacement.Calculate(monitor.Bounds,monitor.WorkingArea,
            rect.Right-rect.Left,rect.Bottom-rect.Top,options.CenterX,options.CenterY);
        if (rect.Left==position.X && rect.Top==position.Y) return;
        positioning=true;
        try { SetWindowPos(source.Handle,IntPtr.Zero,position.X,position.Y,0,0,0x15); } // no size, z-order, activation
        finally { positioning=false; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed=true; timer.Stop(); ExitInteraction(true);
        source?.RemoveHook(WindowMessage); Close();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd,int index,int value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
}
