using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ScreenReader.App;
using ScreenReader.Speech;
using System.Linq;

internal static partial class Program
{
    private sealed class RepeatScreenCapture : IScreenCapture
    {
        public readonly Rectangle Monitor=new(-640,0,640,360);
        public int Selections,Repeats,Frames;
        public Rectangle? RepeatedMonitor;
        public CapturedScreen Capture(CaptureMode mode) { Selections++; return Frame(); }
        public CapturedScreen CaptureMonitor(Rectangle bounds,CaptureMode mode)
        { Repeats++; RepeatedMonitor=bounds; return Frame(); }
        private CapturedScreen Frame()
        {
            var image=new Bitmap(Monitor.Width,Monitor.Height);
            using (var graphics=Graphics.FromImage(image)) graphics.Clear(Color.FromArgb(++Frames,20,30));
            return new(image,Monitor,"Test");
        }
    }
    private sealed class RepeatOcr : IOcrProvider
    {
        public IReadOnlyList<(string Tag,string Name)> Languages=>[];
        public readonly List<(Size Size,int Frame)> Images=[];
        public string Text="test";
        public Task<string> RecognizeAsync(Bitmap bitmap,string language,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Images.Add((bitmap.Size,bitmap.GetPixel(0,0).R));
            return Task.FromResult(Text);
        }
    }
    private static void RunRepeatCaptureChecks()
    {
        var loop=new System.Windows.Threading.DispatcherFrame();
        Exception? failure=null;
        var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(async ()=>
        {
            using var registry=new SpeechProviderRegistry();
            var capture=new RepeatScreenCapture(); var ocr=new RepeatOcr();
            var audio=new ConsumingAudio();
            registry.Register(new("repeat-test","Repeat test",false,_=>new BlockingSpeech(),false));
            var settings=new AppSettings { SpeechProviderId="repeat-test" };
            var reader=new ReadingController(capture,ocr,new IdentityTextTransform(),registry,audio,new NoFocus());
            Exception? readerFailure=null; reader.Failed+=e=>readerFailure=e;
            var overlayField=typeof(ReadingController).GetField("overlay",BindingFlags.Instance|BindingFlags.NonPublic)!;
            async Task WaitFor(Func<bool> condition)
            {
                var deadline=DateTime.UtcNow.AddSeconds(5);
                while (!condition())
                {
                    if (readerFailure is not null) throw readerFailure;
                    if (DateTime.UtcNow>=deadline) throw new TimeoutException("Repeat capture check timed out");
                    await Task.Delay(10);
                }
            }
            async Task Select(Rectangle? region)
            {
                await WaitFor(()=>overlayField.GetValue(reader) is SelectionWindow);
                var window=(SelectionWindow)overlayField.GetValue(reader)!;
                typeof(SelectionWindow).GetField("selectedRegion",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,region);
                window.Close();
            }
            try
            {
                var cancelled=reader.RepeatCaptureAndReadAsync(settings);
                await Select(null); await cancelled;
                Assert(capture.Selections==1 && capture.Repeats==0 && ocr.Images.Count==0,
                    "Repeat without a previous area opens selection; cancelling skips OCR");
                var region=new Rectangle(25,35,120,45);
                var first=reader.RepeatCaptureAndReadAsync(settings);
                await Select(region);
                await audio.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var repeat=reader.RepeatCaptureAndReadAsync(settings);
                await WaitFor(()=>ocr.Images.Count==2 && reader.Queue.Snapshot[1].Stage=="queue.waiting");
                Assert(capture.RepeatedMonitor==capture.Monitor && capture.Selections==2 && capture.Repeats==1,
                    "Repeat captures the remembered monitor without opening a selection overlay");
                Assert(ocr.Images[0].Size==region.Size && ocr.Images[1].Size==region.Size && ocr.Images[1].Frame>ocr.Images[0].Frame,
                    "Repeat recognizes fresh pixels cropped to the last selected area");
                Assert(reader.Queue.Snapshot.Count==2 && !first.IsCompleted && overlayField.GetValue(reader) is null,
                    "Repeat queues recognition while current speech continues");
                reader.Stop(); await Task.WhenAll(first,repeat);

                var cancelledSelection=reader.CaptureAndReadAsync(settings);
                await Select(null); await cancelledSelection;
                ocr.Text="";
                await reader.RepeatCaptureAndReadAsync(settings);
                Assert(capture.Repeats==2 && ocr.Images[2].Size==region.Size,
                    "Cancelling a new selection preserves the previous area");

                var replacement=new Rectangle(80,90,60,25);
                var newSelection=reader.CaptureAndReadAsync(settings);
                await Select(replacement); await newSelection;
                await reader.RepeatCaptureAndReadAsync(settings);
                Assert(capture.Repeats==3 && ocr.Images[^1].Size==replacement.Size,
                    "A new selection replaces the remembered area even when OCR finds no text");
                Assert(readerFailure is null && !reader.IsBusy,"Repeat capture finishes cleanly after cancellation and empty OCR");

                var prefetched=new PrefetchSpeech();
                registry.Register(new("prefetch-test","Prefetch test",false,_=>prefetched,false));
                var orderedAudio=new OrderedPrefetchAudio();
                var translated=new TestTranslator();
                var detected=new TestGenderization { Result=SpeakerGender.Female };
                reader=new ReadingController(capture,ocr,new IdentityTextTransform(),registry,orderedAudio,new NoFocus(),translated,detected);
                reader.Failed+=e=>readerFailure=e;
                settings=settings with { SpeechProviderId="prefetch-test",CacheEnabled=false,Translation=new() { Enabled=true },Genderization=new() { Enabled=true } };
                ocr.Text="First";
                var head=reader.CaptureAndReadAsync(settings);
                await Select(region);
                await orderedAudio.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                ocr.Text="Second";
                var next=reader.RepeatCaptureAndReadAsync(settings);
                await WaitFor(()=>prefetched.Calls==2);
                ocr.Text="Third";
                var last=reader.RepeatCaptureAndReadAsync(settings);
                await WaitFor(()=>prefetched.Completed==3);
                Assert(detected.Calls==3 && orderedAudio.Played.SequenceEqual(new byte[] { 1 }) && !head.IsCompleted,
                    "Queued OCR, translation, gender detection and complete TTS finish while first playback is blocked");
                orderedAudio.Release.TrySetResult();
                await Task.WhenAll(head,next,last).WaitAsync(TimeSpan.FromSeconds(5));
                Assert(orderedAudio.Played.SequenceEqual(new byte[] { 1,2,3 }) && readerFailure is null && !reader.IsBusy,
                    "Prefetched audio plays exactly once in submission order without overlap");
            }
            catch (Exception e) { failure=e; }
            finally { reader.Stop(); loop.Continue=false; }
        });
        System.Windows.Threading.Dispatcher.PushFrame(loop);
        if (failure is not null) throw failure;
    }

    private sealed class PrefetchSpeech : ISpeechProvider
    {
        public int Calls,Completed;
        public Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<SpeechVoice>>([]);
        public async IAsyncEnumerable<byte[]> StreamAsync(SpeechRequest request,[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Assert(request.Text=="Перевод" && request.Gender==SpeakerGender.Female,"Prefetch includes translation and detected voice");
            byte id=(byte)++Calls;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new[] { id };
            Completed++;
        }
    }
    private sealed class OrderedPrefetchAudio : IAudioPlayer
    {
        public readonly List<byte> Played=[];
        public readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool active;
        public Task PlayAsync(IAsyncEnumerable<byte[]> audio,SpeechAudioFormat format,Action started,CancellationToken ct)=>PlayAsync(audio,started,ct);
        public async Task PlayAsync(IAsyncEnumerable<byte[]> audio,Action started,CancellationToken ct)
        {
            Assert(!active,"Only one queued audio player is active");
            active=true;
            try
            {
                await foreach (var chunk in audio.WithCancellation(ct)) Played.AddRange(chunk);
                started(); Started.TrySetResult();
                if (Played.Count==1) await Release.Task.WaitAsync(ct);
            }
            finally { active=false; }
        }
    }
}
