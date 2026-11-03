using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace ScreenReader.App;

public partial class MainWindow
{
    private readonly Dictionary<string,TranslationProviderSettings> genderProviders=new();
    private string genderProvider="lmstudio";
    private bool changingGender;
    private CancellationTokenSource? genderModelLoad;
    private void InitializeGender()
    {
        var settings=draft.Genderization;
        foreach (var pair in settings.Providers) genderProviders[pair.Key]=pair.Value;
        genderProvider=settings.Provider;
        GenderProviderBox.ItemsSource=new[] { new TranslationProviderChoice("ollama","Ollama"),new TranslationProviderChoice("lmstudio","LM Studio"),new TranslationProviderChoice("openai","Custom OpenAI API") };
        GenderProviderBox.SelectedItem=((IEnumerable<TranslationProviderChoice>)GenderProviderBox.ItemsSource).FirstOrDefault(p=>p.Id==genderProvider) ?? GenderProviderBox.Items[0];
        genderProvider=((TranslationProviderChoice)GenderProviderBox.SelectedItem).Id;
        GenderEnabledBox.IsChecked=settings.Enabled;
        GenderPromptBox.Text=settings.Prompt;
        GenderTemperatureEnabledBox.IsChecked=settings.Temperature.HasValue;
        GenderTemperatureBox.Text=(settings.Temperature ?? 0).ToString(CultureInfo.InvariantCulture);
        GenderTimeoutBox.Text=settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ShowGenderProvider();
        Closed+=(_,_)=>genderModelLoad?.Cancel();
    }
    private void RememberGenderProvider()
    {
        genderProviders[genderProvider]=new() { BaseUrl=GenderUrlBox.Text.Trim(),Model=GenderModelBox.Text.Trim(),ApiKeyProtected=SecretProtection.Protect(GenderKeyBox.Password) };
    }
    private void ShowGenderProvider()
    {
        changingGender=true;
        try
        {
            var value=genderProviders.GetValueOrDefault(genderProvider) ?? new() { BaseUrl=GenderizationSettings.DefaultUrl(genderProvider) };
            GenderUrlBox.Text=value.BaseUrl; GenderModelBox.ItemsSource=null; GenderModelBox.Text=value.Model;
            GenderKeyBox.Password="";
            try { GenderKeyBox.Password=SecretProtection.Unprotect(value.ApiKeyProtected); }
            catch (Exception e) { AppLog.Write("Ключ гендеризации недоступен",e); SetStatus(UiText.T("gender.keyUnavailable")); }
            GenderModelsHint.Text=UiText.T("translation.modelsHint");
        }
        finally { changingGender=false; }
    }
    private GenderizationSettings ReadGenderControls(bool forModels=false)
    {
        RememberGenderProvider();
        double? temperature=null;
        if (GenderTemperatureEnabledBox.IsChecked==true)
        {
            if (!double.TryParse(GenderTemperatureBox.Text.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)) throw new ArgumentException(UiText.T("translation.invalid"));
            temperature=value;
        }
        if (!int.TryParse(GenderTimeoutBox.Text,out var timeout)) throw new ArgumentException(UiText.T("translation.invalid"));
        var settings=new GenderizationSettings { Enabled=GenderEnabledBox.IsChecked==true,Provider=genderProvider,Providers=new(genderProviders),Prompt=GenderPromptBox.Text,Temperature=temperature,TimeoutSeconds=timeout };
        if (settings.Enabled || forModels) LlmTranslationClient.Validate(settings,!forModels);
        return settings;
    }
    private void GenderProviderChanged(object sender,SelectionChangedEventArgs e)
    {
        if (!initialized || GenderProviderBox.SelectedItem is not TranslationProviderChoice choice) return;
        RememberGenderProvider(); genderModelLoad?.Cancel(); genderProvider=choice.Id; ShowGenderProvider();
    }
    private void GenderConnectionChanged(object sender,RoutedEventArgs e)
    {
        if (!initialized || changingGender) return;
        genderModelLoad?.Cancel();
        string model=GenderModelBox.Text; GenderModelBox.ItemsSource=null; GenderModelBox.Text=model;
        GenderModelsHint.Text=UiText.T("translation.modelsHint");
    }
    private void GenderEnabledChanged(object sender,RoutedEventArgs e)
    {
        if (!initialized) return;
        if (GenderEnabledBox.IsChecked!=true && editingRole!=SpeakerGender.Unknown) SwitchSpeechRole(SpeakerGender.Unknown);
        RefreshSpeechRoleList(); UpdatePrivacyHint();
    }
    private async void RefreshGenderModels(object sender,RoutedEventArgs e)
    {
        genderModelLoad?.Cancel();
        using var load=CancellationTokenSource.CreateLinkedTokenSource(owner.Lifetime); genderModelLoad=load;
        RefreshGenderModelsButton.IsEnabled=false;
        try
        {
            var settings=ReadGenderControls(true);
            GenderModelsHint.Text=UiText.T("translation.loading");
            var models=await new LlmTranslationClient().GetModelsAsync(settings,load.Token);
            load.Token.ThrowIfCancellationRequested();
            string selected=GenderModelBox.Text;
            GenderModelBox.ItemsSource=models;
            GenderModelBox.Text=string.IsNullOrWhiteSpace(selected) ? models.FirstOrDefault() ?? "" : selected;
            GenderModelsHint.Text=UiText.T(models.Count==0 ? "translation.noModels" : "translation.modelsHint");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!load.IsCancellationRequested) GenderModelsHint.Text=ex.Message; AppLog.Write("Ошибка загрузки моделей гендеризации",ex); }
        finally { if (ReferenceEquals(genderModelLoad,load)) { genderModelLoad=null; RefreshGenderModelsButton.IsEnabled=true; } }
    }
}
