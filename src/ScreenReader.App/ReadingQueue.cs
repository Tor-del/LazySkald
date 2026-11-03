namespace ScreenReader.App;

// Prepare new text immediately; play it in submission order, including after failures.
public sealed record ReadingJob(long Id,string Text,string Stage,DateTimeOffset SubmittedAt);
public sealed class ReadingQueue
{
    private readonly object gate=new();
    private readonly LinkedList<CancellationTokenSource> jobs=new();
    private Task tail=Task.CompletedTask;
    private readonly Dictionary<CancellationTokenSource,ReadingJob> details=new();
    private long nextId;
    public event Action? Changed;
    public IReadOnlyList<ReadingJob> Snapshot { get { lock (gate) return jobs.Select(job=>details[job]).ToArray(); } }
    public void Update(CancellationToken token,string? text=null,string? stage=null)
    {
        lock (gate)
        {
            var source=jobs.FirstOrDefault(job=>job.Token==token);
            if (source is null) return;
            var current=details[source];
            details[source]=current with { Text=text ?? current.Text,Stage=stage ?? current.Stage };
        }
        Changed?.Invoke();
    }
    public bool IsBusy { get { lock (gate) return jobs.Count>0; } }
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
