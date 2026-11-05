using System.Windows;

namespace ScreenReader.App;

public sealed record OllamaModelRow(string Tag,string Size,string Description,string Requirements,string Status,
    bool Installed,bool CanDownload,bool CanUse,bool CanDelete,bool Recommended,bool CatalogModel)
{
    public double? SizeGb { get; init; }
    public bool AvailabilityKnown { get; init; }
    public bool? Supported { get; init; }
    public bool MissingRam { get; init; }
    public bool MissingVram { get; init; }
    public bool LowAvailableRam { get; init; }
    public int? RequiredRamGb { get; init; }
    public int? RequiredVramGb { get; init; }
    public double AvailableRamGb { get; init; }
    public string RamText=>RequiredRamGb.HasValue ? UiText.T("ollama.ramRequirement",RequiredRamGb.Value) : "";
    public string VramText=>RequiredVramGb.HasValue ? UiText.T("ollama.vramRequirement",RequiredVramGb.Value) : "";
    public string AvailableRamText=>UiText.T("ollama.availableRam",AvailableRamGb);
    public Visibility RequirementsVisibility=>CatalogModel ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AvailableRamVisibility=>LowAvailableRam ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PullVisibility=>CatalogModel ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ManageVisibility=>Installed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RecommendedVisibility=>Recommended ? Visibility.Visible : Visibility.Collapsed;
    public string PullLabel=>UiText.T(Installed ? "ollama.updateModel" : "ollama.downloadModel");

    public static bool IsLocalEndpoint(string url)=>Uri.TryCreate(url,UriKind.Absolute,out var uri) &&
        uri.Scheme=="http" && uri.IsLoopback && uri.Port==11434 && uri.AbsolutePath.Trim('/').Length==0;

    public static IReadOnlyList<OllamaModelRow> Build(OllamaState? state,OllamaHardware? hardware,string? activeModel,
        bool busy=false,string? operationModel=null,string? operationKind=null,double? percent=null)
    {
        var catalog=OllamaModelChoice.All.ToDictionary(m=>m.Tag,StringComparer.Ordinal);
        string? recommended=hardware is null ? null : OllamaModelChoice.Recommend(hardware.RamGb,hardware.VramGb).Tag;
        return catalog.Keys.Concat(state?.Models ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).Select(tag=>
        {
            catalog.TryGetValue(tag,out var choice);
            bool installed=state?.Running==true && state.Models.Contains(tag);
            bool transferring=busy && operationModel==tag;
            string status=transferring ? UiText.T(operationKind=="delete" ? "ollama.modelDeleting" : "ollama.modelDownloading")+
                (percent.HasValue ? $" · {percent:0}%" : "") : installed ? UiText.T(activeModel==tag ? "ollama.modelInUse" : "ollama.modelDownloaded") :
                UiText.T(state is null || (state.Executable is not null && !state.Running) ? "ollama.modelUnchecked" : "ollama.modelNotDownloaded");
            double? size=state?.ModelSizes.TryGetValue(tag,out long bytes)==true && bytes>0 ? bytes/1e9 : choice?.DownloadGb;
            string requirements=choice is null ? "" : UiText.T("ollama.requirements",choice.RamGb,choice.VramGb);
            if (choice is not null && hardware is not null && (hardware.RamGb<choice.RamGb || hardware.AvailableRamGb<choice.RamGb-4))
                requirements+=" "+UiText.T("ollama.lowMemory");
            return new OllamaModelRow(tag,size.HasValue ? UiText.T("ollama.modelSize",size.Value) : UiText.T("ollama.modelSizeUnknown"),
                choice?.Description ?? UiText.T("ollama.externalModel"),requirements,status,installed,!busy && choice is not null,
                !busy && installed && activeModel!=tag,!busy && installed,recommended==tag,choice is not null)
            {
                SizeGb=size,AvailabilityKnown=state is not null && (state.Running || state.Executable is null),
                RequiredRamGb=choice?.RamGb,RequiredVramGb=choice?.VramGb,AvailableRamGb=hardware?.AvailableRamGb ?? 0,
                Supported=choice is not null && hardware is { RamGb:>0,VramGb:>=0 } ? hardware.RamGb>=choice.RamGb && hardware.VramGb>=choice.VramGb : null,
                MissingRam=choice is not null && hardware is { RamGb:>0 } && hardware.RamGb<choice.RamGb,
                MissingVram=choice is not null && hardware is not null && hardware.VramGb<choice.VramGb,
                LowAvailableRam=choice is not null && hardware is { RamGb:>0 } && hardware.AvailableRamGb<choice.RamGb-4
            };
        }).ToArray();
    }
}

[Flags]
public enum OllamaModelFilter { None=0,Downloaded=1,NotDownloaded=2,Recommended=4,Supported=8,Unsupported=16 }
public enum OllamaModelSort { Default,Name,SizeAscending,SizeDescending }

public static class OllamaModelQuery
{
    public static IReadOnlyList<OllamaModelRow> Apply(IEnumerable<OllamaModelRow> rows,string search,OllamaModelFilter filters,OllamaModelSort sort)
    {
        var words=search.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matching=rows.Where(r=>words.All(word=>r.Tag.Contains(word,StringComparison.OrdinalIgnoreCase) || r.Description.Contains(word,StringComparison.OrdinalIgnoreCase)))
            .Where(r=>filters==OllamaModelFilter.None ||
                (filters.HasFlag(OllamaModelFilter.Downloaded) && r.Installed) ||
                (filters.HasFlag(OllamaModelFilter.NotDownloaded) && r.AvailabilityKnown && !r.Installed) ||
                (filters.HasFlag(OllamaModelFilter.Recommended) && r.Recommended) ||
                (filters.HasFlag(OllamaModelFilter.Supported) && r.Supported==true) ||
                (filters.HasFlag(OllamaModelFilter.Unsupported) && r.Supported==false));
        return (sort switch
        {
            OllamaModelSort.Name=>matching.OrderBy(r=>r.Tag,StringComparer.OrdinalIgnoreCase),
            OllamaModelSort.SizeAscending=>matching.OrderBy(r=>r.SizeGb is null).ThenBy(r=>r.SizeGb).ThenBy(r=>r.Tag,StringComparer.OrdinalIgnoreCase),
            OllamaModelSort.SizeDescending=>matching.OrderBy(r=>r.SizeGb is null).ThenByDescending(r=>r.SizeGb).ThenBy(r=>r.Tag,StringComparer.OrdinalIgnoreCase),
            _=>matching.OrderBy(r=>r.Installed ? r.Recommended ? 0 : 1 : 2).ThenByDescending(r=>r.Recommended)
                .ThenBy(r=>r.SizeGb ?? double.MaxValue).ThenBy(r=>r.Tag,StringComparer.OrdinalIgnoreCase)
        }).ToArray();
    }
}
