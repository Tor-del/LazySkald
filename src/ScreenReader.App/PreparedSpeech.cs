using System.Threading.Channels;

namespace ScreenReader.App;

// Fetch speech independently of the player so queued jobs keep preparing audio.
internal sealed class PreparedSpeech : IAsyncDisposable
{
    private readonly Channel<byte[]> chunks=Channel.CreateUnbounded<byte[]>(new() { SingleReader=true,SingleWriter=true });
    private readonly CancellationTokenSource cancellation;
    private readonly Task producer;
    public SpeechAudioFormat Format { get; }

    public PreparedSpeech(SpeechProviderLease lease,SpeechRequest request,CancellationToken token)
    {
        Format=lease.Provider.AudioFormat;
        cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
        producer=ProduceAsync(lease,request);
    }

    private async Task ProduceAsync(SpeechProviderLease lease,SpeechRequest request)
    {
        try
        {
            using (lease)
                await foreach (var chunk in lease.Provider.StreamAsync(request,cancellation.Token).WithCancellation(cancellation.Token))
                    await chunks.Writer.WriteAsync(chunk.ToArray(),cancellation.Token);
            chunks.Writer.TryComplete();
        }
        catch (Exception error) { chunks.Writer.TryComplete(error); }
    }

    public IAsyncEnumerable<byte[]> ReadAllAsync(CancellationToken token)=>chunks.Reader.ReadAllAsync(token);

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        await producer;
        while (chunks.Reader.TryRead(out _)) { }
        cancellation.Dispose();
    }
}
