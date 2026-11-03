param([string]$ReleaseTag = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Set-Location (Split-Path $PSScriptRoot -Parent)

$version = [string]([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Invalid version in Directory.Build.props.' }
if ($ReleaseTag -and $ReleaseTag -cne "v$version") {
    throw "Release tag '$ReleaseTag' must match project version 'v$version'."
}

dotnet run --project tests/ScreenReader.Checks/ScreenReader.Checks.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Application checks failed.' }

$portable = "artifacts/LazySkald-win-x64-$version"
$release = 'artifacts/release'
# A fresh output prevents deleted files from an earlier build entering a release.
if (Test-Path -LiteralPath $portable) { throw "Output already exists: $portable. Use a clean checkout or move that folder before packaging." }
if (Test-Path -LiteralPath $release) { throw "Output already exists: $release. Move that folder before packaging." }
& ./build.ps1 -OutputDirectory $portable
& ./build-installer.ps1 -SkipPublish -SourceDirectory $portable -OutputDirectory $release -Version $version

$zip = Join-Path $release "LazySkald-$version-win-x64.zip"
Compress-Archive -Path $portable -DestinationPath $zip -CompressionLevel Optimal
$packages = @(Get-ChildItem -LiteralPath $release -File | Sort-Object Name)
if ($packages.Count -ne 2) { throw 'Expected exactly one installer and one portable ZIP.' }
$checksums = foreach ($package in $packages) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $package.Name
}
$checksums | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Release $version ready in $release"
