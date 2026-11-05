using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ScreenReader.App;

public enum AppTheme { System, Light, Dark }

public sealed class ThemeManager : IDisposable
{
    private readonly Application app;
    private ResourceDictionary? palette;
    public AppTheme Theme { get; private set; }
    public bool IsDark { get; private set; }

    public ThemeManager(Application app)
    {
        this.app=app;
        SystemEvents.UserPreferenceChanged+=SystemPreferenceChanged;
    }

    public void Apply(AppTheme theme)
    {
        Theme=Enum.IsDefined(theme) ? theme : AppTheme.System;
        IsDark=Theme==AppTheme.Dark || (Theme==AppTheme.System && SystemUsesDarkTheme());
        var next=new ResourceDictionary { Source=new Uri($"/{typeof(App).Assembly.GetName().Name};component/Themes/{(IsDark ? "Dark" : "Light")}.xaml",UriKind.Relative) };
        if (palette is not null) app.Resources.MergedDictionaries.Remove(palette);
        app.Resources.MergedDictionaries.Add(next);
        palette=next;
        foreach (Window window in app.Windows) ApplyTitleBar(window);
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) is int value && value==0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    private void SystemPreferenceChanged(object sender,UserPreferenceChangedEventArgs e)
    {
        if (app.Dispatcher.HasShutdownStarted) return;
        app.Dispatcher.BeginInvoke(new Action(()=> { if (Theme==AppTheme.System) Apply(Theme); }));
    }

    public void ApplyTitleBar(Window window)
    {
        var handle=new WindowInteropHelper(window).Handle;
        if (handle==IntPtr.Zero) return;
        int dark=IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle,20,ref dark,sizeof(int));
    }

    public void Dispose()=>SystemEvents.UserPreferenceChanged-=SystemPreferenceChanged;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
}
