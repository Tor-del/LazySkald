using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScreenReader.App;

internal static partial class Program
{
    private static async Task RunOllamaChecksAsync()
    {
        Assert(OllamaModelChoice.Recommend(8,0).Tag=="translategemma:4b","CPU-only machine uses small TranslateGemma");
        Assert(OllamaModelChoice.Recommend(64,0).Tag=="translategemma:4b","CPU latency is considered even with abundant RAM");
        Assert(OllamaModelChoice.Recommend(16,12).Tag=="translategemma:12b","12 GB GPU with 16 GB RAM recommends 12B");
        Assert(OllamaModelChoice.Recommend(32,24).Tag=="translategemma:27b","24 GB GPU with 32 GB RAM recommends 27B");
        Assert(OllamaModelChoice.Recommend(8,24).Tag=="translategemma:4b","Large GPU does not override RAM constraint");
        Assert(OllamaModelChoice.Recommend(0,0).Tag=="translategemma:4b","Unknown hardware falls back conservatively");
        Assert(OllamaModelChoice.Recommend(32,11).Tag=="translategemma:4b","Recommendation leaves GPU context headroom");
        var hardware=OllamaHardware.Detect();
        Assert(hardware.RamGb>0 && hardware.AvailableRamGb>0 && hardware.VramGb>=0,"Native hardware detection returns physical RAM and optional GPU memory");
        using (var http=new HttpClient(new Handler((request,_)=>Task.FromResult(JsonResponse(
            request.RequestUri!.AbsolutePath=="/api/version" ? "{\"version\":\"0.test\"}" : "{\"models\":[{\"name\":\"translategemma:4b\"}]}")))))
        {
            var state=await new OllamaSetupClient(http).CheckAsync(CancellationToken.None);
            Assert(state.Running && state.Version=="0.test" && state.Models[0]=="translategemma:4b","Ollama check reads version and installed model tags");
        }
        using (var http=new HttpClient(new Handler((_,_)=>throw new HttpRequestException("offline"))))
            Assert(!(await new OllamaSetupClient(http).CheckAsync(CancellationToken.None)).Running,"Offline Ollama is distinguished from running server");
        using (var http=new HttpClient(new Handler((_,_)=>Task.FromResult(JsonResponse("{}")))))
            Assert(!(await new OllamaSetupClient(http).CheckAsync(CancellationToken.None)).Running,"Unrelated localhost endpoint is not accepted as Ollama");
        var events=new List<OllamaProgress>();
        using (var http=new HttpClient(new Handler(async (request,token)=>
        {
            Assert(request.Method==HttpMethod.Post && request.RequestUri!.AbsolutePath=="/api/pull","Model setup uses native streaming pull endpoint");
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert(body.RootElement.GetProperty("model").GetString()=="translategemma:12b" && body.RootElement.GetProperty("stream").GetBoolean(),"Selected model size is explicitly requested");
            return JsonResponse("{\"status\":\"pulling manifest\"}\n{\"status\":\"pulling abc\",\"total\":1000,\"completed\":250}\n{\"status\":\"success\"}\n");
        })))
        {
            await new OllamaSetupClient(http).PullAsync("translategemma:12b",new InlineProgress(events.Add),CancellationToken.None);
            Assert(events.Count==3 && events[1].Percent==25 && events[1].Completed==250 && events[2].Stage=="success","Streaming pull reports byte progress and explicit success");
        }
        foreach (string payload in new[] { "{\"status\":\"pulling manifest\"}\n", "{\"error\":\"disk full\"}\n", "invalid json\n" })
        {
            using var http=new HttpClient(new Handler((_,_)=>Task.FromResult(JsonResponse(payload))));
            bool rejected=false;
            try { await new OllamaSetupClient(http).PullAsync("translategemma:4b",new InlineProgress(_=> { }),CancellationToken.None); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or JsonException) { rejected=true; }
            Assert(rejected,"Failed or truncated model download never reports success");
        }
        using (var http=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)))))
        {
            bool rejected=false;
            try { await new OllamaSetupClient(http).PullAsync("translategemma:4b",new InlineProgress(_=> { }),CancellationToken.None); }
            catch (HttpRequestException) { rejected=true; }
            Assert(rejected,"Model download propagates HTTP failure");
        }
        using (var http=new HttpClient(new Handler(async (_,token)=> { await Task.Delay(Timeout.Infinite,token); return JsonResponse("{}"); })))
        {
            using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            bool cancelled=false;
            try { await new OllamaSetupClient(http).PullAsync("translategemma:4b",new InlineProgress(_=> { }),cancel.Token); }
            catch (OperationCanceledException) { cancelled=true; }
            Assert(cancelled,"Cancelling setup interrupts pending model HTTP request");
        }
    }
    private sealed class InlineProgress(Action<OllamaProgress> report) : IProgress<OllamaProgress>
    {
        public void Report(OllamaProgress value)=>report(value);
    }
}
