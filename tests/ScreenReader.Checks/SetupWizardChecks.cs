using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenReader.App;

internal static partial class Program
{
    private static void RunSetupWizardChecks()
    {
        const string recommended="translategemma:4b";
        Assert(SetupWizard.ChooseOllamaPlan(new(null,null,[]),recommended).Action==WizardOllamaAction.InstallAndDownload,
            "Missing Ollama offers installation and model download");
        Assert(SetupWizard.ChooseOllamaPlan(new("ollama.exe",null,[]),recommended).Action==WizardOllamaAction.DownloadModel,
            "Installed stopped Ollama offers only the model download");
        Assert(SetupWizard.ChooseOllamaPlan(new(null,"1.0",[]),recommended).Action==WizardOllamaAction.DownloadModel,
            "Running Ollama without a discoverable executable is reused");
        Assert(SetupWizard.ChooseOllamaPlan(new("ollama.exe","1.0",[recommended]),recommended).Action==WizardOllamaAction.Use,
            "Installed recommended model is used without downloading");
        Assert(SetupWizard.ChooseOllamaPlan(new("ollama.exe","1.0",["unrelated:latest"]),recommended).Action==WizardOllamaAction.DownloadModel,
            "An unrelated installed model does not replace the translation model");
        Assert(SetupWizard.ChooseOllamaPlan(new("ollama.exe","1.0",["qwen2.5:7b"]),recommended,"qwen2.5:7b")
            is { Action:WizardOllamaAction.Use,Model:"qwen2.5:7b" },"Previously configured installed model is reused");
        foreach (bool translate in new[] { false,true })
        {
            int[] path=translate ? [0,1,2,3,4,5,6,7] : [0,1,2,4,5,6,7];
            for (int i=0;i<path.Length-1;i++)
            {
                Assert(SetupWizard.NextStep(path[i],translate)==path[i+1],"Wizard follows conditional forward path");
                if (path[i+1]<6) Assert(SetupWizard.PreviousStep(path[i+1],translate)==path[i],"Wizard back follows conditional path");
            }
        }
        Assert(LanguageCatalog.Tags.Select(SetupWizard.Sample).Distinct().Count()==LanguageCatalog.Tags.Count,
            "Recognition trial has a distinct sample for every built-in language");
        var app=new PreviewApp();
        using var capture=new BlockedCapture();
        var reader=new ReadingController(capture,new NoOcr(),new IdentityTextTransform(),app.Providers,new GainTestAudio(),new NoFocus());
        typeof(App).GetProperty(nameof(App.Reader))!.SetValue(app,reader);
        UiText.SetLanguage("ru");
        var initial=new AppSettings();
        var wizard=new SetupWizard(app,initial);
        typeof(SetupWizard).GetField("speechCatalogLoaded",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(wizard,true);
        var step=typeof(SetupWizard).GetField("step",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var render=typeof(SetupWizard).GetMethod("Render",BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly)!;
        string directory=Path.Combine(Directory.GetCurrentDirectory(),"artifacts","wizard"); Directory.CreateDirectory(directory);
        var busy=typeof(SetupWizard).GetField("busy",BindingFlags.NonPublic|BindingFlags.Instance)!;
        foreach (int index in new[] { 0,1,2,3,4,5,6,7 })
        {
            // Render the provider page without contacting or starting a real Ollama server.
            busy.SetValue(wizard,index==3);
            step.SetValue(wizard,index); render.Invoke(wizard,null);
            busy.SetValue(wizard,false);
            var content=(FrameworkElement)wizard.Content;
            content.Measure(new Size(760,650)); content.Arrange(new Rect(0,0,760,650)); content.UpdateLayout();
            var bitmap=new RenderTargetBitmap(760,650,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
            using var output=File.Create(Path.Combine(directory,$"step-{index}.png"));
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(output);
            Assert(content.DesiredSize.Height<=650,"Wizard page fits its viewport: "+index);
            if (index==4)
            {
                var page=(StackPanel)typeof(SetupWizard).GetField("page",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(wizard)!;
                Assert(!page.Children.OfType<ComboBox>().Any(),"Speech step has no language or voice dropdown");
            }
        }
        Assert(!wizard.Applied,"Previewing wizard pages does not save settings");
        var showPlan=typeof(SetupWizard).GetMethod("ShowOllamaPlan",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var automatic=(ListBoxItem)typeof(SetupWizard).GetField("automatic",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(wizard)!;
        var later=(ListBoxItem)typeof(SetupWizard).GetField("later",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(wizard)!;
        foreach (var theme in new[] { AppTheme.Light,AppTheme.Dark })
        {
            app.Themes.Apply(theme);
            foreach (var action in Enum.GetValues<WizardOllamaAction>())
            {
                busy.SetValue(wizard,true); step.SetValue(wizard,3); render.Invoke(wizard,null); busy.SetValue(wizard,false);
                showPlan.Invoke(wizard,[new WizardOllamaPlan(action,recommended)]);
                automatic.IsSelected=true;
                var content=(FrameworkElement)wizard.Content;
                content.Measure(new Size(760,650)); content.Arrange(new Rect(0,0,760,650)); content.UpdateLayout();
                var nextButton=(Button)typeof(SetupWizard).GetField("next",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(wizard)!;
                var nextPosition=nextButton.TransformToAncestor(content).Transform(new Point());
                Assert(nextPosition.Y+nextButton.ActualHeight<=620,"Provider page keeps navigation buttons in view: "+theme+"/"+action+" y="+nextPosition.Y);
                var choices=(ListBox)typeof(SetupWizard).GetField("translationChoices",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(wizard)!;
                var options=choices.Items.Cast<ListBoxItem>().ToArray();
                Assert(options.Length==2 && options.Contains(automatic) && options.Contains(later),"Provider page offers one Ollama action and defer: "+action);
                Assert(later.IsSelected==false,"Provider options remain mutually exclusive");
                var outline=(Border)automatic.Template.FindName("ChoiceBorder",automatic);
                Assert(((SolidColorBrush)outline.BorderBrush).Color==((SolidColorBrush)app.FindResource("FluentAccent")).Color && outline.BorderThickness.Left==2,
                    "Selected provider has accent outline: "+theme);
                Assert(((SolidColorBrush)automatic.Foreground).Color==((SolidColorBrush)app.FindResource("FluentText")).Color,
                    "Choice text follows current theme: "+theme);
                var bitmap=new RenderTargetBitmap(760,650,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
                using var output=File.Create(Path.Combine(directory,$"provider-{theme}-{action}.png"));
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(output);
            }
        }
        var source=(ComboBox)typeof(SetupWizard).GetField("source",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(wizard)!;
        var target=(ComboBox)typeof(SetupWizard).GetField("target",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(wizard)!;
        var translateBox=(CheckBox)typeof(SetupWizard).GetField("translate",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(wizard)!;
        var speechChoices=(ListBox)typeof(SetupWizard).GetField("speechChoices",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(wizard)!;
        var speechLater=(ListBoxItem)typeof(SetupWizard).GetField("speechLater",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(wizard)!;
        var selectedVoice=typeof(SetupWizard).GetField("selectedSpeechVoice",BindingFlags.NonPublic|BindingFlags.Instance)!;
        foreach (bool translateSpeech in new[] { false,true })
        foreach (string tag in LanguageCatalog.Tags)
        {
            translateBox.IsChecked=translateSpeech;
            var box=translateSpeech ? target : source;
            box.SelectedItem=box.Items.Cast<LanguageChoice>().First(l=>l.Tag==tag);
            step.SetValue(wizard,4); render.Invoke(wizard,null);
            var options=speechChoices.Items.OfType<ListBoxItem>().Where(i=>i.Tag is ScreenReader.Speech.SpeechVoice).ToArray();
            var rows=options.Select(i=>(StackPanel)i.Content).ToArray();
            Assert(options.Length>0 && options.All(o=>o.Tag is ScreenReader.Speech.SpeechVoice v && v.Locale==tag),
                "Only effective language voices are offered: "+tag+"/translation="+translateSpeech);
            Assert(rows.All(r=>r.Children.OfType<Button>().Single().Tag is ScreenReader.Speech.SpeechVoice v && v.Locale==tag),
                "Every offered voice has its own matching sample button");
            options[0].IsSelected=true;
            var choice=(ScreenReader.Speech.SpeechVoice)options[0].Tag;
            Assert(selectedVoice.GetValue(wizard)==choice && speechLater.IsSelected==false,"Selecting a voice deselects defer");
            Assert(speechChoices.SelectionMode==SelectionMode.Single && speechChoices.SelectedItems.Count==1,"Voice list permits exactly one selection");
            var preview=SetupWizard.VoicePreviewSettings(new AppSettings
            {
                Language="ru-RU",Translation=new() { Enabled=true,TargetLanguage="de-DE" }
            },choice);
            Assert(preview.Language==tag && preview.SelectedVoice==choice.ShortName && preview.SpeechProviderId=="edge" &&
                !preview.Translation.Enabled && preview.FallbackRole is null,
                "Voice sample uses the selected voice language without translation or role replacement");
        }
        speechLater.IsSelected=true;
        var buildSettings=typeof(SetupWizard).GetMethod("BuildSettingsAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var deferred=((System.Threading.Tasks.Task<AppSettings>)buildSettings.Invoke(wizard,[System.Threading.CancellationToken.None])!).GetAwaiter().GetResult();
        Assert(deferred.OverlayInteractionHotkey==new Hotkey(0,0x12),"Wizard defaults overlay interaction to Alt");
        var overlayKey=typeof(SetupWizard).GetField("overlayHotkey",BindingFlags.NonPublic|BindingFlags.Instance)!;
        overlayKey.SetValue(wizard,new Hotkey(2,0x4B));
        var customOverlay=((System.Threading.Tasks.Task<AppSettings>)buildSettings.Invoke(wizard,[System.Threading.CancellationToken.None])!).GetAwaiter().GetResult();
        Assert(customOverlay.OverlayInteractionHotkey==new Hotkey(2,0x4B),"Wizard saves a custom overlay shortcut");
        overlayKey.SetValue(wizard,null);
        var disabledOverlay=((System.Threading.Tasks.Task<AppSettings>)buildSettings.Invoke(wizard,[System.Threading.CancellationToken.None])!).GetAwaiter().GetResult();
        Assert(disabledOverlay.OverlayInteractionHotkey is null,"Wizard preserves a cleared overlay shortcut");
        Assert(deferred.SpeechProviderId=="edge" && deferred.SelectedVoice==initial.SelectedVoice && deferred.FallbackRole==initial.FallbackRole,
            "Deferring speech setup preserves existing provider and voice without requiring local voices");
        var customSpeech=new AppSettings { SpeechEnabled=false,SpeechProviderId="openai-compatible",OpenAiVoice="custom" };
        Assert(SetupWizard.ApplyWizardVoice(customSpeech,null,[])==customSpeech,"Deferring preserves custom speech configuration and enabled state");
        var english=LanguageCatalog.EdgeVoices.First(v=>v.Locale=="en-US");
        var configured=SetupWizard.ApplyWizardVoice(customSpeech,english,[new("windows-en","Local","en-US","Female")]);
        Assert(configured.SpeechEnabled && configured.SelectedVoice==english.ShortName && configured.FallbackRole?.ProviderVoices["windows"]=="windows-en",
            "Choosing a voice enables it and installs a matching Windows fallback");
        source.SelectedItem=source.Items.Cast<LanguageChoice>().First(l=>l.Tag=="ru-RU");
        translateBox.IsChecked=false;
        foreach (var theme in new[] { AppTheme.Light,AppTheme.Dark })
        {
            app.Themes.Apply(theme); step.SetValue(wizard,4); render.Invoke(wizard,null);
            var option=speechChoices.Items.OfType<ListBoxItem>().First(i=>i.Tag is ScreenReader.Speech.SpeechVoice);
            option.IsSelected=true;
            var content=(FrameworkElement)wizard.Content;
            content.Measure(new Size(760,650)); content.Arrange(new Rect(0,0,760,650)); content.UpdateLayout();
            var row=(StackPanel)option.Content;
            var label=row.Children.OfType<TextBlock>().Single();
            var play=row.Children.OfType<Button>().Single();
            double gap=play.TransformToAncestor(row).Transform(new Point()).X-label.ActualWidth;
            Assert(Equals(play.Content,"▶") && play.ActualWidth==28 && gap is >= 0 and <= 12,
                "Compact play triangle stays beside the voice name");
            var outline=(Border)option.Template.FindName("ChoiceBorder",option);
            Assert(((SolidColorBrush)outline.BorderBrush).Color==((SolidColorBrush)app.FindResource("FluentAccent")).Color,
                "Selected voice has accent outline: "+theme);
            var bitmap=new RenderTargetBitmap(760,650,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
            using var output=File.Create(Path.Combine(directory,$"speech-{theme}.png"));
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(output);
            Assert(((SolidColorBrush)option.Foreground).Color==((SolidColorBrush)app.FindResource("FluentText")).Color,
                "Voice options follow the current theme: "+theme);
            source.SelectedItem=source.Items.Cast<LanguageChoice>().First(l=>l.Tag=="en-US"); render.Invoke(wizard,null);
            Assert(selectedVoice.GetValue(wizard) is null && speechLater.IsSelected==true,
                "Changing the earlier language clears an incompatible selected voice");
            source.SelectedItem=source.Items.Cast<LanguageChoice>().First(l=>l.Tag=="ru-RU");
        }
        app.Themes.Apply(AppTheme.Light);
        wizard.Close();
        typeof(App).GetProperty(nameof(App.Settings))!.SetValue(app,new AppSettings { Language="en-US",Voice="de-DE-KatjaNeural" });
        var settings=new MainWindow(app);
        var wizardButton=(Button)settings.FindName("SetupWizardButton");
        Assert(wizardButton.Parent is WrapPanel footer && footer.Children.OfType<Button>().Count()==3 &&
            footer.Children.OfType<Button>().Any(b=>Equals(b.Content,UiText.T("button.save"))),"Wizard button shares the footer actions row");
        Assert(wizardButton.Style==(Style)app.FindResource(typeof(Button)),"Wizard button uses the standard action button style");
        Assert(((ComboBox)settings.FindName("VoiceBox")).SelectedItem is ScreenReader.Speech.SpeechVoice { ShortName:"de-DE-KatjaNeural" },
            "Settings retain a wizard voice whose language differs from recognition");
        settings.CloseForReload(); app.Providers.Dispose();
    }
}
