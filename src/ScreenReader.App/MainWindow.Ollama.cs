using System.Windows;
using System.Windows.Controls;

namespace ScreenReader.App;

public partial class MainWindow
{
    private readonly OllamaSetupClient ollamaSetup=new();
    private CancellationTokenSource? ollamaOperation;
    private OllamaHardware? ollamaHardware;
    private OllamaState? ollamaState;
    private bool ollamaBusy;
    private Func<string>? ollamaProgressText;
    private void SetOllamaProgress(string key,params object[] args)
    {
        ollamaProgressText=()=>UiText.T(key,args);
        RefreshOllamaProgress();
    }
    private void RefreshOllamaProgress()
    {
        if (ollamaProgressText is { } text) OllamaProgressText.Text=text();
    }
    private void InitializeOllama()
    {
        OllamaModelBox.ItemsSource=OllamaModelChoice.All;
        OllamaModelBox.SelectedIndex=0;
        Closed+=(_,_)=>ollamaOperation?.Cancel();
    }
    private async void OllamaLoaded(object sender,RoutedEventArgs e)
    {
        if (!initialized || ollamaBusy) return;
        await RunOllamaAsync(async token=>
        {
            if (ollamaHardware is null)
            {
                ollamaHardware=await Task.Run(OllamaHardware.Detect,token);
                OllamaModelBox.SelectedItem=OllamaModelChoice.Recommend(ollamaHardware.RamGb,ollamaHardware.VramGb);
            }
            UpdateOllamaHardware();
            await RefreshOllamaStateAsync(token);
            SetOllamaProgress("ollama.checked");
        });
    }
    private void UpdateOllamaHardware()
    {
        if (ollamaHardware is not { } hardware) return;
        OllamaHardwareText.Text=UiText.T("ollama.hardware",hardware.RamGb,hardware.AvailableRamGb,
            string.IsNullOrEmpty(hardware.Gpu) ? UiText.T("ollama.gpuUnknown") : hardware.Gpu,hardware.VramGb);
        OllamaRecommendationText.Text=UiText.T("ollama.recommendation",OllamaModelChoice.Recommend(hardware.RamGb,hardware.VramGb).Tag);
        UpdateOllamaModel();
    }
    private void OllamaModelChanged(object sender,SelectionChangedEventArgs e)=>UpdateOllamaModel();
    private void UpdateOllamaModel()
    {
        if (OllamaModelBox.SelectedItem is not OllamaModelChoice model) return;
        OllamaModelHint.Text=UiText.T("ollama.requirements",model.RamGb,model.VramGb)+
            (ollamaHardware is { } hardware && (hardware.RamGb<model.RamGb || hardware.AvailableRamGb<model.RamGb-4)
                ? " "+UiText.T("ollama.lowMemory") : "");
        OllamaUseButton.IsEnabled=!ollamaBusy && ollamaState?.Running==true && ollamaState.Models.Contains(model.Tag);
    }
    private void ShowOllamaState()
    {
        if (ollamaState is null) OllamaStateText.Text=UiText.T("ollama.unchecked");
        else if (!ollamaState.Running) OllamaStateText.Text=UiText.T(ollamaState.Executable is null ? "ollama.notInstalled" : "ollama.stopped");
        else
        {
            string models=string.Join(", ",ollamaState.Models.Where(m=>m.StartsWith("translategemma:",StringComparison.Ordinal)));
            OllamaStateText.Text=UiText.T("ollama.running",ollamaState.Version!,models.Length>0 ? models : UiText.T("ollama.noModels"));
        }
        UpdateOllamaModel();
    }
    private async Task RefreshOllamaStateAsync(CancellationToken token)
    {
        ollamaState=await ollamaSetup.CheckAsync(token); ShowOllamaState();
    }
    private async Task RunOllamaAsync(Func<CancellationToken,Task> action)
    {
        if (ollamaBusy) return;
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(owner.Lifetime);
        ollamaOperation=operation; ollamaBusy=true;
        OllamaCheckButton.IsEnabled=OllamaSetupButton.IsEnabled=OllamaModelBox.IsEnabled=OllamaUseButton.IsEnabled=false;
        OllamaCancelButton.IsEnabled=true;
        OllamaProgressBar.Value=0;
        OllamaProgressBar.IsIndeterminate=true;
        SetOllamaProgress("ollama.checking");
        try { await action(operation.Token); }
        catch (OperationCanceledException)
        { SetOllamaProgress(operation.IsCancellationRequested ? "ollama.cancelled" : "ollama.timedOut"); }
        catch (Exception ex) { SetOllamaProgress("ollama.error",ex.Message); AppLog.Write("Ollama setup failed",ex); }
        finally
        {
            ollamaOperation=null; ollamaBusy=false;
            OllamaCheckButton.IsEnabled=OllamaSetupButton.IsEnabled=OllamaModelBox.IsEnabled=true;
            OllamaCancelButton.IsEnabled=false; OllamaProgressBar.IsIndeterminate=false;
            UpdateOllamaModel();
        }
    }
    private async void CheckOllama(object sender,RoutedEventArgs e)=>await RunOllamaAsync(async token=>
    {
        ollamaHardware=await Task.Run(OllamaHardware.Detect,token); UpdateOllamaHardware();
        await RefreshOllamaStateAsync(token); SetOllamaProgress("ollama.checked");
    });
    private async void SetupOllama(object sender,RoutedEventArgs e)
    {
        if (OllamaModelBox.SelectedItem is not OllamaModelChoice model) return;
        await RunOllamaAsync(async token=>
        {
            var progress=new Progress<OllamaProgress>(value=>
            {
                if (ollamaOperation?.IsCancellationRequested!=false) return;
                OllamaProgressBar.IsIndeterminate=!value.Percent.HasValue;
                OllamaProgressBar.Value=value.Percent ?? 0;
                string key=value.Stage switch
                {
                    "pulling manifest"=>"ollama.manifest",
                    "verifying sha256 digest"=>"ollama.verifying",
                    "writing manifest"=>"ollama.writing",
                    "success"=>"ollama.finished",
                    _=>value.Stage.StartsWith("pulling ",StringComparison.Ordinal) ? "ollama.pulling" : value.Stage
                };
                ollamaProgressText=()=>(key.StartsWith("ollama.",StringComparison.Ordinal) ? UiText.T(key) : key)
                    +(value.Total>0 ? string.Format(System.Globalization.CultureInfo.CurrentUICulture,
                        " · {0:0}% · {1:0.00} / {2:0.00} GB",value.Percent,value.Completed/1e9,value.Total/1e9) : "");
                RefreshOllamaProgress();
            });
            await RefreshOllamaStateAsync(token);
            if (!ollamaState!.Running && ollamaState.Executable is null)
            {
                await ollamaSetup.InstallAsync(progress,()=>OllamaCancelButton.IsEnabled=false,token);
                OllamaCancelButton.IsEnabled=true;
            }
            SetOllamaProgress("ollama.starting");
            await ollamaSetup.EnsureRunningAsync(token);
            await RefreshOllamaStateAsync(token);
            if (!ollamaState!.Models.Contains(model.Tag)) await ollamaSetup.PullAsync(model.Tag,progress,token);
            await RefreshOllamaStateAsync(token);
            if (!ollamaState!.Running || !ollamaState.Models.Contains(model.Tag)) throw new InvalidOperationException(UiText.T("ollama.verifyFailed"));
            // Flush pending progress callbacks before displaying the final result.
            await Dispatcher.InvokeAsync(()=> { },System.Windows.Threading.DispatcherPriority.Background);
            OllamaProgressBar.IsIndeterminate=false; OllamaProgressBar.Value=100;
            SetOllamaProgress("ollama.ready",model.Tag);
        });
    }
    private void CancelOllama(object sender,RoutedEventArgs e)=>ollamaOperation?.Cancel();
    private void UseOllamaModel(object sender,RoutedEventArgs e)
    {
        if (ollamaBusy || OllamaModelBox.SelectedItem is not OllamaModelChoice model || ollamaState?.Models.Contains(model.Tag)!=true) return;
        RememberTranslationProvider(); modelLoad?.Cancel();
        TranslationProviderBox.SelectedItem=TranslationProviderBox.Items.Cast<TranslationProviderChoice>().First(p=>p.Id=="ollama");
        translationProviders["ollama"]=new() { BaseUrl=OllamaSetupClient.BaseUrl,Model=model.Tag };
        ShowTranslationProvider(); TranslateEnabledBox.IsChecked=true;
        SetOllamaProgress("ollama.applied",model.Tag);
    }
}
