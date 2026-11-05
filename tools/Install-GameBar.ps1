param([switch]$Development)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if (!$Development) { throw 'For an unsigned local build, enable Windows Developer Mode and run this script with -Development. Install a signed MSIX using Windows App Installer.' }
$manifest=Join-Path $PSScriptRoot 'Development/AppxManifest.xml'
if (!(Test-Path -LiteralPath $manifest)) { throw 'Run build-gamebar.ps1 first, then use Install-GameBar.ps1 from its output directory.' }
$identity=([xml](Get-Content -LiteralPath $manifest -Raw)).Package.Identity.Name
if ($identity -ne 'LazySkald.GameBar') { throw 'Unexpected package identity.' }
$developerMode=Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue
if ($developerMode -ne 1) { throw 'Enable Developer Mode in Windows Settings > System > Advanced (or For developers), then run this script again.' }
$dependencyDirectories=Get-ChildItem -LiteralPath $PSScriptRoot -Directory | Where-Object Name -Like '*_Test'
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($directory in $dependencyDirectories) {
    $dependencyPath=Join-Path $directory.FullName 'Dependencies/x64'
    if (Test-Path -LiteralPath $dependencyPath) {
        foreach ($dependency in Get-ChildItem -LiteralPath $dependencyPath -File | Where-Object Extension -In '.appx','.msix') {
            $archive=[IO.Compression.ZipFile]::OpenRead($dependency.FullName)
            try {
                $reader=[IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
                try { $dependencyIdentity=([xml]$reader.ReadToEnd()).Package.Identity }
                finally { $reader.Dispose() }
            }
            finally { $archive.Dispose() }
            $installed=Get-AppxPackage -Name $dependencyIdentity.Name | Where-Object { [version]$_.Version -ge [version]$dependencyIdentity.Version }
            if (!$installed) { Add-AppxPackage -Path $dependency.FullName }
        }
    }
}
Add-AppxPackage -Register $manifest
Write-Host 'Installed LazySkald Game Bar for the current user. Open Win+G > Widgets > LazySkald and pin it.'
