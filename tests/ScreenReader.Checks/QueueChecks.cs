using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScreenReader.App;

internal static partial class Program
{
    private static async Task RunQueueChecksAsync()
    {
        var defaults=new AppSettings();
        Assert(HotkeyService.Bindings(defaults).SequenceEqual(new[] { (HotkeyAction.Capture,new Hotkey(3,0x52)),(HotkeyAction.OverlayInteraction,new Hotkey(0,0x12)) }),"Queue capture and overlay interaction are assigned by default");
        var settings=defaults with { RepeatCaptureHotkey=new(7,0x52),ToggleHotkey=new(3,0x53),CancelCurrentHotkey=new(3,0x54),CancelAllHotkey=new(3,0x55) };
        HotkeyService.Validate(settings);
        Assert(HotkeyService.Bindings(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!).Count()==6,"Six independent shortcuts survive serialization");
        Throws<ArgumentException>(()=>HotkeyService.Validate(settings with { ToggleHotkey=defaults.CaptureHotkey }),"Duplicate hotkeys rejected before registration");
        Throws<ArgumentException>(()=>HotkeyService.Validate(settings with { RepeatCaptureHotkey=defaults.CaptureHotkey }),"Repeat capture cannot share another action shortcut");
        var cleared=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(defaults with { CaptureHotkey=null,OverlayInteractionHotkey=null }))!;
        HotkeyService.Validate(cleared);
        Assert(!HotkeyService.Bindings(cleared).Any(),"All shortcuts can remain unassigned after reload");

        var queue=new ReadingQueue();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played=new List<int>();
        var first=queue.EnqueueAsync(_=>Task.FromResult(1),async (value,ct)=>
        {
            played.Add(value); started.SetResult(); await Task.Delay(Timeout.Infinite,ct);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        bool prepared=false;
        var second=queue.EnqueueAsync(_=> { prepared=true; return Task.FromResult(2); },async (value,ct)=>
        {
            played.Add(value); nextStarted.SetResult(); await Task.Delay(Timeout.Infinite,ct);
        });
        var third=queue.EnqueueAsync(_=>Task.FromResult(3),(value,_)=> { played.Add(value); return Task.CompletedTask; });
        Assert(prepared && played.SequenceEqual(new[] { 1 }),"New recognition prepares while current speech continues; playback waits");
        Assert(queue.Snapshot.Count==3 && queue.Snapshot[0].Stage=="queue.processing" && queue.Snapshot[1].Stage=="queue.waiting","Queue snapshot exposes active and waiting jobs in submission order");
        queue.CancelCurrent();
        await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(played.SequenceEqual(new[] { 1,2 }),"Cancel current advances to the next queued task");
        queue.CancelAll();
        await Task.WhenAll(first,second,third).WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!queue.IsBusy && !played.Contains(3),"Cancel all stops current and discards pending tasks");
        Assert(queue.Snapshot.Count==0,"Completed and cancelled jobs leave active queue snapshot");

        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial=queue.EnqueueAsync(_=>Task.FromResult(0),(_,_)=>release.Task);
        var failure=queue.EnqueueAsync<int>(_=>Task.FromException<int>(new InvalidOperationException("test")),(_,_)=>Task.CompletedTask);
        bool lastPlayed=false;
        var last=queue.EnqueueAsync(_=>Task.FromResult(0),(_,_)=> { lastPlayed=true; return Task.CompletedTask; });
        Assert(!lastPlayed,"Failed preparation cannot let later speech overtake earlier speech");
        release.SetResult();
        try { await failure; } catch (InvalidOperationException) { }
        await Task.WhenAll(initial,last).WaitAsync(TimeSpan.FromSeconds(3));
        Assert(lastPlayed && !queue.IsBusy,"Queue continues after failed preparation and becomes idle");
    }
}
