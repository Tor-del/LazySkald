namespace ScreenReader.App;

// Prepare new text immediately; play it in submission order, including after failures.
public sealed record ReadingJob(long Id,string Text,string Stage,DateTimeOffset SubmittedAt,
    string? TranslationText=null,bool AwaitingClose=false,PlaybackSession? Playback=null,
    System.Drawing.Rectangle? CaptureMonitor=null);
public sealed class ReadingQueue
{
    private readonly object gate=new();
    private readonly LinkedList<CancellationTokenSource> jobs=new();
    private Task tail=Task.CompletedTask;
    private readonly Dictionary<CancellationTokenSource,ReadingJob> details=new();
    private readonly Dictionary<long,TaskCompletionSource> dismissals=new();
    private long nextId;
    public event Action? Changed;
    public IReadOnlyList<ReadingJob> Snapshot { get { lock (gate) return jobs.Select(job=>details[job]).ToArray(); } }
    public void Update(CancellationToken token,string? text=null,string? stage=null,string? translationText=null,System.Drawing.Rectangle? captureMonitor=null)
    {
        lock (gate)
        {
            var source=jobs.FirstOrDefault(job=>job.Token==token);
            if (source is null) return;
            var current=details[source];
            details[source]=current with { Text=text ?? current.Text,Stage=stage ?? current.Stage,TranslationText=translationText ?? current.TranslationText,CaptureMonitor=captureMonitor ?? current.CaptureMonitor };
        }
        Changed?.Invoke();
    }
    public bool IsBusy { get { lock (gate) return jobs.Count>0; } }
    public bool HasTranslation(CancellationToken token)
    {
        lock (gate) return jobs.Any(job=>job.Token==token && !string.IsNullOrWhiteSpace(details[job].TranslationText));
    }
    public void SetPlayback(CancellationToken token,PlaybackSession? playback)
    {
        lock (gate)
        {
            var source=jobs.FirstOrDefault(job=>job.Token==token);
            if (source is null) return;
            details[source]=details[source] with { Playback=playback };
        }
        Changed?.Invoke();
    }
    public async Task WaitForCloseAsync(CancellationToken token)
    {
        TaskCompletionSource completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var source=jobs.First(job=>job.Token==token);
            var current=details[source]; id=current.Id;
            dismissals[id]=completion;
            details[source]=current with { AwaitingClose=true,Stage="overlay.waiting" };
        }
        try { Changed?.Invoke(); await completion.Task.WaitAsync(token); }
        finally { lock (gate) dismissals.Remove(id); }
    }
    public void Close(long id)
    {
        lock (gate)
        {
            var source=jobs.FirstOrDefault(job=>details[job].Id==id);
            if (source is null) return;
            if (dismissals.TryGetValue(id,out var completion)) completion.TrySetResult();
            else source.Cancel();
        }
    }
    public void CompleteTextWaits()
    {
        lock (gate) foreach (var completion in dismissals.Values) completion.TrySetResult();
    }
    public void CancelCurrent()
    {
        lock (gate) jobs.FirstOrDefault(job=>!job.IsCancellationRequested)?.Cancel();
    }
    public void CancelAll()
    {
        lock (gate) foreach (var job in jobs.ToArray()) job.Cancel();
    }
    public async Task EnqueueAsync<T>(Func<CancellationToken,Task<T>> prepare,Func<T,CancellationToken,Task> execute,Func<T,string>? describe=null)
    {
        using var operation=new CancellationTokenSource();
        var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        LinkedListNode<CancellationTokenSource> node;
        lock (gate) { previous=tail; tail=finished.Task; node=jobs.AddLast(operation); details[operation]=new(++nextId,"","queue.preparing",DateTimeOffset.Now); }
        Changed?.Invoke();
        T? value=default;
        bool prepared=false;
        try
        {
            value=await prepare(operation.Token);
            prepared=true;
            Update(operation.Token,describe?.Invoke(value),"queue.waiting");
            await previous.WaitAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            Update(operation.Token,stage:"queue.processing");
            await execute(value,operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        finally
        {
            // A later cancelled/failed preparation must not let its successor overtake
            // an earlier task that is still speaking.
            try
            {
                if (prepared && value is IAsyncDisposable resource) await resource.DisposeAsync();
            }
            finally
            {
                await previous;
                lock (gate) { jobs.Remove(node); details.Remove(operation); }
                finished.TrySetResult();
                Changed?.Invoke();
            }
        }
    }
}
