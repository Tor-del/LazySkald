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
        var window=new MainWindow(app);
        var tabs=(TabControl)window.FindName("MainTabs");
        tabs.SelectedItem=tabs.Items.Cast<TabItem>().Single(t=>Equals(t.Header,UiText.T("tab.application")));
        var selector=(ComboBox)window.FindName("ThemeBox");
        Assert((string)selector.SelectedValue=="System","Application section defaults to System");
        var input=(TextBox)window.FindName("EndpointBox");
        foreach (var theme in new[] { AppTheme.Dark,AppTheme.Light,AppTheme.Dark,AppTheme.System })
        {
            selector.SelectedValue=theme.ToString();
            var content=(FrameworkElement)window.Content;
            content.Measure(new Size(window.Width,window.Height));
            content.Arrange(new Rect(0,0,window.Width,window.Height)); content.UpdateLayout();
            Assert(app.Themes.Theme==theme,"Selector applies theme immediately: "+theme);
            bool dark=app.Themes.IsDark;
            Assert(((SolidColorBrush)window.Foreground).Color==(Color)ColorConverter.ConvertFromString(dark ? "#F5F5F5" : "#1A1A1A"),"Existing window updates text color: "+theme);
            Assert(((SolidColorBrush)tabs.Foreground).Color==((SolidColorBrush)window.Foreground).Color,"Section headings and content inherit readable text: "+theme);
            Assert(((SolidColorBrush)input.Background).Color==(Color)ColorConverter.ConvertFromString(dark ? "#323232" : "#FFFFFF"),"Existing text fields update background: "+theme);
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
