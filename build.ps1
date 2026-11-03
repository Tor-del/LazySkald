param([switch]$FrameworkDependent,[string]$OutputDirectory = 'artifacts\LazySkald-win-x64')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$destination = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
dotnet publish src/ScreenReader.App/ScreenReader.App.csproj -c Release -r win-x64 --self-contained $selfContained -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath 'README.md','THIRD-PARTY-NOTICES.md' -Destination $destination -Force
if (Test-Path -LiteralPath 'README.en.md') { Copy-Item -LiteralPath 'README.en.md' -Destination $destination -Force }
Copy-Item -LiteralPath 'licenses' -Destination $destination -Recurse -Force
$sourceRoot = Join-Path $destination 'source'
foreach ($projectName in @('ScreenReader.Edge','ScreenReader.Speech')) {
    $projectSource = Join-Path $sourceRoot $projectName
    New-Item -ItemType Directory -Path $projectSource -Force | Out-Null
    Copy-Item "src/$projectName/*.cs","src/$projectName/*.csproj" -Destination $projectSource -Force
}
Copy-Item -LiteralPath 'Directory.Build.props' -Destination $sourceRoot -Force
Write-Host "Ready: $destination\LazySkald.exe"
