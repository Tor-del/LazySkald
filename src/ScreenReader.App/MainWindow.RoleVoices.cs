using System.Windows;
using System.Windows.Controls;

namespace ScreenReader.App;

public enum SpeechProfile { Default, Fallback }
public sealed record SpeechRoleChoice(SpeechProfile Profile,string Name);

public partial class MainWindow
{
    private readonly Dictionary<SpeechProfile,SpeechRoleSettings> speechRoles=new();
    private const SpeechProfile FallbackRole=SpeechProfile.Fallback;
    private SpeechProfile editingRole=SpeechProfile.Default;
    private bool changingRoleList;

    private static string RoleName(SpeechProfile role)=>UiText.T(role switch
    {
        FallbackRole=>"speech.fallbackRole",_=>"speech.defaultRole"
    });

    private void InitializeSpeechRoles()
    {
        speechRoles[SpeechProfile.Default]=SpeechRoleSettings.From(draft);
        if (draft.FallbackRole is { } fallback) speechRoles[FallbackRole]=fallback;
        RefreshSpeechRoleList();
    }

    private void SaveSpeechEditor()
    {
        RememberVoice(providerId);
        speechRoles[editingRole]=new()
        {
            VolumePercent=(int)VolumeSlider.Value,ProviderId=providerId,ProviderVoices=new(selectedVoices),RatePercent=(int)RateSlider.Value,
            AudioOutputDeviceId=(AudioDeviceBox.SelectedItem as AudioOutputChoice)?.Id ?? "",
            Endpoint=EndpointBox.Text.Trim(),Model=ModelBox.Text.Trim(),
            ApiKeyProtected=SecretProtection.Protect(ApiKeyBox.Password),Format=(SpeechAudioFormat)AudioFormatBox.SelectedIndex
        };
    }

    private void ValidateSpeechRole(SpeechRoleSettings role)
    {
        owner.Providers.Get(role.ProviderId);
        if (string.IsNullOrWhiteSpace(role.ApplyTo(draft).SelectedVoice)) throw new InvalidOperationException(UiText.T("error.voice"));
        if (role.VolumePercent is < 0 or > 100 || role.RatePercent is < -50 or > 100 || !Enum.IsDefined(role.Format)) throw new InvalidOperationException(UiText.T("error.voice"));
        if (role.ProviderId=="openai-compatible") OpenAiSpeechClient.Validate(new(role.Endpoint,role.Model,SecretProtection.Unprotect(role.ApiKeyProtected),role.Format));
    }

    private void RefreshSpeechRoleList()
    {
        changingRoleList=true;
        try
        {
            RoleVoicesPanel.Visibility=Visibility.Visible;
            AddFallbackRoleButton.IsEnabled=!speechRoles.ContainsKey(FallbackRole);
            SpeechRolesBox.ItemsSource=new[] { SpeechProfile.Default,FallbackRole }
                .Where(g=>speechRoles.ContainsKey(g)).Select(g=>new SpeechRoleChoice(g,RoleName(g))).ToArray();
            SpeechRolesBox.SelectedItem=SpeechRolesBox.Items.Cast<SpeechRoleChoice>().FirstOrDefault(r=>r.Profile==editingRole);
            RemoveRoleButton.IsEnabled=editingRole!=SpeechProfile.Default;
            SpeechRoleHeading.Text=RoleName(editingRole);
        }
        finally { changingRoleList=false; }
    }

    private void SwitchSpeechRole(SpeechProfile role)
    {
        if (role==editingRole) return;
        SaveSpeechEditor();
        voiceLoad?.Cancel(); voiceLoad=null;
        editingRole=role;
        var settings=speechRoles[role];
        bool wasInitialized=initialized; initialized=false;
        try
        {
            selectedVoices.Clear();
            foreach (var pair in settings.ProviderVoices) selectedVoices[pair.Key]=pair.Value;
            providerId=settings.ProviderId;
            ProviderBox.SelectedItem=owner.Providers.Get(providerId);
            EndpointBox.Text=settings.Endpoint; ModelBox.Text=settings.Model;
            ApiKeyBox.Password="";
            try { ApiKeyBox.Password=SecretProtection.Unprotect(settings.ApiKeyProtected); }
            catch (Exception e) { AppLog.Write("Ключ роли TTS недоступен",e); SetStatus(UiText.T("status.keyUnavailable")); }
            VolumeSlider.Value=settings.VolumePercent;
            AudioFormatBox.SelectedIndex=(int)settings.Format; RateSlider.Value=settings.RatePercent;
            LoadAudioDevices(settings.AudioOutputDeviceId);
            ApplyProviderUI(); RefreshSpeechRoleList();
        }
        finally { initialized=wasInitialized; }
        if (!owner.Providers.Get(providerId).CustomVoice && !voiceCache.ContainsKey(providerId)) _=LoadVoicesAsync();
    }

    private void SpeechRoleChanged(object sender,SelectionChangedEventArgs e)
    {
        if (!initialized || changingRoleList || SpeechRolesBox.SelectedItem is not SpeechRoleChoice role) return;
        SwitchSpeechRole(role.Profile);
    }
    private void AddFallbackRole(object sender,RoutedEventArgs e)=>AddSpeechRole(FallbackRole);
    private void AddSpeechRole(SpeechProfile profile)
    {
        if (profile!=FallbackRole || speechRoles.ContainsKey(profile)) return;
        SaveSpeechEditor();
        var fallback=WindowsSpeechProvider.CreateDefaultFallback();
        if (fallback is null) { SetStatus(UiText.T("error.systemVoice")); return; }
        speechRoles[profile]=fallback;
        SwitchSpeechRole(profile);
    }
    private void RemoveSpeechRole(object sender,RoutedEventArgs e)
    {
        if (editingRole==SpeechProfile.Default) return;
        var removed=editingRole;
        SwitchSpeechRole(SpeechProfile.Default);
        speechRoles.Remove(removed); RefreshSpeechRoleList(); UpdatePrivacyHint();
    }

    private void UpdatePrivacyHint()
    {
        var providers=new HashSet<string> { providerId };
        if (speechRoles.TryGetValue(FallbackRole,out var fallback)) providers.Add(fallback.ProviderId);
        if (speechRoles.TryGetValue(SpeechProfile.Default,out var defaults)) providers.Add(defaults.ProviderId);
        PrivacyHint.Text=UiText.T(providers.Contains("openai-compatible") ? "privacy.server" : providers.Contains("edge") ? "privacy.edge" : "privacy.windows")
            +(TranslateEnabledBox.IsChecked==true ? UiText.T("privacy.translation") : "");
    }
}
