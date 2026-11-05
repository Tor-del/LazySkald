using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        await RunOllamaRuntimeChecksAsync();
        var rows=OllamaModelRow.Build(new OllamaState(null,"test",new[] { "translategemma:4b","private:latest" })
            { ModelSizes=new Dictionary<string,long> { ["private:latest"]=2500000000 } },new OllamaHardware(16,8,8,"Test GPU"),"translategemma:4b");
        Assert(rows.Count==9 && rows.Single(r=>r.Tag=="translategemma:4b") is { Installed:true,CanUse:false,CanDelete:true,Recommended:true },
            "Unified model list includes catalog and installed models and recognizes the active model");
        Assert(rows.Single(r=>r.Tag=="private:latest").Size==UiText.T("ollama.modelSize",2.5) && rows.Single(r=>r.Tag=="private:latest").CanUse,
            "External installed model cards show actual disk size and can be selected");
        Assert(rows.Single(r=>r.Tag=="qwen2.5:7b") is { Installed:false,CanUse:false,CanDelete:false,CanDownload:true },
            "Missing model cards offer download and no installed-model actions");
        var ordered=OllamaModelQuery.Apply(rows,"",OllamaModelFilter.None,OllamaModelSort.Default);
        Assert(ordered[0].Tag=="translategemma:4b" && ordered[1].Tag=="private:latest" && ordered.Skip(2).All(r=>!r.Installed),
            "Default order prioritizes installed recommended, installed and remaining models");
        Assert(OllamaModelQuery.Apply(rows," QWEN2.5:7B ",OllamaModelFilter.None,OllamaModelSort.Default).Single().Tag=="qwen2.5:7b",
            "Model search trims whitespace and ignores case");
        Assert(OllamaModelQuery.Apply(rows,"qwen2.5:7b",OllamaModelFilter.Downloaded,OllamaModelSort.Default).Count==0,
            "Search narrows tag-filtered results");
        var union=OllamaModelQuery.Apply(rows,"",OllamaModelFilter.Downloaded | OllamaModelFilter.Unsupported,OllamaModelSort.Default);
        Assert(union.Count==rows.Count(r=>r.Installed || r.Supported==false),"Multiple filter tags are combined as a union");
        Assert(OllamaModelQuery.Apply(rows,"",OllamaModelFilter.Supported,OllamaModelSort.Default).All(r=>r.Supported==true) &&
            !OllamaModelQuery.Apply(rows,"",OllamaModelFilter.Unsupported,OllamaModelSort.Default).Any(r=>r.Tag=="private:latest"),
            "Unknown external requirements are not classified as supported or unsupported");
        Assert(rows.Single(r=>r.Tag=="translategemma:12b") is { MissingRam:false,MissingVram:true,LowAvailableRam:true,Supported:false } &&
            rows.Single(r=>r.Tag=="translategemma:27b") is { MissingRam:true,MissingVram:true },
            "RAM, VRAM and currently free RAM deficits are evaluated separately");
        Assert(OllamaModelQuery.Apply(rows,"",OllamaModelFilter.None,OllamaModelSort.SizeAscending).First().Tag=="qwen2.5:3b" &&
            OllamaModelQuery.Apply(rows,"",OllamaModelFilter.None,OllamaModelSort.SizeDescending).First().Tag=="translategemma:27b",
            "Size sorting uses numeric sizes rather than formatted text");
        var unknown=OllamaModelRow.Build(new OllamaState("ollama.exe",null,Array.Empty<string>()),null,null);
        Assert(OllamaModelQuery.Apply(unknown,"",OllamaModelFilter.NotDownloaded | OllamaModelFilter.Unsupported,OllamaModelSort.Default).Count==0,
            "Stopped-server availability and unknown hardware do not create false filter classifications");
        Assert(OllamaModelRow.Build(new OllamaState("ollama.exe",null,Array.Empty<string>()),null,null).All(r=>r.Status==UiText.T("ollama.modelUnchecked")),
            "A stopped server does not falsely classify unverified models as missing");
        var busy=OllamaModelRow.Build(new OllamaState(null,"test",Array.Empty<string>()),null,null,true,"qwen2.5:7b","download",42);
        Assert(busy.All(r=>!r.CanUse && !r.CanDelete && !r.CanDownload) && busy.Single(r=>r.Tag=="qwen2.5:7b").Status.Contains("42%"),
            "Download progress belongs to the selected model and serializes management actions");
        Assert(OllamaModelRow.IsLocalEndpoint("http://localhost:11434/") && OllamaModelRow.IsLocalEndpoint(OllamaSetupClient.BaseUrl) &&
            !OllamaModelRow.IsLocalEndpoint("http://remote:11434") && !OllamaModelRow.IsLocalEndpoint("http://127.0.0.1:1234"),
            "Translation model status distinguishes the local server from remote providers");
        var requirements=OllamaGpuRequirements.BuiltIn;
        Assert(requirements.NvidiaMinimum==550 && requirements.LegacyMinimum==570 && requirements.AmdRocmMajor==7,
            "Built-in requirements preserve the checked official Windows requirements");
        Assert(requirements.MinimumFor(new OllamaDriver("NVIDIA GeForce GTX 1060 6GB","31.0.15.6094",""))==570 &&
            requirements.IsBelowMinimum(new OllamaDriver("NVIDIA GeForce GTX 1060 6GB","31.0.15.6094","")),
            "A 560.94 driver fails the stricter requirement for Pascal GPUs");
        Assert(requirements.MinimumFor(new OllamaDriver("NVIDIA GeForce RTX 3070","31.0.15.6094",""))==550 &&
            !requirements.IsBelowMinimum(new OllamaDriver("NVIDIA GeForce GTX 1060 6GB","32.0.15.7000","")),
            "Newer GPUs use baseline and exactly 570 meets the legacy threshold");
        Assert(requirements.ComputeCapability("NVIDIA GeForce GTX 750 Ti")==5 &&
            requirements.ComputeCapability("NVIDIA GeForce GTX 980 Ti")==5.2 &&
            requirements.ComputeCapability("NVIDIA Quadro P6000")==6.1 &&
            requirements.ComputeCapability("NVIDIA Test GPU 10600") is null,
            "GPU matching uses longest complete model names and inherited table capabilities");
        Assert(new OllamaDriver("Microsoft Basic Display Adapter","","","PCI\\VEN_10DE&DEV_1234").Vendor=="NVIDIA" &&
            new OllamaDriver("AMD Radeon","","").Vendor=="AMD" && new OllamaDriver("Intel Arc","","").Vendor=="Intel" &&
            new OllamaDriver("Unknown","","").Vendor is null,"GPU vendor detection handles PCI IDs and recognized names");
        string fixture=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","ollama-gpu.md"));
        var parsed=OllamaGpuRequirements.Parse(fixture,DateTimeOffset.UtcNow);
        Assert(parsed.NvidiaCards.Count==requirements.NvidiaCards.Count && parsed.MinimumFor(new OllamaDriver("NVIDIA GeForce GTX 1060","",""))==570,
            "Official Markdown snapshot is parsed including GPU-specific requirements");
        foreach (string invalid in new[] { "<html>Unavailable</html>",fixture.Replace("version 550","version unknown"),
            fixture.Replace("HIP7","HIP8"),fixture.Replace("| Compute Capability","| Renamed column"),
            fixture.Replace("Check your compute", "Another driver version rule applies. Check your compute") })
            Throws<InvalidDataException>(()=>OllamaGpuRequirements.Parse(invalid,DateTimeOffset.UtcNow),"Changed or incomplete requirements are rejected instead of treated as current");
        string directory=Path.Combine(Path.GetTempPath(),"ScreenReader-Ollama-Checks-"+Guid.NewGuid().ToString("N"));
        string cache=Path.Combine(directory,"requirements.json");
        try
        {
            using (var http=new HttpClient(new Handler((request,_)=>
            {
                Assert(request.RequestUri!.AbsoluteUri==OllamaGpuRequirements.DownloadUrl,"Requirements refresh uses official Markdown endpoint");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(fixture.Replace("version 550","version 555")) });
            })))
            {
                var updated=await OllamaGpuRequirements.RefreshAsync(CancellationToken.None,http,cache);
                Assert(updated.NvidiaMinimum==555 && JsonSerializer.Deserialize<OllamaGpuRequirements>(File.ReadAllText(cache))!.NvidiaMinimum==555,
                    "New requirements are applied and persisted only after successful parsing");
            }
            string before=File.ReadAllText(cache);
            using (var http=new HttpClient(new Handler((_,_)=>Task.FromResult(JsonResponse("invalid page")))))
            {
                bool rejected=false;
                try { await OllamaGpuRequirements.RefreshAsync(CancellationToken.None,http,cache); }
                catch (InvalidDataException) { rejected=true; }
                Assert(rejected && File.ReadAllText(cache)==before,"Failed requirements refresh preserves the last valid cache");
            }
            using (var cancelled=new CancellationTokenSource())
            using (var http=new HttpClient(new Handler(async (_,token)=> { await Task.Delay(Timeout.Infinite,token); return JsonResponse(""); })))
            {
                cancelled.Cancel(); bool rejected=false;
                try { await OllamaGpuRequirements.RefreshAsync(cancelled.Token,http,cache); }
                catch (OperationCanceledException) { rejected=true; }
                Assert(rejected && File.ReadAllText(cache)==before,"Cancelled requirements refresh preserves the last valid cache");
            }
        }
        finally { if (File.Exists(cache)) File.Delete(cache); if (Directory.Exists(directory)) Directory.Delete(directory); }
        Assert(OllamaModelChoice.All.Select(m=>m.Tag).Distinct().Count()==8 &&
            new[] { "qwen2.5:3b","qwen2.5:7b","qwen2.5:14b","gemma3:4b","gemma3:12b" }.All(tag=>OllamaModelChoice.All.Any(m=>m.Tag==tag)),
            "Translation catalog offers explicit multilingual alternatives to TranslateGemma");
        Assert(new OllamaDriver("NVIDIA GeForce RTX 3070","32.0.15.6094","").NvidiaVersion==560.94 &&
            !new OllamaDriver("NVIDIA GeForce RTX 3070","32.0.15.6094","").BelowMinimum,
            "Windows NVIDIA driver numbering is converted to the public version");
        Assert(new OllamaDriver("NVIDIA GeForce RTX 3070","31.0.15.3623","").BelowMinimum,
            "Old NVIDIA driver triggers minimum version warning");
        Assert(!new OllamaDriver("AMD Radeon","31.0.15.3623","").BelowMinimum &&
            new OllamaDriver("NVIDIA","unknown","").NvidiaVersion is null,
            "Unknown versions and other GPU vendors are not compared as NVIDIA drivers");
        var drivers=await OllamaDrivers.CheckAsync(false,CancellationToken.None);
        Assert(!drivers.Searched && drivers.Updates.Length==0 && drivers.Drivers.All(d=>!string.IsNullOrEmpty(d.Name)),
            "Local driver inspection parses Windows CIM data without an update search");
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
            request.RequestUri!.AbsolutePath=="/api/version" ? "{\"version\":\"0.test\"}" : "{\"models\":[{\"name\":\"translategemma:4b\",\"size\":3300000000}]}")))))
        {
            var state=await new OllamaSetupClient(http).CheckAsync(CancellationToken.None);
            Assert(state.Running && state.Version=="0.test" && state.Models[0]=="translategemma:4b","Ollama check reads version and installed model tags");
            Assert(state.ModelSizes["translategemma:4b"]==3300000000,"Installed model disk sizes are read from Ollama tags");
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
        using (var http=new HttpClient(new Handler(async (request,token)=>
        {
            Assert(request.Method==HttpMethod.Delete && request.RequestUri!.AbsolutePath=="/api/delete","Model removal uses native DELETE endpoint");
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert(body.RootElement.GetProperty("model").GetString()=="custom/russian:latest","Removal supports any installed tag, including models outside the catalog");
            return new HttpResponseMessage(HttpStatusCode.OK);
        })))
            await new OllamaSetupClient(http).DeleteAsync("custom/russian:latest",CancellationToken.None);
        using (var http=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))))
        {
            bool rejected=false;
            try { await new OllamaSetupClient(http).DeleteAsync("missing:latest",CancellationToken.None); }
            catch (HttpRequestException) { rejected=true; }
            Assert(rejected,"Deletion HTTP failure is not reported as success");
        }
        using (var http=new HttpClient(new Handler(async (_,token)=> { await Task.Delay(Timeout.Infinite,token); return JsonResponse("{}"); })))
        {
            using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            bool cancelled=false;
            try { await new OllamaSetupClient(http).PullAsync("translategemma:4b",new InlineProgress(_=> { }),cancel.Token); }
            catch (OperationCanceledException) { cancelled=true; }
            Assert(cancelled,"Cancelling setup interrupts pending model HTTP request");
            cancelled=false;
            try { await new OllamaSetupClient(http).DeleteAsync("custom:latest",cancel.Token); }
            catch (OperationCanceledException) { cancelled=true; }
            Assert(cancelled,"Cancelling model deletion interrupts pending HTTP request");
        }
    }
    private static async Task RunOllamaRuntimeChecksAsync()
    {
        var start=OllamaSetupClient.ServerStartInfo(@"C:\Ollama\ollama.exe");
        Assert(start.FileName.EndsWith("ollama.exe") && start.Arguments=="serve" && start.CreateNoWindow && !start.UseShellExecute &&
            start.Environment["OLLAMA_HOST"]=="127.0.0.1:11434","Ollama startup runs the CLI server hidden without launching its GUI");
        byte[] Package(params string[] names)
        {
            using var memory=new MemoryStream();
            using (var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
                foreach (string name in names) { using var writer=new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("test payload; never executed"); }
            return memory.ToArray();
        }
        string root=Path.Combine(Path.GetTempPath(),"ScreenReader-Runtime-Checks-"+Guid.NewGuid().ToString("N"));
        try
        {
            int requests=0;
            using var http=new HttpClient(new Handler((request,_)=>
            {
                bool rocm=request.RequestUri!.AbsoluteUri==OllamaSetupClient.RocmUrl;
                Assert(rocm || request.RequestUri.AbsoluteUri==OllamaSetupClient.RuntimeUrl,"Runtime installation downloads official standalone packages only");
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new ByteArrayContent(rocm ? Package("lib/ollama/rocm.dll") : Package("ollama.exe","lib/ollama/cuda.dll")) });
            }));
            bool installing=false;
            await new OllamaSetupClient(http,root).InstallAsync(new InlineProgress(_=> { }),()=>installing=true,CancellationToken.None,true);
            string installed=Directory.GetDirectories(root,"installed-*").Single();
            Assert(OllamaSetupClient.FindManagedExecutable(root)==Path.Combine(installed,"ollama.exe"),"A completed managed runtime can be discovered after installation");
            Assert(requests==2 && installing && File.Exists(Path.Combine(installed,"ollama.exe")) && File.Exists(Path.Combine(installed,"lib","ollama","rocm.dll")) &&
                !Directory.GetDirectories(root,".install-*").Any(),"Standalone runtime and AMD extension are staged and published atomically without executing a GUI installer");
            using var broken=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new ByteArrayContent(Package("missing-executable.txt")) })));
            bool rejected=false;
            try { await new OllamaSetupClient(broken,root).InstallAsync(new InlineProgress(_=> { }),()=> { },CancellationToken.None); }
            catch (InvalidDataException) { rejected=true; }
            Assert(rejected && Directory.GetDirectories(root,"installed-*").Length==1 && !Directory.GetDirectories(root,".install-*").Any(),
                "Incomplete runtime packages never replace an installed runtime and staging is cleaned");
            using var traversal=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new ByteArrayContent(Package("../escaped.txt")) })));
            rejected=false;
            try { await new OllamaSetupClient(traversal,root).InstallAsync(new InlineProgress(_=> { }),()=> { },CancellationToken.None); }
            catch (IOException) { rejected=true; }
            Assert(rejected && !Directory.GetDirectories(root,".install-*").Any() && Directory.GetDirectories(root,"installed-*").Length==1,
                "Archive entries cannot escape the runtime extraction directory");
            using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
            rejected=false;
            try { await new OllamaSetupClient(http,root).InstallAsync(new InlineProgress(_=> { }),()=> { },cancelled.Token); }
            catch (OperationCanceledException) { rejected=true; }
            Assert(rejected && !Directory.GetDirectories(root,".install-*").Any(),"Cancelled runtime installation leaves no discoverable partial runtime");
        }
        finally
        {
            string owned=Path.GetFullPath(root);
            string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (!owned.StartsWith(temp,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            if (Directory.Exists(owned)) Directory.Delete(owned,true);
        }
    }
    private static async Task RunLiveOllamaRequirementsAsync()
    {
        string cache=Path.Combine(Directory.GetCurrentDirectory(),"artifacts","ollama-gpu-requirements-live.json");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var requirements=await OllamaGpuRequirements.RefreshAsync(timeout.Token,cachePath:cache);
        Assert(requirements.NvidiaCards.Count>20 && File.Exists(cache),"Live official documentation can be fetched, parsed and cached");
        Console.WriteLine($"NVIDIA: {requirements.NvidiaMinimum}; legacy: {requirements.LegacyMinimum}; GPU models: {requirements.NvidiaCards.Count}");
    }
    private sealed class InlineProgress(Action<OllamaProgress> report) : IProgress<OllamaProgress>
    {
        public void Report(OllamaProgress value)=>report(value);
    }
}
