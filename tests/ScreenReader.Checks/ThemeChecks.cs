using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenReader.App;

internal static partial class Program
{
    private static void RunThemeChecks()
    {
        Assert(JsonSerializer.Deserialize<AppSettings>("{}")!.Theme==AppTheme.System,"Existing settings default to system theme");
        foreach (var theme in Enum.GetValues<AppTheme>())
            Assert(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { Theme=theme }))!.Theme==theme,"Theme survives serialization: "+theme);

        var app=new App(); app.InitializeComponent();
        app.Themes.Apply(AppTheme.System);
        var legacy=JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert(!legacy.MinimizeOnMinimizeToTray && !legacy.SkipExitConfirmation,
            "Existing installations keep minimize-to-tray opt-in and exit confirmation enabled");
        var behavior=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(legacy with
            { MinimizeOnMinimizeToTray=true,SkipExitConfirmation=true }))!;
        Assert(behavior.MinimizeOnMinimizeToTray && behavior.SkipExitConfirmation,
            "Tray and exit confirmation preferences survive restart");
        var confirmation=new ExitConfirmationWindow();
        confirmation.Loaded+=(_,_)=>confirmation.Dispatcher.BeginInvoke(new Action(()=>confirmation.Close()));
        Assert(confirmation.ShowDialog()!=true && !confirmation.DoNotAskAgain,
            "Dismissing exit confirmation cancels exit without opting out");
        var window=new MainWindow(app);
        var tabs=(TabControl)window.FindName("MainTabs");
        tabs.SelectedItem=tabs.Items.Cast<TabItem>().Single(t=>Equals(t.Header,UiText.T("tab.application")));
        var selector=(ComboBox)window.FindName("ThemeBox");
        Assert((string)selector.SelectedValue=="System","Application section defaults to System");
        var input=(TextBox)window.FindName("EndpointBox");
        var slider=new Slider { Minimum=0,Maximum=100,Value=40,Width=260 };
        var sliderHost=(StackPanel)((ScrollViewer)((TabItem)tabs.SelectedItem).Content).Content; sliderHost.Children.Add(slider);
        foreach (var theme in new[] { AppTheme.Dark,AppTheme.Light,AppTheme.Dark,AppTheme.System })
        {
            selector.SelectedValue=theme.ToString();
            var content=(FrameworkElement)window.Content;
            content.Measure(new Size(window.Width,window.Height));
            content.Arrange(new Rect(0,0,window.Width,window.Height)); content.UpdateLayout();
            Assert(app.Themes.Theme==theme,"Selector applies theme immediately: "+theme);
            slider.ApplyTemplate(); slider.UpdateLayout();
            var track=(System.Windows.Controls.Primitives.Track)slider.Template.FindName("PART_Track",slider);
            Assert(track.Thumb.ActualWidth==22 && track.Value==40,"Themed slider keeps its thumb and value binding: "+theme);
            Assert(((SolidColorBrush)track.DecreaseRepeatButton.Background).Color==((SolidColorBrush)app.FindResource("FluentAccent")).Color,
                "Slider fill follows active theme: "+theme);
            bool dark=app.Themes.IsDark;
            Assert(((SolidColorBrush)window.Foreground).Color==(Color)ColorConverter.ConvertFromString(dark ? "#F5F5F5" : "#1A1A1A"),"Existing window updates text color: "+theme);
            Assert(((SolidColorBrush)tabs.Foreground).Color==((SolidColorBrush)window.Foreground).Color,"Section headings and content inherit readable text: "+theme);
            Assert(((SolidColorBrush)input.Background).Color==(Color)ColorConverter.ConvertFromString(dark ? "#323232" : "#FFFFFF"),"Existing text fields update background: "+theme);
            foreach (bool enabled in new[] { true,false })
            {
                var list=new ListBox();
                list.Items.Add("Dictionary entry");
                var parent=new StackPanel { IsEnabled=enabled,Width=300,Height=130 };
                parent.Children.Add(list);
                list.Height=130;
                parent.Measure(new Size(300,130));
                parent.Arrange(new Rect(0,0,300,130)); parent.UpdateLayout();
                Assert(list.IsEnabled==enabled,"List inherits translation enabled state: "+enabled);
                var rendered=new RenderTargetBitmap(300,130,96,96,PixelFormats.Pbgra32);
                rendered.Render(parent);
                var pixel=new byte[4];
                rendered.CopyPixels(new Int32Rect(150,110,1,1),pixel,4,0);
                var expected=((SolidColorBrush)app.FindResource("FluentSurfaceHover")).Color;
                Assert(pixel[0]==expected.B && pixel[1]==expected.G && pixel[2]==expected.R && pixel[3]==255,
                    $"List renders theme background with inherited IsEnabled={enabled}: {theme}");
            }
            if (theme!=AppTheme.System)
            {
                var image=new RenderTargetBitmap((int)window.Width,(int)window.Height,96,96,PixelFormats.Pbgra32);
                image.Render(content);
                Directory.CreateDirectory("artifacts");
                using var output=File.Create(Path.Combine("artifacts",$"settings-theme-{theme.ToString().ToLowerInvariant()}.png"));
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(output);
            }
        }
        selector.SelectedValue="Dark";
        UiText.SetLanguage("ru");
        var dictionary=tabs.Items.Cast<TabItem>().Single(t=>Equals(t.Header,UiText.T("tab.textCorrection")));
        var layout=(FrameworkElement)window.Content;
        tabs.SelectedIndex=0; layout.UpdateLayout();
        double headerHeight=dictionary.ActualHeight;
        tabs.SelectedItem=dictionary; layout.UpdateLayout();
        Assert(Math.Abs(dictionary.ActualHeight-headerHeight)<0.1,"Selecting recognition dictionary does not resize the sidebar header");
        foreach (var locale in UiText.Languages)
        {
            UiText.SetLanguage(locale.Id);
            Assert((string)selector.SelectedValue=="Dark" && app.Themes.Theme==AppTheme.Dark,"Language change preserves theme: "+locale.Id);
            Assert(((ComboBoxItem)selector.SelectedItem).Content.Equals(UiText.T("theme.dark")),"Theme choice is localized: "+locale.Id);
        }
        Assert(app.Resources.MergedDictionaries.Count==3,"Repeated theme changes replace the active palette");
        app.Themes.Apply((AppTheme)999);
        Assert(app.Themes.Theme==AppTheme.System,"Unknown theme safely falls back to system");
        app.Themes.Dispose();
    }
}
