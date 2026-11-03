using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ScreenReader.App;

public partial class MainWindow
{
    private void InitializeCacheAndQueue()
    {
        CacheEnabledBox.IsChecked=draft.CacheEnabled;
        if (owner.Reader is null) return;
        owner.Reader.Cache.Changed+=CacheChanged;
        owner.Reader.Queue.Changed+=QueueChanged;
        Closed+=(_,_)=>
        {
            owner.Reader.Cache.Changed-=CacheChanged;
            owner.Reader.Queue.Changed-=QueueChanged;
        };
        RefreshCache(); RefreshQueue();
    }
    private void CacheChanged()=>Dispatcher.BeginInvoke(RefreshCache);
    private void QueueChanged()=>Dispatcher.BeginInvoke(RefreshQueue);
    private void RefreshCache()
    {
        if (owner.Reader is null) return;
        try
        {
            string? selected=(CacheList.SelectedItem as CachedReadingInfo)?.Key;
            CacheList.ItemsSource=owner.Reader.Cache.List();
            CacheList.SelectedItem=CacheList.Items.Cast<CachedReadingInfo>().FirstOrDefault(entry=>entry.Key==selected);
        }
        catch (Exception e) { SetStatus(e.Message); }
    }
    private void RefreshQueue()
    {
        if (owner.Reader is null) return;
        QueueList.ItemsSource=owner.Reader.Queue.Snapshot.Select(job=>new
        {
            Heading=$"#{job.Id} · {job.SubmittedAt.LocalDateTime:T} · {UiText.T(job.Stage)}",
            job.Text
        }).ToArray();
    }
    private void CacheSelected(object sender,SelectionChangedEventArgs e)
    {
        CacheDetails.Text=CacheList.SelectedItem is CachedReadingInfo entry ? entry.Text+Environment.NewLine+Environment.NewLine+entry.SpokenText : "";
    }
    private async void ReplayCache(object sender,RoutedEventArgs e)
    {
        if (owner.Reader is null || CacheList.SelectedItem is not CachedReadingInfo info) return;
        if (owner.Reader.Cache.Find(info.Key) is { } entry) await owner.Reader.ReplayAsync(entry,owner.Settings);
        else RefreshCache();
    }
    private void DeleteCache(object sender,RoutedEventArgs e)
    {
        if (owner.Reader is null || CacheList.SelectedItem is not CachedReadingInfo entry) return;
        try { owner.Reader.Cache.Delete(entry.Key); RefreshCache(); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void OpenCacheFolder(object sender,RoutedEventArgs e)
    {
        if (owner.Reader is null) return;
        try
        {
            string path=owner.Reader.Cache.DirectoryPath;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute=true });
        }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void ClearCache(object sender,RoutedEventArgs e)
    {
        if (owner.Reader is null) return;
        try { owner.Reader.Cache.Clear(); RefreshCache(); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void CancelQueueCurrent(object sender,RoutedEventArgs e)=>owner.Reader?.CancelCurrent();
}
