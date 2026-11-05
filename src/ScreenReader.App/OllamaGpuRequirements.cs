using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenReader.App;

public sealed record OllamaGpuRequirements(int NvidiaMinimum,double LegacyFrom,double LegacyThrough,int LegacyMinimum,
    int AmdRocmMajor,Dictionary<string,double> NvidiaCards,DateTimeOffset CheckedAt)
{
    public const string SourceUrl="https://docs.ollama.com/gpu";
    public const string DownloadUrl=SourceUrl+".md";
    private static readonly HttpClient Http=new() { Timeout=TimeSpan.FromSeconds(15),MaxResponseContentBufferSize=262144 };
    private static string CachePath=>Path.Combine(SettingsStore.DataDirectory,"ollama-gpu-requirements.json");
    public static OllamaGpuRequirements BuiltIn { get; }=LoadBuiltIn();

    private static OllamaGpuRequirements LoadBuiltIn()
    {
        using var stream=typeof(OllamaGpuRequirements).Assembly.GetManifestResourceStream("ScreenReader.App.OllamaGpuRequirements.json")!;
        return JsonSerializer.Deserialize<OllamaGpuRequirements>(stream)!;
    }
    public static OllamaGpuRequirements LoadSaved()
    {
        try
        {
            if (new FileInfo(CachePath) is { Exists:true,Length:>0 and <262144 })
            {
                var saved=JsonSerializer.Deserialize<OllamaGpuRequirements>(File.ReadAllText(CachePath));
                if (saved is not null && saved.Valid() && saved.CheckedAt>=BuiltIn.CheckedAt) return saved;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { AppLog.Write("Saved Ollama GPU requirements unavailable",ex); }
        return BuiltIn;
    }
    private bool Valid()=>NvidiaMinimum is >=100 and <=9999 && LegacyMinimum>=NvidiaMinimum && LegacyMinimum<=9999 &&
        LegacyFrom>=1 && LegacyThrough>=LegacyFrom && LegacyThrough<100 && AmdRocmMajor is >=1 and <100 &&
        NvidiaCards is { Count:>20 and <2000 } && NvidiaCards.All(p=>p.Key.Length is >0 and <100 && double.IsFinite(p.Value) && p.Value is >=1 and <100);

    public static OllamaGpuRequirements Parse(string markdown,DateTimeOffset checkedAt)
    {
        if (markdown.Length>262144) throw new InvalidDataException();
        // Match documented Windows-relevant rules, never arbitrary numbers elsewhere on the page.
        var basic=Regex.Match(markdown,@"Ollama supports Nvidia GPUs with compute capability [\d.]+\+ and driver version (\d+) and newer\.",RegexOptions.IgnoreCase);
        var legacy=Regex.Match(markdown,@"Nvidia GPUs with compute capability (\d+\.\d+) through (\d+\.\d+) require driver version (\d+) or newer\.",RegexOptions.IgnoreCase);
        var amd=Regex.Match(markdown,@"Ollama requires an AMD ROCm v(\d+) / HIP\1-capable driver stack on Windows\.",RegexOptions.IgnoreCase);
        int begin=markdown.IndexOf("| Compute Capability",StringComparison.Ordinal);
        int end=begin>=0 ? markdown.IndexOf("###",begin,StringComparison.Ordinal) : -1;
        if (!basic.Success || !legacy.Success || !amd.Success || begin<0 || end<0) throw new InvalidDataException(UiText.T("ollama.requirementsInvalid"));
        if (Regex.Matches(markdown[..begin],"driver version",RegexOptions.IgnoreCase).Count!=2)
            throw new InvalidDataException(UiText.T("ollama.requirementsInvalid"));
        var cards=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
        double? capability=null;
        foreach (string line in markdown[begin..end].Split('\n'))
        {
            var cells=line.Split('|');
            if (cells.Length<5) continue;
            if (double.TryParse(cells[1].Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out double value)) capability=value;
            if (capability is not double cc) continue;
            foreach (Match card in Regex.Matches(cells[3],"`([^`]+)`"))
            {
                string name=card.Groups[1].Value;
                // Some professional GPU labels occur in multiple rows. Only merge them
                // when all occurrences have the same applicable driver requirement.
                bool Legacy(double capability)=>capability>=double.Parse(legacy.Groups[1].Value,CultureInfo.InvariantCulture) &&
                    capability<=double.Parse(legacy.Groups[2].Value,CultureInfo.InvariantCulture);
                if (cards.TryGetValue(name,out double previous) && Legacy(previous)!=Legacy(cc))
                    throw new InvalidDataException(UiText.T("ollama.requirementsInvalid"));
                cards[name]=cc;
            }
        }
        var result=new OllamaGpuRequirements(int.Parse(basic.Groups[1].Value,CultureInfo.InvariantCulture),
            double.Parse(legacy.Groups[1].Value,CultureInfo.InvariantCulture),double.Parse(legacy.Groups[2].Value,CultureInfo.InvariantCulture),
            int.Parse(legacy.Groups[3].Value,CultureInfo.InvariantCulture),int.Parse(amd.Groups[1].Value,CultureInfo.InvariantCulture),cards,checkedAt);
        if (!result.Valid()) throw new InvalidDataException(UiText.T("ollama.requirementsInvalid"));
        return result;
    }
    public static async Task<OllamaGpuRequirements> RefreshAsync(CancellationToken token,HttpClient? http=null,string? cachePath=null)
    {
        string markdown=await (http ?? Http).GetStringAsync(DownloadUrl,token);
        var requirements=Parse(markdown,DateTimeOffset.UtcNow);
        string path=cachePath ?? CachePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(requirements),Encoding.UTF8,token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary,path,true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return requirements;
    }
    public double? ComputeCapability(string name)=>NvidiaCards.OrderByDescending(p=>p.Key.Length)
        .Where(p=>Regex.IsMatch(name,@"(?<![\p{L}\p{N}])"+Regex.Escape(p.Key)+@"(?![\p{L}\p{N}])",RegexOptions.IgnoreCase))
        .Select(p=>(double?)p.Value).FirstOrDefault();

    public int MinimumFor(OllamaDriver driver)=>ComputeCapability(driver.Name) is double cc && cc>=LegacyFrom && cc<=LegacyThrough ? LegacyMinimum : NvidiaMinimum;
    public bool IsBelowMinimum(OllamaDriver driver)=>driver.NvidiaVersion is double current && current<MinimumFor(driver);
    public string Describe(OllamaDriver driver)
    {
        if (driver.Vendor=="NVIDIA")
        {
            int minimum=MinimumFor(driver);
            if (IsBelowMinimum(driver)) return UiText.T("ollama.driverRequired",driver.Name,driver.NvidiaVersion!,minimum);
            if (driver.NvidiaVersion is null || ComputeCapability(driver.Name) is null) return UiText.T("ollama.driverUnknownRequirement",driver.Name,NvidiaMinimum);
            return UiText.T("ollama.driverMeetsRequirement",driver.Name,driver.NvidiaVersion!,minimum);
        }
        return driver.Vendor=="AMD" ? UiText.T("ollama.driverAmdRequirement",AmdRocmMajor) : UiText.T("ollama.driverNoNumericRequirement");
    }
}
