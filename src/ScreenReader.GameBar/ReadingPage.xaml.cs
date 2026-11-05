using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace ScreenReader.GameBar
{
    public sealed partial class ReadingPage : Page
    {
        private readonly DispatcherTimer timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(250) };
        private StorageFolder folder;
        private string session;
        private long? job;
        private bool busy,stopped;
        public ReadingPage()
        {
            InitializeComponent();
            timer.Tick+=async (_,e)=>await RefreshAsync();
            Loaded+=async (_,e)=> { timer.Start(); await RefreshAsync(); };
        }
        public void Stop() { stopped=true; timer.Stop(); session=null; job=null; }
        private void Disconnected()
        {
            session=null; job=null;
            StageText.Text="Запустите LazySkald / Start LazySkald";
            TranslationText.Text="";
            PauseButton.Visibility=PlaybackPanel.Visibility=Visibility.Collapsed;
            CloseButton.IsEnabled=false;
        }
        private async Task RefreshAsync()
        {
            if (busy || stopped) return;
            busy=true;
            try
            {
                if (folder==null) folder=await ApplicationData.Current.LocalFolder.CreateFolderAsync("Overlay",CreationCollisionOption.OpenIfExists);
                var file=await folder.GetFileAsync("state.json");
                var state=JsonObject.Parse(await FileIO.ReadTextAsync(file));
                long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                double stamp=state.GetNamedNumber("UpdatedAt");
                if (stopped) return;
                if (state.GetNamedNumber("Version")!=1 || stamp<now-3000 || stamp>now+1000) { Disconnected(); return; }
                string nextSession=state.GetNamedString("Session");
                var id=state.GetNamedValue("JobId");
                long? nextJob=id.ValueType==JsonValueType.Number ? (long?)id.GetNumber() : null;
                if (session!=nextSession || job!=nextJob) TranslationPanel.ChangeView(null,0,null);
                session=nextSession; job=nextJob;
                StageText.Text=state.GetNamedString("Stage");
                TranslationText.Text=state.GetNamedString("Translation");
                CloseButton.IsEnabled=job.HasValue;
                bool playing=state.GetNamedBoolean("Playing");
                PauseButton.Visibility=PlaybackPanel.Visibility=playing ? Visibility.Visible : Visibility.Collapsed;
                PauseButton.Content=state.GetNamedBoolean("Paused") ? "▶" : "Ⅱ";
                ToolTipService.SetToolTip(PauseButton,state.GetNamedString("PauseLabel"));
                ToolTipService.SetToolTip(CloseButton,state.GetNamedString("CloseLabel"));
                double position=Math.Max(0,state.GetNamedNumber("Position"));
                var duration=state.GetNamedValue("Duration");
                PlaybackBar.IsIndeterminate=duration.ValueType!=JsonValueType.Number;
                double total=PlaybackBar.IsIndeterminate ? 0 : duration.GetNumber();
                PlaybackBar.Value=total>0 ? Math.Min(1,position/total) : 0;
                TimeText.Text=FormatTime(position)+(total>0 ? " / "+FormatTime(total) : "");
                await WriteAsync("heartbeat.json",new JsonObject
                {
                    ["Version"]=JsonValue.CreateNumberValue(1),["Session"]=JsonValue.CreateStringValue(session),
                    ["UpdatedAt"]=JsonValue.CreateNumberValue(now)
                });
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is System.Runtime.InteropServices.COMException)
            { Disconnected(); }
            finally { busy=false; }
        }
        private static string FormatTime(double seconds)
        {
            var time=TimeSpan.FromSeconds(seconds);
            return ((int)time.TotalMinutes)+":"+time.Seconds.ToString("00");
        }
        private async Task WriteAsync(string name,JsonObject value)
        {
            var temporary=await folder.CreateFileAsync(name+".tmp",CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(temporary,value.Stringify());
            await temporary.RenameAsync(name,NameCollisionOption.ReplaceExisting);
        }
        private async Task SendAsync(string action)
        {
            if (session==null || !job.HasValue || stopped) return;
            var command=new JsonObject
            {
                ["Version"]=JsonValue.CreateNumberValue(1),["Session"]=JsonValue.CreateStringValue(session),
                ["JobId"]=JsonValue.CreateNumberValue(job.Value),["Action"]=JsonValue.CreateStringValue(action)
            };
            try { await WriteAsync("command-"+Guid.NewGuid().ToString("N")+".json",command); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Runtime.InteropServices.COMException) { Disconnected(); }
        }
        private async void Pause(object sender,RoutedEventArgs args)=>await SendAsync("pause");
        private async void CloseJob(object sender,RoutedEventArgs args)=>await SendAsync("close");
    }
}
