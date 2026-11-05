using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ScreenReader.App;

public sealed record OllamaDriver(string Name,string Version,string Date,string DeviceId="")
{
    public string? Vendor=>DeviceId.Contains("VEN_10DE",StringComparison.OrdinalIgnoreCase) || Name.Contains("NVIDIA",StringComparison.OrdinalIgnoreCase) ? "NVIDIA" :
        DeviceId.Contains("VEN_1002",StringComparison.OrdinalIgnoreCase) || Name.Contains("AMD",StringComparison.OrdinalIgnoreCase) || Name.Contains("Radeon",StringComparison.OrdinalIgnoreCase) ? "AMD" :
        DeviceId.Contains("VEN_8086",StringComparison.OrdinalIgnoreCase) || Name.Contains("Intel",StringComparison.OrdinalIgnoreCase) ? "Intel" : null;
    // Windows reports NVIDIA's 560.94 as e.g. 32.0.15.6094.
    public double? NvidiaVersion=>Vendor=="NVIDIA" &&
        System.Version.TryParse(Version,out var value) && value.Build>=0 && value.Revision>=0
            ? (value.Build%10*10000+value.Revision)/100d : null;
    public bool BelowMinimum=>OllamaGpuRequirements.BuiltIn.IsBelowMinimum(this);
}

public sealed record OllamaDriverReport(OllamaDriver[] Drivers,string[] Updates,bool Searched);

public static class OllamaDrivers
{
    public static async Task<OllamaDriverReport> CheckAsync(bool searchUpdates,CancellationToken token)
    {
        // Read only: neither the COM search nor CIM installs or changes a driver.
        const string script="""
            $ErrorActionPreference='Stop'
            [Console]::OutputEncoding=[System.Text.Encoding]::UTF8
            $cards=@(Get-CimInstance Win32_VideoController)
            $drivers=@($cards | ForEach-Object {
                [pscustomobject]@{Name=$_.Name;Version=[string]$_.DriverVersion;DeviceId=[string]$_.PNPDeviceID;Date=if ($_.DriverDate) {$_.DriverDate.ToString('yyyy-MM-dd')} else {''}}
            })
            $updates=@()
            if (SEARCH_UPDATES) {
                $session=New-Object -ComObject Microsoft.Update.Session
                $searcher=$session.CreateUpdateSearcher()
                $searcher.Online=$true
                $result=$searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0")
                if ($result.ResultCode -ne 2) {throw 'Windows Update search did not complete successfully'}
                $updates=@($result.Updates | Where-Object {$_.DriverClass -eq 'Display'} | ForEach-Object {$_.Title})
            }
            [pscustomobject]@{Drivers=$drivers;Updates=$updates;Searched=SEARCH_UPDATES} | ConvertTo-Json -Depth 4 -Compress
            """;
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
            RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script.Replace("SEARCH_UPDATES",searchUpdates ? "$true" : "$false",StringComparison.Ordinal))));
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(searchUpdates ? 55 : 15));
        using var process=Process.Start(start) ?? throw new InvalidOperationException(UiText.T("ollama.driverUnavailable"));
        var output=process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error=process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string json=await output;
            string details=await error;
            if (process.ExitCode!=0) throw new InvalidOperationException(details.Trim());
            return JsonSerializer.Deserialize<OllamaDriverReport>(json) ?? throw new InvalidDataException();
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree:true);
            try { await Task.WhenAll(output,error); }
            catch (OperationCanceledException) { }
        }
    }
}
