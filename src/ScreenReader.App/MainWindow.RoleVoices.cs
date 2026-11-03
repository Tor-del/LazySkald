using System.Windows;
using System.Windows.Controls;

namespace ScreenReader.App;

public sealed record SpeechRoleChoice(SpeakerGender Gender,string Name);

public partial class MainWindow
{
    private readonly Dictionary<SpeakerGender,SpeechRoleSettings> speechRoles=new();
    private SpeakerGender editingRole=SpeakerGender.Unknown;
    private bool changingRoleList;

    private SpeakerGender? previewGender;
    public void SetGender(SpeakerGender gender)
    {
        previewGender=gender;
        GenderPreview.Text=UiText.T("gender.result",UiText.T("gender."+gender.ToString().ToLowerInvariant()));
    }
    private static string RoleName(SpeakerGender role)=>UiText.T(role switch
    {
        SpeakerGender.Female=>"speech.femaleRole",SpeakerGender.Male=>"speech.maleRole",_=>"speech.defaultRole"
    });

    private void InitializeSpeechRoles()
    {
        speechRoles[SpeakerGender.Unknown]=SpeechRoleSettings.From(draft);
        if (draft.RoleFor(SpeakerGender.Female) is { } female) speechRoles[SpeakerGender.Female]=female;
        if (draft.RoleFor(SpeakerGender.Male) is { } male) speechRoles[SpeakerGender.Male]=male;
        RefreshSpeechRoleList();
    }

    private void SaveSpeechEditor()
    {
        RememberVoice(providerId);
        speechRoles[editingRole]=new()
        {
            ProviderId=providerId,ProviderVoices=new(selectedVoices),RatePercent=(int)RateSlider.Value,
            AudioOutputDeviceId=(AudioDeviceBox.SelectedItem as AudioOutputChoice)?.Id ?? "",
            Endpoint=EndpointBox.Text.Trim(),Model=ModelBox.Text.Trim(),
            ApiKeyProtected=SecretProtection.Protect(ApiKeyBox.Password),Format=(SpeechAudioFormat)AudioFormatBox.SelectedIndex
        };
    }

    private void ValidateSpeechRole(SpeechRoleSettings role)
    {
        owner.Providers.Get(role.ProviderId);
        if (string.IsNullOrWhiteSpace(role.ApplyTo(draft).SelectedVoice)) throw new InvalidOperationException(UiText.T("error.voice"));
        if (role.RatePercent is < -50 or > 100 || !Enum.IsDefined(role.Format)) throw new InvalidOperationException(UiText.T("error.voice"));
        if (role.ProviderId=="openai-compatible") OpenAiSpeechClient.Validate(new(role.Endpoint,role.Model,SecretProtection.Unprotect(role.ApiKeyProtected),role.Format));
    }

    private void RefreshSpeechRoleList()
    {
        changingRoleList=true;
        try
        {
            bool enabled=GenderEnabledBox.IsChecked==true;
            RoleVoicesPanel.Visibility=enabled ? Visibility.Visible : Visibility.Collapsed;
            SpeechRolesBox.ItemsSource=new[] { SpeakerGender.Unknown,SpeakerGender.Female,SpeakerGender.Male }
                .Where(speechRoles.ContainsKey).Select(g=>new SpeechRoleChoice(g,RoleName(g))).ToArray();
            SpeechRolesBox.SelectedItem=SpeechRolesBox.Items.Cast<SpeechRoleChoice>().FirstOrDefault(r=>r.Gender==editingRole);
            AddFemaleRoleButton.IsEnabled=!speechRoles.ContainsKey(SpeakerGender.Female);
            AddMaleRoleButton.IsEnabled=!speechRoles.ContainsKey(SpeakerGender.Male);
            RemoveRoleButton.IsEnabled=editingRole!=SpeakerGender.Unknown;
            SpeechRoleHeading.Text=RoleName(editingRole);
        }
        finally { changingRoleList=false; }
    }

    private void SwitchSpeechRole(SpeakerGender role)
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
        SwitchSpeechRole(role.Gender);
    }
    private void AddFemaleRole(object sender,RoutedEventArgs e)=>AddSpeechRole(SpeakerGender.Female);
    private void AddMaleRole(object sender,RoutedEventArgs e)=>AddSpeechRole(SpeakerGender.Male);
    private void AddSpeechRole(SpeakerGender gender)
    {
        if (GenderEnabledBox.IsChecked!=true || speechRoles.ContainsKey(gender)) return;
        SaveSpeechEditor();
        // Start from the default profile; subsequent edits belong only to the new role.
        speechRoles[gender]=speechRoles[SpeakerGender.Unknown] with { ProviderVoices=new(speechRoles[SpeakerGender.Unknown].ProviderVoices) };
        SwitchSpeechRole(gender);
    }
    private void RemoveSpeechRole(object sender,RoutedEventArgs e)
    {
        if (editingRole==SpeakerGender.Unknown) return;
        var removed=editingRole;
        SwitchSpeechRole(SpeakerGender.Unknown);
        speechRoles.Remove(removed); RefreshSpeechRoleList(); UpdatePrivacyHint();
    }

    private void UpdatePrivacyHint()
    {
        var providers=new HashSet<string> { providerId };
        if (speechRoles.TryGetValue(SpeakerGender.Unknown,out var defaults)) providers.Add(defaults.ProviderId);
        if (GenderEnabledBox.IsChecked==true)
            foreach (var role in speechRoles.Values) providers.Add(role.ProviderId);
        PrivacyHint.Text=UiText.T(providers.Contains("openai-compatible") ? "privacy.server" : providers.Contains("edge") ? "privacy.edge" : "privacy.windows")
            +(TranslateEnabledBox.IsChecked==true ? UiText.T("privacy.translation") : "")
            +(GenderEnabledBox.IsChecked==true ? UiText.T("gender.privacyShort") : "");
    }
}
