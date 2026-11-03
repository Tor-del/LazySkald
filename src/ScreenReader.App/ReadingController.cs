using System.Diagnostics;
using System.Drawing.Imaging;
using ScreenReader.Edge;

namespace ScreenReader.App;

public sealed class ReadingController(IScreenCapture capture,IOcrProvider ocr,ITextTransform transform,SpeechProviderRegistry providers,IAudioPlayer audio,IForegroundWindowService? focus=null,ITranslationClient? translation=null,IGenderizationClient? genderization=null,ReadingCache? cache=null)
{
    private readonly IForegroundWindowService windowFocus=focus ?? new ForegroundWindowService();
    private readonly ReadingQueue queue=new();
    public ReadingCache Cache { get; }=cache ?? new();
    public ReadingQueue Queue=>queue;
    private readonly SemaphoreSlim captureGate=new(1,1);
    private SelectionWindow? overlay;
    private (System.Drawing.Rectangle Monitor,System.Drawing.Rectangle Region)? lastSelection;
    private Task pendingFocusRestore=Task.CompletedTask;
    private readonly SemaphoreSlim playbackGate=new(1,1);
    public event Action<string>? StatusChanged;
    public event Action<string>? TextRecognized;
    public event Action<string>? TextTranslated;
    public event Action<Exception>? Failed;
    public event Action<SpeakerGender>? GenderDetected;
    public bool IsBusy=>queue.IsBusy || overlay is not null || !pendingFocusRestore.IsCompleted;
    public Task ToggleAsync(AppSettings settings)
    {
        if (IsBusy) { Stop(); return Task.CompletedTask; }
        return CaptureAndReadAsync(settings);
    }

    public void CancelCurrent()=>queue.CancelCurrent();
    public void Stop()
    {
        queue.CancelAll();
        StatusChanged?.Invoke(UiText.T("status.stopped"));
    }
    public Task CaptureAndReadAsync(AppSettings settings)=>EnqueueCaptureAsync(settings,false);
    public Task RepeatCaptureAndReadAsync(AppSettings settings)=>EnqueueCaptureAsync(settings,true);
    private Task EnqueueCaptureAsync(AppSettings settings,bool repeat)=>RunSafelyAsync(()=>queue.EnqueueAsync(
        token=>CaptureTextAsync(settings,token,repeat),
        async (result,token)=>
        {
            if (result is not { } captured) return;
            TextRecognized?.Invoke(captured.Text);
            await SpeakCoreAsync(captured.Text,settings,captured.Timer,captured.OcrMs,token);
        },result=>result?.Text ?? ""));

