using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace ScreenReader.App;

public sealed record TranslationProviderChoice(string Id,string Name);
public partial class MainWindow
{
    private readonly Dictionary<string,TranslationProviderSettings> translationProviders=new();
    private string translationProvider="lmstudio";
    private bool changingTranslation;
    private CancellationTokenSource? modelLoad;
    private void InitializeTranslation()
    {
        var settings=draft.Translation;
        foreach (var pair in settings.Providers) translationProviders[pair.Key]=pair.Value;
        translationProvider=settings.Provider;
        TranslationProviderBox.ItemsSource=new[] { new TranslationProviderChoice("ollama","Ollama"),new TranslationProviderChoice("lmstudio","LM Studio"),new TranslationProviderChoice("openai","Custom OpenAI API") };
        TranslationProviderBox.SelectedItem=((IEnumerable<TranslationProviderChoice>)TranslationProviderBox.ItemsSource).FirstOrDefault(p=>p.Id==translationProvider) ?? TranslationProviderBox.Items[0];
        translationProvider=((TranslationProviderChoice)TranslationProviderBox.SelectedItem).Id;
        TranslateEnabledBox.IsChecked=settings.Enabled;
        RefreshTranslationLanguages();
        TranslationTargetBox.Text=settings.TargetLanguage;
        // Observe the value itself: the editable TextBox may not exist on an inactive tab.
        var targetText=DependencyPropertyDescriptor.FromProperty(ComboBox.TextProperty,typeof(ComboBox));
        EventHandler targetChanged=(_,_)=>UpdateTranslationLanguage();
        targetText.AddValueChanged(TranslationTargetBox,targetChanged);
        Closed+=(_,_)=>targetText.RemoveValueChanged(TranslationTargetBox,targetChanged);
        TranslationPromptBox.Text=settings.Prompt;
        TranslationStressMarksBox.IsChecked=settings.AddStressMarks;
        TranslationTemperatureEnabledBox.IsChecked=settings.Temperature.HasValue;
        TranslationTemperatureBox.Text=(settings.Temperature ?? 0.2).ToString(CultureInfo.InvariantCulture);
        TranslationTimeoutBox.Text=settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ShowTranslationProvider();
        Closed+=(_,_)=>modelLoad?.Cancel();
    }
    private void RefreshTranslationLanguages()
    {
        string target=TranslationTargetBox.Text;
        TranslationTargetBox.ItemsSource=LanguageCatalog.Tags.Select(tag=>new LanguageChoice(tag,LanguageCatalog.DisplayName(tag),false)).ToArray();
        TranslationTargetBox.Text=target;
    }
    private void RememberTranslationProvider()
    {
        translationProviders[translationProvider]=new() { BaseUrl=TranslationUrlBox.Text.Trim(),Model=TranslationModelBox.Text.Trim(),ApiKeyProtected=SecretProtection.Protect(TranslationKeyBox.Password) };
    }
    private void ShowTranslationProvider()
    {
        changingTranslation=true;
        try
        {
            var value=translationProviders.GetValueOrDefault(translationProvider) ?? new() { BaseUrl=TranslationSettings.DefaultUrl(translationProvider) };
            TranslationUrlBox.Text=value.BaseUrl; TranslationModelBox.ItemsSource=null; TranslationModelBox.Text=value.Model;
            TranslationKeyBox.Password="";
            try { TranslationKeyBox.Password=SecretProtection.Unprotect(value.ApiKeyProtected); }
            catch (Exception e) { AppLog.Write("Ключ перевода недоступен",e); SetStatus(UiText.T("translation.keyUnavailable")); }
            TranslationModelsHint.Text=UiText.T("translation.modelsHint");
        }
        finally { changingTranslation=false; }
    }
    private TranslationSettings ReadTranslationControls(bool forModels=false)
    {
        RememberTranslationProvider();
        double? temperature=null;
        if (TranslationTemperatureEnabledBox.IsChecked==true)
        {
            if (!double.TryParse(TranslationTemperatureBox.Text.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)) throw new ArgumentException(UiText.T("translation.invalid"));
            temperature=value;
        }
        if (!int.TryParse(TranslationTimeoutBox.Text,out var timeout)) throw new ArgumentException(UiText.T("translation.invalid"));
        var settings=new TranslationSettings { Enabled=TranslateEnabledBox.IsChecked==true,Provider=translationProvider,Providers=new(translationProviders),TargetLanguage=TranslationTargetBox.Text.Trim(),Prompt=TranslationPromptBox.Text,AddStressMarks=TranslationStressMarksBox.IsChecked==true,Temperature=temperature,TimeoutSeconds=timeout };
        if (settings.Enabled || forModels) LlmTranslationClient.Validate(settings,!forModels);
        return settings;
    }
    private void TranslationProviderChanged(object sender,SelectionChangedEventArgs e)
    {
        if (!initialized || TranslationProviderBox.SelectedItem is not TranslationProviderChoice choice) return;
        RememberTranslationProvider(); modelLoad?.Cancel(); translationProvider=choice.Id; ShowTranslationProvider();
    }
    private void TranslationConnectionChanged(object sender,RoutedEventArgs e)
    {
        if (!initialized || changingTranslation) return;
        modelLoad?.Cancel();
        string model=TranslationModelBox.Text; TranslationModelBox.ItemsSource=null; TranslationModelBox.Text=model;
        TranslationModelsHint.Text=UiText.T("translation.modelsHint");
    }
    private void TranslationLanguageChanged(object sender,RoutedEventArgs e)
        =>UpdateTranslationLanguage();
    private void UpdateTranslationLanguage()
    {
        if (!initialized) return;
        RememberVoice(providerId);
        if (!owner.Providers.Get(providerId).CustomVoice) FilterVoices(selectedVoices.GetValueOrDefault(providerId));
        TranslationPreview.Text="";
        ApplyProviderUI();
    }
    private async void RefreshTranslationModels(object sender,RoutedEventArgs e)
    {
        modelLoad?.Cancel();
        using var load=CancellationTokenSource.CreateLinkedTokenSource(owner.Lifetime); modelLoad=load;
        RefreshModelsButton.IsEnabled=false;
        try
        {
            var settings=ReadTranslationControls(true);
            TranslationModelsHint.Text=UiText.T("translation.loading");
            var models=await new LlmTranslationClient().GetModelsAsync(settings,load.Token);
            load.Token.ThrowIfCancellationRequested();
            string selected=TranslationModelBox.Text;
            TranslationModelBox.ItemsSource=models;
            TranslationModelBox.Text=string.IsNullOrWhiteSpace(selected) ? models.FirstOrDefault() ?? "" : selected;
            TranslationModelsHint.Text=UiText.T(models.Count==0 ? "translation.noModels" : "translation.modelsHint");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!load.IsCancellationRequested) TranslationModelsHint.Text=ex.Message; AppLog.Write("Ошибка загрузки моделей перевода",ex); }
        finally { if (ReferenceEquals(modelLoad,load)) { modelLoad=null; RefreshModelsButton.IsEnabled=true; } }
    }
}
