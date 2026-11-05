using System.Windows;
using System.Windows.Controls;

namespace ScreenReader.App;

internal sealed class ExitConfirmationWindow : Window
{
    private readonly CheckBox doNotAskAgain=new() { Content=UiText.T("app.doNotAskAgain"),Margin=new Thickness(0,18,0,20) };
    public bool DoNotAskAgain=>doNotAskAgain.IsChecked==true;

    public ExitConfirmationWindow()
    {
        Title=UiText.T("tray.exit");
        Width=440; SizeToContent=SizeToContent.Height;
        ResizeMode=ResizeMode.NoResize; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        ShowInTaskbar=false;
        SetResourceReference(BackgroundProperty,"FluentSurface");
        SetResourceReference(ForegroundProperty,"FluentText");
        var panel=new StackPanel { Margin=new Thickness(24) };
        panel.Children.Add(new TextBlock { Text=UiText.T("app.exitConfirm"),TextWrapping=TextWrapping.Wrap });
        panel.Children.Add(doNotAskAgain);
        var buttons=new WrapPanel { HorizontalAlignment=HorizontalAlignment.Right };
        var exit=new Button { Content=UiText.T("tray.exit") };
        exit.Click+=(_,_)=>DialogResult=true;
        var cancel=new Button { Content=UiText.T("button.cancel"),IsCancel=true,IsDefault=true };
        buttons.Children.Add(exit); buttons.Children.Add(cancel);
        panel.Children.Add(buttons); Content=panel;
        SourceInitialized+=(_,_)=>((App)Application.Current).Themes.ApplyTitleBar(this);
    }
}
