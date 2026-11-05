using System.Text.Json;

namespace ScreenReader.App;

public sealed record Hotkey(uint Modifiers, uint Key)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsReserved=>Key is 0x01 or 0x02 or 0x1B;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMouse=>Key is 0x01 or 0x02 or 0x04 or 0x05 or 0x06;
    public override string ToString() => string.Join(" + ",new[] {
        (Modifiers&2)!=0 ? "Ctrl" : null,(Modifiers&1)!=0 ? "Alt" : null,
        (Modifiers&4)!=0 ? "Shift" : null,(Modifiers&8)!=0 ? "Win" : null,
        Key switch { 0x12=>"Alt",0x11=>"Ctrl",0x10=>"Shift",0x01=>"Mouse Left",0x02=>"Mouse Right",0x04=>"Mouse Middle",0x05=>"Mouse X1",0x06=>"Mouse X2",0x14=>"Caps Lock",_=>((System.Windows.Forms.Keys)Key).ToString() } }.Where(x => x != null));
}
public enum CaptureMode { Auto, DesktopDuplication, Gdi }
public enum HotkeyMode { GameHook, Registered }
public sealed record OverlaySettings
{
    public bool Enabled { get; init; }
    public bool ShowTranslation { get; init; }
    public double Left { get; init; }=40;
    public double Top { get; init; }=40;
    public double Width { get; init; }=360;
    // Relative window centre follows the selected monitor, regardless of its DPI or origin.
    public double CenterX { get; init; }=0.5;
    public double CenterY { get; init; }=0.75;
}
public sealed record AppSettings
{
    public OverlaySettings Overlay { get; init; }=new();
    public Hotkey? OverlayInteractionHotkey { get; init; }=new(0,0x12);
    public bool SpeechEnabled { get; init; }=true;
    public int VolumePercent { get; init; }=100;
    public SpeechRoleSettings? FallbackRole { get; init; }
    public bool ExclusiveHotkeys { get; init; }=true;
    public bool CacheEnabled { get; init; }
    public string OcrProviderId { get; init; }="windows";
    public bool OcrReplaceLineBreaks { get; init; }=true;
    public bool OcrPreserveParagraphs { get; init; }
    public bool OcrCorrectionsEnabled { get; init; }
    public string OcrCorrectionRules { get; init; }="/ => l";
    public List<OcrCorrectionEntry>? OcrCorrectionEntries { get; init; }
    public GenderizationSettings Genderization { get; init; }=new();
    public Dictionary<string,string> FemaleProviderVoices { get; init; }=new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string,string> MaleProviderVoices { get; init; }=new(StringComparer.OrdinalIgnoreCase);
    public SpeechRoleSettings? FemaleRole { get; init; }
    public SpeechRoleSettings? MaleRole { get; init; }
    public SpeechRoleSettings? RoleFor(SpeakerGender gender)
    {
        var role=gender switch { SpeakerGender.Female=>FemaleRole,SpeakerGender.Male=>MaleRole,_=>null };
        if (role is not null) return role;
        // Migrate the original voice-only roles using the default TTS configuration.
        var legacy=gender switch { SpeakerGender.Female=>FemaleProviderVoices,SpeakerGender.Male=>MaleProviderVoices,_=>null };
        string? voice=legacy?.GetValueOrDefault(SpeechProviderId);
        return string.IsNullOrWhiteSpace(voice) ? null : SpeechRoleSettings.From(this) with { ProviderVoices=new(legacy!) };
    }
    public AppSettings SpeechFor(SpeakerGender gender)=>RoleFor(gender)?.ApplyTo(this) ?? this;
    public string VoiceFor(SpeakerGender gender)=>SpeechFor(gender).SelectedVoice;
    public TranslationSettings Translation { get; init; } = new();
    public string Language { get; init; } = "ru-RU";
    public string Voice { get; init; } = "ru-RU-SvetlanaNeural";
    public int RatePercent { get; init; }
    public string UiLanguage { get; init; }="ru";
    public AppTheme Theme { get; init; }=AppTheme.System;
    public string AudioOutputDeviceId { get; init; }="";
    public Hotkey? ToggleHotkey { get; init; }
    public Hotkey? CancelCurrentHotkey { get; init; }
    public Hotkey? CancelAllHotkey { get; init; }
    public Hotkey? CaptureHotkey { get; init; } = new(2|1,0x52);
    public Hotkey? RepeatCaptureHotkey { get; init; }
    public HotkeyMode HotkeyMode { get; init; }=HotkeyMode.GameHook;
    public string SpeechProviderId { get; init; }="edge";
    public Dictionary<string,string> ProviderVoices { get; init; }=new(StringComparer.OrdinalIgnoreCase);
    public string WindowsVoice { get; init; }="";
    public string OpenAiEndpoint { get; init; }="http://127.0.0.1:5050/v1/audio/speech";
    public string OpenAiModel { get; init; }="tts-1";
    public string OpenAiVoice { get; init; }="alloy";
    public string OpenAiApiKeyProtected { get; init; }="";
    public SpeechAudioFormat OpenAiFormat { get; init; }=SpeechAudioFormat.Mp3;
    [System.Text.Json.Serialization.JsonIgnore]
    public string SelectedVoice=>ProviderVoices?.GetValueOrDefault(SpeechProviderId) ?? (SpeechProviderId switch { "windows"=>WindowsVoice,"openai-compatible"=>OpenAiVoice,_=>Voice });
    public CaptureMode CaptureMode { get; init; } = CaptureMode.Auto;
    public bool MinimizeToTray { get; init; } = true;
    public bool StartInTray { get; init; }
}
public static class SettingsStore
{
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenReader");
    public static string SettingsPath => Path.Combine(DataDirectory,"settings.json");
    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
            if (value.VolumePercent is < 0 or > 100 || value.FallbackRole?.IsStructurallyValid()==false || value.FemaleRole?.IsStructurallyValid()==false || value.MaleRole?.IsStructurallyValid()==false || value.Genderization is null || value.Genderization.Providers is null || value.FemaleProviderVoices is null || value.MaleProviderVoices is null || value.OcrProviderId!="windows" || value.Translation is null || value.Translation.Providers is null || string.IsNullOrWhiteSpace(value.Language) || string.IsNullOrWhiteSpace(value.SpeechProviderId) || value.RatePercent is < -50 or > 100 || !Enum.IsDefined(value.CaptureMode) || !Enum.IsDefined(value.HotkeyMode) || !Enum.IsDefined(value.OpenAiFormat))
                throw new InvalidDataException("Некорректные параметры.");
            if (value.OcrCorrectionRules is null) throw new InvalidDataException("Некорректные правила коррекции текста.");
            if (value.OcrCorrectionsEnabled || value.OcrCorrectionEntries is not null)
                OcrTextCorrections.ValidateEntries(OcrTextCorrections.EntriesFor(value));
            if (value.Translation.CorrectionRules is null) throw new InvalidDataException("Некорректный словарь перевода.");
            if (value.Translation.CorrectionsEnabled || value.Translation.CorrectionEntries is not null)
                TranslationCorrections.ValidateEntries(TranslationCorrections.EntriesFor(value.Translation));
            return ClearReservedHotkeys(value with { Translation=value.Translation.UpgradeDefaultPrompt(),Theme=Enum.IsDefined(value.Theme) ? value.Theme : AppTheme.System });
        }
        catch (Exception e) { AppLog.Write("Настройки повреждены; использованы значения по умолчанию.",e); return new(); }
    }
    public static void Save(AppSettings value)
    {
        Directory.CreateDirectory(DataDirectory);
        string temporary = SettingsPath+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(value,new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary,SettingsPath,true);
    }
    public static AppSettings ClearReservedHotkeys(AppSettings value)
    {
        Hotkey? Allowed(Hotkey? key)=>key?.IsReserved==true ? null : key;
        return value with
        {
            CaptureHotkey=Allowed(value.CaptureHotkey),RepeatCaptureHotkey=Allowed(value.RepeatCaptureHotkey),
            ToggleHotkey=Allowed(value.ToggleHotkey),CancelCurrentHotkey=Allowed(value.CancelCurrentHotkey),
            CancelAllHotkey=Allowed(value.CancelAllHotkey),OverlayInteractionHotkey=Allowed(value.OverlayInteractionHotkey),
            Overlay=NormalizeOverlay(value.Overlay)
        };
    }
    public static OverlaySettings NormalizeOverlay(OverlaySettings? value)
    {
        value ??=new();
        return value with
        {
            Left=double.IsFinite(value.Left) ? Math.Clamp(value.Left,-100000,100000) : 40,
            Top=double.IsFinite(value.Top) ? Math.Clamp(value.Top,-100000,100000) : 40,
            Width=double.IsFinite(value.Width) ? Math.Clamp(value.Width,260,800) : 360,
            CenterX=double.IsFinite(value.CenterX) ? Math.Clamp(value.CenterX,0,1) : 0.5,
            CenterY=double.IsFinite(value.CenterY) ? Math.Clamp(value.CenterY,0,1) : 0.75
        };
    }
}
public static class AppLog
{
    private static readonly object Gate = new();
    public static string LogPath => Path.Combine(SettingsStore.DataDirectory,"screenreader.log");
    public static void Write(string text, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.DataDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2000000) File.Move(LogPath,LogPath+".old",true);
                // Do not log recognized text, screenshots, request URLs or speech content.
                string detail = exception is null ? "" : $" [{exception.GetType().Name}; HRESULT=0x{exception.HResult:X8}]";
                if (exception is ScreenReader.Edge.SpeechServiceException speech)
                    detail+=$" voice={speech.Voice}; attempts={speech.Attempts}; audioStarted={speech.AudioStarted}; cause={speech.InnerException?.GetType().Name}";
                File.AppendAllText(LogPath,$"{DateTimeOffset.Now:O} {text}{detail}{Environment.NewLine}");
            }
        }
        catch { /* Logging must never prevent cancellation or shutdown. */ }
    }
}