    private async Task<(string Text,Stopwatch Timer,long OcrMs)?> CaptureTextAsync(AppSettings settings,CancellationToken token,bool repeat)
    {
        await captureGate.WaitAsync(token);
        SelectionWindow? window=null;
        try
        {
            await pendingFocusRestore;
            token.ThrowIfCancellationRequested();
            var saved=repeat ? lastSelection : null;
            var originalWindow=saved is null ? windowFocus.Capture() : null;
            using var snapshot=await Task.Run(()=>saved is { } previous
                ? capture.CaptureMonitor(previous.Monitor,settings.CaptureMode)
                : capture.Capture(settings.CaptureMode));
            token.ThrowIfCancellationRequested();
            AppLog.Write("Захват: "+snapshot.Backend);
            System.Drawing.Rectangle? selected=saved?.Region;
            if (saved is null)
            {
                window=new SelectionWindow(snapshot,originalWindow); overlay=window;
                window.Show();
                using var cancellation=token.Register(()=>window.Dispatcher.Invoke(() =>
                {
                    if (overlay!=window) return;
                    pendingFocusRestore=window.Completion; overlay=null; window.Close();
                }));
                selected=await window.Completion;
                if (overlay==window) overlay=null;
            }
            token.ThrowIfCancellationRequested();
            if (selected is null) { Report(UiText.T("status.cancelled"),token); return null; }
            lastSelection=(snapshot.Bounds,selected.Value);
            var timer=Stopwatch.StartNew();
            using var crop=snapshot.Image.Clone(selected.Value,PixelFormat.Format32bppArgb);
            Report(UiText.T("status.ocr"),token);
            if (settings.OcrProviderId!="windows") throw new InvalidOperationException(UiText.T("ocr.unknownProvider"));
            string text=OcrTextFormatter.Format(await ocr.RecognizeAsync(crop,settings.Language,token));
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) { Report(UiText.T("status.noText"),token); return null; }
            return (text,timer,timer.ElapsedMilliseconds);
        }
        finally
        {
            if (window is not null && overlay==window)
            { pendingFocusRestore=window.Completion; overlay=null; window.Close(); }
            captureGate.Release();
        }
    }
    public Task ReadTextAsync(string text,AppSettings settings)
    {
        Stop();
        return RunSafelyAsync(()=>queue.EnqueueAsync(_=>Task.FromResult(text),
            (value,token)=>SpeakCoreAsync(value,settings,Stopwatch.StartNew(),0,token),value=>value));
    }
    private async Task RunSafelyAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Failed?.Invoke(new TimeoutException(UiText.T("error.timeout"))); }
        catch (Exception e) { AppLog.Write("Ошибка чтения",e); Failed?.Invoke(e); }
    }
    private async Task SpeakCoreAsync(string text,AppSettings settings,Stopwatch timer,long ocrMs,CancellationToken token)
    {
        TextTranslated?.Invoke("");
        GenderDetected?.Invoke(SpeakerGender.Unknown);
        string? cacheKey=settings.CacheEnabled ? ReadingCache.KeyFor(text,settings) : null;
        long cacheRevision=Cache.Revision;
        if (cacheKey is not null && Cache.Find(cacheKey) is { } cached)
        {
            await PlayCachedCoreAsync(cached,settings,token);
            return;
        }
        string prepared=await transform.TransformAsync(text,settings.Language,token);
        token.ThrowIfCancellationRequested();
        if (settings.Translation.Enabled)
        {
            Report(UiText.T("translation.working"),token);
            prepared=await (translation ?? new LlmTranslationClient()).TranslateAsync(prepared,settings.Translation,token);
            token.ThrowIfCancellationRequested();
            TextTranslated?.Invoke(prepared);
        }
        SpeakerGender gender=SpeakerGender.Unknown;
        if (settings.Genderization.Enabled)
        {
            Report(UiText.T("gender.working"),token);
            gender=await (genderization ?? new LlmGenderizationClient()).DetectAsync(text,prepared,settings.Genderization,token);
            token.ThrowIfCancellationRequested();
        }
        GenderDetected?.Invoke(gender);
        var speech=settings.SpeechFor(gender);
        Report(UiText.T("status.preparing",providers.Get(speech.SpeechProviderId).DisplayName),token);
        // Serialize audio teardown so old and new speech never overlap.
        await playbackGate.WaitAsync(token);
        try
        {
            using var lease=providers.Acquire(speech);
            var request=new SpeechRequest(prepared,speech.SelectedVoice,settings.Translation.Enabled ? settings.Translation.TargetLanguage : settings.Language,speech.RatePercent,gender);
            using var recorded=new MemoryStream();
            bool complete=false;
            async IAsyncEnumerable<byte[]> Record([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                await foreach (var chunk in lease.Provider.StreamAsync(request,ct).WithCancellation(ct))
                {
                    if (cacheKey is not null) recorded.Write(chunk);
                    yield return chunk;
                }
                complete=true;
            }
            await audio.PlayAsync(Record(token),lease.Provider.AudioFormat,speech.AudioOutputDeviceId,() =>
            {
                long firstSound=timer.ElapsedMilliseconds;
                AppLog.Write($"До запуска аудио: {firstSound} мс; OCR: {ocrMs} мс");
                string status=UiText.T("status.reading",firstSound,ocrMs);
                if (System.Windows.Application.Current is { } app) app.Dispatcher.BeginInvoke(() => Report(status,token));
                else Report(status,token);
            },token);
            token.ThrowIfCancellationRequested();
            if (cacheKey is not null && complete)
            {
                try { Cache.Save(new(cacheKey,text,prepared,settings.Translation.Enabled,gender,lease.Provider.AudioFormat,recorded.ToArray(),DateTimeOffset.Now),cacheRevision); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write("Не удалось сохранить кэш.",e); }
            }
            Report(UiText.T("ready"),token);
        }
        finally { playbackGate.Release(); }
    }
    public Task ReplayAsync(CachedReading entry,AppSettings settings)=>RunSafelyAsync(()=>queue.EnqueueAsync(
        _=>Task.FromResult(entry),(value,token)=>PlayCachedCoreAsync(value,settings,token),value=>value.Text));
    private async Task PlayCachedCoreAsync(CachedReading entry,AppSettings settings,CancellationToken token)
    {
        TextRecognized?.Invoke(entry.Text);
        TextTranslated?.Invoke(entry.Translated ? entry.SpokenText : "");
        GenderDetected?.Invoke(entry.Gender);
        await playbackGate.WaitAsync(token);
        try
        {
            async IAsyncEnumerable<byte[]> Chunks([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                for (int offset=0;offset<entry.Audio.Length;offset+=65536)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return entry.Audio.AsSpan(offset,Math.Min(65536,entry.Audio.Length-offset)).ToArray();
                    await Task.CompletedTask;
                }
            }
            Report(UiText.T("cache.playing"),token);
            await audio.PlayAsync(Chunks(token),entry.Format,settings.SpeechFor(entry.Gender).AudioOutputDeviceId,()=>{},token);
            Report(UiText.T("ready"),token);
        }
        finally { playbackGate.Release(); }
    }
    private void Report(string value,CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        queue.Update(token,stage:value);
        StatusChanged?.Invoke(value);
    }
}
