using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vortice.DXGI;
using static Vortice.DXGI.DXGI;

namespace ScreenReader.App;

public sealed record OllamaModelChoice(string Tag,double DownloadGb,int RamGb,int VramGb)
{
    public string Name=>$"{Tag} · ~{DownloadGb:0.#} GB";
    public string Description=>UiText.T(Tag.StartsWith("translategemma:",StringComparison.Ordinal) ? "ollama.descTranslation" :
        Tag.StartsWith("qwen2.5:",StringComparison.Ordinal) ? "ollama.descQwen" : "ollama.descGemma");
    public static IReadOnlyList<OllamaModelChoice> All { get; }=new OllamaModelChoice[]
    {
        new("translategemma:4b",3.3,8,6),new("translategemma:12b",8.1,16,12),new("translategemma:27b",17,32,24),
        new("qwen2.5:3b",1.9,8,4),new("qwen2.5:7b",4.7,12,8),new("qwen2.5:14b",9,20,12),
        new("gemma3:4b",3.4,8,6),new("gemma3:12b",8.2,16,12)
    };
    // Leave room for Windows, the reader, and the inference context. Never sum VRAM across GPUs.
    // Without a large dedicated GPU, prefer the smallest model for interactive CPU translation.
    public static OllamaModelChoice Recommend(double ramGb,double vramGb)=>
        All.LastOrDefault(m=>m.Tag.StartsWith("translategemma:",StringComparison.Ordinal) && ramGb>=m.RamGb && vramGb>=m.VramGb) ?? All[0];
}

