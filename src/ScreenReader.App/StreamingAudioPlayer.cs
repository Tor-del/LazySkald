using System.Threading.Channels;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;

namespace ScreenReader.App;

public interface IAudioPlayer
{
    Task PlayAsync(IAsyncEnumerable<byte[]> audio,Action started,CancellationToken cancellationToken);
    Task PlayAsync(IAsyncEnumerable<byte[]> audio,SpeechAudioFormat format,Action started,CancellationToken cancellationToken);
    Task PlayAsync(IAsyncEnumerable<byte[]> audio,SpeechAudioFormat format,string deviceId,Action started,CancellationToken cancellationToken)=>PlayAsync(audio,format,started,cancellationToken);
}
public sealed class StreamingAudioPlayer(float volume = 1f) : IAudioPlayer
{
    public Task PlayAsync(IAsyncEnumerable<byte[]> audio,Action started,CancellationToken cancellationToken)=>PlayAsync(audio,SpeechAudioFormat.Mp3,started,cancellationToken);
    public Task PlayAsync(IAsyncEnumerable<byte[]> audio,SpeechAudioFormat format,Action started,CancellationToken cancellationToken)=>PlayAsync(audio,format,"",started,cancellationToken);
    public async Task PlayAsync(IAsyncEnumerable<byte[]> audio,SpeechAudioFormat format,string deviceId,Action started,CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16) { SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait });
        var producer = Task.Run(async () =>
        {
            Exception? failure = null;
            try { await foreach (var chunk in audio.WithCancellation(stop.Token).ConfigureAwait(false)) await channel.Writer.WriteAsync(chunk,stop.Token).ConfigureAwait(false); }
            catch (Exception e) { failure=e; throw; }
            finally { channel.Writer.TryComplete(failure); }
        },CancellationToken.None);
        var consumer = Task.Run(() =>
        {
            try { DecodeAndPlay(channel.Reader,format,deviceId,started,stop.Token); }
            finally { stop.Cancel(); }
        },CancellationToken.None);
        try { await Task.WhenAll(producer,consumer).ConfigureAwait(false); }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Prefer the original network/decoder error over an internal cancellation.
            var error = producer.Exception?.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException)
                ?? consumer.Exception?.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException);
            if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
    }
    private void DecodeAndPlay(ChannelReader<byte[]> reader,SpeechAudioFormat format,string deviceId,Action started,CancellationToken ct)
    {
        using var stream = new ChunkStream(reader,ct);
        using var device=AudioOutputDevices.Resolve(deviceId);
        using var output=new WasapiOut(device,AudioClientShareMode.Shared,true,100);
        IMp3FrameDecompressor? decoder=null; BufferedWaveProvider? provider=null;
        Exception? playbackFailure=null;
        output.PlaybackStopped += (_,e) => { if (e.Exception is not null) Volatile.Write(ref playbackFailure,e.Exception); };
        try
        {
            var pcm = new byte[65536]; bool playing=false;
            WavePcmDecoder? wave=format==SpeechAudioFormat.Wave ? new WavePcmDecoder(stream) : null;
            if (wave is not null)
            {
                provider=new BufferedWaveProvider(wave.Format) { BufferDuration=TimeSpan.FromSeconds(5),ReadFully=true,DiscardOnBufferOverflow=false };
                InitOutput(provider);
            }
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int count;
                if (wave is not null) { count=wave.Read(pcm); if (count==0) break; }
                else
                {
                    var frame=Mp3Frame.LoadFromStream(stream);
                    if (frame is null) break;
                    if (decoder is null)
                    {
                        var mp3format=new Mp3WaveFormat(frame.SampleRate,frame.ChannelMode==ChannelMode.Mono ? 1 : 2,frame.FrameLength,frame.BitRate);
                        decoder=new AcmMp3FrameDecompressor(mp3format);
                        provider=new BufferedWaveProvider(decoder.OutputFormat) { BufferDuration=TimeSpan.FromSeconds(5),ReadFully=true,DiscardOnBufferOverflow=false };
                        InitOutput(provider);
                    }
                    count=decoder.DecompressFrame(frame,pcm,0);
                }
                while (provider!.BufferedDuration.TotalSeconds>2)
                { ct.ThrowIfCancellationRequested(); CheckOutput(); ct.WaitHandle.WaitOne(20); }
                provider.AddSamples(pcm,0,count);
                if (!playing && provider.BufferedDuration.TotalMilliseconds>=80)
                { output.Play(); playing=true; started(); }
                CheckOutput();
            }
            if (provider is null) throw new InvalidDataException(UiText.T("audio.empty"));
            if (!playing) { output.Play(); started(); }
            while (provider.BufferedBytes>0) { ct.ThrowIfCancellationRequested(); CheckOutput(); ct.WaitHandle.WaitOne(20); }
            // WaveOut has a small native buffer after the provider has drained.
            if (ct.WaitHandle.WaitOne(150)) ct.ThrowIfCancellationRequested();
        }
        finally { output.Stop(); decoder?.Dispose(); }
        void CheckOutput() { if (Volatile.Read(ref playbackFailure) is { } e) throw new IOException(UiText.T("audio.failed"),e); }
        void InitOutput(BufferedWaveProvider source)
        {
            // Software gain: never change the user's endpoint/master volume.
            var gain=new VolumeSampleProvider(source.ToSampleProvider()) { Volume=Math.Clamp(volume,0,1) };
            output.Init(gain.ToWaveProvider());
        }
    }

    private sealed class ChunkStream(ChannelReader<byte[]> reader,CancellationToken ct) : Stream
    {
        private byte[]? chunk; private int offset; private long position;
        public override int Read(byte[] buffer,int bufferOffset,int count) => Read(buffer.AsSpan(bufferOffset,count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length==0) return 0;
            while (chunk is null || offset==chunk.Length)
            {
                ct.ThrowIfCancellationRequested();
                if (reader.TryRead(out chunk)) { offset=0; continue; }
                if (!reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult()) return 0;
            }
            int length=Math.Min(buffer.Length,chunk.Length-offset);
            chunk.AsSpan(offset,length).CopyTo(buffer); offset+=length; position+=length; return length;
        }
        public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException(); public override long Position { get=>position; set=>throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
