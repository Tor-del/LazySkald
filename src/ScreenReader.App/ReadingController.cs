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
        async token=>
        {
            var captured=await CaptureTextAsync(settings,token,repeat);
            if (captured is null) return null;
            var result=captured.Value;
            TextRecognized?.Invoke(result.Text);
            return await PrepareSpeechAsync(result.Text,settings,result.Timer,result.OcrMs,token);
        },
        async (result,token)=>
        {
            if (result is not null) await PlayPreparedAsync(result,settings,token);
        },result=>result?.Text ?? ""));

    private async Task<(string Text,Stopwatch Timer,long OcrMs)?> CaptureTextAsync(AppSettings settings,CancellationToken token,bool repeat)
    {
        await captureGate.WaitAsync(token);
        SelectionWindow? window=null;
        bool captureHeld=true;
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
                window=new SelectionWindow(snapshot,originalWindow,settings.ExclusiveHotkeys); overlay=window;
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
            captureGate.Release(); captureHeld=false;
            Report(UiText.T("status.ocr"),token);
            if (settings.OcrProviderId!="windows") throw new InvalidOperationException(UiText.T("ocr.unknownProvider"));
            string text=OcrTextFormatter.Format(await ocr.RecognizeAsync(crop,settings.Language,token),settings);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) { Report(UiText.T("status.noText"),token); return null; }
            return (text,timer,timer.ElapsedMilliseconds);
        }
        finally
        {
            if (window is not null && overlay==window)
            { pendingFocusRestore=window.Completion; overlay=null; window.Close(); }
            if (captureHeld) captureGate.Release();
        }
    }
    public Task ReadTextAsync(string text,AppSettings settings)
    {
        Stop();
        return RunSafelyAsync(()=>queue.EnqueueAsync(token=>PrepareSpeechAsync(text,settings,Stopwatch.StartNew(),0,token),
            (value,token)=>PlayPreparedAsync(value,settings,token),value=>value.Text));
    }
    private async Task RunSafelyAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Failed?.Invoke(new TimeoutException(UiText.T("error.timeout"))); }
        catch (Exception e) { AppLog.Write("Ошибка чтения",e); Failed?.Invoke(e); }
    }
    private async Task<PreparedReading> PrepareSpeechAsync(string text,AppSettings settings,Stopwatch timer,long ocrMs,CancellationToken token)
    {
        TextTranslated?.Invoke("");
        GenderDetected?.Invoke(SpeakerGender.Unknown);
        string? cacheKey=settings.CacheEnabled ? ReadingCache.KeyFor(text,settings) : null;
        long cacheRevision=Cache.Revision;
        if (cacheKey is not null && Cache.Find(cacheKey) is { } cached)
        {
            return new(text,cached.SpokenText,cached.Gender,timer,ocrMs,null,cacheRevision,cached,null,settings.SpeechFor(cached.Gender));
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
        PreparedSpeech Create(AppSettings voice)
        {
            var lease=providers.Acquire(voice);
            return new PreparedSpeech(lease,new SpeechRequest(prepared,voice.SelectedVoice,settings.Translation.Enabled ? settings.Translation.TargetLanguage : settings.Language,voice.RatePercent,gender),token);
        }
        PreparedSpeech pending;
        bool usedFallback=false;
        try { pending=Create(speech); }
        catch (Exception) when (!token.IsCancellationRequested && settings.FallbackRole is not null)
        {
            AppLog.Write("Не удалось создать основной провайдер; используется резервный.");
            Report(UiText.T("speech.fallbackWorking"),token);
            speech=settings.FallbackRole.ApplyTo(settings); pending=Create(speech); cacheKey=null; usedFallback=true;
        }
        return new(text,prepared,gender,timer,ocrMs,cacheKey,cacheRevision,null,pending,speech,usedFallback);
    }
    private sealed record PreparedReading(string Text,string SpokenText,SpeakerGender Gender,Stopwatch Timer,long OcrMs,
        string? CacheKey,long CacheRevision,CachedReading? Cached,PreparedSpeech? Speech,AppSettings VoiceSettings,bool UsedFallback=false) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()=>Speech?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
    private async Task PlayPreparedAsync(PreparedReading reading,AppSettings settings,CancellationToken token)
    {
        bool started=false;
        try { await PlayPreparedCoreAsync(reading,settings,()=>started=true,token); }
        catch (Exception error) when (!started && !token.IsCancellationRequested && reading.Cached is null && !reading.UsedFallback && settings.FallbackRole is not null)
        {
            await reading.Speech!.DisposeAsync();
            await PlayFallbackAsync(reading,settings,error,token);
        }
    }
    private async Task PlayFallbackAsync(PreparedReading reading,AppSettings settings,Exception error,CancellationToken token)
    {
        AppLog.Write("Основной голос недоступен; используется резервный.",error);
        Report(UiText.T("speech.fallbackWorking"),token);
        var speech=settings.FallbackRole!.ApplyTo(settings);
        var request=new SpeechRequest(reading.SpokenText,speech.SelectedVoice,settings.Translation.Enabled ? settings.Translation.TargetLanguage : settings.Language,speech.RatePercent,reading.Gender);
        await using var fallback=new PreparedSpeech(providers.Acquire(speech),request,token);
        await PlayPreparedCoreAsync(reading with { Speech=fallback,VoiceSettings=speech,CacheKey=null },settings,()=>{},token);
    }
    private async Task PlayPreparedCoreAsync(PreparedReading reading,AppSettings settings,Action started,CancellationToken token)
    {
        if (reading.Cached is { } cached) { await PlayCachedCoreAsync(cached,settings,token); return; }
        TextRecognized?.Invoke(reading.Text);
        TextTranslated?.Invoke(settings.Translation.Enabled ? reading.SpokenText : "");
        GenderDetected?.Invoke(reading.Gender);
        await playbackGate.WaitAsync(token);
        try
        {
            using var recorded=new MemoryStream();
            bool complete=false;
            async IAsyncEnumerable<byte[]> Record([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                await foreach (var chunk in reading.Speech!.ReadAllAsync(ct))
                {
                    if (reading.CacheKey is not null) recorded.Write(chunk);
                    yield return chunk;
                }
                complete=true;
            }
            await audio.PlayAsync(Record(token),reading.Speech!.Format,reading.VoiceSettings.AudioOutputDeviceId,reading.VoiceSettings.VolumePercent/100f,() =>
            {
                started();
                long firstSound=reading.Timer.ElapsedMilliseconds;
                long ocrMs=reading.OcrMs;
                AppLog.Write($"До запуска аудио: {firstSound} мс; OCR: {ocrMs} мс");
                string status=UiText.T("status.reading",firstSound,ocrMs);
                if (System.Windows.Application.Current is { } app) app.Dispatcher.BeginInvoke(() => Report(status,token));
                else Report(status,token);
            },token);
            token.ThrowIfCancellationRequested();
            if (reading.CacheKey is not null && complete)
            {
                try { Cache.Save(new(reading.CacheKey,reading.Text,reading.SpokenText,settings.Translation.Enabled,reading.Gender,reading.Speech!.Format,recorded.ToArray(),DateTimeOffset.Now),reading.CacheRevision); }
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
            await audio.PlayAsync(Chunks(token),entry.Format,settings.SpeechFor(entry.Gender).AudioOutputDeviceId,settings.SpeechFor(entry.Gender).VolumePercent/100f,()=>{},token);
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
