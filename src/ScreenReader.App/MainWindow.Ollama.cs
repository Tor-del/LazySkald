using System.Windows;
using System.Windows.Controls;

namespace ScreenReader.App;

public partial class MainWindow
{
    private readonly OllamaSetupClient ollamaSetup=new();
    private CancellationTokenSource? ollamaOperation;
    private OllamaHardware? ollamaHardware;
    private OllamaState? ollamaState;
    private OllamaDriverReport? ollamaDrivers;
    private OllamaGpuRequirements ollamaRequirements=OllamaGpuRequirements.LoadSaved();
    private bool ollamaDriverFailed;
    private bool ollamaBusy;
    private string? ollamaOperationModel,ollamaOperationKind;
    private double? ollamaRowPercent;
    private OllamaModelFilter ollamaFilters;
    private OllamaModelSort ollamaSort;
    private Func<string>? ollamaProgressText;
    private void SetOllamaProgress(string key,params object[] args)
    {
        ollamaProgressText=()=>UiText.T(key,args);
        RefreshOllamaProgress();
    }
    private void RefreshOllamaProgress()
    {
        if (ollamaProgressText is { } text)
        {
            OllamaProgressText.Text=text(); OllamaOperationPanel.Visibility=Visibility.Visible;
            OllamaCancelButton.Visibility=OllamaProgressBar.Visibility=ollamaBusy ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private void InitializeOllama()
    {
        RefreshOllamaModels();
        var modelText=System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ComboBox.TextProperty,typeof(ComboBox));
        EventHandler changed=(_,_)=>RefreshOllamaModels();
        modelText.AddValueChanged(TranslationModelBox,changed);
        Closed+=(_,_)=>modelText.RemoveValueChanged(TranslationModelBox,changed);
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
            }
            UpdateOllamaHardware();
            if (ollamaDrivers is null) await RefreshOllamaDriversAsync(false,token);
            await RefreshOllamaStateAsync(token);
            SetOllamaProgress("ollama.checked");
        });
    }
    private void UpdateOllamaHardware()
    {
        ShowOllamaDrivers();
        RefreshOllamaModels();
        if (ollamaHardware is not { } hardware) return;
        OllamaHardwareText.Text=UiText.T("ollama.hardware",hardware.RamGb,hardware.AvailableRamGb,
            string.IsNullOrEmpty(hardware.Gpu) ? UiText.T("ollama.gpuUnknown") : hardware.Gpu,hardware.VramGb);
        OllamaRecommendationText.Text=UiText.T("ollama.recommendationShort",OllamaModelChoice.Recommend(hardware.RamGb,hardware.VramGb).Tag);
        OllamaCpuText.Text=UiText.T("ollama.cpu",string.IsNullOrWhiteSpace(hardware.Cpu) ? UiText.T("ollama.gpuUnknown") : hardware.Cpu,Environment.ProcessorCount);
    }
    private void RefreshOllamaModels()
    {
        if (OllamaModelsList is null) return;
        string? active=translationProvider=="ollama" && TranslateEnabledBox.IsChecked==true && OllamaModelRow.IsLocalEndpoint(TranslationUrlBox.Text.Trim())
            ? TranslationModelBox.Text.Trim() : null;
        var rows=OllamaModelRow.Build(ollamaState,ollamaHardware,active,ollamaBusy,ollamaOperationModel,ollamaOperationKind,ollamaRowPercent);
        var visible=OllamaModelQuery.Apply(rows,OllamaSearchBox.Text,ollamaFilters,ollamaSort);
        OllamaModelsList.ItemsSource=visible;
        OllamaModelsCount.Text=UiText.T("ollama.results",visible.Count,rows.Count);
        OllamaModelsEmpty.Visibility=visible.Count==0 ? Visibility.Visible : Visibility.Collapsed;
        int count=Enum.GetValues<OllamaModelFilter>().Count(f=>f!=OllamaModelFilter.None && ollamaFilters.HasFlag(f));
        OllamaFilterLabel.Text=UiText.T("ollama.filters")+(count>0 ? $" ({count})" : "");
        OllamaSortButton.ToolTip=UiText.T("ollama.sort"+ollamaSort);
    }
    private void OllamaSearchChanged(object sender,TextChangedEventArgs e)=>RefreshOllamaModels();
    private void OpenOllamaModelMenu(object sender,RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu:{ } menu } button) return;
        menu.PlacementTarget=button; menu.Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen=true;
    }
    private void OllamaFilterChanged(object sender,RoutedEventArgs e)
    {
        if (sender is not MenuItem item || !Enum.TryParse<OllamaModelFilter>(item.Tag as string,out var filter)) return;
        ollamaFilters=item.IsChecked ? ollamaFilters | filter : ollamaFilters & ~filter;
        RefreshOllamaModels();
    }
    private void ResetOllamaFilters(object sender,RoutedEventArgs e)
    {
        ollamaFilters=OllamaModelFilter.None;
        foreach (var item in OllamaFilterButton.ContextMenu.Items.OfType<MenuItem>()) if (item.IsCheckable) item.IsChecked=false;
        RefreshOllamaModels();
    }
    private void OllamaSortChanged(object sender,RoutedEventArgs e)
    {
        if (sender is not MenuItem item || !Enum.TryParse<OllamaModelSort>(item.Tag as string,out var sort)) return;
        ollamaSort=sort;
        foreach (var choice in OllamaSortButton.ContextMenu.Items.OfType<MenuItem>()) choice.IsChecked=choice.Tag as string==sort.ToString();
        RefreshOllamaModels();
    }
    private void ShowOllamaState()
    {
        OllamaStateText.Text=ollamaState is null ? UiText.T("ollama.unchecked") : !ollamaState.Running
            ? UiText.T(ollamaState.Executable is null ? "ollama.runtimeMissing" : "ollama.runtimeStopped")
            : UiText.T("ollama.runtimeRunning",ollamaState.Version!,ollamaState.Models.Count);
        OllamaStartButton.Visibility=ollamaState is { Executable:not null,Running:false } ? Visibility.Visible : Visibility.Collapsed;
        RefreshOllamaModels();
    }
    private async void StartOllama(object sender,RoutedEventArgs e)=>await RunOllamaAsync(async token=>
    {
        SetOllamaProgress("ollama.starting");
        await ollamaSetup.EnsureRunningAsync(token);
        await RefreshOllamaStateAsync(token);
        SetOllamaProgress("ollama.checked");
    });
    private async Task RefreshOllamaStateAsync(CancellationToken token)
    {
        ollamaState=await ollamaSetup.CheckAsync(token); ShowOllamaState();
    }
    private async Task RunOllamaAsync(Func<CancellationToken,Task> action)
    {
        if (ollamaBusy) return;
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(owner.Lifetime);
        ollamaOperation=operation; ollamaBusy=true;
        OllamaCheckButton.IsEnabled=OllamaStartButton.IsEnabled=OllamaDriverCheckButton.IsEnabled=OllamaRequirementsButton.IsEnabled=false;
        RefreshOllamaModels();
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
            if (ollamaOperationModel is not null && !owner.Lifetime.IsCancellationRequested)
            {
                try { await RefreshOllamaStateAsync(owner.Lifetime); }
                catch (Exception ex) { AppLog.Write("Ollama status refresh failed",ex); }
            }
            ollamaOperationModel=ollamaOperationKind=null; ollamaRowPercent=null;
            ollamaOperation=null; ollamaBusy=false;
            OllamaCheckButton.IsEnabled=OllamaStartButton.IsEnabled=OllamaDriverCheckButton.IsEnabled=OllamaRequirementsButton.IsEnabled=true;
            OllamaCancelButton.IsEnabled=false; OllamaProgressBar.IsIndeterminate=false;
            RefreshOllamaModels();
            RefreshOllamaProgress();
        }
    }
    private async void CheckOllama(object sender,RoutedEventArgs e)=>await RunOllamaAsync(async token=>
    {
        ollamaHardware=await Task.Run(OllamaHardware.Detect,token); UpdateOllamaHardware();
        await RefreshOllamaDriversAsync(false,token);
        await RefreshOllamaStateAsync(token); SetOllamaProgress("ollama.checked");
    });
    private async void SetupOllama(object sender,RoutedEventArgs e)
    {
        if (ollamaBusy || (sender as FrameworkElement)?.DataContext is not OllamaModelRow row) return;
        var model=OllamaModelChoice.All.FirstOrDefault(m=>m.Tag==row.Tag);
        if (model is null) return;
        await RunOllamaAsync(async token=>
        {
            ollamaOperationModel=model.Tag; ollamaOperationKind="download";
            RefreshOllamaModels();
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
                ollamaRowPercent=value.Percent; RefreshOllamaModels();
            });
            await RefreshOllamaStateAsync(token);
            if (!ollamaState!.Running && ollamaState.Executable is null)
            {
                bool amd=ollamaDrivers?.Drivers.Any(d=>d.Vendor=="AMD")==true ||
                    ollamaHardware?.Gpu.Contains("AMD",StringComparison.OrdinalIgnoreCase)==true || ollamaHardware?.Gpu.Contains("Radeon",StringComparison.OrdinalIgnoreCase)==true;
                await ollamaSetup.InstallAsync(progress,()=>OllamaCancelButton.IsEnabled=false,token,amd);
                OllamaCancelButton.IsEnabled=true;
            }
            SetOllamaProgress("ollama.starting");
            await ollamaSetup.EnsureRunningAsync(token);
            await RefreshOllamaStateAsync(token);
            await ollamaSetup.PullAsync(model.Tag,progress,token);
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
        if (!ollamaBusy && (sender as FrameworkElement)?.DataContext is OllamaModelRow row && ollamaState?.Running==true && ollamaState.Models.Contains(row.Tag))
            ApplyOllamaModel(row.Tag);
    }
    private void ApplyOllamaModel(string model)
    {
        RememberTranslationProvider(); modelLoad?.Cancel();
        TranslationProviderBox.SelectedItem=TranslationProviderBox.Items.Cast<TranslationProviderChoice>().First(p=>p.Id=="ollama");
        translationProviders["ollama"]=new() { BaseUrl=OllamaSetupClient.BaseUrl,Model=model };
        ShowTranslationProvider(); TranslateEnabledBox.IsChecked=true;
        SetOllamaProgress("ollama.applied",model);
        RefreshOllamaModels();
    }
    private async void DeleteOllamaModel(object sender,RoutedEventArgs e)
    {
        if (ollamaBusy || (sender as FrameworkElement)?.DataContext is not OllamaModelRow row || ollamaState?.Models.Contains(row.Tag)!=true) return;
        string model=row.Tag;
        if (System.Windows.MessageBox.Show(this,UiText.T("ollama.deleteConfirm",model),UiText.T("ollama.delete"),
            MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes) return;
        await RunOllamaAsync(async token=>
        {
            ollamaOperationModel=model; ollamaOperationKind="delete"; RefreshOllamaModels();
            SetOllamaProgress("ollama.deleting",model);
            await ollamaSetup.DeleteAsync(model,token);
            RememberTranslationProvider();
            if (translationProviders.TryGetValue("ollama",out var profile) && profile.Model==model && OllamaModelRow.IsLocalEndpoint(profile.BaseUrl))
            {
                translationProviders["ollama"]=profile with { Model="" };
                if (TranslationProviderBox.SelectedItem is TranslationProviderChoice { Id:"ollama" })
                { ShowTranslationProvider(); TranslateEnabledBox.IsChecked=false; }
            }
            await RefreshOllamaStateAsync(token);
            if (!ollamaState!.Running || ollamaState.Models.Contains(model)) throw new InvalidOperationException(UiText.T("ollama.verifyFailed"));
            SetOllamaProgress("ollama.deleted",model);
        });
    }
    private async Task RefreshOllamaDriversAsync(bool search,CancellationToken token)
    {
        try { ollamaDrivers=await OllamaDrivers.CheckAsync(search,token); ollamaDriverFailed=false; }
        catch (Exception ex) when (!token.IsCancellationRequested)
        { ollamaDriverFailed=true; AppLog.Write("GPU driver check unavailable",ex); }
        ShowOllamaDrivers();
    }
    private void ShowOllamaDrivers()
    {
        if (OllamaDriverText is null) return;
        var lines=new List<string>();
        if (ollamaDrivers is { } report)
        {
            lines.AddRange(report.Drivers.Select(d=>UiText.T("ollama.driverVersion",d.Name,d.Version,d.Date)+"\n"+ollamaRequirements.Describe(d)));
            if (report.Searched && !ollamaDriverFailed)
                lines.Add(report.Updates.Length>0 ? UiText.T("ollama.driverUpdates",string.Join("\n",report.Updates)) : UiText.T("ollama.driverNoUpdates"));
        }
        if (ollamaDriverFailed || ollamaDrivers?.Drivers.Length==0) lines.Add(UiText.T("ollama.driverUnavailable"));
        OllamaDriverText.Text=string.Join("\n",lines);
        OllamaDriverWarning.Text=string.Join("\n",ollamaDrivers?.Drivers.Where(ollamaRequirements.IsBelowMinimum).Select(ollamaRequirements.Describe) ?? Array.Empty<string>());
        OllamaDriverWarning.Visibility=OllamaDriverWarning.Text.Length>0 ? Visibility.Visible : Visibility.Collapsed;
        var vendors=ollamaDrivers?.Drivers.Select(d=>d.Vendor).ToHashSet() ?? new HashSet<string?>();
        OllamaNvidiaButton.Visibility=vendors.Contains("NVIDIA") ? Visibility.Visible : Visibility.Collapsed;
        OllamaAmdButton.Visibility=vendors.Contains("AMD") ? Visibility.Visible : Visibility.Collapsed;
        OllamaIntelButton.Visibility=vendors.Contains("Intel") ? Visibility.Visible : Visibility.Collapsed;
        OllamaRequirementsText.Text=UiText.T("ollama.requirementsSnapshot",ollamaRequirements.CheckedAt.ToString("yyyy-MM-dd"),
            ollamaRequirements.NvidiaMinimum,ollamaRequirements.LegacyFrom,ollamaRequirements.LegacyThrough,ollamaRequirements.LegacyMinimum,ollamaRequirements.AmdRocmMajor);
    }
    private async void RefreshOllamaRequirements(object sender,RoutedEventArgs e)=>await RunOllamaAsync(async token=>
    {
        SetOllamaProgress("ollama.requirementsRefreshing");
        try
        {
            ollamaRequirements=await OllamaGpuRequirements.RefreshAsync(token);
            ShowOllamaDrivers();
            SetOllamaProgress("ollama.requirementsRefreshed");
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        { AppLog.Write("Ollama GPU requirements refresh failed",ex); SetOllamaProgress("ollama.requirementsRefreshFailed"); }
    });
    private async void CheckOllamaDrivers(object sender,RoutedEventArgs e)=>await RunOllamaAsync(async token=>
    {
        SetOllamaProgress("ollama.driverSearching");
        await RefreshOllamaDriversAsync(true,token);
        SetOllamaProgress(ollamaDriverFailed ? "ollama.driverUnavailable" : "ollama.checked");
    });
    private void OpenOllamaDriverPage(object sender,RoutedEventArgs e)
    {
        string url=(sender as FrameworkElement)?.Tag as string ?? "https://docs.ollama.com/gpu";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute=true }); }
        catch (Exception ex) { SetOllamaProgress("ollama.error",ex.Message); }
    }
}
