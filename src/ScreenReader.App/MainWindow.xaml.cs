using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenReader.Edge;

namespace ScreenReader.App;

public sealed record LanguageChoice(string Tag,string Name,bool Installed);
public sealed record CaptureChoice(CaptureMode Mode,string Name);
public sealed record HotkeyModeChoice(HotkeyMode Mode,string Name);
public sealed record OcrProviderChoice(string Id,string Name);
public sealed record UiLanguageChoice(string Id,string Name);
public partial class MainWindow : Window
{
    private readonly App owner;
    private Hotkey? captureKey,repeatCaptureKey,toggleKey,cancelCurrentKey,cancelAllKey;
    private readonly AppSettings draft;
    private readonly Dictionary<string,string> selectedVoices=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,List<SpeechVoice>> voiceCache=new(StringComparer.OrdinalIgnoreCase);
    private string providerId;
    private CancellationTokenSource? voiceLoad;
    private List<SpeechVoice> voices = LanguageCatalog.EdgeVoices.ToList();
    private bool initialized;

    public MainWindow(App owner)
    {
        this.owner=owner; draft=owner.Settings; captureKey=draft.CaptureHotkey; repeatCaptureKey=draft.RepeatCaptureHotkey; toggleKey=draft.ToggleHotkey; cancelCurrentKey=draft.CancelCurrentHotkey; cancelAllKey=draft.CancelAllHotkey; providerId=draft.SpeechProviderId;
        if (draft.ProviderVoices is not null) foreach (var voice in draft.ProviderVoices) selectedVoices[voice.Key]=voice.Value;
        selectedVoices.TryAdd("edge",draft.Voice); selectedVoices.TryAdd("windows",draft.WindowsVoice); selectedVoices.TryAdd("openai-compatible",draft.OpenAiVoice);
        voiceCache["edge"]=voices;
        UiText.ApplyResources();
        InitializeComponent();
        InitializeCacheAndQueue();
        ProviderBox.ItemsSource=owner.Providers.Definitions;
        ProviderBox.SelectedItem=owner.Providers.Definitions.FirstOrDefault(p=>p.Id==providerId) ?? owner.Providers.Definitions.First();
        providerId=((SpeechProviderDefinition)ProviderBox.SelectedItem).Id;
        OcrProviderBox.ItemsSource=new[] { new OcrProviderChoice("windows",UiText.T("ocr.windows")) }; OcrProviderBox.SelectedIndex=0;
        LanguageBox.ItemsSource=LanguageCatalog.RecognitionChoices(owner.Ocr.Languages,owner.Settings.Language);
        LanguageBox.SelectedItem=((IEnumerable<LanguageChoice>)LanguageBox.ItemsSource).First(l => l.Tag.Equals(owner.Settings.Language,StringComparison.OrdinalIgnoreCase));
        UpdateHotkeyFields();
        HotkeyModeBox.ItemsSource=new[] { new HotkeyModeChoice(HotkeyMode.GameHook,UiText.T("mode.game")),new HotkeyModeChoice(HotkeyMode.Registered,UiText.T("mode.registered")) };
        HotkeyModeBox.SelectedItem=((IEnumerable<HotkeyModeChoice>)HotkeyModeBox.ItemsSource).First(m=>m.Mode==draft.HotkeyMode);
        EndpointBox.Text=draft.OpenAiEndpoint; ModelBox.Text=draft.OpenAiModel;
        try { ApiKeyBox.Password=SecretProtection.Unprotect(draft.OpenAiApiKeyProtected); }
        catch (Exception e) { AppLog.Write("Не удалось прочитать сохранённый ключ TTS.",e); SetStatus(UiText.T("status.keyUnavailable")); }
        AudioFormatBox.ItemsSource=new[] { "MP3","WAV (PCM)" }; AudioFormatBox.SelectedIndex=(int)draft.OpenAiFormat;
        CaptureModeBox.ItemsSource=new[] { new CaptureChoice(CaptureMode.Auto,UiText.T("capture.auto")),new CaptureChoice(CaptureMode.DesktopDuplication,UiText.T("capture.dxgi")),new CaptureChoice(CaptureMode.Gdi,UiText.T("capture.gdi")) };
        CaptureModeBox.SelectedItem=((IEnumerable<CaptureChoice>)CaptureModeBox.ItemsSource).First(c => c.Mode==owner.Settings.CaptureMode);
        CloseToTrayBox.IsChecked=owner.Settings.MinimizeToTray; StartInTrayBox.IsChecked=owner.Settings.StartInTray; RateSlider.Value=owner.Settings.RatePercent;
        RateLabel.Text=$"{owner.Settings.RatePercent:+0;-0;0}%";
        UiLanguageBox.ItemsSource=UiText.Languages;
        UiLanguageBox.SelectedItem=((IEnumerable<UiLanguageChoice>)UiLanguageBox.ItemsSource).First(l=>l.Id==UiText.Language);
        PreviewText.Text=UiText.T("preview.sample");
        VersionText.Text="LazySkald "+typeof(App).Assembly.GetName().Version!.ToString(3);
        LoadAudioDevices(draft.AudioOutputDeviceId);
        InitializeTranslation(); InitializeGender(); InitializeOllama();
        InitializeSpeechRoles();
        initialized=true; ApplyProviderUI(); UpdateLanguageHint();
        CaptureModeBox.SelectionChanged += (_,_)=> { if (initialized) SetStatus(UiText.T("status.captureChanged")); };
        UiText.LanguageChanged+=UpdateLocalization;
        Closed+=(_,_)=>UiText.LanguageChanged-=UpdateLocalization;
        Loaded += async (_,_) => await LoadVoicesAsync();
        Closing += OnClosing;
        foreach (var box in new[] { CaptureKeyBox,RepeatCaptureKeyBox,ToggleKeyBox,CancelCurrentKeyBox,CancelAllKeyBox })
        {
            box.GotKeyboardFocus += (_,_)=>owner.SetHotkeyEditing(true);
            box.LostKeyboardFocus += (_,_)=>owner.SetHotkeyEditing(false);
        }
        IsVisibleChanged += (_,_)=> { if (!IsVisible) owner.SetHotkeyEditing(false); };
        StateChanged += (_,_) => { if (WindowState==WindowState.Minimized) Hide(); };
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if (owner.IsExiting) return;
        e.Cancel=true;
        if (owner.Settings.MinimizeToTray) Hide(); else owner.Quit();
    }
    public void SetStatus(string value) => StatusText.Text=value;
    public void SetRecognizedText(string value) { PreviewText.Text=value; TranslationPreview.Text=""; }
    public void SetTranslatedText(string value) => TranslationPreview.Text=value;
    private AppSettings ReadControls()
    {
        var language=(LanguageChoice?)LanguageBox.SelectedItem ?? throw new InvalidOperationException(UiText.T("error.language"));
        SaveSpeechEditor();
        foreach (var pair in speechRoles)
        {
            // Dormant gender roles remain editable without blocking default speech.
            if (pair.Key!=SpeakerGender.Unknown && GenderEnabledBox.IsChecked!=true) continue;
            try { ValidateSpeechRole(pair.Value); }
            catch (Exception e) { throw new InvalidOperationException(UiText.T("speech.roleError",RoleName(pair.Key),e.Message),e); }
        }
        var settings=speechRoles[SpeakerGender.Unknown].ApplyTo(draft) with
        {
            CacheEnabled=CacheEnabledBox.IsChecked==true,
            Language=language.Tag,CaptureHotkey=captureKey,RepeatCaptureHotkey=repeatCaptureKey,ToggleHotkey=toggleKey,
            CancelCurrentHotkey=cancelCurrentKey,CancelAllHotkey=cancelAllKey,
            HotkeyMode=((HotkeyModeChoice)HotkeyModeBox.SelectedItem).Mode,
            CaptureMode=((CaptureChoice)CaptureModeBox.SelectedItem).Mode,
            MinimizeToTray=CloseToTrayBox.IsChecked==true,StartInTray=StartInTrayBox.IsChecked==true,
            OcrProviderId=((OcrProviderChoice)OcrProviderBox.SelectedItem).Id,
            Genderization=ReadGenderControls(),Translation=ReadTranslationControls(),UiLanguage=UiText.Language,
            FemaleRole=speechRoles.GetValueOrDefault(SpeakerGender.Female),
            MaleRole=speechRoles.GetValueOrDefault(SpeakerGender.Male),
            FemaleProviderVoices=new(),MaleProviderVoices=new()
        };
        return settings;
    }
    private void SaveSettings(object sender,RoutedEventArgs e)
    {
        try { owner.ApplySettings(ReadControls()); SetStatus(UiText.T("status.saved",captureKey?.ToString() ?? UiText.T("hotkey.unassigned"))); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private async void PreviewSpeech(object sender,RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(PreviewText.Text)) { SetStatus(UiText.T("error.previewEmpty")); return; }
            await owner.Reader.ReadTextAsync(PreviewText.Text,ReadControls());
        }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void StopSpeech(object sender,RoutedEventArgs e)=>owner.Reader.Stop();
    private void HideWindow(object sender,RoutedEventArgs e)=>Hide();
    private void OpenLog(object sender,RoutedEventArgs e)
    {
        try { AppLog.Write("Журнал открыт"); Process.Start(new ProcessStartInfo("notepad.exe",AppLog.LogPath) { UseShellExecute=true }); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private async void RefreshVoices(object sender,RoutedEventArgs e)=>await LoadVoicesAsync();
    private async Task LoadVoicesAsync()
    {
        voiceLoad?.Cancel();
        if (owner.Providers.Get(providerId).CustomVoice) { ApplyProviderUI(); return; }
        string requestedProvider=providerId;
        var requestedRole=editingRole;
        var load=CancellationTokenSource.CreateLinkedTokenSource(owner.Lifetime); voiceLoad=load;
        RefreshVoicesButton.IsEnabled=false; VoiceHint.Text=UiText.T("voices.loading");
        try
        {
            RememberVoice(providerId);
            using var lease=owner.Providers.Acquire(draft with { SpeechProviderId=requestedProvider });
            var loaded=(await lease.Provider.GetVoicesAsync(load.Token)).ToList();
            load.Token.ThrowIfCancellationRequested();
            voiceCache[requestedProvider]=loaded;
            if (providerId!=requestedProvider || editingRole!=requestedRole) return;
            voices=loaded; FilterVoices(selectedVoices.GetValueOrDefault(providerId));
            VoiceHint.Text=UiText.T(VoiceBox.SelectedItem is null ? "voices.none" : requestedProvider=="windows" ? "voices.windows" : "voices.loaded");
        }
        catch (OperationCanceledException) { if (!load.IsCancellationRequested) VoiceHint.Text=UiText.T("voices.timeout"); }
        catch (Exception ex) { if (providerId==requestedProvider) VoiceHint.Text=UiText.T("voices.error",ex.Message); AppLog.Write("Не удалось загрузить голоса",ex); }
        finally { if (ReferenceEquals(voiceLoad,load)) { voiceLoad=null; RefreshVoicesButton.IsEnabled=!owner.Providers.Get(providerId).CustomVoice; } load.Dispose(); }
    }
    private async void ProviderChanged(object sender,SelectionChangedEventArgs e)
    {
        if (!initialized || ProviderBox.SelectedItem is not SpeechProviderDefinition definition) return;
        RememberVoice(providerId); providerId=definition.Id;
        ApplyProviderUI(); await LoadVoicesAsync();
    }
    private void RememberVoice(string id)
    {
        if (owner.Providers.Get(id).CustomVoice) selectedVoices[id]=VoiceBox.Text.Trim();
        else if (VoiceBox.SelectedItem is SpeechVoice voice) selectedVoices[id]=voice.ShortName;
    }
    private void ApplyProviderUI()
    {
        bool custom=owner.Providers.Get(providerId).CustomVoice;
        ServerPanel.Visibility=providerId=="openai-compatible" ? Visibility.Visible : Visibility.Collapsed;
        VoiceBox.IsEditable=custom; RefreshVoicesButton.IsEnabled=!custom;
        voices=voiceCache.GetValueOrDefault(providerId) ?? [];
        if (custom)
        {
            VoiceBox.ItemsSource=null; VoiceBox.SelectedItem=null; VoiceBox.Text=selectedVoices.GetValueOrDefault(providerId,"alloy");
            VoiceHint.Text=UiText.T("voices.custom");
        }
        else { FilterVoices(selectedVoices.GetValueOrDefault(providerId)); VoiceHint.Text=UiText.T(providerId=="windows" ? "voices.windows" : "voices.edge"); }
        UpdatePrivacyHint();
    }
    private void LanguageChanged(object sender,SelectionChangedEventArgs e)
    { if (initialized) { RememberVoice(providerId); if (!owner.Providers.Get(providerId).CustomVoice) FilterVoices(null); UpdateLanguageHint(); } }
    private void UpdateLanguageHint()
    {
        if (LanguageBox.SelectedItem is LanguageChoice language)
            OcrHint.Text=UiText.T(language.Installed ? "ocr.installed" : "ocr.missing");
    }
    private void FilterVoices(string? preferred)
    {
        if (LanguageBox.SelectedItem is not LanguageChoice language) return;
        string speechLanguage=TranslateEnabledBox.IsChecked==true ? TranslationTargetBox.Text.Trim() : language.Tag;
        var available=voices.Where(v => v.Locale.Equals(speechLanguage,StringComparison.OrdinalIgnoreCase)).OrderBy(v => v.ShortName).ToArray();
        VoiceBox.ItemsSource=available;
        VoiceBox.SelectedItem=available.FirstOrDefault(v => v.ShortName==preferred) ?? available.FirstOrDefault();
    }
    private void RateChanged(object sender,RoutedPropertyChangedEventArgs<double> e)
    { if (RateLabel is not null) RateLabel.Text=$"{e.NewValue:+0;-0;0}%"; }
    private void RefreshAudioDevices(object sender,RoutedEventArgs e)=>LoadAudioDevices((AudioDeviceBox.SelectedItem as AudioOutputChoice)?.Id ?? draft.AudioOutputDeviceId);
    private void LoadAudioDevices(string? preferred)
    {
        preferred ??="";
        try
        {
            var choices=AudioOutputDevices.List().ToList();
            if (!choices.Any(d=>d.Id==preferred)) choices.Add(new(preferred,UiText.T("audio.missingChoice"),false));
            AudioDeviceBox.ItemsSource=choices; AudioDeviceBox.SelectedItem=choices.First(d=>d.Id==preferred);
        }
        catch (Exception e) { SetStatus(e.Message); AudioDeviceBox.ItemsSource=new[] { new AudioOutputChoice(preferred,UiText.T("audio.missingChoice"),false) }; AudioDeviceBox.SelectedIndex=0; }
    }
    private void UiLanguageChanged(object sender,SelectionChangedEventArgs e)
    { if (initialized && UiLanguageBox.SelectedItem is UiLanguageChoice language) UiText.SetLanguage(language.Id); }
    private void UpdateLocalization()
    {
        RefreshQueue();
        UpdateHotkeyFields();
        RememberVoice(providerId);
        string tag=(LanguageBox.SelectedItem as LanguageChoice)?.Tag ?? draft.Language;
        var mode=(HotkeyModeBox.SelectedItem as HotkeyModeChoice)?.Mode ?? draft.HotkeyMode;
        var capture=(CaptureModeBox.SelectedItem as CaptureChoice)?.Mode ?? draft.CaptureMode;
        initialized=false;
        LanguageBox.ItemsSource=LanguageCatalog.RecognitionChoices(owner.Ocr.Languages,tag);
        LanguageBox.SelectedItem=((IEnumerable<LanguageChoice>)LanguageBox.ItemsSource).First(l=>l.Tag==tag);
        RefreshTranslationLanguages();
        HotkeyModeBox.ItemsSource=new[] { new HotkeyModeChoice(HotkeyMode.GameHook,UiText.T("mode.game")),new HotkeyModeChoice(HotkeyMode.Registered,UiText.T("mode.registered")) };
        HotkeyModeBox.SelectedItem=((IEnumerable<HotkeyModeChoice>)HotkeyModeBox.ItemsSource).First(m=>m.Mode==mode);
        CaptureModeBox.ItemsSource=new[] { new CaptureChoice(CaptureMode.Auto,UiText.T("capture.auto")),new CaptureChoice(CaptureMode.DesktopDuplication,UiText.T("capture.dxgi")),new CaptureChoice(CaptureMode.Gdi,UiText.T("capture.gdi")) };
        CaptureModeBox.SelectedItem=((IEnumerable<CaptureChoice>)CaptureModeBox.ItemsSource).First(m=>m.Mode==capture);
        UiLanguageBox.SelectedItem=((IEnumerable<UiLanguageChoice>)UiLanguageBox.ItemsSource).First(l=>l.Id==UiText.Language);
        // Recreate the selection as ComboBox caches the displayed name.
        ProviderBox.ItemsSource=null;
        ProviderBox.ItemsSource=owner.Providers.Definitions;
        ProviderBox.SelectedItem=owner.Providers.Get(providerId);
        RefreshSpeechRoleList();
        TranslationModelsHint.Text=UiText.T("translation.modelsHint");
        GenderModelsHint.Text=UiText.T("translation.modelsHint");
        UpdateOllamaHardware(); ShowOllamaState();
        RefreshOllamaProgress();
        if (previewGender is { } gender) SetGender(gender);
        OcrProviderBox.ItemsSource=new[] { new OcrProviderChoice("windows",UiText.T("ocr.windows")) }; OcrProviderBox.SelectedIndex=0;
        LoadAudioDevices((AudioDeviceBox.SelectedItem as AudioOutputChoice)?.Id ?? draft.AudioOutputDeviceId);
        ApplyProviderUI(); UpdateLanguageHint(); initialized=true;
        SetStatus(UiText.T(owner.Reader?.IsBusy==true ? "status.working" : "ready"));
    }
    private void UpdateHotkeyFields()
    {
        string Display(Hotkey? key)=>key?.ToString() ?? UiText.T("hotkey.unassigned");
        CaptureKeyBox.Text=Display(captureKey); RepeatCaptureKeyBox.Text=Display(repeatCaptureKey); ToggleKeyBox.Text=Display(toggleKey);
        CancelCurrentKeyBox.Text=Display(cancelCurrentKey); CancelAllKeyBox.Text=Display(cancelAllKey);
        HelpCaptureText.Text=captureKey is null ? UiText.T("help.captureUnassigned") : UiText.T("help.capture",Display(captureKey));
    }
    private void CaptureKeyDown(object sender,KeyEventArgs e)
    {
        if (e.Key==Key.Tab) return;
        e.Handled=true; var key=e.Key==Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var mods=Keyboard.Modifiers;
        Hotkey? value=null;
        if (mods!=ModifierKeys.None || key is not (Key.Delete or Key.Back))
        {
            uint flags=((mods&ModifierKeys.Alt)!=0 ? 1u:0)|((mods&ModifierKeys.Control)!=0 ? 2u:0)|((mods&ModifierKeys.Shift)!=0 ? 4u:0)|((mods&ModifierKeys.Windows)!=0 ? 8u:0);
            value=new Hotkey(flags,(uint)KeyInterop.VirtualKeyFromKey(key));
            try { HotkeyService.Validate(value); }
            catch (Exception ex) { SetStatus(ex.Message); return; }
        }
        if (sender==CaptureKeyBox) captureKey=value;
        else if (sender==RepeatCaptureKeyBox) repeatCaptureKey=value;
        else if (sender==ToggleKeyBox) toggleKey=value;
        else if (sender==CancelCurrentKeyBox) cancelCurrentKey=value;
        else if (sender==CancelAllKeyBox) cancelAllKey=value;
        UpdateHotkeyFields();
    }
}
