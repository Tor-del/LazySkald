using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScreenReader.App;
using ScreenReader.Speech;

internal static partial class Program
{
    private static async Task RunReadingOverlayChecksAsync()
    {
        var defaults=JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert(!defaults.Overlay.Enabled && !defaults.Overlay.ShowTranslation && defaults.OverlayInteractionHotkey==new Hotkey(0,0x12),
            "Existing settings keep overlay disabled and default interaction to Alt");
        HotkeyService.Validate(defaults);
        Throws<ArgumentException>(()=>HotkeyService.Validate(new Hotkey(0,0x12)),"Standalone modifiers remain invalid for ordinary shortcuts");
        var alt=new HotkeyChordRecognizer(new(0,0x12),hold:true);
        Assert(alt.Process(0xA4,true).Trigger && alt.Held,"Left Alt starts overlay interaction");
        Assert(!alt.Process(0xA4,true).Trigger && alt.Held,"Alt auto-repeat never toggles interaction");
        alt.Synchronize([]);
        alt.Process(0xA4,false);
        Assert(!alt.Held,"Alt release ends interaction even after hook refresh");
        alt.Process(0xA5,true); alt.Process(0xA4,true); alt.Process(0xA5,false);
        Assert(alt.Held,"Either Alt keeps interaction active when both are pressed");
        alt.Process(0xA4,false);
        Assert(!alt.Held,"Releasing the last Alt ends interaction");
        var chord=new HotkeyChordRecognizer(new(2,0x4F),hold:true);
        chord.Process(0xA2,true); chord.Process(0x4F,true); chord.Process(0xA2,false);
        Assert(!chord.Held,"Releasing a required modifier ends a held chord");
        var initial=new HotkeyChordRecognizer(new(0,0x12),[0xA4],hold:true);
        Assert(!initial.Process(0x52,true).Trigger && !initial.Held,"Editing or reconfiguration cannot activate a previously held Alt with another key");
        var options=new OverlaySettings { Enabled=true,ShowTranslation=true,Left=-900,Top=120,Width=470,CenterX=0.3,CenterY=0.6 };
        var settings=new AppSettings { Overlay=options,OverlayInteractionHotkey=new(3,0x4F),SpeechEnabled=false,Translation=new() { Enabled=true } };
        var restored=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert(restored.Overlay==options && restored.OverlayInteractionHotkey==settings.OverlayInteractionHotkey,
            "Overlay position, width, translation and hotkey survive serialization");
        HotkeyService.Validate(settings);
        Assert(HotkeyService.Bindings(settings).Any(binding=>binding.Action==HotkeyAction.OverlayInteraction),"Overlay interaction participates in hotkey registration");
        Throws<ArgumentException>(()=>HotkeyService.Validate(settings with { OverlayInteractionHotkey=settings.CaptureHotkey }),"Overlay interaction cannot duplicate another shortcut");
        Assert(SettingsStore.ClearReservedHotkeys(settings with { OverlayInteractionHotkey=new(0,0x1B) }).OverlayInteractionHotkey is null,
            "Reserved overlay shortcut is removed during migration");
        Assert(SettingsStore.NormalizeOverlay(options with { Left=double.NaN,Top=double.PositiveInfinity,Width=-1 }) is { Left:40,Top:40,Width:260 },
            "Invalid overlay geometry is normalized");
        Assert(defaults.Overlay is { CenterX:0.5,CenterY:0.75 },"Default overlay centre is halfway between screen centre and bottom");
        Assert(SettingsStore.NormalizeOverlay(new() { CenterX=double.NaN,CenterY=double.PositiveInfinity }) is { CenterX:0.5,CenterY:0.75 },
            "Invalid relative positions return to the new default");
        foreach (var screen in new[] { new System.Drawing.Rectangle(0,0,1920,1080),new System.Drawing.Rectangle(-2560,0,2560,1440),new System.Drawing.Rectangle(300,-2160,3840,2160) })
        {
            var position=OverlayPlacement.Calculate(screen,screen,360,160);
            Assert(Math.Abs(position.X+180-(screen.Left+screen.Width*0.5))<=1 &&
                Math.Abs(position.Y+80-(screen.Top+screen.Height*0.75))<=1,
                "Overlay is centred at 75 percent height on the capture monitor: "+screen);
        }
        var highDpiScreen=new System.Drawing.Rectangle(1920,-200,3840,2160);
        var highDpiPosition=OverlayPlacement.Calculate(highDpiScreen,highDpiScreen,720,320);
        Assert(highDpiPosition==new System.Drawing.Point(3480,1260),"Physical window size preserves placement on a 200 percent DPI monitor");
        Assert(OverlayPlacement.Calculate(new(0,0,1920,1080),new(0,0,1920,1040),360,600)==new System.Drawing.Point(780,440),
            "Tall overlay remains inside the working area above the taskbar");
        Assert(OverlayPlacement.Calculate(new(-1920,0,1920,1080),new(-1920,0,1920,1040),360,160,0.25,0.5)==new System.Drawing.Point(-1620,460),
            "Custom position stays relative to the capture monitor");
        var session=new PlaybackSession();
        bool paused=false; int commands=0;
        session.TogglePause();
        Assert(!session.Progress.Paused,"An idle playback session cannot be paused");
        session.Attach(value=> { paused=value; commands++; },()=>TimeSpan.FromSeconds(2));
        session.SetDuration(TimeSpan.FromSeconds(5)); session.Start(); session.TogglePause();
        Assert(paused && session.Progress is { Started:true,Paused:true,Position.TotalSeconds:2,Duration.TotalSeconds:5 },
            "Pause controls the bound output and preserves track position and duration");
        session.TogglePause();
        Assert(!paused && !session.Progress.Paused,"Resume continues the same playback session");
        int beforeDetach=commands; session.Detach(); session.TogglePause();
        Assert(commands==beforeDetach && !session.Progress.Started,"Detached session ignores stale UI commands after output disposal");

        var queue=new ReadingQueue();
        var firstMonitor=new System.Drawing.Rectangle(-1920,0,1920,1080);
        var secondMonitor=new System.Drawing.Rectangle(0,0,2560,1440);
        var first=queue.EnqueueAsync(token=> { queue.Update(token,translationText:"first translation",captureMonitor:firstMonitor); return Task.FromResult(1); },
            (_,token)=>queue.WaitForCloseAsync(token));
        long firstId=queue.Snapshot.Single().Id;
        bool nextPlayed=false;
        var second=queue.EnqueueAsync(token=> { queue.Update(token,translationText:"second translation",captureMonitor:secondMonitor); return Task.FromResult(2); },
            (_,_)=> { nextPlayed=true; return Task.CompletedTask; });
        Assert(queue.Snapshot[0] is { AwaitingClose:true,TranslationText:"first translation" } &&
            queue.Snapshot[1].TranslationText=="second translation" && !nextPlayed,
            "Held translation belongs to its job while the next job prepares independently");
        Assert(queue.Snapshot[0].CaptureMonitor==firstMonitor && queue.Snapshot[1].CaptureMonitor==secondMonitor,
            "Each queued reading retains its own capture monitor while later jobs prepare");
        queue.Close(firstId);
        await Task.WhenAll(first,second).WaitAsync(TimeSpan.FromSeconds(3));
        Assert(nextPlayed && !queue.IsBusy,"Closing held translation completes the job and advances the queue");
        var current=queue.EnqueueAsync(_=>Task.FromResult(0),(_,token)=>Task.Delay(Timeout.Infinite,token));
        queue.Close(firstId);
        Assert(!current.IsCompleted,"A stale widget close cannot cancel the next job");
        queue.Close(queue.Snapshot.Single().Id);
        await current.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!queue.IsBusy,"Closing a working job cancels it");
        var waiting=queue.EnqueueAsync(_=>Task.FromResult(0),(_,token)=>queue.WaitForCloseAsync(token));
        queue.CancelAll();
        await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!queue.IsBusy,"Cancel all releases manual text wait");

        using var providers=new SpeechProviderRegistry(); using var capture=new BlockedCapture();
        var player=new CacheAudio();
        var reader=new ReadingController(capture,new NoOcr(),new IdentityTextTransform(),providers,player,new NoFocus(),new TestTranslator());
        async Task WaitForText()
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!reader.Queue.Snapshot.Any(job=>job.AwaitingClose)) await Task.Delay(5,timeout.Token);
        }
        var reading=reader.ReadTextAsync("Original",settings);
        await WaitForText();
        Assert(reader.IsBusy && !reading.IsCompleted && reader.Queue.Snapshot.Single().TranslationText=="Перевод",
            "Translation without speech remains active until widget close");
        reader.Queue.Close(reader.Queue.Snapshot.Single().Id);
        await reading.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!reader.IsBusy && player.Calls==0,"Closing silent translation completes without audio playback");
        await reader.ReadTextAsync("Original",settings with { Overlay=options with { ShowTranslation=false } }).WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!reader.IsBusy,"Hidden translation never holds the job open");
        await reader.ReadTextAsync("Original",settings with { Overlay=options with { Enabled=false } }).WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!reader.IsBusy,"Disabled overlay never holds the job open");
        await reader.ReadTextAsync("Original",settings with { Translation=new() { Enabled=false } }).WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!reader.IsBusy,"No translated text means automatic completion");

        var cached=new CachedReading("test","Original","Saved translation",true,SpeakerGender.Unknown,SpeechAudioFormat.Wave,[1,2,3],DateTimeOffset.Now);
        reading=reader.ReplayAsync(cached,settings with { SpeechEnabled=true });
        await WaitForText();
        Assert(player.Calls==1 && !reading.IsCompleted && reader.Queue.Snapshot.Single().TranslationText=="Saved translation",
            "After cached playback, shown translation waits for manual close");
        reader.Queue.Close(reader.Queue.Snapshot.Single().Id);
        await reading.WaitAsync(TimeSpan.FromSeconds(3));
        await reader.ReplayAsync(cached,settings with { SpeechEnabled=true,Overlay=options with { ShowTranslation=false } }).WaitAsync(TimeSpan.FromSeconds(3));
        Assert(player.Calls==2 && !reader.IsBusy,"Playback without shown translation finishes automatically");
        reading=reader.ReplayAsync(cached,settings);
        await WaitForText();
        reader.UpdateOverlaySettings(options with { Enabled=false });
        await reading.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!reader.IsBusy,"Disabling overlay releases an existing text wait");
        await reader.ReplayAsync(cached,settings).WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!reader.IsBusy,"Live overlay settings prevent stale per-job settings from reopening a text wait");
        reader.UpdateOverlaySettings(options);
        reading=reader.ReplayAsync(cached,settings);
        await WaitForText();
        reader.UpdateOverlaySettings(options with { ShowTranslation=false });
        await reading.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!reader.IsBusy,"Disabling translation display releases an existing text wait");

        string directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"ScreenReader-overlay-cache-"+Guid.NewGuid().ToString("N"));
        var cache=new ReadingCache(directory);
        var speech=new CacheSpeech(); providers.Register(new("overlay-test","Overlay test",false,_=>speech,false));
        reader=new ReadingController(capture,new NoOcr(),new IdentityTextTransform(),providers,player,new NoFocus(),new TestTranslator(),cache:cache);
        var cacheSettings=settings with { SpeechEnabled=true,CacheEnabled=true,SpeechProviderId="overlay-test" };
        try
        {
            reading=reader.ReadTextAsync("Original",cacheSettings);
            await WaitForText();
            Assert(cache.List().Count==0,"A job awaiting translation close is not cached as completed");
            reader.Stop(); await reading.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(cache.List().Count==0,"Cancelling a held translation does not save a cancelled job to cache");
            reading=reader.ReadTextAsync("Original",cacheSettings);
            await WaitForText(); reader.Queue.Close(reader.Queue.Snapshot.Single().Id);
            await reading.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(cache.List().Count==1,"Closing held translation successfully commits the completed reading to cache");
        }
        finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory,true); }
    }
    private static void RenderReadingOverlayPreviews()
    {
        using var providers=new SpeechProviderRegistry(); using var capture=new BlockedCapture();
        var reader=new ReadingController(capture,new NoOcr(),new IdentityTextTransform(),providers,new CacheAudio());
        using var widget=new ReadingOverlayWindow(reader);
        widget.ApplySettings(new() { Enabled=true });
        Assert(!widget.IsVisible,"Idle overlay stays hidden when enabled");
        var content=(System.Windows.FrameworkElement)widget.Content;
        void Render(string name)
        {
            content.UpdateLayout(); content.InvalidateMeasure();
            content.Measure(new System.Windows.Size(360,double.PositiveInfinity));
            int height=(int)Math.Ceiling(content.DesiredSize.Height);
            content.Arrange(new System.Windows.Rect(0,0,360,height)); content.UpdateLayout();
            var image=new System.Windows.Media.Imaging.RenderTargetBitmap(360,height,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(content);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var output=System.IO.File.Create(System.IO.Path.Combine("artifacts",name+".png")); encoder.Save(output);
            Assert(height is >40 and <600,"Overlay preview fits compact window: "+name);
        }
        ((System.Windows.Controls.TextBlock)widget.FindName("StageText")).Text=UiText.T("overlay.paused");
        ((System.Windows.Controls.TextBlock)widget.FindName("TranslationText")).Text="Стражник: Дорога через перевал закрыта. Возвращайтесь утром, когда у ворот сменится караул.";
        ((System.Windows.UIElement)widget.FindName("TranslationPanel")).Visibility=System.Windows.Visibility.Visible;
        ((System.Windows.UIElement)widget.FindName("PlaybackPanel")).Visibility=System.Windows.Visibility.Visible;
        ((System.Windows.UIElement)widget.FindName("PauseButton")).Visibility=System.Windows.Visibility.Visible;
        ((System.Windows.Controls.ProgressBar)widget.FindName("PlaybackBar")).Value=0.4;
        ((System.Windows.Controls.TextBlock)widget.FindName("TimeText")).Text="0:04 / 0:10";
        Render("reading-overlay-playback");
        ((System.Windows.UIElement)widget.FindName("PlaybackPanel")).Visibility=System.Windows.Visibility.Collapsed;
        ((System.Windows.UIElement)widget.FindName("PauseButton")).Visibility=System.Windows.Visibility.Collapsed;
        ((System.Windows.Controls.TextBlock)widget.FindName("StageText")).Text=UiText.T("overlay.waiting");
        Render("reading-overlay-text");
        ((System.Windows.UIElement)widget.FindName("ConfigurationPanel")).Visibility=System.Windows.Visibility.Visible;
        ((System.Windows.Controls.TextBlock)widget.FindName("StageText")).Text=UiText.T("overlay.preview");
        Render("reading-overlay-layout");
    }
    private static async Task RunOverlayAudioChecksAsync()
    {
        using var stream=new System.IO.MemoryStream();
        using (var writer=new NAudio.Wave.WaveFileWriter(new NonClosingStream(stream),new NAudio.Wave.WaveFormat(16000,16,1)))
            writer.Write(new byte[16000*2*2],0,16000*2*2);
        byte[] wave=stream.ToArray();
        async System.Collections.Generic.IAsyncEnumerable<byte[]> Audio()
        { yield return wave; await Task.CompletedTask; }
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var player=new StreamingAudioPlayer(0);
        var session=new PlaybackSession();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playing=player.PlayAsync(Audio(),SpeechAudioFormat.Wave,"",1,session,()=>started.TrySetResult(),timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        session.TogglePause();
        await Task.Delay(150,timeout.Token);
        var pausedAt=session.Progress.Position;
        await Task.Delay(150,timeout.Token);
        Assert(session.Progress.Paused && session.Progress.Position==pausedAt && !playing.IsCompleted,
            "Native silent playback pauses without advancing progress or finishing");
        session.TogglePause();
        await playing.WaitAsync(timeout.Token);
        Assert(!session.Progress.Started,"Native silent playback resumes and finishes normally");
        session=new PlaybackSession();
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        playing=player.PlayAsync(Audio(),SpeechAudioFormat.Wave,"",1,session,()=>started.TrySetResult(),cancel.Token);
        await started.Task.WaitAsync(cancel.Token);
        session.TogglePause(); cancel.Cancel();
        try { await playing.WaitAsync(TimeSpan.FromSeconds(2)); Assert(false,"Paused playback must cancel"); }
        catch (OperationCanceledException) { Assert(true,"Cancel interrupts native playback while paused"); }
    }
}
