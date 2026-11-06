using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using ScreenReader.App;
using ScreenReader.Edge;
using ScreenReader.Speech;

internal static partial class Program
{
    private sealed class PreviewApp : ScreenReader.App.App
    {
        public PreviewApp()
        {
            ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown;
            string assembly=typeof(ScreenReader.App.App).Assembly.GetName().Name!;
            foreach (string file in new[] { "Light","Fluent" })
                Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source=new Uri($"/{assembly};component/Themes/{file}.xaml",UriKind.Relative) });
        }
        protected override void OnStartup(System.Windows.StartupEventArgs e) { }
    }
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--wizard-ui")) { RunSetupWizardChecks(); Console.WriteLine($"PASS: {checks} checks"); return 0; }
            if (args.Contains("--settings-ui")) { RunUi(); Console.WriteLine($"PASS: {checks} checks"); return 0; }
            if (args.Contains("--ollama-requirements-online")) { RunLiveOllamaRequirementsAsync().GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--theme-only")) { RunThemeChecks(); Console.WriteLine($"PASS: {checks} checks"); return 0; }
            if (args.Contains("--gamebar-ui")) { RunGameBarUi(); Console.WriteLine($"PASS: {checks} checks"); return 0; }
            RunOffline();
            RunRetryChecksAsync().GetAwaiter().GetResult();
            RunProviderChecksAsync().GetAwaiter().GetResult();
            RunSpeechFallbackChecksAsync().GetAwaiter().GetResult();
            RunTranslationChecksAsync().GetAwaiter().GetResult();
            RunTranslationCorrectionChecksAsync().GetAwaiter().GetResult();
            RunHotkeyChecks();
            RunSelectionCancellationChecks();
            RunQueueChecksAsync().GetAwaiter().GetResult();
            RunReadingOverlayChecksAsync().GetAwaiter().GetResult();
            RunGameBarChecksAsync().GetAwaiter().GetResult();
            RunCacheChecksAsync().GetAwaiter().GetResult();
            RunLocalizationChecks();
            RunOllamaChecksAsync().GetAwaiter().GetResult();
            RunLifetimeSignalAsync().GetAwaiter().GetResult();
            if (args.Contains("--devices")) RunDeviceChecksAsync().GetAwaiter().GetResult();
            if (args.Contains("--windows")) RunWindowsSpeechAsync().GetAwaiter().GetResult();
            if (args.Contains("--hotkey-service")) RunHotkeyService();
            if (args.Contains("--hotkey-input")) RunHotkeyInput();
            if (args.Contains("--overlay-interaction")) RunOverlayInteractionInput();
            if (args.Contains("--repeat-capture")) RunRepeatCaptureChecks();
            if (args.Contains("--ocr")) RunOcrAsync().GetAwaiter().GetResult();
            if (args.Contains("--online")) RunOnlineAsync().GetAwaiter().GetResult();
            if (args.Contains("--dmitry")) CheckDmitryAsync().GetAwaiter().GetResult();
            if (args.Contains("--audio")) RunAudioAsync().GetAwaiter().GetResult();
            if (args.Contains("--overlay-audio")) RunOverlayAudioChecksAsync().GetAwaiter().GetResult();
            if (args.Contains("--capture")) RunCapture();
            if (args.Contains("--overlay")) RunOverlay();
            if (args.Contains("--overlay-window")) RunOverlayWindow();
            if (args.Contains("--focus-window")) RunFocusWindow();
            if (args.Contains("--ui")) RunUi();
            Console.WriteLine($"PASS: {checks} checks"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void Assert(bool result,string description)
    { if (!result) throw new Exception("FAIL: "+description); checks++; Console.WriteLine("PASS: "+description); }
    private static void Throws<T>(Action action,string description) where T:Exception
    { try { action(); } catch (T) { Assert(true,description); return; } throw new Exception("FAIL: "+description); }
    private static void RunOffline()
    {
        using var vectors=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","edge-token-vectors.json")));
        foreach (var vector in vectors.RootElement.EnumerateArray())
        {
            long unix=vector.GetProperty("unix").GetInt64();
            Assert(EdgeToken.Generate(DateTimeOffset.FromUnixTimeSeconds(unix))==vector.GetProperty("expected").GetString(),"Token matches upstream at "+unix);
        }
        var text=string.Concat(Enumerable.Repeat("Пример <текста> & emoji 😀 with words.\n",500));
        var parts=EdgeSpeechClient.SplitText(text).ToArray();
        Assert(string.Concat(parts)==text,"Unicode and XML text survives chunking");
        Assert(parts.All(p => Encoding.UTF8.GetByteCount(SecurityElement.Escape(p)!)<=3000),"Every escaped text chunk fits protocol limit");
        Assert(!EdgeSpeechClient.SplitText(" \n\0 ").Any(),"Empty/control-only text produces no request");
        byte[] header=Encoding.UTF8.GetBytes("Path:audio\r\nContent-Type:audio/mpeg\r\n");
        byte[] frame=new byte[header.Length+5]; frame[0]=(byte)(header.Length>>8); frame[1]=(byte)header.Length;
        header.CopyTo(frame,2); frame[^3]=0xFF; frame[^2]=0xFB; frame[^1]=0x90;
        Assert(EdgeSpeechClient.ParseAudioFrame(frame).SequenceEqual(new byte[]{0xFF,0xFB,0x90}),"Binary envelope strips length and headers");
        Throws<InvalidDataException>(()=>EdgeSpeechClient.ParseAudioFrame(new byte[]{0}),"Truncated frame rejected");
        Throws<InvalidDataException>(()=>EdgeSpeechClient.ParseAudioFrame(new byte[]{0xFF,0xFF,0}),"Oversized frame header rejected");
        Assert(SelectionMath.ToPixels(900,400,100,50,1280,720,2560,1440)==Rectangle.FromLTRB(200,100,1800,800),"Reverse drag converts 200% DPI coordinates");
        Assert(SelectionMath.ToPixels(-5,-5,1500,800,1280,720,1920,1080)==new Rectangle(0,0,1920,1080),"Selection clamps to monitor bounds");
        HotkeyService.Validate(new Hotkey(0,0x52));
        Assert(true,"Unmodified hotkey accepted");
        Assert(new Hotkey(3,0x52).ToString()=="Ctrl + Alt + R","Hotkey display");
        Assert(EdgeSpeechClient.ToServiceVoiceName("ru-RU-DmitryNeural")=="Microsoft Server Speech Text to Speech Voice (ru-RU, DmitryNeural)","Russian voice uses canonical service name");
        RunOcrFormattingChecks();
    }
    private static async Task RunOcrAsync()
    {
        var ocr=new WindowsOcrProvider();
        Console.WriteLine("Installed OCR: "+string.Join(", ",ocr.Languages.Select(l=>l.Tag)));
        foreach (var (tag,sample,word) in new[] { ("en-US","Hello screen reader","Hello"),("ru-RU","Привет чтение экрана","Привет") })
        {
            if (!ocr.Languages.Any(l=>l.Tag==tag)) { Console.WriteLine("SKIP: OCR "+tag+" is not installed"); continue; }
            using var bitmap=new Bitmap(1000,140);
            using (var g=Graphics.FromImage(bitmap))
            using (var font=new Font("Segoe UI",32)) { g.Clear(Color.White); g.DrawString(sample,font,Brushes.Black,20,30); }
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string actual=await ocr.RecognizeAsync(bitmap,tag,timeout.Token);
            Assert(actual.Contains(word,StringComparison.OrdinalIgnoreCase),"Windows OCR recognizes synthetic "+tag+" sample");
        }
    }
    private static async Task RunOnlineAsync()
    {
        using var client=new EdgeSpeechClient();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var voices=await client.GetVoicesAsync(timeout.Token);
        Assert(voices.Any(v=>v.ShortName=="ru-RU-SvetlanaNeural"),"Live service lists Russian voice");
        Assert(voices.Any(v=>v.Locale=="en-US"),"Live service lists English voices");
        foreach (var (voice,language,text) in new[] { ("ru-RU-SvetlanaNeural","ru-RU","Проверка прямого подключения."),("en-US-JennyNeural","en-US","Direct connection test.") })
        {
            using var audio=new MemoryStream(); int chunks=0;
            await foreach (byte[] data in client.StreamAsync(new(text,voice,language,0),timeout.Token)) { audio.Write(data); chunks++; }
            Assert(chunks>0 && audio.Length>1000,"Live "+voice+" synthesis returns MP3 stream");
            audio.Position=0;
            var mp3=NAudio.Wave.Mp3Frame.LoadFromStream(audio);
            Assert(mp3 is not null && mp3.SampleRate==24000,"Live "+language+" audio decodes as 24 kHz MP3");
        }
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        bool stopped=false;
        try { await foreach (var _ in client.StreamAsync(new("cancel","en-US-JennyNeural","en-US",0),cancelled.Token)) { } }
        catch (OperationCanceledException) { stopped=true; }
        Assert(stopped,"Cancelled synthesis exits before connecting");
    }
    private static async Task CheckDmitryAsync()
    {
        using var client=new EdgeSpeechClient();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        for (int attempt=1; attempt<=3; attempt++)
        {
            if (attempt==2) await client.GetVoicesAsync(timeout.Token);
            using var audio=new MemoryStream();
            await foreach (var data in client.StreamAsync(new("Привет! Это проверка чтения текста с экрана.","ru-RU-DmitryNeural","ru-RU",0),timeout.Token)) audio.Write(data);
            audio.Position=0;
            var mp3=NAudio.Wave.Mp3Frame.LoadFromStream(audio);
            Assert(audio.Length>1000 && mp3 is not null && mp3.SampleRate==24000,"Live Dmitry synthesis decodes as MP3, request "+attempt);
        }
    }
    private static async Task RunAudioAsync()
    {
        using var client=new EdgeSpeechClient();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var player=new StreamingAudioPlayer(0); // Exercise native audio path without audible speech.
        using var cached=new MemoryStream();
        async System.Collections.Generic.IAsyncEnumerable<byte[]> CacheAudio([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var bytes in client.StreamAsync(new("Проверка звукового декодера.","ru-RU-SvetlanaNeural","ru-RU",0),ct))
            { cached.Write(bytes); yield return bytes; }
        }
        int started=0;
        await player.PlayAsync(CacheAudio(timeout.Token),()=>Interlocked.Increment(ref started),timeout.Token);
        Assert(started==1,"Streaming MP3 decode and silent native audio playback completes");
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var timer=new System.Diagnostics.Stopwatch();
        bool cancelled=false,requested=false;
        byte[] cachedAudio=cached.ToArray();
        async System.Collections.Generic.IAsyncEnumerable<byte[]> RepeatedAudio([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            for (int i=0; i<1000; i++) { ct.ThrowIfCancellationRequested(); yield return cachedAudio; await Task.Delay(1,ct); }
        }
        await CancelOnStart();
        async Task CancelOnStart()
        {
            try
            {
                await player.PlayAsync(RepeatedAudio(cancel.Token),()=> { requested=true; timer.Start(); cancel.Cancel(); },cancel.Token);
            }
            catch (OperationCanceledException) { cancelled=true; }
        }
        Assert(requested && cancelled && timer.Elapsed.TotalSeconds<3,"Cancellation promptly stops decoder, audio output and streaming producer");
    }
    private static void RunUi()
    {
        var app=new PreviewApp();
        // Exercise adding and removing fallback roles independently of installed Windows voices.
        typeof(App).GetProperty(nameof(App.Settings))!.SetValue(app,new AppSettings { FallbackRole=null });
        var window=new MainWindow(app);
        CheckGameBarControls(window);
        int previewWidth=(int)window.Width, previewHeight=(int)window.Height;
        Assert(((System.Windows.Controls.TextBox)window.FindName("CaptureKeyBox")).Text=="Ctrl + Alt + R" &&
            new[] { "RepeatCaptureKeyBox","ToggleKeyBox","CancelCurrentKeyBox","CancelAllKeyBox" }.All(name=>((System.Windows.Controls.TextBox)window.FindName(name)).Text==UiText.T("hotkey.unassigned")),"UI displays queue shortcut and four unassigned actions by default");
        Assert(new[] { "OverlayKeyBox","OverlayControlsKeyBox" }.All(name=>((System.Windows.Controls.TextBox)window.FindName(name)).Text=="Alt"),
            "Overlay and Controls display the same default Alt binding");
        var setHotkey=typeof(MainWindow).GetMethod("SetHotkey",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        foreach (string name in new[] { "OverlayKeyBox","OverlayControlsKeyBox" })
        {
            setHotkey.Invoke(window,[window.FindName(name),new Hotkey(0,0x75)]);
            Assert(new[] { "OverlayKeyBox","OverlayControlsKeyBox" }.All(field=>((System.Windows.Controls.TextBox)window.FindName(field)).Text=="F6"),
                "Changing either interaction field synchronizes both tabs: "+name);
        }
        setHotkey.Invoke(window,[window.FindName("OverlayKeyBox"),new Hotkey(0,0x12)]);
        var content=(System.Windows.FrameworkElement)window.Content;
        content.Measure(new System.Windows.Size(previewWidth,previewHeight));
        content.Arrange(new System.Windows.Rect(0,0,previewWidth,previewHeight)); content.UpdateLayout();
        var image=new System.Windows.Media.Imaging.RenderTargetBitmap(previewWidth,previewHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(content);
        var pixels=new byte[previewWidth*previewHeight*4]; image.CopyPixels(pixels,previewWidth*4,0);
        Assert(Enumerable.Range(0,previewWidth*previewHeight).Count(i=>pixels[i*4+3]>0)>previewWidth*previewHeight/2,"Settings preview contains rendered content");
        string directory=Path.Combine(Directory.GetCurrentDirectory(),"artifacts"); Directory.CreateDirectory(directory);
        using var output=File.Create(Path.Combine(directory,"settings-preview.png"));
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image)); encoder.Save(output);
        Assert(window.FindName("LanguageBox") is System.Windows.Controls.ComboBox box && box.SelectedItem is LanguageChoice { Tag:"ru-RU",Installed:true },"Settings window loads with installed Russian OCR");
        Assert(window.FindName("VoiceBox") is System.Windows.Controls.ComboBox voice && voice.SelectedItem is SpeechVoice { ShortName:"ru-RU-SvetlanaNeural" },"Settings window selects Russian voice");
        var providerBox=(System.Windows.Controls.ComboBox)window.FindName("ProviderBox");
        var voiceBox=(System.Windows.Controls.ComboBox)window.FindName("VoiceBox");
        var ocrLanguages=(System.Windows.Controls.ComboBox)window.FindName("LanguageBox");
        var targetLanguages=(System.Windows.Controls.ComboBox)window.FindName("TranslationTargetBox");
        var translateEnabled=(System.Windows.Controls.CheckBox)window.FindName("TranslateEnabledBox");
        var replaceBreaks=(System.Windows.Controls.CheckBox)window.FindName("OcrReplaceLineBreaksBox");
        var preserveParagraphs=(System.Windows.Controls.CheckBox)window.FindName("OcrPreserveParagraphsBox");
        Assert(replaceBreaks.IsChecked==true && preserveParagraphs.IsChecked==false && preserveParagraphs.IsEnabled,"Recognition shows default line break settings");
        replaceBreaks.IsChecked=false;
        Assert(!preserveParagraphs.IsEnabled,"Paragraph preservation is disabled when replacement is off");
        replaceBreaks.IsChecked=true;
        Assert(preserveParagraphs.IsEnabled,"Paragraph preservation is available when replacement is on");
        foreach (string tag in LanguageCatalog.Tags)
        {
            ocrLanguages.SelectedItem=ocrLanguages.Items.Cast<LanguageChoice>().First(l=>l.Tag==tag);
            Assert(voiceBox.Items.Cast<SpeechVoice>().Any(v=>v.Locale==tag && v.Gender=="Female") &&
                voiceBox.Items.Cast<SpeechVoice>().Any(v=>v.Locale==tag && v.Gender=="Male"),"Built-in voices follow OCR language: "+tag);
        }
        ocrLanguages.SelectedItem=ocrLanguages.Items.Cast<LanguageChoice>().First(l=>l.Tag=="ru-RU");
        translateEnabled.IsChecked=true;
        foreach (string tag in LanguageCatalog.Tags)
        {
            targetLanguages.SelectedItem=targetLanguages.Items.Cast<LanguageChoice>().First(l=>l.Tag==tag);
            Assert(targetLanguages.Text==tag && voiceBox.SelectedItem is SpeechVoice selected && selected.Locale==tag,
                "Translation choice uses locale code and updates voices: "+tag);
        }
        targetLanguages.Text="pt-BR";
        UiText.SetLanguage("en");
        Assert(targetLanguages.Text=="pt-BR" && targetLanguages.Items.Cast<LanguageChoice>().Any(l=>l.Name=="Japanese (ja-JP)"),
            "Translation language accepts custom input and preserves it during localization");
        UiText.SetLanguage("ru");
        targetLanguages.SelectedItem=targetLanguages.Items.Cast<LanguageChoice>().First(l=>l.Tag=="ja-JP");
        UiText.SetLanguage("en");
        Assert(targetLanguages.Text=="ja-JP","Selected translation locale survives localization");
        UiText.SetLanguage("ru");
        targetLanguages.Text="ru-RU"; translateEnabled.IsChecked=false;
        providerBox.SelectedItem=app.Providers.Definitions.First(p=>p.Id=="windows");
        Assert(voiceBox.SelectedItem is SpeechVoice { Locale:"ru-RU" },"Switching provider loads local Russian voices");
        string windowsVoice=((SpeechVoice)voiceBox.SelectedItem).ShortName;
        providerBox.SelectedItem=app.Providers.Definitions.First(p=>p.Id=="openai-compatible");
        Assert(voiceBox.IsEditable && ((System.Windows.Controls.StackPanel)window.FindName("ServerPanel")).Visibility==System.Windows.Visibility.Visible,"Compatible server exposes endpoint, model, key and editable voice");
        voiceBox.Text="custom-ui-voice";
        ((System.Windows.Controls.TextBox)window.FindName("EndpointBox")).Text="http://127.0.0.1:9876/v1/audio/speech";
        ((System.Windows.Controls.PasswordBox)window.FindName("ApiKeyBox")).Password="fake-ui-secret";
        providerBox.SelectedItem=app.Providers.Definitions.First(p=>p.Id=="windows");
        Assert(((SpeechVoice)voiceBox.SelectedItem).ShortName==windowsVoice,"Voice selection is preserved separately per provider");
        providerBox.SelectedItem=app.Providers.Definitions.First(p=>p.Id=="openai-compatible");
        Assert(voiceBox.Text=="custom-ui-voice","Custom server voice survives switching providers");
        var settings=(AppSettings)typeof(MainWindow).GetMethod("ReadControls",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null)!;
        Assert(settings.SelectedVoice=="custom-ui-voice" && settings.OpenAiEndpoint=="http://127.0.0.1:9876/v1/audio/speech" && SecretProtection.Unprotect(settings.OpenAiApiKeyProtected)=="fake-ui-secret","Server settings round-trip from UI with encrypted key");
        var tabs=(System.Windows.Controls.TabControl)window.FindName("MainTabs");
        Assert(tabs.Items.Count==13,"Settings include overlay, text correction, translation correction and help tabs");
        var overlayTranslation=(System.Windows.Controls.CheckBox)window.FindName("OverlayTranslationBox");
        var overlayAutoAdvance=(System.Windows.Controls.CheckBox)window.FindName("OverlayAutoAdvanceBox");
        // Select the containing tab so ElementName bindings have an active visual tree.
        tabs.SelectedItem=tabs.Items.Cast<System.Windows.Controls.TabItem>().First(tab=>
            tab.Tag?.ToString()=="\uE7F4");
        content.UpdateLayout();
        Assert(overlayAutoAdvance.IsChecked==true && overlayAutoAdvance.Visibility==System.Windows.Visibility.Collapsed,
            "Automatic advance defaults to enabled and is hidden without translation display");
        overlayTranslation.IsChecked=true; content.UpdateLayout();
        Assert(overlayAutoAdvance.Visibility==System.Windows.Visibility.Visible,"Translation display reveals automatic advance");
        overlayAutoAdvance.IsChecked=false;
        overlayTranslation.IsChecked=false; content.UpdateLayout();
        Assert(overlayAutoAdvance.Visibility==System.Windows.Visibility.Collapsed && !((AppSettings)typeof(MainWindow)
            .GetMethod("ReadControls",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null)!).Overlay.AutoAdvance,
            "Hidden automatic advance preserves and saves the disabled choice");
        overlayTranslation.IsChecked=true; content.UpdateLayout();
        Assert(overlayAutoAdvance.Visibility==System.Windows.Visibility.Visible && overlayAutoAdvance.IsChecked==false,
            "Re-enabling translation display preserves automatic advance choice");
        overlayTranslation.IsChecked=false; overlayAutoAdvance.IsChecked=true;
        var translationCorrections=(System.Windows.Controls.CheckBox)window.FindName("TranslationCorrectionsEnabledBox");
        var glossaryList=(System.Windows.Controls.ListBox)window.FindName("TranslationCorrectionsList");
        var glossarySource=(System.Windows.Controls.TextBox)window.FindName("TranslationSourceBox");
        var glossaryReplacement=(System.Windows.Controls.TextBox)window.FindName("TranslationReplacementBox");
        var glossaryPreserve=(System.Windows.Controls.CheckBox)window.FindName("TranslationPreserveBox");
        var glossaryIgnoreCase=(System.Windows.Controls.CheckBox)window.FindName("TranslationIgnoreCaseBox");
        void GlossaryClick(string name)=>((System.Windows.Controls.Button)window.FindName(name)).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        void GlossaryRowAction(string method,TranslationCorrectionEntry entry)=>typeof(MainWindow).GetMethod(method,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,new object[] { new System.Windows.Controls.Button { Tag=entry },new System.Windows.RoutedEventArgs() });
        AppSettings GlossarySettings()=>(AppSettings)typeof(MainWindow).GetMethod("ReadControls",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null)!;
        tabs.SelectedIndex=3; content.UpdateLayout();
        Assert(((System.Windows.Controls.TabItem)tabs.SelectedItem).Header.ToString()==UiText.T("tab.translationCorrection") && translationCorrections.IsChecked==false && !glossarySource.IsEnabled && glossaryList.Items.Count==0,"Translation glossary starts with disabled editor and empty list");
        Assert(!translationCorrections.IsEnabled && !glossaryList.IsEnabled,"Translation glossary cannot be enabled while translation is off");
        translateEnabled.IsChecked=true; content.UpdateLayout();
        Assert(translationCorrections.IsEnabled && !glossarySource.IsEnabled,"Translation enables the glossary toggle while its editor still requires opting in");
        translationCorrections.IsChecked=true; content.UpdateLayout();
        Assert(glossarySource.IsEnabled && glossaryIgnoreCase.IsChecked==true,"Enabling glossary unlocks editor with case-insensitive default");
        glossarySource.Text="Skyrim"; glossaryPreserve.IsChecked=true;
        Assert(!glossaryReplacement.IsEnabled,"Preserve checkbox disables replacement field");
        GlossaryClick("TranslationCorrectionAddButton");
        glossarySource.Text="Dragonborn"; glossaryReplacement.Text="Довакин";
        GlossaryClick("TranslationCorrectionAddButton");
        translateEnabled.IsChecked=false; content.UpdateLayout();
        Assert(!translationCorrections.IsEnabled && !glossarySource.IsEnabled && !glossaryList.IsEnabled && translationCorrections.IsChecked==true && glossaryList.Items.Count==2,"Disabling translation locks the glossary and preserves its selection and entries");
        translateEnabled.IsChecked=true; content.UpdateLayout();
        Assert(translationCorrections.IsEnabled && glossarySource.IsEnabled && glossaryList.IsEnabled && glossaryList.Items.Count==2,"Re-enabling translation restores glossary availability and entries");
        translateEnabled.IsChecked=false; content.UpdateLayout();
        Assert(glossaryList.Items.Count==2 && GlossarySettings().Translation.CorrectionEntries is { Count:2 } && GlossarySettings().Translation.CorrectionRules=="","Glossary form adds typed entries and saves without legacy text");
        GlossaryRowAction("EditTranslationCorrection",(TranslationCorrectionEntry)glossaryList.Items[1]);
        glossaryIgnoreCase.IsChecked=false; glossaryReplacement.Text="Драконорождённый";
        GlossaryClick("TranslationCorrectionAddButton");
        Assert(glossaryList.Items.Count==2 && glossaryList.Items[1] is TranslationCorrectionEntry { IgnoreCase:false,Replacement:"Драконорождённый" },"Editing updates selected entry without duplication");
        glossarySource.Text="skyrim"; glossaryPreserve.IsChecked=true;
        GlossaryClick("TranslationCorrectionAddButton");
        Assert(glossaryList.Items.Count==2 && !string.IsNullOrEmpty(((System.Windows.Controls.TextBlock)window.FindName("TranslationCorrectionError")).Text),"Overlapping glossary duplicates show inline validation");
        GlossaryRowAction("EditTranslationCorrection",(TranslationCorrectionEntry)glossaryList.Items[1]);
        glossaryReplacement.Text="Unsaved"; GlossaryClick("TranslationCorrectionCancelButton");
        Assert(glossaryList.Items[1] is TranslationCorrectionEntry { Replacement:"Драконорождённый" } && glossarySource.Text=="","Cancelling an edit preserves saved list entry");
        GlossaryRowAction("DeleteTranslationCorrection",(TranslationCorrectionEntry)glossaryList.Items[1]);
        Assert(glossaryList.Items.Count==1,"Row delete removes a glossary entry");
        glossarySource.Text="Dragonborn"; glossaryReplacement.Text="Довакин";
        GlossaryClick("TranslationCorrectionAddButton");
        content.UpdateLayout();
        var glossaryImage=new System.Windows.Media.Imaging.RenderTargetBitmap(previewWidth,previewHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        glossaryImage.Render(content);
        var glossaryEncoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); glossaryEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(glossaryImage));
        using (var glossaryOutput=File.Create(Path.Combine(directory,"translation-correction-preview.png"))) glossaryEncoder.Save(glossaryOutput);
        var corrections=(System.Windows.Controls.CheckBox)window.FindName("OcrCorrectionsEnabledBox");
        var ocrList=(System.Windows.Controls.ListBox)window.FindName("OcrCorrectionsList");
        var ocrSource=(System.Windows.Controls.TextBox)window.FindName("OcrSourceBox");
        var ocrReplacement=(System.Windows.Controls.TextBox)window.FindName("OcrReplacementBox");
        var ocrIgnoreCase=(System.Windows.Controls.CheckBox)window.FindName("OcrIgnoreCaseBox");
        void OcrClick(string name)=>((System.Windows.Controls.Button)window.FindName(name)).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        void OcrRowAction(string method,OcrCorrectionEntry entry)=>typeof(MainWindow).GetMethod(method,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,new object[] { new System.Windows.Controls.Button { Tag=entry },new System.Windows.RoutedEventArgs() });
        tabs.SelectedIndex=1; content.UpdateLayout();
        Assert(((System.Windows.Controls.TabItem)tabs.SelectedItem).Header.ToString()==UiText.T("tab.textCorrection"),"Corrections have a dedicated tab");
        Assert(corrections.IsChecked==false && !ocrSource.IsEnabled && ocrList.Items.Count==1 && ocrList.Items[0] is OcrCorrectionEntry { Source:"/",Replacement:"l",IgnoreCase:false },"OCR editor imports the default italic l rule with case sensitivity");
        corrections.IsChecked=true; content.UpdateLayout();
        ocrSource.Text="rn"; ocrReplacement.Text="m"; ocrIgnoreCase.IsChecked=true;
        OcrClick("OcrCorrectionAddButton");
        Assert(ocrList.Items.Count==2 && ocrList.Items[1] is OcrCorrectionEntry { Source:"rn",Replacement:"m",IgnoreCase:true },"OCR form adds replacements with individual case settings");
        OcrRowAction("EditOcrCorrection",(OcrCorrectionEntry)ocrList.Items[1]);
        ocrReplacement.Text="M"; OcrClick("OcrCorrectionAddButton");
        Assert(ocrList.Items.Count==2 && ocrList.Items[1] is OcrCorrectionEntry { Replacement:"M" },"OCR rule edits update existing entry");
        OcrRowAction("EditOcrCorrection",(OcrCorrectionEntry)ocrList.Items[1]);
        ocrReplacement.Text="Unsaved"; OcrClick("OcrCorrectionCancelButton");
        Assert(ocrList.Items[1] is OcrCorrectionEntry { Replacement:"M" },"Cancelling OCR rule edit preserves previous value");
        ocrSource.Text="RN"; ocrReplacement.Text="duplicate";
        OcrClick("OcrCorrectionAddButton");
        Assert(ocrList.Items.Count==2 && !string.IsNullOrEmpty(((System.Windows.Controls.TextBlock)window.FindName("OcrCorrectionError")).Text),"OCR duplicate rules display inline validation");
        ocrSource.Text="noise"; ocrReplacement.Text="";
        OcrClick("OcrCorrectionAddButton");
        Assert(ocrList.Items.Count==3 && ocrList.Items[2] is OcrCorrectionEntry { Replacement:"" },"OCR editor supports deletion through an empty replacement");
        OcrRowAction("DeleteOcrCorrection",(OcrCorrectionEntry)ocrList.Items[2]);
        var correctionSettings=(AppSettings)typeof(MainWindow).GetMethod("ReadControls",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null)!;
        Assert(correctionSettings.OcrCorrectionsEnabled && correctionSettings.OcrCorrectionEntries is { Count:2 } && correctionSettings.OcrCorrectionRules=="","OCR list deletion and settings round-trip preserve typed rules");
        content.UpdateLayout();
        var correctionImage=new System.Windows.Media.Imaging.RenderTargetBitmap(previewWidth,previewHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        correctionImage.Render(content);
        var correctionEncoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); correctionEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(correctionImage));
        using (var correctionOutput=File.Create(Path.Combine(directory,"text-correction-preview.png"))) correctionEncoder.Save(correctionOutput);
        var helpCapture=(System.Windows.Controls.TextBlock)window.FindName("HelpCaptureText");
        var captureField=typeof(MainWindow).GetField("captureKey",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        var updateHotkeys=typeof(MainWindow).GetMethod("UpdateHotkeyFields",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        captureField.SetValue(window,new Hotkey(3,0x54)); updateHotkeys.Invoke(window,null);
        Assert(helpCapture.Text==UiText.T("help.capture","Ctrl + Alt + T"),"Help follows edited capture shortcut");
        captureField.SetValue(window,null); updateHotkeys.Invoke(window,null);
        Assert(helpCapture.Text==UiText.T("help.captureUnassigned"),"Help explains how to assign a missing shortcut");
        captureField.SetValue(window,new Hotkey(3,0x52)); updateHotkeys.Invoke(window,null);
        tabs.SelectedIndex=tabs.Items.Count-1;
        content.Measure(new System.Windows.Size(previewWidth,previewHeight));
        content.Arrange(new System.Windows.Rect(0,0,previewWidth,previewHeight)); content.UpdateLayout();
        var helpImage=new System.Windows.Media.Imaging.RenderTargetBitmap(previewWidth,previewHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        helpImage.Render(content);
        var helpEncoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); helpEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(helpImage));
        using (var helpOutput=File.Create(Path.Combine(directory,"help-preview.png"))) helpEncoder.Save(helpOutput);
        var cacheSwitch=(System.Windows.Controls.CheckBox)window.FindName("CacheEnabledBox");
        cacheSwitch.IsChecked=true;
        Assert(((AppSettings)typeof(MainWindow).GetMethod("ReadControls",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null)!).CacheEnabled,"Cache switch is included in saved settings");
        var devices=(System.Windows.Controls.ComboBox)window.FindName("AudioDeviceBox");
        string selectedDevice=((AudioOutputChoice)devices.SelectedItem).Id;
        var uiLanguage=(System.Windows.Controls.ComboBox)window.FindName("UiLanguageBox");
        uiLanguage.SelectedItem=((System.Collections.Generic.IEnumerable<UiLanguageChoice>)uiLanguage.ItemsSource).First(l=>l.Id=="en");
        Assert(((System.Windows.Controls.TabItem)tabs.Items[0]).Header.ToString()=="Recognition" && ((System.Windows.Controls.TabItem)tabs.Items[1]).Header.ToString()=="Text correction" && ((System.Windows.Controls.TabItem)tabs.Items[11]).Header.ToString()=="Application","English localization updates tab headers immediately");
        Assert(((System.Windows.Controls.TabItem)tabs.Items[12]).Header.ToString()=="How to use" && helpCapture.Text==UiText.T("help.capture","Ctrl + Alt + R"),"Help header and shortcut instructions follow language changes");
        Assert(((AudioOutputChoice)devices.SelectedItem).Id==selectedDevice && voiceBox.Text=="custom-ui-voice","Changing UI language preserves device and provider settings");
        var englishSettings=(AppSettings)typeof(MainWindow).GetMethod("ReadControls",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null)!;
        Assert(englishSettings.UiLanguage=="en" && englishSettings.AudioOutputDeviceId==selectedDevice,"Interface language and playback device are included in saved settings");
        var llmBox=(System.Windows.Controls.ComboBox)window.FindName("TranslationProviderBox");
        Assert(llmBox.SelectedItem is TranslationProviderChoice { Id:"ollama" },"Translation UI defaults to Ollama");
        llmBox.SelectedIndex=1;
        Assert(llmBox.Items[0] is TranslationProviderChoice { Id:"ollama" },"Ollama is first in translation providers");
        var llmModel=(System.Windows.Controls.ComboBox)window.FindName("TranslationModelBox");
        var llmKey=(System.Windows.Controls.PasswordBox)window.FindName("TranslationKeyBox");
        llmModel.Text="lm-model"; llmKey.Password="llm-test-key";
        llmBox.SelectedIndex=0; llmModel.Text="ollama-model";
        Assert(((System.Windows.Controls.TextBox)window.FindName("TranslationUrlBox")).Text=="http://127.0.0.1:11434","Ollama selection supplies its native base URL");
        llmBox.SelectedIndex=1;
        Assert(llmModel.Text=="lm-model" && llmKey.Password=="llm-test-key","Translation provider switching preserves model and key separately");
        ((System.Windows.Controls.CheckBox)window.FindName("TranslateEnabledBox")).IsChecked=true;
        var translatedSettings=(AppSettings)typeof(MainWindow).GetMethod("ReadControls",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null)!;
        Assert(translatedSettings.Translation.Enabled && translatedSettings.Translation.Selected.Model=="lm-model" && SecretProtection.Unprotect(translatedSettings.Translation.Selected.ApiKeyProtected)=="llm-test-key" && translatedSettings.Translation.Providers["ollama"].Model=="ollama-model","Translation UI saves provider profiles with encrypted keys");
        Assert(tabs.Items.Cast<System.Windows.Controls.TabItem>().Select(t=>t.Header.ToString()).SequenceEqual(new[] { "Recognition","Text correction","Translation","Translation correction","Speech","Checks","Cache","Queue","Overlay","Controls","Ollama","Application","How to use" }),"Tabs follow pipeline order with help at the end");
        Assert(((System.Windows.Controls.ComboBox)window.FindName("OcrProviderBox")).SelectedItem is OcrProviderChoice { Id:"windows" },"Recognition defaults to Windows OCR");
        Assert(new[] { "GenderEnabledBox","GenderPreview","AddFemaleRoleButton","AddMaleRoleButton" }.All(name=>window.FindName(name) is null),"Gender detection and gender-specific role controls are removed");
        var roleList=(System.Windows.Controls.ListBox)window.FindName("SpeechRolesBox");
        var rolePanel=(System.Windows.Controls.StackPanel)window.FindName("RoleVoicesPanel");
        var rate=(System.Windows.Controls.Slider)window.FindName("RateSlider");
        var endpoint=(System.Windows.Controls.TextBox)window.FindName("EndpointBox");
        var ttsModel=(System.Windows.Controls.TextBox)window.FindName("ModelBox");
        var ttsKey=(System.Windows.Controls.PasswordBox)window.FindName("ApiKeyBox");
        void Click(string name)=>((System.Windows.Controls.Button)window.FindName(name)).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        void SelectRole(SpeechProfile profile)=>roleList.SelectedItem=roleList.Items.Cast<SpeechRoleChoice>().First(r=>r.Profile==profile);
        AppSettings ReadUi()=>(AppSettings)typeof(MainWindow).GetMethod("ReadControls",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null)!;
        Assert(rolePanel.Visibility==System.Windows.Visibility.Visible && roleList.Items.Count==1,"Default and optional fallback are accessible");
        var volume=(System.Windows.Controls.Slider)window.FindName("VolumeSlider");
        volume.Value=73;
        Click("AddFallbackRoleButton"); volume.Value=31;
        voiceBox.Text="fallback-ui";
        var fallbackSettings=ReadUi();
        Assert(fallbackSettings.VolumePercent==73 && fallbackSettings.FallbackRole is { VolumePercent:31 },"UI keeps fallback and primary volume independent");
        SelectRole(SpeechProfile.Default);
        Assert(volume.Value==73,"Switching profiles restores voice volume");
        roleList.SelectedItem=roleList.Items.Cast<SpeechRoleChoice>().First(r=>r.Profile!=SpeechProfile.Default);
        Click("RemoveRoleButton");
        Assert(ReadUi().FallbackRole is null,"Removing fallback disables it");
        ((System.Windows.Controls.CheckBox)window.FindName("ExclusiveHotkeysBox")).IsChecked=false;
        Assert(!ReadUi().ExclusiveHotkeys,"Controls save optional exclusive interception");
        var exclusiveBox=(System.Windows.Controls.CheckBox)window.FindName("ExclusiveHotkeysBox");
        var modePanel=(System.Windows.Controls.StackPanel)window.FindName("HotkeyModePanel");
        var exclusiveHint=(System.Windows.Controls.TextBlock)window.FindName("ExclusiveHotkeyModeHint");
        var hotkeyMode=(System.Windows.Controls.ComboBox)window.FindName("HotkeyModeBox");
        hotkeyMode.SelectedItem=hotkeyMode.Items.Cast<HotkeyModeChoice>().First(m=>m.Mode==HotkeyMode.Registered);
        content.UpdateLayout();
        Assert(modePanel.Visibility==System.Windows.Visibility.Visible && exclusiveHint.Visibility==System.Windows.Visibility.Collapsed,"Non-exclusive interception exposes the selected detection mode");
        exclusiveBox.IsChecked=true; content.UpdateLayout();
        Assert(modePanel.Visibility==System.Windows.Visibility.Collapsed && exclusiveHint.Visibility==System.Windows.Visibility.Visible,"Exclusive interception replaces redundant mode selection with the actual hook behavior");
        Assert(ReadUi().ExclusiveHotkeys,"Exclusive interception still saves when mode selection is hidden");
        exclusiveBox.IsChecked=false; content.UpdateLayout();
        Assert(modePanel.Visibility==System.Windows.Visibility.Visible && hotkeyMode.SelectedItem is HotkeyModeChoice { Mode:HotkeyMode.Registered } && ReadUi().HotkeyMode==HotkeyMode.Registered,"Disabling exclusive interception restores the previously selected mode");

        ((System.Windows.Controls.ListBox)window.FindName("CacheList")).ItemsSource=new[]
        {
            new CachedReadingInfo(new string('A',64),"Я вернулась домой. В окне ещё горел свет.","I came home. The light was still on in the window.",DateTimeOffset.Now,245760),
            new CachedReadingInfo(new string('B',64),"A cached reading can be replayed without translation or speech generation.","Сохранённую озвучку можно воспроизвести без перевода и синтеза речи.",DateTimeOffset.Now.AddMinutes(-5),123456)
        };
        ((System.Windows.Controls.ListBox)window.FindName("CacheList")).SelectedIndex=0;
        ((System.Windows.Controls.ListBox)window.FindName("QueueList")).ItemsSource=new[]
        {
            new { Heading="#1 · 14:35:10 · Playing from cache…",Text="Я вернулась домой. В окне ещё горел свет." },
            new { Heading="#2 · 14:35:12 · Waiting",Text="The next recognized passage waits for the current audio to finish." }
        };
        var ollamaModels=(System.Windows.Controls.ItemsControl)window.FindName("OllamaModelsList");
        Assert(ollamaModels.Items.Count==8 && ollamaModels.Items.Cast<OllamaModelRow>().All(r=>!r.CanUse && !r.CanDelete),"Ollama model cards offer no use or removal before verification");
        typeof(MainWindow).GetField("ollamaState",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,
            new OllamaState(null,"test",new[] { "translategemma:4b","custom/russian:latest" }));
        typeof(MainWindow).GetField("ollamaHardware",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,new OllamaHardware(16,8,8,"Test GPU","Test CPU"));
        typeof(MainWindow).GetMethod("UpdateOllamaHardware",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null);
        typeof(MainWindow).GetMethod("ShowOllamaState",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null);
        Assert(ollamaModels.Items.Count==9 && ollamaModels.Items.Cast<OllamaModelRow>().Single(r=>r.Tag=="custom/russian:latest") is { Installed:true,CanUse:true,CanDelete:true,CatalogModel:false },
            "Installed models outside the catalog appear with their management actions");
        var modelSearch=(System.Windows.Controls.TextBox)window.FindName("OllamaSearchBox");
        var filterMenu=((System.Windows.Controls.Button)window.FindName("OllamaFilterButton")).ContextMenu;
        var sortMenu=((System.Windows.Controls.Button)window.FindName("OllamaSortButton")).ContextMenu;
        modelSearch.Text="QwEn";
        Assert(ollamaModels.Items.Count==3,"Typing in model search immediately narrows the visible cards");
        var downloadedFilter=filterMenu.Items.OfType<System.Windows.Controls.MenuItem>().First(i=>i.Tag as string=="Downloaded");
        downloadedFilter.IsChecked=true; downloadedFilter.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Assert(ollamaModels.Items.Count==0 && ((System.Windows.Controls.TextBlock)window.FindName("OllamaModelsEmpty")).Visibility==System.Windows.Visibility.Visible,
            "Checked filter tags combine with search and display an empty result message");
        modelSearch.Clear();
        Assert(ollamaModels.Items.Count==2,"Clearing search preserves the active downloaded filter");
        filterMenu.Items.OfType<System.Windows.Controls.MenuItem>().Last().RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        var descending=sortMenu.Items.OfType<System.Windows.Controls.MenuItem>().First(i=>i.Tag as string=="SizeDescending");
        descending.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Assert(ollamaModels.Items.Cast<OllamaModelRow>().First().Tag=="translategemma:27b" && sortMenu.Items.OfType<System.Windows.Controls.MenuItem>().Count(i=>i.IsChecked)==1,
            "Sorting menu applies numeric order and keeps exactly one checked choice");
        sortMenu.Items.OfType<System.Windows.Controls.MenuItem>().First().RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        void ClickModelUse(string tag)
        {
            tabs.SelectedItem=window.FindName("OllamaTab"); content.UpdateLayout();
            var row=ollamaModels.Items.Cast<OllamaModelRow>().Single(r=>r.Tag==tag);
            var presenter=(System.Windows.Controls.ContentPresenter)ollamaModels.ItemContainerGenerator.ContainerFromItem(row);
            var button=(System.Windows.Controls.Button)presenter.ContentTemplate.FindName("RowUseButton",presenter);
            Assert(button.IsEnabled,"Installed model row exposes an enabled translation action: "+tag);
            button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }
        tabs.SelectedItem=window.FindName("OllamaTab"); content.UpdateLayout();
        var oversized=ollamaModels.Items.Cast<OllamaModelRow>().Single(r=>r.Tag=="translategemma:27b");
        var oversizedPresenter=(System.Windows.Controls.ContentPresenter)ollamaModels.ItemContainerGenerator.ContainerFromItem(oversized);
        foreach (string requirement in new[] { "RamRequirementText","VramRequirementText" })
        {
            var text=(System.Windows.Controls.TextBlock)oversizedPresenter.ContentTemplate.FindName(requirement,oversizedPresenter);
            Assert(((System.Windows.Media.SolidColorBrush)text.Foreground).Color==((System.Windows.Media.SolidColorBrush)app.FindResource("FluentError")).Color,
                "Exceeding a hardware requirement uses the deficit color: "+requirement);
        }
        ClickModelUse("custom/russian:latest");
        Assert(ReadUi().Translation.Selected.Model=="custom/russian:latest","A model outside the catalog can be applied from its own card");
        typeof(MainWindow).GetField("ollamaBusy",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,true);
        typeof(MainWindow).GetMethod("RefreshOllamaModels",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null);
        Assert(ollamaModels.Items.Cast<OllamaModelRow>().All(r=>!r.CanUse && !r.CanDelete && !r.CanDownload),"Model actions are disabled during an Ollama operation");
        typeof(MainWindow).GetField("ollamaBusy",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,false);
        typeof(MainWindow).GetField("ollamaDrivers",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,
            new OllamaDriverReport(new[] { new OllamaDriver("NVIDIA Test GPU","31.0.15.3623","2023-06-14") },Array.Empty<string>(),false));
        typeof(MainWindow).GetMethod("UpdateOllamaHardware",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null);
        Assert(((System.Windows.Controls.TextBlock)window.FindName("OllamaDriverWarning")).Visibility==System.Windows.Visibility.Visible,
            "An old NVIDIA driver warning is visible even while driver details are collapsed");
        typeof(MainWindow).GetField("ollamaDrivers",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,
            new OllamaDriverReport(new[] { new OllamaDriver("NVIDIA Test GPU","32.0.15.6094","2024-08-20") },Array.Empty<string>(),false));
        typeof(MainWindow).GetMethod("UpdateOllamaHardware",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null);
        Assert(((System.Windows.Controls.TextBlock)window.FindName("OllamaDriverWarning")).Visibility==System.Windows.Visibility.Collapsed,
            "Meeting the baseline clears the old driver warning without claiming driver currency");
        Assert(((System.Windows.Controls.Button)window.FindName("OllamaNvidiaButton")).Visibility==System.Windows.Visibility.Visible &&
            ((System.Windows.Controls.Button)window.FindName("OllamaAmdButton")).Visibility==System.Windows.Visibility.Collapsed &&
            ((System.Windows.Controls.Button)window.FindName("OllamaIntelButton")).Visibility==System.Windows.Visibility.Collapsed,
            "Only the detected GPU vendor website button is shown");
        typeof(MainWindow).GetField("ollamaDrivers",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,
            new OllamaDriverReport(new[] { new OllamaDriver("AMD Radeon","unknown",""),new OllamaDriver("Intel Arc","unknown","") },Array.Empty<string>(),false));
        typeof(MainWindow).GetMethod("UpdateOllamaHardware",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null);
        Assert(((System.Windows.Controls.Button)window.FindName("OllamaNvidiaButton")).Visibility==System.Windows.Visibility.Collapsed &&
            ((System.Windows.Controls.Button)window.FindName("OllamaAmdButton")).Visibility==System.Windows.Visibility.Visible &&
            ((System.Windows.Controls.Button)window.FindName("OllamaIntelButton")).Visibility==System.Windows.Visibility.Visible,
            "Multi-vendor systems show each detected vendor without unrelated buttons");
        ClickModelUse("translategemma:4b");
        var ollamaSettings=ReadUi();
        Assert(ollamaSettings.Translation.Enabled && ollamaSettings.Translation.Provider=="ollama" && ollamaSettings.Translation.Selected.Model=="translategemma:4b" && ollamaSettings.Translation.Selected.BaseUrl==OllamaSetupClient.BaseUrl,"A model card configures the local Ollama provider and enables translation");
        Assert(ollamaSettings.Translation.Providers["lmstudio"].Model=="lm-model","Model selection preserves other provider profiles");
        Assert(ollamaModels.Items.Cast<OllamaModelRow>().Single(r=>r.Tag=="translategemma:4b").Status==UiText.T("ollama.modelInUse"),"The configured translation model is marked as in use");
        for (int tab=0; tab<tabs.Items.Count; tab++)
        {
            tabs.SelectedIndex=tab; content.UpdateLayout();
            var host=(System.Windows.Controls.ContentPresenter)tabs.Template.FindName("PART_SelectedContentHost",tabs);
            var page=(System.Windows.FrameworkElement)((System.Windows.Controls.TabItem)tabs.Items[tab]).Content;
            Assert(Math.Abs(page.ActualWidth+page.Margin.Left+page.Margin.Right-host.ActualWidth)<1,
                $"Tab {tab} content fills available width");
            var preview=new System.Windows.Media.Imaging.RenderTargetBitmap(previewWidth,previewHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32); preview.Render(content);
            var png=new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
            using var file=File.Create(Path.Combine(directory,$"settings-en-tab-{tab}.png")); png.Save(file);
        }
        string currentVoice=voiceBox.Text,currentEndpoint=endpoint.Text,targetLocale=targetLanguages.Text;
        string sourceLocale=((LanguageChoice)ocrLanguages.SelectedItem).Tag;
        foreach (var locale in UiText.Languages.Where(l=>l.Id is not ("ru" or "en")))
        {
            uiLanguage.SelectedItem=locale;
            Assert(UiText.Language==locale.Id && ReadUi().UiLanguage==locale.Id,"UI language selection is saved: "+locale.Id);
            Assert(ReadUi().OcrCorrectionEntries is { Count:2 } && ocrList.Items[1] is OcrCorrectionEntry { IgnoreCase:true,Replacement:"M" },"UI language preserves OCR correction list: "+locale.Id);
            Assert(ReadUi().Translation is { CorrectionsEnabled:true,CorrectionEntries.Count:2 } && glossaryList.Items[0] is TranslationCorrectionEntry { Source:"Skyrim",Preserve:true },"UI language changes preserve translation glossary: "+locale.Id);
            Assert(((System.Windows.Controls.TabItem)tabs.Items[0]).Header.ToString()==UiText.Get("tab.recognition",locale.Id)
                && window.Title==UiText.Get("app.title",locale.Id),"Localized title and tabs update immediately: "+locale.Id);
            Assert(voiceBox.Text==currentVoice && endpoint.Text==currentEndpoint && targetLanguages.Text==targetLocale
                && ((LanguageChoice)ocrLanguages.SelectedItem).Tag==sourceLocale && ((AudioOutputChoice)devices.SelectedItem).Id==selectedDevice,
                "UI language preserves voice, server, device, OCR and translation settings: "+locale.Id);
            Assert(ocrLanguages.Items.Cast<LanguageChoice>().First(l=>l.Tag=="ja-JP").Name==UiText.Get("language.ja",locale.Id),
                "Language choices are localized: "+locale.Id);
            Assert(((System.Windows.Controls.TextBlock)window.FindName("OllamaProgressText")).Text==UiText.T("ollama.applied","translategemma:4b"),
                "Existing Ollama progress is localized: "+locale.Id);
            for (int tab=0;tab<tabs.Items.Count;tab++)
            {
                tabs.SelectedIndex=tab; content.UpdateLayout();
                var host=(System.Windows.Controls.ContentPresenter)tabs.Template.FindName("PART_SelectedContentHost",tabs);
                var page=(System.Windows.FrameworkElement)((System.Windows.Controls.TabItem)tabs.Items[tab]).Content;
                if (tab==4)
                {
                    System.Collections.Generic.IEnumerable<System.Windows.DependencyObject> Visuals(System.Windows.DependencyObject node)
                    {
                        yield return node;
                        for (int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);i++)
                            foreach (var child in Visuals(System.Windows.Media.VisualTreeHelper.GetChild(node,i))) yield return child;
                    }
                    Assert(Visuals(providerBox).OfType<System.Windows.Controls.TextBlock>().Any(t=>t.Text==UiText.T("provider.openai-compatible")),
                        "Selected provider display is localized: "+locale.Id);
                }
                Assert(Math.Abs(page.ActualWidth+page.Margin.Left+page.Margin.Right-host.ActualWidth)<1,
                    $"Localized page fits available width: {locale.Id}/{tab}");
                var preview=new System.Windows.Media.Imaging.RenderTargetBitmap(previewWidth,previewHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
                preview.Render(content);
                var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
                using var file=File.Create(Path.Combine(directory,$"settings-{locale.Id}-tab-{tab}.png"));png.Save(file);
            }
        }
        uiLanguage.SelectedItem=((System.Collections.Generic.IEnumerable<UiLanguageChoice>)uiLanguage.ItemsSource).First(l=>l.Id=="ru");
        tabs.SelectedItem=window.FindName("OllamaTab"); content.UpdateLayout();
        var ollamaScroll=(System.Windows.Controls.ScrollViewer)((System.Windows.Controls.TabItem)window.FindName("OllamaTab")).Content;
        foreach (int offset in new[] { 0,600,1300 })
        {
            ollamaScroll.ScrollToVerticalOffset(offset); content.UpdateLayout();
            var ollamaPreview=new System.Windows.Media.Imaging.RenderTargetBitmap(previewWidth,previewHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            ollamaPreview.Render(content);
            var ollamaPng=new System.Windows.Media.Imaging.PngBitmapEncoder(); ollamaPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(ollamaPreview));
            using var ollamaFile=File.Create(Path.Combine(directory,$"ollama-layout-ru-{offset}.png")); ollamaPng.Save(ollamaFile);
        }
        var driversExpander=(System.Windows.Controls.Expander)window.FindName("OllamaDriversExpander");
        driversExpander.IsExpanded=true;
        foreach (var theme in new[] { AppTheme.Light,AppTheme.Dark })
        {
            app.Themes.Apply(theme);
            content.Measure(new System.Windows.Size(920,previewHeight));
            content.Arrange(new System.Windows.Rect(0,0,920,previewHeight)); content.UpdateLayout();
            ollamaScroll.ScrollToVerticalOffset(155); content.UpdateLayout();
            var driverPreview=new System.Windows.Media.Imaging.RenderTargetBitmap(920,previewHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            driverPreview.Render(content);
            var driverPng=new System.Windows.Media.Imaging.PngBitmapEncoder(); driverPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(driverPreview));
            using var driverFile=File.Create(Path.Combine(directory,$"ollama-drivers-ru-{theme.ToString().ToLowerInvariant()}.png")); driverPng.Save(driverFile);
            foreach (string buttonName in new[] { "OllamaFilterButton","OllamaSortButton" })
            {
                var menuButton=(System.Windows.Controls.Button)window.FindName(buttonName);
                var menu=menuButton.ContextMenu;
                // The settings preview is never shown. Give native popups a loaded
                // off-screen placement target without triggering settings Loaded tasks.
                var target=new System.Windows.Controls.Button { Content=buttonName,ContextMenu=menu };
                var popupHost=new System.Windows.Window { Content=target,Width=240,Height=80,Left=-10000,Top=-10000,
                    WindowStartupLocation=System.Windows.WindowStartupLocation.Manual,ShowActivated=false,ShowInTaskbar=false };
                popupHost.Show(); popupHost.UpdateLayout();
                bool staysOpen=menu.StaysOpen; menu.StaysOpen=true;
                try
                {
                    typeof(MainWindow).GetMethod("OpenOllamaModelMenu",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(window,new object[] { target,new System.Windows.RoutedEventArgs() });
                    menu.Dispatcher.Invoke(()=> { },System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    menu.UpdateLayout();
                    Assert(menu.IsOpen && menu.ActualWidth>100 && menu.ActualHeight>50,"Model dropdown opens with readable dimensions: "+buttonName);
                    var menuPreview=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth),(int)Math.Ceiling(menu.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
                    menuPreview.Render(menu);
                    var menuPng=new System.Windows.Media.Imaging.PngBitmapEncoder();menuPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(menuPreview));
                    using var menuFile=File.Create(Path.Combine(directory,$"{buttonName}-{theme}.png"));menuPng.Save(menuFile);
                }
                finally { menu.IsOpen=false; menu.StaysOpen=staysOpen; popupHost.Close(); }
            }
        }
        driversExpander.IsExpanded=false; app.Themes.Apply(AppTheme.System);
        Console.WriteLine("UI rendered to "+Path.Combine(directory,"settings-preview.png"));
        RunFocusLayoutChecks();
        RenderReadingOverlayPreviews();
        window.Close(); app.Shutdown();
    }
    private static void RunCapture()
    {
        var capture=new ScreenCapture();
        foreach (var mode in new[] {CaptureMode.DesktopDuplication,CaptureMode.Gdi})
        {
            using var frame=capture.Capture(mode);
            Assert(frame.Image.Width==frame.Bounds.Width && frame.Image.Height==frame.Bounds.Height && frame.Image.Width>0,"Desktop capture dimensions: "+frame.Backend);
            var source=SelectionWindow.ToBitmapSource(frame.Image);
            var pixels=new byte[frame.Image.Width*frame.Image.Height*4];
            var converted=new System.Windows.Media.Imaging.FormatConvertedBitmap(source,System.Windows.Media.PixelFormats.Bgr32,null,0);
            converted.CopyPixels(pixels,frame.Image.Width*4,0);
            long nonBlack=Enumerable.Range(0,frame.Image.Width*frame.Image.Height).Count(i=>pixels[i*4]>15 || pixels[i*4+1]>15 || pixels[i*4+2]>15);
            Console.WriteLine($"Capture {frame.Backend}: non-black RGB pixels {nonBlack}, WPF source format {source.Format}");
        }
    }
    private static void RunOverlayWindow()
    {
        var bounds=System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var bitmap=new Bitmap(bounds.Width,bounds.Height);
        using (var graphics=Graphics.FromImage(bitmap)) graphics.Clear(Color.Orange);
        using var frame=new CapturedScreen(bitmap,bounds,"Synthetic");
        var window=new SelectionWindow(frame);
        window.Show();
        var timer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(500) };
        var loop=new System.Windows.Threading.DispatcherFrame();
        timer.Tick += (_,_)=>
        {
            timer.Stop();
            var content=(System.Windows.Controls.Grid)window.Content;
            var background=(System.Windows.Controls.Image)content.Children[0];
            Console.WriteLine($"Overlay size: {window.ActualWidth} x {window.ActualHeight}; grid {content.ActualWidth} x {content.ActualHeight}; image {background.ActualWidth} x {background.ActualHeight}; source {background.Source.Width} x {background.Source.Height}");
            Assert(background.ActualWidth>100 && background.ActualHeight>100,"Visible overlay lays out screenshot image");
            foreach (var mode in new[] {CaptureMode.DesktopDuplication,CaptureMode.Gdi})
            {
                using var captured=new ScreenCapture().CaptureMonitor(bounds,mode);
                var center=captured.Image.GetPixel(captured.Image.Width/2,captured.Image.Height/2);
                Console.WriteLine($"Controlled {mode} capture on {bounds}: center={center}");
                Assert(center.R>180 && center.G>110 && center.B<20,"Visible screenshot pixels survive "+mode+" capture");
            }
            window.Close(); loop.Continue=false;
        };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(loop);
    }
    private static void RunFocusWindow()
    {
        var focus=new ForegroundWindowService();
        var original=focus.Capture();
        var loop=new System.Windows.Threading.DispatcherFrame();
        Exception? failure=null;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            var source=new System.Windows.Window { Title="ScreenReader — проверка восстановления",Width=420,Height=160,Content=new System.Windows.Controls.TextBlock { Text="Контрольное окно для проверки возврата фокуса.",Margin=new System.Windows.Thickness(20) } };
            try
            {
                source.Show(); source.Activate();
                await Task.Delay(150);
                var handle=new System.Windows.Interop.WindowInteropHelper(source).Handle;
                // A console launched in the background may lack permission to become
                // foreground. Test restoration of our explicit handle in either case.
                var target=focus.CaptureWindow(handle);
                Assert(target is not null,"Source window identity is captured");
                source.WindowState=System.Windows.WindowState.Minimized;
                await Task.Delay(100);
                Assert(source.WindowState==System.Windows.WindowState.Minimized,"Control window simulates a game minimized by overlay focus");
                using var bitmap=new Bitmap(640,360);
                using (var graphics=Graphics.FromImage(bitmap)) graphics.Clear(Color.SteelBlue);
                using var frame=new CapturedScreen(bitmap,new Rectangle(0,0,640,360),"Synthetic");
                var selection=new SelectionWindow(frame,target); selection.Show();
                await Task.Delay(150); selection.Close();
                await selection.Completion;
                Assert(source.WindowState!=System.Windows.WindowState.Minimized,"Closing selection restores minimized source before completing");
                if (GetForegroundWindow()==handle) Assert(true,"Closing selection returns foreground input to source");
                else Console.WriteLine("INFO: Windows denied foreground activation to the background test process; minimized-window restoration verified.");
                source.Close();
                await target!.RestoreAsync();
                Assert(true,"Closed source window is ignored during restoration");
                var delayed=new DelayedFocusTarget();
                var cancellationWindow=new SelectionWindow(frame,delayed); cancellationWindow.Show(); cancellationWindow.Close();
                Assert(delayed.Called && !cancellationWindow.Completion.IsCompleted,"Cancelled selection waits for focus restoration");
                delayed.Release();
                Assert(await cancellationWindow.Completion is null,"Cancelled selection completes without an OCR region");
            }
            catch (Exception e) { failure=e; }
            finally
            {
                source.Close();
                if (original is not null) await original.RestoreAsync();
                loop.Continue=false;
            }
        });
        System.Windows.Threading.Dispatcher.PushFrame(loop);
        if (failure is not null) throw failure;
    }
    private sealed class DelayedFocusTarget : IWindowFocusTarget
    {
        private readonly TaskCompletionSource completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Called { get; private set; }
        public Task RestoreAsync() { Called=true; return completion.Task; }
        public void Release()=>completion.SetResult();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static async Task RunRetryChecksAsync()
    {
        int opened=0;
        async System.Collections.Generic.IAsyncEnumerable<byte[]> FailsBeforeAudio([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested();
            if (++opened<3) throw new System.Net.WebSockets.WebSocketException();
            yield return new byte[]{1,2,3};
        }
        int received=0;
        await foreach (var bytes in SpeechStreamRetry.Run(FailsBeforeAudio,"test",CancellationToken.None,TimeSpan.Zero)) received+=bytes.Length;
        Assert(opened==3 && received==3,"Transient failure retries before first audio without duplicating bytes");
        opened=0;
        async System.Collections.Generic.IAsyncEnumerable<byte[]> FailsAfterAudio([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            opened++; await Task.Yield(); ct.ThrowIfCancellationRequested();
            yield return new byte[]{1}; throw new System.Net.WebSockets.WebSocketException();
        }
        bool failed=false;
        try { await foreach (var _ in SpeechStreamRetry.Run(FailsAfterAudio,"test",CancellationToken.None,TimeSpan.Zero)) { } }
        catch (SpeechServiceException e) { failed=e.AudioStarted; }
        Assert(opened==1 && failed,"Partial speech is never replayed after stream failure");
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel(); opened=0;
        bool stopped=false;
        try { await foreach (var _ in SpeechStreamRetry.Run(FailsBeforeAudio,"test",cancelled.Token,TimeSpan.Zero)) { } }
        catch (OperationCanceledException) { stopped=true; }
        Assert(stopped && opened==0,"Cancellation does not start or retry a speech request");
    }
    private static void RunOverlay()
    {
        using var bitmap=new Bitmap(640,360,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        // Desktop captures may contain valid RGB with a zero/undefined alpha byte.
        var data=bitmap.LockBits(new Rectangle(0,0,640,360),System.Drawing.Imaging.ImageLockMode.WriteOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rgb=new byte[640*360*4];
        for (int i=0; i<640*360; i++) { rgb[i*4]=50; rgb[i*4+1]=170; rgb[i*4+2]=230; rgb[i*4+3]=0; }
        System.Runtime.InteropServices.Marshal.Copy(rgb,0,data.Scan0,rgb.Length); bitmap.UnlockBits(data);
        using var snapshot=new CapturedScreen(bitmap,new Rectangle(0,0,640,360),"Synthetic");
        var window=new SelectionWindow(snapshot);
        var content=(System.Windows.FrameworkElement)window.Content;
        content.Measure(new System.Windows.Size(640,360)); content.Arrange(new System.Windows.Rect(0,0,640,360)); content.UpdateLayout();
        var image=new System.Windows.Media.Imaging.RenderTargetBitmap(640,360,96,96,System.Windows.Media.PixelFormats.Pbgra32); image.Render(content);
        var pixel=new byte[4]; image.CopyPixels(new System.Windows.Int32Rect(320,180,1,1),pixel,4,0);
        Assert(pixel[2]>180 && pixel[1]>120 && pixel[3]==255,"Selection overlay displays captured RGB even with zero alpha");
        string directory=Path.Combine(Directory.GetCurrentDirectory(),"artifacts"); Directory.CreateDirectory(directory);
        using var output=File.Create(Path.Combine(directory,"selection-preview.png"));
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image)); encoder.Save(output);
        window.Close();
    }
}
