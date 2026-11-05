using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace ScreenReader.GameBar
{
    sealed partial class App : Application
    {
        // Hold the SDK object for the lifetime of the Game Bar CoreWindow.
        private XboxGameBarWidget widget;
        private ReadingPage page;
        public App()
        {
            InitializeComponent();
            Suspending+=(_,e)=> { page?.Stop(); page=null; widget=null; };
        }
        protected override void OnActivated(IActivatedEventArgs args)
        {
            if (args.Kind!=ActivationKind.Protocol || !(args is IProtocolActivatedEventArgs protocol) ||
                protocol.Uri.Scheme!="ms-gamebarwidget" || !(args is XboxGameBarWidgetActivatedEventArgs activation)) return;
            if (!activation.IsLaunchActivation) return;
            var window=Window.Current;
            var frame=new Frame();
            window.Content=frame;
            widget=new XboxGameBarWidget(activation,window.CoreWindow,frame);
            page=new ReadingPage();
            frame.Content=page;
            window.Closed+=(_,e)=> { page?.Stop(); page=null; widget=null; };
            window.Activate();
        }
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (args.PrelaunchActivated) return;
            Window.Current.Content=new TextBlock
            {
                Text="LazySkald Game Bar\n\nWin+G → Widgets → LazySkald → Pin\n\nЗапустите LazySkald и выберите Overlay → Xbox Game Bar.\nStart LazySkald and select Overlay → Xbox Game Bar.",
                TextWrapping=TextWrapping.Wrap,Margin=new Thickness(24),FontSize=20
            };
            Window.Current.Activate();
        }
    }
}
