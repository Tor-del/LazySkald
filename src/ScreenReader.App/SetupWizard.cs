using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenReader.App;

internal enum WizardOllamaAction { Use, DownloadModel, InstallAndDownload }
internal sealed record WizardOllamaPlan(WizardOllamaAction Action,string Model);

// A separate window keeps the selected settings section in place.
public sealed class SetupWizard : Window
{
    private readonly App app;
    private readonly AppSettings initial;
    private readonly CancellationTokenSource lifetime;
    private readonly StackPanel page=new();
    private readonly TextBlock heading=new() { FontSize=26,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap };
    private readonly TextBlock status=new() { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12) };
    private readonly Button back=new(),next=new(),cancel=new();
    private readonly ComboBox source=new(),target=new();
    private readonly ListBox speechChoices=new(),translationChoices=new();
    private readonly ListBoxItem speechLater=new();
    private IReadOnlyList<SpeechVoice> speechVoices=LanguageCatalog.EdgeVoices;
    private SpeechVoice? selectedSpeechVoice;
    private bool speechCatalogLoaded;
    private readonly TextBox hotkeyBox=new() { IsReadOnly=true };
    private readonly CheckBox translate=new(),overlay=new();
    private readonly ListBoxItem automatic=new(),later=new();
    private readonly OllamaSetupClient ollama=new();
    private Hotkey? hotkey;
    private IReadOnlyList<SpeechVoice> windowsVoices=[];
    private OllamaModelChoice? recommendation;
    private WizardOllamaPlan? ollamaPlan;
    private TranslationSettings translation;
    private int step;
    private bool busy,closed;
    public bool Applied { get; private set; }
    internal static string Text(string ru,string en)=>UiText.Language=="ru" ? ru : en;
    public static string Caption=>UiText.T("wizard.title");

    public SetupWizard(App app,AppSettings initial)
    {
        this.app=app; this.initial=initial; translation=initial.Translation; hotkey=initial.CaptureHotkey;
        lifetime=CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime);
        Title=Caption; Width=760; Height=650; MinWidth=620; MinHeight=520;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty,"FluentBackdrop");
        SetResourceReference(ForegroundProperty,"FluentText");
        FontFamily=new FontFamily("Segoe UI"); FontSize=15;
        var root=new DockPanel { Margin=new Thickness(30) }; Content=root;
        root.SetResourceReference(Panel.BackgroundProperty,"FluentBackdrop");
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
        DockPanel.SetDock(buttons,Dock.Bottom); root.Children.Add(buttons);
        foreach (var button in new[] { cancel,back,next }) { button.Margin=new Thickness(8,0,0,0); buttons.Children.Add(button); }
        cancel.Content=Text("Закрыть","Close"); back.Content=Text("Назад","Back");
        cancel.Click+=(_,_)=>Close(); back.Click+=(_,_)=> { step=PreviousStep(step,translate.IsChecked==true); Render(); };
        next.Click+=async (_,_)=>await AdvanceAsync();
        DockPanel.SetDock(heading,Dock.Top); root.Children.Add(heading);
        DockPanel.SetDock(status,Dock.Bottom); root.Children.Add(status);
        root.Children.Add(new ScrollViewer { Content=page,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,20,0,16) });
        var languages=LanguageCatalog.RecognitionChoices(app.Ocr.Languages,initial.Language);
        FillLanguages(source,languages,initial.Language); FillLanguages(target,languages,translation.TargetLanguage);
        foreach (var choices in new[] { speechChoices,translationChoices }) choices.SetResourceReference(StyleProperty,"WizardChoiceList");
        SetOptionText(speechLater,Text("Настрою сам позже","I will set it up myself later"));
        speechChoices.SelectionChanged+=(_,_)=>selectedSpeechVoice=(speechChoices.SelectedItem as ListBoxItem)?.Tag as SpeechVoice;
        hotkeyBox.Text=hotkey?.ToString() ?? "";
        hotkeyBox.GotKeyboardFocus+=(_,_)=>app.SetHotkeyEditing(true);
        hotkeyBox.LostKeyboardFocus+=(_,_)=>app.SetHotkeyEditing(false);
        hotkeyBox.PreviewKeyDown+=CaptureKey;
        translate.Content=Text("Переводить распознанный текст","Translate recognized text"); translate.IsChecked=translation.Enabled;
        overlay.Content=Text("Включить оверлей","Enable overlay"); overlay.IsChecked=initial.Overlay.Enabled;
        SetOptionText(automatic,Text("Проверяем Ollama…","Checking Ollama…"));
        SetOptionText(later,Text("Настрою сам позже","I will set it up myself later"));
        translationChoices.Items.Add(automatic); translationChoices.Items.Add(later); later.IsSelected=true;
        SourceInitialized+=(_,_)=>app.Themes.ApplyTitleBar(this);
        app.Reader.TextRecognized+=Recognized; app.Reader.Failed+=Failed;
        Closed+=(_,_)=>
        {
            closed=true; lifetime.Cancel(); app.SetHotkeyEditing(false);
            app.Reader.TextRecognized-=Recognized; app.Reader.Failed-=Failed;
            lifetime.Dispose();
        };
        Render();
    }

    internal static int NextStep(int step,bool translation)=>step==2 && !translation ? 4 : step+1;
    internal static int PreviousStep(int step,bool translation)=>step==4 && !translation ? 2 : step-1;
    private static void FillLanguages(ComboBox box,IEnumerable<LanguageChoice> choices,string tag)
    {
        box.DisplayMemberPath="Name"; box.ItemsSource=choices.ToArray();
        box.SelectedItem=box.Items.Cast<LanguageChoice>().FirstOrDefault(l=>l.Tag.Equals(tag,StringComparison.OrdinalIgnoreCase)) ?? box.Items[0];
    }
    private static string SelectedLanguage(ComboBox box)=>((LanguageChoice)box.SelectedItem).Tag;
    private void Add(string text)=>page.Children.Add(new TextBlock { Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,18) });
    private void Field(string label,Control control)
    {
        if (label.Length>0) Add(label);
        control.Margin=new Thickness(0,0,0,20); page.Children.Add(control);
    }
    private void Render()
    {
        page.Children.Clear(); status.Text="";
        back.IsEnabled=step>0 && step<6;
        next.Content=step==5 ? Text("Сохранить и попробовать","Save and try") : step==6 ? Text("Пропустить","Skip") : step==7 ? Text("Готово","Finish") : Text("Далее","Next");
        if (step==0)
        {
            heading.Text=Text("Добро пожаловать в LazySkald","Welcome to LazySkald");
            Add(Text("LazySkald распознаёт текст в выбранной области экрана, при необходимости переводит его и читает вслух. Одна горячая клавиша запускает выбор области и добавляет текст в очередь озвучивания.","LazySkald recognizes text in a selected screen area, optionally translates it, and reads it aloud. One shortcut starts area selection and adds the text to the speech queue."));
            Add(Text("Мастер поможет выбрать язык, горячую клавишу, перевод, голос и оверлей. Настройки сохранятся перед пробным распознаванием. До этого мастер можно закрыть без сохранения; загруженные компоненты Ollama останутся установленными.","This wizard sets up your language, shortcut, translation, voice and overlay. Settings are saved before the recognition trial. Until then you can close without saving; any downloaded Ollama components remain installed."));
        }
        else if (step==1)
        {
            heading.Text=Text("Распознавание и горячая клавиша","Recognition and shortcut");
            Field(Text("На каком языке текст на экране?","What language is the screen text in?"),source);
            Field(Text("Нажмите сочетание клавиш в поле ниже","Press your shortcut in the field below"),hotkeyBox);
            Add(Text("После нажатия выделите область мышью. Текст распознаётся и сразу добавляется в очередь — вторая горячая клавиша не нужна.","Press the shortcut, then select an area with the mouse. Recognized text is added to the queue immediately; no second shortcut is needed."));
        }
        else if (step==2)
        {
            heading.Text=Text("Перевод текста","Text translation");
            Field("",translate); Field(Text("Целевой язык перевода","Translation target language"),target);
        }
        else if (step==3)
        {
            heading.Text=Text("Подготовка перевода","Prepare translation");
            Add(Text("Для перевода нужна большая языковая модель (LLM). Ollama запускает её на вашем компьютере. Мастер проверит установленную Ollama и подходящую модель и предложит нужное действие. При необходимости загрузка может занять несколько гигабайт.","Translation needs a large language model (LLM). Ollama runs it on your computer. The wizard checks for Ollama and a suitable model and offers the appropriate action. If a download is needed, it may require several gigabytes."));
            ollamaPlan=null; automatic.IsEnabled=false;
            SetOptionText(automatic,Text("Проверяем Ollama…","Checking Ollama…"));
            Field("",translationChoices);
            Add(Text("Если отложить настройку, целевой язык сохранится, а перевод останется выключенным. Включите его во вкладке «Перевод», когда настроите провайдер.","If you defer setup, the target language is saved and translation stays off. Enable it in Translation once your provider is configured."));
            _=CheckOllamaAsync();
        }
        else if (step==4)
        {
            heading.Text=Text("Голос озвучивания","Speech voice");
            Add(Text($"Язык озвучивания: {LanguageCatalog.DisplayName(SpeechLanguage)}. Он выбран на предыдущих шагах. Выберите голос Microsoft Edge TTS или отложите настройку.",$"Speech language: {LanguageCatalog.DisplayName(SpeechLanguage)}. It follows your earlier choices. Choose a Microsoft Edge TTS voice or set it up later."));
            Add(Text("В настройках на вкладке «Озвучивание» можно подробнее настроить провайдер, голоса, скорость и устройство вывода. Если выбрать «Настрою сам позже», текущие параметры озвучивания сохранятся.","In the Speech settings you can configure the provider, voices, speed and audio output in more detail. Choosing to set it up later preserves your current speech settings."));
            page.Children.Add(speechChoices); RenderSpeechChoices();
            Add(Text("Для основного голоса нужен интернет. Если сервис озвучивания недоступен, будет использован локальный голос Windows. Язык резервного голоса зависит от установленных в Windows голосов.","The primary voice needs internet access. If the speech service is unavailable, a local Windows voice will be used. Its language depends on the voices installed in Windows."));
            if (!speechCatalogLoaded) _=LoadSpeechVoicesAsync();
        }
        else if (step==5)
        {
            heading.Text=Text("Оверлей поверх экрана","On-screen overlay");
            Field("",overlay);
            Add(Text("Оверлей показывает очередь и управление озвучиванием поверх других окон. Будет использован обычный настольный оверлей WPF.","The overlay shows the queue and playback controls above other windows. The standard WPF desktop overlay will be used."));
            Add(Text("Удерживайте ALT, чтобы взаимодействовать с оверлеем: нажимать кнопки и перемещать его. Когда ALT отпущен, нажатия проходят в приложение под ним.","Hold ALT to interact with the overlay: click its buttons or move it. Release ALT to pass clicks through to the application below."));
        }
        else if (step==6)
        {
            heading.Text=Text("Попробуйте распознавание","Try recognition");
            Add(Text($"Настройки сохранены. Нажмите {hotkey}, затем выделите только текст в рамке. Он будет добавлен в очередь озвучивания. Можно пропустить этот шаг.",$"Settings saved. Press {hotkey}, then select only the text in the box. It will be added to the speech queue. You can skip this step."));
            var sample=new TextBlock { Text=Sample(SelectedLanguage(source)),FontSize=26,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.Black,Margin=new Thickness(20) };
            page.Children.Add(new Border { Background=Brushes.White,CornerRadius=new CornerRadius(8),Child=sample });
        }
        else
        {
            heading.Text=Text("Настройка завершена!","Setup complete!");
            Add(Text("Спасибо, что используете LazySkald. Приятного прослушивания! Все параметры можно изменить в настройках, а мастер — открыть снова в любой момент.","Thank you for using LazySkald. Enjoy listening! You can change any option in settings and reopen this wizard at any time."));
        }
    }

    private static void SetOptionText(ContentControl option,string text)=>option.Content=new TextBlock { Text=text,TextWrapping=TextWrapping.Wrap };

    internal static WizardOllamaPlan ChooseOllamaPlan(OllamaState state,string recommended,string? preferred=null)
    {
        bool installed=state.Running || state.Executable is not null;
        string model=installed && preferred is not null && state.Models.Contains(preferred,StringComparer.OrdinalIgnoreCase)
            ? preferred : recommended;
        var action=!installed ? WizardOllamaAction.InstallAndDownload
            : state.Models.Contains(model,StringComparer.OrdinalIgnoreCase) ? WizardOllamaAction.Use : WizardOllamaAction.DownloadModel;
        return new(action,model);
    }

    private void ShowOllamaPlan(WizardOllamaPlan plan)
    {
        ollamaPlan=plan;
        SetOptionText(automatic,plan.Action switch
        {
            WizardOllamaAction.Use=>Text($"Использовать Ollama и установленную модель {plan.Model}",$"Use Ollama and the installed {plan.Model} model"),
            WizardOllamaAction.DownloadModel=>Text($"Скачать модель {plan.Model} для установленной Ollama",$"Download the {plan.Model} model for installed Ollama"),
            _=>Text($"Установить Ollama и скачать модель {plan.Model}",$"Install Ollama and download the {plan.Model} model")
        });
        automatic.IsEnabled=true;
    }

    private string SpeechLanguage=>translate.IsChecked==true ? SelectedLanguage(target) : SelectedLanguage(source);
    internal static SpeechVoice[] VoicesForLanguage(IEnumerable<SpeechVoice> voices,string language)=>
        voices.Where(v=>v.Locale.Equals(language,StringComparison.OrdinalIgnoreCase)).OrderBy(v=>v.FriendlyName).ToArray();

    private void RenderSpeechChoices()
    {
        var available=VoicesForLanguage(speechVoices,SpeechLanguage);
        string? selected=available.FirstOrDefault(v=>v.ShortName==selectedSpeechVoice?.ShortName)?.ShortName;
        speechChoices.Items.Clear();
        speechChoices.Items.Add(speechLater); speechLater.IsSelected=selected is null;
        foreach (var choice in available)
        {
            var row=new StackPanel { Orientation=Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text=choice.FriendlyName,TextWrapping=TextWrapping.Wrap,MaxWidth=490,VerticalAlignment=VerticalAlignment.Center });
            var option=new ListBoxItem { Tag=choice,Content=row };
            var preview=new Button { Content="▶",Tag=choice,Margin=new Thickness(10,0,0,0),Padding=new Thickness(0),Width=28,Height=28,MinWidth=0,MinHeight=0,
                ToolTip=Text($"Прослушать голос {choice.FriendlyName}",$"Listen to {choice.FriendlyName}") };
            System.Windows.Automation.AutomationProperties.SetName(preview,preview.ToolTip.ToString());
            preview.Click+=async (_,_)=>
            {
                option.IsSelected=true;
                await RunAsync(async token=>await app.Reader.ReadTextAsync(Sample(choice.Locale),VoicePreviewSettings(initial,choice)));
            };
            row.Children.Add(preview); speechChoices.Items.Add(option); option.IsSelected=selected==choice.ShortName;
        }
        if (available.Length==0) speechChoices.Items.Add(new ListBoxItem { IsEnabled=false,Content=new TextBlock
        {
            Text=Text("Для этого языка нет голосов в списке. Настройте провайдер озвучивания позже во вкладке «Озвучивание».","No voices are listed for this language. Configure a speech provider later in Speech settings."),
            TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,18)
        } });
    }

    private async Task LoadSpeechVoicesAsync()=>await RunAsync(async token=>
    {
        try
        {
            using var lease=app.Providers.Acquire(initial with { SpeechProviderId="edge" });
            var loaded=await lease.Provider.GetVoicesAsync(token);
            token.ThrowIfCancellationRequested(); speechVoices=loaded; speechCatalogLoaded=true;
            RenderSpeechChoices();
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            AppLog.Write("Wizard voice catalog unavailable",ex);
            status.Text=Text("Не удалось обновить список голосов. Показан встроенный список; для прослушивания нужен интернет.","Could not refresh voices. Showing the built-in list; listening needs internet access.");
        }
    });

    internal static AppSettings VoicePreviewSettings(AppSettings initial,SpeechVoice voice)=>initial with
    {
        Language=voice.Locale,SpeechEnabled=true,SpeechProviderId="edge",Voice=voice.ShortName,
        ProviderVoices=new(initial.ProviderVoices) { ["edge"]=voice.ShortName },FallbackRole=null,
        Translation=initial.Translation with { Enabled=false },Genderization=initial.Genderization with { Enabled=false },
        CacheEnabled=false,Overlay=initial.Overlay with { Enabled=false }
    };
    private void CaptureKey(object sender,KeyEventArgs e)
    {
        if (e.Key==Key.Tab) return;
        e.Handled=true;
        var key=e.Key==Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var mods=Keyboard.Modifiers;
        uint flags=((mods&ModifierKeys.Alt)!=0 ? 1u:0)|((mods&ModifierKeys.Control)!=0 ? 2u:0)|((mods&ModifierKeys.Shift)!=0 ? 4u:0)|((mods&ModifierKeys.Windows)!=0 ? 8u:0);
        var candidate=new Hotkey(flags,(uint)KeyInterop.VirtualKeyFromKey(key));
        try { HotkeyService.Validate(candidate); hotkey=candidate; hotkeyBox.Text=candidate.ToString(); status.Text=""; }
        catch (Exception ex) { status.Text=ex.Message; }
    }
    private async Task CheckOllamaAsync()=>await RunAsync(async token=>
    {
        var hardware=await Task.Run(OllamaHardware.Detect,token);
        recommendation=OllamaModelChoice.Recommend(hardware.RamGb,hardware.VramGb);
        var state=await ollama.CheckAsync(token);
        if (!state.Running && state.Executable is not null) { await ollama.EnsureRunningAsync(token); state=await ollama.CheckAsync(token); }
        token.ThrowIfCancellationRequested();
        string? preferred=translation.Provider=="ollama" && OllamaModelRow.IsLocalEndpoint(translation.Selected.BaseUrl) ? translation.Selected.Model : null;
        var plan=ChooseOllamaPlan(state,recommendation.Tag,preferred);
        ShowOllamaPlan(plan);
        status.Text=plan.Action==WizardOllamaAction.Use
            ? Text("Всё готово. Ollama и модель будут выбраны провайдером перевода автоматически.","Everything is ready. Ollama and this model will be configured for translation automatically.")
            : Text($"Рекомендованная модель: {recommendation.Name}. После загрузки провайдер перевода будет настроен автоматически.",$"Recommended model: {recommendation.Name}. Translation will be configured automatically after the download.");
    });
    private async Task AdvanceAsync()
    {
        if (busy) return;
        if (step==7) { Close(); return; }
        int previous=step;
        await RunAsync(async token=>
        {
            if (step==1)
            {
                if (hotkey is null) throw new InvalidOperationException(Text("Задайте горячую клавишу.","Choose a shortcut."));
                if (source.SelectedItem is LanguageChoice { Installed:false }) throw new InvalidOperationException(UiText.T("ocr.missing"));
                HotkeyService.Validate(initial with { CaptureHotkey=hotkey,OverlayInteractionHotkey=new(0,0x12) });
            }
            if (step==3)
            {
                if (later.IsSelected) translation=translation with { Enabled=false,TargetLanguage=SelectedLanguage(target) };
                else
                {
                    var plan=ollamaPlan ?? throw new InvalidOperationException(Text("Дождитесь проверки Ollama или выберите «Настрою сам позже».","Wait for the Ollama check or choose to set it up later."));
                    string model=plan.Model;
                    var progress=new Progress<OllamaProgress>(p=> { if (!closed) status.Text=(p.Stage.StartsWith("ollama.",StringComparison.Ordinal) ? UiText.T(p.Stage) : p.Stage)+(p.Percent is double percent ? $" · {percent:0}%" : ""); });
                    var state=await ollama.CheckAsync(token);
                    if (!state.Running && state.Executable is null)
                    {
                        if (plan.Action!=WizardOllamaAction.InstallAndDownload) throw new InvalidOperationException(Text("Ollama больше не найдена. Вернитесь на предыдущий шаг и повторите проверку.","Ollama is no longer available. Go back and check again."));
                        var hardware=await Task.Run(OllamaHardware.Detect,token);
                        bool amd=hardware.Gpu.Contains("AMD",StringComparison.OrdinalIgnoreCase) || hardware.Gpu.Contains("Radeon",StringComparison.OrdinalIgnoreCase);
                        await ollama.InstallAsync(progress,()=>{},token,amd);
                    }
                    await ollama.EnsureRunningAsync(token);
                    state=await ollama.CheckAsync(token);
                    if (!state.Models.Contains(model,StringComparer.OrdinalIgnoreCase))
                    {
                        if (plan.Action==WizardOllamaAction.Use) throw new InvalidOperationException(Text("Модель больше не найдена. Вернитесь на предыдущий шаг и повторите проверку.","The model is no longer available. Go back and check again."));
                        await ollama.PullAsync(model,progress,token);
                    }
                    var verified=await ollama.CheckAsync(token);
                    if (!verified.Running || !verified.Models.Contains(model)) throw new InvalidOperationException(UiText.T("ollama.verifyFailed"));
                    translation=translation with { Enabled=true,Provider="ollama",TargetLanguage=SelectedLanguage(target),Providers=new(translation.Providers) { ["ollama"]=new() { BaseUrl=OllamaSetupClient.BaseUrl,Model=model } } };
                }
            }
            if (step==5)
            {
                var settings=await BuildSettingsAsync(token);
                app.SetHotkeyEditing(false); app.ApplySettings(settings); Applied=true;
            }
            step=NextStep(step,translate.IsChecked==true);
        });
        if (!closed && previous!=step) Render();
    }
    private async Task RunAsync(Func<CancellationToken,Task> action)
    {
        if (busy || closed) return;
        busy=true; next.IsEnabled=back.IsEnabled=false; page.IsEnabled=false; status.Text=Text("Подождите…","Please wait…");
        try
        {
            await action(lifetime.Token);
            if (!closed && status.Text==Text("Подождите…","Please wait…")) status.Text="";
        }
        catch (OperationCanceledException) { if (!closed) status.Text=Text("Операция отменена или истекло время ожидания. Повторите попытку.","Operation cancelled or timed out. Please try again."); }
        catch (Exception ex) { if (!closed) status.Text=ex.Message; AppLog.Write("Setup wizard operation failed",ex); }
        finally { busy=false; if (!closed) { next.IsEnabled=true; back.IsEnabled=step>0 && step<6; page.IsEnabled=true; } }
    }
    private async Task<AppSettings> BuildSettingsAsync(CancellationToken token)
    {
        var settings=initial with
        {
            Language=SelectedLanguage(source),CaptureHotkey=hotkey,
            Translation=translation with { Enabled=translate.IsChecked==true && translation.Enabled,TargetLanguage=SelectedLanguage(target) },
            Overlay=initial.Overlay with { Enabled=overlay.IsChecked==true,Renderer=OverlayRenderer.Desktop },
            OverlayInteractionHotkey=new(0,0x12)
        };
        if (selectedSpeechVoice is null) return settings;
        using var lease=app.Providers.Acquire(initial with { SpeechProviderId="windows" });
        windowsVoices=await lease.Provider.GetVoicesAsync(token);
        return ApplyWizardVoice(settings,selectedSpeechVoice,windowsVoices);
    }

    internal static AppSettings ApplyWizardVoice(AppSettings settings,SpeechVoice? selected,IReadOnlyList<SpeechVoice> localVoices)
    {
        if (selected is null) return settings;
        var fallback=localVoices.FirstOrDefault(v=>v.Locale.Equals(selected.Locale,StringComparison.OrdinalIgnoreCase)) ?? localVoices.FirstOrDefault();
        if (fallback is null) throw new InvalidOperationException(Text("В Windows нет локальных голосов. Установите голос в параметрах речи Windows и повторите попытку.","Windows has no local voices. Install a voice in Windows speech settings and retry."));
        return settings with
        {
            SpeechEnabled=true,SpeechProviderId="edge",Voice=selected.ShortName,
            ProviderVoices=new(settings.ProviderVoices) { ["edge"]=selected.ShortName },
            FallbackRole=SpeechRoleSettings.From(settings) with { ProviderId="windows",ProviderVoices=new() { ["windows"]=fallback.ShortName } }
        };
    }
    private void Recognized(string text)
    {
        if (closed || step!=6) return;
        Dispatcher.Invoke(()=> { status.Text=Text("Текст распознан и отправлен в очередь. Можно завершить настройку.","Text recognized and added to the queue. You can finish setup."); next.Content=Text("Продолжить","Continue"); });
    }
    private void Failed(Exception ex) { if (!closed && step is 4 or 6) Dispatcher.Invoke(()=>status.Text=ex.Message); }
    internal static string Sample(string language)=>language.Split('-')[0].ToLowerInvariant() switch
    {
        "ru"=>"Добро пожаловать, путник! За старым мостом начинается новая история.",
        "en"=>"Welcome, traveler! Beyond the old bridge, a new story begins.",
        "zh"=>"欢迎你，旅行者！古老的桥梁之外，一个新的故事开始了。",
        "ja"=>"旅人よ、ようこそ！古い橋の向こうで、新しい物語が始まります。",
        "es"=>"¡Bienvenido, viajero! Más allá del viejo puente comienza una nueva historia.",
        "ko"=>"여행자여, 환영합니다! 오래된 다리 너머에서 새로운 이야기가 시작됩니다.",
        "fr"=>"Bienvenue, voyageur ! Au-delà du vieux pont, une nouvelle histoire commence.",
        "de"=>"Willkommen, Reisender! Jenseits der alten Brücke beginnt eine neue Geschichte.",
        _=>GenericSample(language)
    };
    private static string GenericSample(string language)
    {
        try { return System.Globalization.CultureInfo.GetCultureInfo(language).NativeName+" — 12345"; }
        catch (System.Globalization.CultureNotFoundException) { return "12345"; }
    }
}
