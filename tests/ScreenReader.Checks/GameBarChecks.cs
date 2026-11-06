using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ScreenReader.App;

internal static partial class Program
{
    private static void RunGameBarUi()
    {
        var app=new App(); app.InitializeComponent();
        var window=new MainWindow(app);
        CheckGameBarControls(window);
        window.Close(); app.Shutdown();
    }
    private static void CheckGameBarControls(MainWindow window)
    {
        var selector=(System.Windows.Controls.ComboBox)window.FindName("OverlayRendererBox");
        var hint=(System.Windows.FrameworkElement)window.FindName("GameBarPanel");
        var desktop=(System.Windows.FrameworkElement)window.FindName("DesktopOverlayHint");
        Assert((string)selector.SelectedValue=="Desktop" && hint.Visibility==System.Windows.Visibility.Collapsed,
            "Existing UI defaults to the desktop renderer");
        selector.SelectedValue="XboxGameBar";
        Assert(hint.Visibility==System.Windows.Visibility.Visible && desktop.Visibility==System.Windows.Visibility.Collapsed,
            "Selecting Xbox Game Bar exposes its setup instructions");
        var settings=(AppSettings)typeof(MainWindow).GetMethod("ReadControls",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(window,null)!;
        Assert(settings.Overlay.Renderer==OverlayRenderer.XboxGameBar,"Saving controls includes the selected Game Bar renderer");
        var tabs=(System.Windows.Controls.TabControl)window.FindName("MainTabs");
        int previous=tabs.SelectedIndex;
        var content=(System.Windows.FrameworkElement)window.Content;
        content.Measure(new System.Windows.Size(window.Width,window.Height));
        content.Arrange(new System.Windows.Rect(0,0,window.Width,window.Height)); content.UpdateLayout();
        tabs.SelectedIndex=8;
        content.Measure(new System.Windows.Size(window.Width,window.Height));
        content.Arrange(new System.Windows.Rect(0,0,window.Width,window.Height));
        content.UpdateLayout();
        Assert(hint.ActualWidth>0 && hint.ActualHeight>0,"Game Bar instructions are laid out in the selected tab");
        var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.Width,(int)window.Height,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(content);
        Directory.CreateDirectory("artifacts");
        using (var output=File.Create("artifacts/settings-gamebar.png"))
        {
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image)); encoder.Save(output);
        }
        tabs.SelectedIndex=previous;
        UiText.SetLanguage("en");
        Assert((string)selector.SelectedValue=="XboxGameBar","Changing language preserves the selected overlay renderer");
        UiText.SetLanguage("ru");
        selector.SelectedValue="Desktop";
        Assert(hint.Visibility==System.Windows.Visibility.Collapsed && desktop.Visibility==System.Windows.Visibility.Visible,
            "Switching back restores desktop compatibility instructions");
    }
    private static async Task RunGameBarChecksAsync()
    {
        Assert(JsonSerializer.Deserialize<AppSettings>("{}")!.Overlay.Renderer==OverlayRenderer.Desktop,
            "Old settings keep the desktop overlay");
        var options=new OverlaySettings { Enabled=true,Renderer=OverlayRenderer.XboxGameBar };
        Assert(JsonSerializer.Deserialize<OverlaySettings>(JsonSerializer.Serialize(options))==options,
            "Xbox Game Bar selection survives serialization");
        Assert(SettingsStore.NormalizeOverlay(options with { Renderer=(OverlayRenderer)999 }).Renderer==OverlayRenderer.Desktop,
            "An unknown renderer falls back to the desktop overlay");

        string directory=Path.Combine(Path.GetTempPath(),"LazySkald-GameBar-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var queue=new ReadingQueue();
            var playback=new PlaybackSession(); bool paused=false;
            playback.Attach(value=>paused=value,()=>TimeSpan.FromSeconds(1)); playback.Start();
            var first=queue.EnqueueAsync(token=> { queue.SetPlayback(token,playback); return Task.FromResult(0); },(_,token)=>queue.WaitForCloseAsync(token));
            var second=queue.EnqueueAsync(_=>Task.FromResult(0),(_,token)=>queue.WaitForCloseAsync(token));
            long id=queue.Snapshot[0].Id;
            long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var bridge=new GameBarBridge(directory);
            var state=new GameBarState(1,"",0,id,"stage","Перевод 😀",false,false,0,null,"Pause","Close");
            void Publish()=>bridge.Publish(state,queue,now);
            void Heartbeat(int version,string session,long stamp)=>File.WriteAllText(Path.Combine(directory,"heartbeat.json"),
                JsonSerializer.Serialize(new GameBarHeartbeat(version,session,stamp)));
            void Command(string action,string session,long jobId,int version=1)=>File.WriteAllText(Path.Combine(directory,"command-"+Guid.NewGuid().ToString("N")+".json"),
                JsonSerializer.Serialize(new GameBarCommand(version,session,jobId,action)));
            Publish();
            Assert(!bridge.Connected,"Missing widget heartbeat keeps desktop fallback active");
            var exported=JsonSerializer.Deserialize<GameBarState>(File.ReadAllText(Path.Combine(directory,"state.json")))!;
            Assert(exported.Session==bridge.Session && exported.UpdatedAt==now && exported.Translation=="Перевод 😀",
                "Game Bar state carries a fresh session and Unicode translation");
            Heartbeat(1,bridge.Session,now-4000); Publish();
            Assert(!bridge.Connected,"A closed or stalled widget loses its lease");
            Heartbeat(1,"previous-session",now); Publish();
            Assert(!bridge.Connected,"Previous app instances cannot keep the widget lease alive");
            Heartbeat(2,bridge.Session,now); Publish();
            Assert(!bridge.Connected,"An incompatible widget protocol uses desktop fallback");
            File.WriteAllText(Path.Combine(directory,"heartbeat.json"),"{"); Publish();
            Assert(!bridge.Connected,"Partial heartbeat JSON cannot interrupt reading");
            Heartbeat(1,bridge.Session,now); Publish();
            Assert(bridge.Connected,"A current widget heartbeat activates Game Bar");
            Command("close","previous-session",id); Command("close",bridge.Session,id,2); Command("invalid",bridge.Session,id); Publish();
            Assert(!first.IsCompleted && queue.Snapshot.Count==2,"Wrong session, version and unknown commands cannot cancel a task");
            Command("pause",bridge.Session,id); Publish(); Publish();
            Assert(paused,"A pause command is consumed exactly once");
            Command("pause",bridge.Session,id); Publish();
            Assert(!paused,"A second pause command resumes the same playback session");
            Command("close",bridge.Session,id); Publish();
            await first.WaitAsync(TimeSpan.FromSeconds(3));
            Command("close",bridge.Session,id); Publish();
            Assert(!second.IsCompleted && queue.Snapshot.Single().Id!=id,"A late close cannot cancel the next queued task");
            Heartbeat(1,bridge.Session,now-4000);
            Command("close",bridge.Session,queue.Snapshot.Single().Id); Publish();
            Assert(!second.IsCompleted,"Commands from a disconnected widget are discarded");
            bridge.Dispose();
            Assert(!File.Exists(Path.Combine(directory,"state.json")),"Disabling Game Bar removes exported translation state");
            queue.CancelAll(); await second.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { Directory.Delete(directory,true); }
    }
}
