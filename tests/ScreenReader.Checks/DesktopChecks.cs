using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScreenReader.App;
using ScreenReader.Edge;
using ScreenReader.Speech;

internal static partial class Program
{
    private static void RunSelectionCancellationChecks()
    {
        using var image=new System.Drawing.Bitmap(640,360);
        using var frame=new CapturedScreen(image,new System.Drawing.Rectangle(-10000,-10000,640,360),"Synthetic");
        foreach (bool dragging in new[] { false,true })
        {
            var overlay=new SelectionWindow(frame);
            try
            {
                overlay.Show();
                var handle=new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
                Assert(System.Windows.Interop.HwndSource.FromHwnd(handle)!.CompositionTarget.RenderMode==System.Windows.Interop.RenderMode.SoftwareOnly,
                    "Selection renders independently of the game's GPU presentation");
                void Mouse(System.Windows.RoutedEvent routedEvent,System.Windows.Input.MouseButton button)=>overlay.RaiseEvent(
                    new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,button) { RoutedEvent=routedEvent });
                if (dragging) Mouse(System.Windows.Input.Mouse.MouseDownEvent,System.Windows.Input.MouseButton.Left);
                Mouse(System.Windows.Input.Mouse.MouseDownEvent,System.Windows.Input.MouseButton.Right);
                // Releasing left during cancellation must not commit the selection.
                if (dragging) Mouse(System.Windows.Input.Mouse.MouseUpEvent,System.Windows.Input.MouseButton.Left);
                Assert(overlay.IsVisible,"Right-button cancellation waits for release: dragging="+dragging);
                Mouse(System.Windows.Input.Mouse.MouseUpEvent,System.Windows.Input.MouseButton.Right);
                Assert(overlay.Completion.IsCompletedSuccessfully && overlay.Completion.Result is null,
                    "Right click cancels ordinary selection without returning a region: dragging="+dragging);
            }
            finally { overlay.Close(); }
        }
    }

    private static void RunFocusLayoutChecks()
    {
        // Use real keyboard focus: static previews do not exercise focus triggers.
        var panel=new System.Windows.Controls.StackPanel { Width=320, Margin=new System.Windows.Thickness(16) };
        var controls=new System.Windows.Controls.Control[]
        {
            new System.Windows.Controls.TextBox { Text="Single line" },
            new System.Windows.Controls.TextBox { Text="First line\nSecond line", AcceptsReturn=true, TextWrapping=System.Windows.TextWrapping.Wrap },
            new System.Windows.Controls.PasswordBox { Password="layout-check" },
            new System.Windows.Controls.ComboBox { ItemsSource=new[] { "First", "Second" }, SelectedIndex=0 },
            new System.Windows.Controls.ComboBox { ItemsSource=new[] { "First", "Second" }, IsEditable=true, Text="Custom" },
            new System.Windows.Controls.CheckBox { Content="Keep this row stable" },
            new System.Windows.Controls.Button { Content="Action" },
            new System.Windows.Controls.ListBoxItem { Content="List entry" }
        };
        foreach (var control in controls) panel.Children.Add(control);
        var sink=new System.Windows.Controls.Button { Content="Move focus here" };
        panel.Children.Add(sink);
        var host=new System.Windows.Window
        {
            Content=panel, SizeToContent=System.Windows.SizeToContent.WidthAndHeight,
            Left=-10000, Top=-10000, ShowInTaskbar=false, WindowStyle=System.Windows.WindowStyle.None
        };
        System.Windows.Rect Bounds(System.Windows.FrameworkElement element)=>
            element.TransformToAncestor(panel).TransformBounds(new System.Windows.Rect(element.RenderSize));
        try
        {
            host.Show();
            foreach (var control in controls)
            {
                System.Windows.Input.Keyboard.Focus(sink); host.UpdateLayout();
                var before=panel.Children.Cast<System.Windows.FrameworkElement>().Select(Bounds).ToArray();
                var fieldHost=control.Template.FindName("PART_ContentHost",control) as System.Windows.FrameworkElement;
                var fieldBefore=fieldHost is null ? default : Bounds(fieldHost);
                System.Windows.Input.Keyboard.Focus(control); host.UpdateLayout();
                Assert(control.IsKeyboardFocusWithin,"Keyboard focus reaches "+control.GetType().Name);
                Assert(panel.Children.Cast<System.Windows.FrameworkElement>().Select(Bounds).SequenceEqual(before),
                    "Focus preserves control sizes and neighbouring positions: "+control.GetType().Name);
                if (fieldHost is not null) Assert(Bounds(fieldHost)==fieldBefore,"Focus preserves text viewport geometry: "+control.GetType().Name);
                if (control is System.Windows.Controls.CheckBox check)
                {
                    check.IsChecked=true; host.UpdateLayout();
                    Assert(panel.Children.Cast<System.Windows.FrameworkElement>().Select(Bounds).SequenceEqual(before),"Checking a focused checkbox preserves layout");
                }
                System.Windows.Input.Keyboard.Focus(sink); host.UpdateLayout();
                Assert(panel.Children.Cast<System.Windows.FrameworkElement>().Select(Bounds).SequenceEqual(before),
                    "Leaving focus preserves layout: "+control.GetType().Name);
            }
        }
        finally { host.Close(); }
    }

    private static void RunLocalizationChecks()
    {
        foreach (var locale in UiText.Languages)
        {
            string language=locale.Id;
            Assert(UiText.Keys.All(key=>UiText.HasTranslation(key,language)),"Complete UI string catalog for "+language);
            foreach (string key in UiText.Keys)
            {
                string[] Tokens(string text)=>System.Text.RegularExpressions.Regex.Matches(text,@"\{[^{}]+\}")
                    .Select(m=>m.Value).OrderBy(t=>t,StringComparer.Ordinal).ToArray();
                Assert(Tokens(UiText.Get(key,language)).SequenceEqual(Tokens(UiText.Get(key,"en"))),
                    $"Translation preserves parameters: {language}/{key}");
            }
            UiText.SetLanguage(language);
            Assert(UiText.Language==language && System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName==language,
                "UI culture follows language: "+language);
            try { OpenAiSpeechClient.Validate(new("not-a-url","model","")); }
            catch (ArgumentException e) { Assert(e.Message==UiText.T("tts.url"),"TTS errors follow UI language: "+language); }
            var retry=new SpeechServiceException("voice-name",2,false,new IOException());
            Assert(retry.Message==UiText.T("edge.retries","voice-name",2),"Parameterized Edge errors follow UI language: "+language);
            var saved=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { UiLanguage=language }))!;
            Assert(saved.UiLanguage==language,"UI language survives settings serialization: "+language);
        }
        Assert(!UiText.SupportsLanguage(null) && UiText.SupportsLanguage("FR"),"Startup language validation handles missing and uppercase values");
        UiText.SetLanguage("unsupported");
        Assert(UiText.Language=="ru","Unsupported interface language falls back to Russian");
        Assert(UiText.Get("button.save","unsupported")==UiText.Get("button.save","en"),"Missing translation falls back to English");
        UiText.SetLanguage("en");
        Assert(UiText.T("status.saved","Ctrl + Alt + R").StartsWith("Settings saved"),"Status messages are localized to English");
        try { OpenAiSpeechClient.Validate(new("not-a-url","model","")); }
        catch (ArgumentException e) { Assert(e.Message.StartsWith("Enter the full"),"TTS validation errors follow UI language"); }
        Assert(UiText.T("tray.exit")=="Exit" && UiText.T("selection.hint").StartsWith("Drag"),"Tray and selection overlay have English labels");
        using var icon=BrandAssets.CreateTrayIcon();
        Assert(icon.Width>=16 && icon.Height>=16,"Storyteller icon is embedded and readable by Windows");
        UiText.SetLanguage("ru");
    }
    private static async Task RunLifetimeSignalAsync()
    {
        string name="Local\\ScreenReader.Tests."+Guid.NewGuid().ToString("N");
        var requested=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var signal=new AppLifetimeSignal(()=>requested.TrySetResult(),name);
        using (var sender=EventWaitHandle.OpenExisting(name)) sender.Set();
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(true,"Installer shutdown signal is delivered without terminating unrelated processes");
    }
    private static async Task RunDeviceChecksAsync()
    {
        var devices=AudioOutputDevices.List();
        Assert(devices.Count>=1 && devices[0].Id=="","Windows default output is always a selectable option");
        Console.WriteLine("Outputs: "+string.Join(", ",devices.Select(d=>d.Name)));
        using var device=AudioOutputDevices.Resolve("");
        string id=device.ID;
        using (var same=AudioOutputDevices.Resolve(id)) Assert(same.ID==id,"Saved endpoint ID resolves to the same playback device");
        Throws<InvalidOperationException>(()=>AudioOutputDevices.Resolve("missing-test-device"),"Missing selected output fails clearly rather than playing on another device");
        byte[] wave=MakeWave();
        async System.Collections.Generic.IAsyncEnumerable<byte[]> Audio()
        {
            await Task.Yield();
            for (int at=0; at<wave.Length; at+=1024) yield return wave.AsSpan(at,Math.Min(1024,wave.Length-at)).ToArray();
        }
        float before=device.AudioEndpointVolume.MasterVolumeLevelScalar;
        int starts=0;
        await new StreamingAudioPlayer(0).PlayAsync(Audio(),SpeechAudioFormat.Wave,id,()=>starts++,CancellationToken.None);
        float after=device.AudioEndpointVolume.MasterVolumeLevelScalar;
        Assert(starts==1,"WASAPI plays through an explicitly selected endpoint");
        Assert(Math.Abs(before-after)<0.00001,"Silent tests and app playback do not change device master volume");
    }
}