public sealed record OllamaHardware(double RamGb,double AvailableRamGb,double VramGb,string Gpu,string Cpu="")
{
    public static OllamaHardware Detect()
    {
        var memory=new MemoryStatus { Length=(uint)Marshal.SizeOf<MemoryStatus>() };
        double ram=0,available=0,vram=0; string gpu="";
        if (GlobalMemoryStatusEx(ref memory)) { ram=memory.TotalPhysical/1073741824d; available=memory.AvailablePhysical/1073741824d; }
        try
        {
            using var factory=CreateDXGIFactory1<IDXGIFactory1>();
            for (uint index=0; factory.EnumAdapters1(index,out var adapter).Success; index++)
                using (adapter)
                {
                    var description=adapter.Description1;
                    double size=(ulong)description.DedicatedVideoMemory/1073741824d;
                    if (size>vram) { vram=size; gpu=description.Description; }
                }
        }
        catch (Exception ex) { AppLog.Write("GPU memory detection unavailable",ex); }
        string cpu="";
        try { cpu=Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0","ProcessorNameString","") as string ?? ""; }
        catch (Exception ex) { AppLog.Write("CPU detection unavailable",ex); }
        return new(ram,available,vram,gpu,cpu.Trim());
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length,Load;
        public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

public sealed record OllamaProgress(string Stage,double? Percent=null,long Completed=0,long Total=0);
public sealed record OllamaState(string? Executable,string? Version,IReadOnlyList<string> Models)
{
    public bool Running=>Version is not null;
    public IReadOnlyDictionary<string,long> ModelSizes { get; init; }=new Dictionary<string,long>();
}

public sealed class OllamaSetupClient(HttpClient? http=null,string? runtimeRoot=null)
{
    private static readonly HttpClient Shared=new() { Timeout=Timeout.InfiniteTimeSpan };
    private HttpClient Http=>http ?? Shared;
    public const string BaseUrl="http://127.0.0.1:11434";
    public const string RuntimeUrl="https://github.com/ollama/ollama/releases/latest/download/ollama-windows-amd64.zip";
    public const string RocmUrl="https://github.com/ollama/ollama/releases/latest/download/ollama-windows-amd64-rocm.zip";
    public static string RuntimeRoot=>Path.Combine(SettingsStore.DataDirectory,"OllamaRuntime");
    public static string? FindExecutable()
    {
        var directories=new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Ollama") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (string directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string path=Path.Combine(directory.Trim().Trim('"'),"ollama.exe");
            if (File.Exists(path)) return path;
        }
        return FindManagedExecutable(RuntimeRoot);
    }
    internal static string? FindManagedExecutable(string root)
    {
        if (Directory.Exists(root))
            foreach (string directory in Directory.EnumerateDirectories(root,"installed-*"))
            {
                string executable=Path.Combine(directory,"ollama.exe");
                if (File.Exists(executable)) return executable;
            }
        return null;
    }
    public async Task<OllamaState> CheckAsync(CancellationToken token)
    {
        string? executable=FindExecutable();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var version=await Http.GetAsync(BaseUrl+"/api/version",timeout.Token);
            version.EnsureSuccessStatusCode();
            using var document=JsonDocument.Parse(await version.Content.ReadAsStringAsync(timeout.Token));
            string number=document.RootElement.GetProperty("version").GetString() ?? throw new InvalidDataException();
            using var tags=await Http.GetAsync(BaseUrl+"/api/tags",timeout.Token);
            tags.EnsureSuccessStatusCode();
            using var models=JsonDocument.Parse(await tags.Content.ReadAsStringAsync(timeout.Token));
            var entries=models.RootElement.GetProperty("models").EnumerateArray().ToArray();
            return new(executable,number,entries.Select(m=>m.GetProperty("name").GetString()!).ToArray())
            {
                ModelSizes=entries.Where(m=>m.TryGetProperty("size",out var size) && size.TryGetInt64(out long bytes) && bytes>0)
                    .GroupBy(m=>m.GetProperty("name").GetString()!).ToDictionary(g=>g.Key,g=>g.First().GetProperty("size").GetInt64())
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidDataException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return new(executable,null,Array.Empty<string>());
        }
    }
    public async Task EnsureRunningAsync(CancellationToken token)
    {
        if ((await CheckAsync(token)).Running) return;
        string executable=FindExecutable() ?? throw new InvalidOperationException(UiText.T("ollama.notInstalled"));
        using var process=Process.Start(ServerStartInfo(executable));
        for (int attempt=0; attempt<30; attempt++)
        {
            await Task.Delay(1000,token);
            if ((await CheckAsync(token)).Running) return;
        }
        throw new InvalidOperationException(UiText.T("ollama.startFailed"));
    }
    internal static ProcessStartInfo ServerStartInfo(string executable)
    {
        var start=new ProcessStartInfo(executable) { Arguments="serve",UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden };
        start.Environment["OLLAMA_HOST"]="127.0.0.1:11434";
        return start;
    }
    public async Task InstallAsync(IProgress<OllamaProgress> progress,Action installing,CancellationToken token,bool includeRocm=false)
    {
        string root=Path.GetFullPath(runtimeRoot ?? RuntimeRoot);
        Directory.CreateDirectory(root);
        string id=Guid.NewGuid().ToString("N");
        string directory=Path.Combine(root,".install-"+id);
        string stage=Path.Combine(directory,"runtime");
        Directory.CreateDirectory(stage);
        try
        {
            var packages=includeRocm ? new[] { RuntimeUrl,RocmUrl } : new[] { RuntimeUrl };
            var archives=new List<string>();
            foreach (string url in packages)
            {
                string path=Path.Combine(directory,archives.Count+".zip");
                await DownloadRuntimeAsync(url,path,progress,token);
                archives.Add(path);
            }
            token.ThrowIfCancellationRequested();
            installing(); progress.Report(new("ollama.installing"));
            // Finish extraction atomically; incomplete runtimes are never discoverable.
            await Task.Run(()=>
            {
                foreach (string archive in archives) ZipFile.ExtractToDirectory(archive,stage,overwriteFiles:true);
                if (!File.Exists(Path.Combine(stage,"ollama.exe"))) throw new InvalidDataException(UiText.T("ollama.installFailed"));
                Directory.Move(stage,Path.Combine(root,"installed-"+id));
            },CancellationToken.None);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            // Delete only the unique staging directory belonging to this installation.
            string owned=Path.GetFullPath(directory);
            if (!owned.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            try { Directory.Delete(owned,true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private async Task DownloadRuntimeAsync(string url,string path,IProgress<OllamaProgress> progress,CancellationToken token)
    {
        progress.Report(new("ollama.downloadingRuntime"));
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(60));
        using (var response=await Http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,timeout.Token))
        {
            response.EnsureSuccessStatusCode();
            long total=response.Content.Headers.ContentLength ?? 0,completed=0;
            await using var source=await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var target=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true);
            var buffer=new byte[81920]; int count;
            var watch=Stopwatch.StartNew();
            while ((count=await source.ReadAsync(buffer,timeout.Token))>0)
            {
                await target.WriteAsync(buffer.AsMemory(0,count),timeout.Token); completed+=count;
                if (watch.ElapsedMilliseconds<100 && completed!=total) continue;
                progress.Report(new("ollama.downloadingRuntime",total>0 ? completed*100d/total : null,completed,total)); watch.Restart();
            }
            if (total>0 && completed!=total) throw new InvalidDataException(UiText.T("ollama.incomplete"));
        }
    }
    public async Task PullAsync(string model,IProgress<OllamaProgress> progress,CancellationToken token)
    {
        if (!OllamaModelChoice.All.Any(m=>m.Tag==model)) throw new ArgumentException(nameof(model));
        using var request=new HttpRequestMessage(HttpMethod.Post,BaseUrl+"/api/pull") { Content=JsonContent.Create(new { model,stream=true }) };
        using var idle=CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(TimeSpan.FromMinutes(3));
        using var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,idle.Token);
        response.EnsureSuccessStatusCode();
        using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(idle.Token));
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromMinutes(3));
            string? line=await reader.ReadLineAsync(idle.Token);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document=JsonDocument.Parse(line); var root=document.RootElement;
            if (root.TryGetProperty("error",out var error)) throw new InvalidOperationException(error.GetString());
            string status=root.GetProperty("status").GetString() ?? "";
            long total=root.TryGetProperty("total",out var t) ? t.GetInt64() : 0;
            long completed=root.TryGetProperty("completed",out var c) ? c.GetInt64() : 0;
            progress.Report(new(status,total>0 ? Math.Clamp(completed*100d/total,0,100) : null,completed,total));
            if (status=="success") return;
        }
        throw new InvalidDataException(UiText.T("ollama.incomplete"));
    }
    public async Task DeleteAsync(string model,CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException(nameof(model));
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request=new HttpRequestMessage(HttpMethod.Delete,BaseUrl+"/api/delete") { Content=JsonContent.Create(new { model }) };
        using var response=await Http.SendAsync(request,timeout.Token);
        response.EnsureSuccessStatusCode();
    }
}
