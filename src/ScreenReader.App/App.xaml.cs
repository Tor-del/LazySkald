using System.Windows;
using ScreenReader.Edge;
using Forms=System.Windows.Forms;

namespace ScreenReader.App;

public partial class App : Application
{
    private Mutex? instance;
    private bool exitConfirmationOpen;
    private System.Diagnostics.ProcessStartInfo? restart;
    private Forms.NotifyIcon? tray;
    private HotkeyService? hotkeys;
    private System.Drawing.Icon? trayIcon;
    private AppLifetimeSignal? exitSignal;
    private MainWindow? settingsWindow;
    public IReadingOverlay? ReadingOverlay { get; private set; }
    private readonly CancellationTokenSource lifetime=new();
    public AppSettings Settings { get; private set; }=new();
    private ThemeManager? themes;
    public ThemeManager Themes=>themes ??=new ThemeManager(this);
    public WindowsOcrProvider Ocr { get; }=new();
    public SpeechProviderRegistry Providers { get; }=new();
    public ReadingController Reader { get; private set; }=null!;
    public CancellationToken Lifetime=>lifetime.Token;
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool firstRun=!File.Exists(SettingsStore.SettingsPath);
        Settings=SettingsStore.Load();
        string? requestedLanguage=e.Args.FirstOrDefault(a=>a.StartsWith("--ui-language=",StringComparison.Ordinal))?.Split('=',2)[1];
        if (firstRun && UiText.SupportsLanguage(requestedLanguage)) { Settings=Settings with { UiLanguage=requestedLanguage!.ToLowerInvariant() }; SettingsStore.Save(Settings); }
        UiText.SetLanguage(Settings.UiLanguage);
        instance=new Mutex(true,"Local\\ScreenReader.Desktop",out bool created);
        if (!created) { MessageBox.Show(UiText.T("app.alreadyRunning"),"LazySkald"); Shutdown(); return; }
        Themes.Apply(Settings.Theme);
        exitSignal=new AppLifetimeSignal(()=> { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Quit); });
        Reader=new ReadingController(new ScreenCapture(),Ocr,new IdentityTextTransform(),Providers,new StreamingAudioPlayer());
        Reader.UpdateOverlaySettings(Settings.Overlay);
        ReadingOverlay=new ReadingOverlayHost(Reader);
        ReadingOverlay.ApplySettings(Settings.Overlay);
        Reader.StatusChanged += value=>settingsWindow?.SetStatus(value);
        Reader.TextTranslated += value=>settingsWindow?.SetTranslatedText(value);
        Reader.TextRecognized += value=>settingsWindow?.SetRecognizedText(value);
        Reader.Failed += error=>
        {
            string message=error.Message;
            settingsWindow?.SetStatus(message);
            tray?.ShowBalloonTip(5000,"LazySkald",message,Forms.ToolTipIcon.Warning);
        };
        trayIcon=BrandAssets.CreateTrayIcon();
        tray=new Forms.NotifyIcon { Icon=trayIcon,Text="LazySkald — "+(Settings.CaptureHotkey?.ToString() ?? UiText.T("hotkey.unassigned")),Visible=true };
        UpdateTrayLanguage(); UiText.LanguageChanged+=UpdateTrayLanguage;
        tray.DoubleClick += (_,_)=>ShowSettings();
        hotkeys=new HotkeyService();
        hotkeys.OverlayInteractionChanged += held=>ReadingOverlay?.SetInteractionHeld(held);
        hotkeys.ActionRequested += async action=>
        {
            switch (action)
            {
                case HotkeyAction.Toggle: await Reader.ToggleAsync(Settings); break;
                case HotkeyAction.Capture: await Reader.CaptureAndReadAsync(Settings); break;
                case HotkeyAction.RepeatCapture: await Reader.RepeatCaptureAndReadAsync(Settings); break;
                case HotkeyAction.CancelCurrent: Reader.CancelCurrent(); break;
                case HotkeyAction.CancelAll: Reader.Stop(); break;
            }
        };
        string? warning=null;
        try { hotkeys.Configure(Settings); }
        catch (Exception ex) { warning=ex.Message; AppLog.Write("Ошибка регистрации горячих клавиш",ex); }
        try { settingsWindow=new MainWindow(this); MainWindow=settingsWindow; }
        catch (Exception ex) { MessageBox.Show(UiText.T("app.startFailed",ex.Message),"LazySkald"); Quit(); return; }
        if (firstRun || !Settings.StartInTray || warning is not null) ShowSettings();
        else tray.ShowBalloonTip(2500,"LazySkald",UiText.T("tray.started",Settings.CaptureHotkey?.ToString() ?? UiText.T("hotkey.unassigned")),Forms.ToolTipIcon.Info);
        if (warning is not null) settingsWindow.SetStatus(warning);
        AppLog.Write("Приложение запущено");
        if (firstRun)
        {
            // Persist the first launch even if the user closes the wizard without applying it.
            try { SettingsStore.Save(Settings); }
            catch (Exception ex) { AppLog.Write("Could not persist first launch settings",ex); }
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,new Action(()=>
            {
                if (!IsExiting) settingsWindow?.ShowSetupWizard(Settings);
            }));
        }
    }
    public void ApplySettings(AppSettings value)
    {
        var previous=Settings;
        Providers.Get(value.SpeechProviderId);
        hotkeys!.Configure(value);
        try { SettingsStore.Save(value); Settings=value; }
        catch { hotkeys.Configure(previous); throw; }
        Themes.Apply(value.Theme);
        Reader.UpdateOverlaySettings(value.Overlay);
        ReadingOverlay?.ApplySettings(value.Overlay);
        if (previous.SpeechEnabled && !value.SpeechEnabled) Reader.Stop();
        if (tray is not null) tray.Text="LazySkald — "+(value.CaptureHotkey?.ToString() ?? UiText.T("hotkey.unassigned"));
    }
    public void SetHotkeyEditing(bool editing)
    {
        try { hotkeys?.SetSuspended(editing); }
        catch (Exception e) { settingsWindow?.SetStatus(e.Message); AppLog.Write("Ошибка восстановления горячих клавиш",e); }
    }
    internal void ReloadSettingsWindow()
    {
        var previous=settingsWindow;
        var replacement=new MainWindow(this);
        settingsWindow=replacement; MainWindow=replacement;
        previous?.CloseForReload();
        ShowSettings();
    }
    private void UpdateTrayLanguage()
    {
        if (tray is null) return;
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add(UiText.T("tray.toggle"),null,async (_,_)=>await Reader.ToggleAsync(Settings));
        menu.Items.Add(UiText.T("button.stop"),null,(_,_)=>Reader.Stop());
        menu.Items.Add(UiText.T("tray.settings"),null,(_,_)=>ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(UiText.T("tray.exit"),null,(_,_)=>RequestQuit());
        var previous=tray.ContextMenuStrip; tray.ContextMenuStrip=menu; previous?.Dispose();
    }
    private void ShowSettings()
    { settingsWindow?.Show(); if (settingsWindow is not null) { settingsWindow.WindowState=WindowState.Normal; settingsWindow.Activate(); } }
    public void RequestQuit()
    {
        if (IsExiting || exitConfirmationOpen) return;
        if (!Settings.SkipExitConfirmation)
        {
            exitConfirmationOpen=true;
            try
            {
                ShowSettings();
                var dialog=new ExitConfirmationWindow { Owner=settingsWindow };
                if (dialog.ShowDialog()!=true) return;
                if (dialog.DoNotAskAgain)
                {
                    var updated=Settings with { SkipExitConfirmation=true };
                    SettingsStore.Save(updated);
                    Settings=updated;
                }
            }
            catch (Exception ex) { settingsWindow?.SetStatus(ex.Message); return; }
            finally { exitConfirmationOpen=false; }
        }
        Quit();
    }
    public void ResetSettingsAndRestart()
    {
        string executable=Environment.ProcessPath ?? throw new InvalidOperationException("Application executable is unavailable.");
        var launch=new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute=false };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
            launch.ArgumentList.Add(typeof(App).Assembly.Location);
        // Removing the saved settings restores the first-launch flow, including the wizard.
        File.Delete(SettingsStore.SettingsPath);
        restart=launch;
        Quit();
    }
    public void Quit()
    {
        if (IsExiting) return;
        IsExiting=true; Reader?.Stop(); lifetime.Cancel(); ReadingOverlay?.Dispose(); settingsWindow?.Close(); Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        UiText.LanguageChanged-=UpdateTrayLanguage;
        themes?.Dispose();
        exitSignal?.Dispose(); hotkeys?.Dispose(); tray?.Dispose(); trayIcon?.Dispose(); Providers.Dispose(); lifetime.Dispose(); instance?.Dispose();
        base.OnExit(e);
        if (restart is not null)
        {
            try { System.Diagnostics.Process.Start(restart); }
            catch (Exception ex) { AppLog.Write("Could not restart application",ex); MessageBox.Show(ex.Message,"LazySkald"); }
        }
    }
}
