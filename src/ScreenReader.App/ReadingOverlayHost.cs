using System.Windows;
using System.Windows.Threading;
using Windows.Management.Deployment;

namespace ScreenReader.App;

// The desktop window also keeps controls available while the widget is closed or uninstalled.
public sealed class ReadingOverlayHost : IReadingOverlay
{
    private readonly ReadingController reader;
    private readonly ReadingOverlayWindow desktop;
    private readonly DispatcherTimer timer;
    private OverlaySettings options=new();
    private GameBarBridge? bridge;
    private DateTimeOffset nextDiscovery;
    private bool held,disposed;
    public ReadingOverlayHost(ReadingController reader)
    {
        this.reader=reader;
        desktop=new(reader);
        timer=new DispatcherTimer(TimeSpan.FromMilliseconds(250),DispatcherPriority.Background,(_,_)=>Refresh(),desktop.Dispatcher);
        timer.Stop();
    }
    public void ApplySettings(OverlaySettings settings)
    {
        options=SettingsStore.NormalizeOverlay(settings);
        bool gameBar=options.Enabled && options.Renderer==OverlayRenderer.XboxGameBar;
        if (gameBar) timer.Start();
        else { timer.Stop(); bridge?.Dispose(); bridge=null; }
        Refresh();
    }
    private void Refresh()
    {
        if (disposed) return;
        if (options.Enabled && options.Renderer==OverlayRenderer.XboxGameBar)
        {
            var now=DateTimeOffset.UtcNow;
            try
            {
                if (bridge is null && now>=nextDiscovery)
                {
                    nextDiscovery=now.AddSeconds(5);
                    var package=new PackageManager().FindPackagesForUser("").FirstOrDefault(p=>p.Id.Name==GameBarBridge.PackageName);
                    if (package is not null)
                        bridge=new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Packages",package.Id.FamilyName,"LocalState","Overlay"));
                }
                var job=reader.Queue.Snapshot.FirstOrDefault();
                var progress=job?.Playback?.Progress;
                bridge?.Publish(new(1,"",0,job?.Id,
                    job is null ? UiText.T("overlay.gameBarIdle") : UiText.T(progress?.Started==true ? progress.Paused ? "overlay.paused" : "overlay.playing" : job.Stage),
                    options.ShowTranslation ? job?.TranslationText ?? "" : "",progress?.Started==true,progress?.Paused==true,
                    progress?.Position.TotalSeconds ?? 0,progress?.Duration?.TotalSeconds,
                    UiText.T(progress?.Paused==true ? "overlay.resume" : "overlay.pause"),UiText.T(job?.AwaitingClose==true ? "overlay.finish" : "overlay.close")),reader.Queue,now.ToUnixTimeMilliseconds());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                AppLog.Write("Game Bar недоступен; используется оконный оверлей",e);
                bridge?.Dispose(); bridge=null; nextDiscovery=now.AddSeconds(5);
            }
        }
        bool useDesktop=!options.Enabled || options.Renderer==OverlayRenderer.Desktop || bridge?.Connected!=true;
        desktop.ApplySettings(options with { Enabled=options.Enabled && useDesktop });
        desktop.SetInteractionHeld(held && useDesktop);
    }
    public void SetInteractionHeld(bool value)
    {
        held=value;
        desktop.SetInteractionHeld(value && (options.Renderer==OverlayRenderer.Desktop || bridge?.Connected!=true));
    }
    public OverlaySettings? ConfigureLayout(Window owner,OverlaySettings settings)=>desktop.ConfigureLayout(owner,settings);
    public void Dispose()
    {
        if (disposed) return;
        disposed=true; timer.Stop(); bridge?.Dispose(); desktop.Dispose();
    }
}
