param(
    [string]$SourceDirectory,
    [string]$OutputDirectory = 'artifacts\installers',
    [string]$ConfigFile = 'installer\AppConfig.iss',
    [string]$Version,
    [string]$CompilerPath,
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Version must contain 3 or 4 numeric components.' }
if (-not $SourceDirectory) { $SourceDirectory = "artifacts\LazySkald-win-x64-$Version" }
if (-not $SkipPublish) { & ./build.ps1 -OutputDirectory $SourceDirectory }
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$config = (Resolve-Path -LiteralPath $ConfigFile).Path
$output = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
if (-not $CompilerPath) {
    $localCompiler = Join-Path $PSScriptRoot 'tools\innosetup\ISCC.exe'
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $localCompiler) { $CompilerPath = $localCompiler }
    elseif ($command) { $CompilerPath = $command.Source }
    else { $CompilerPath = & ./tools/bootstrap-inno.ps1 }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $CompilerPath '/Qp' "/DAppVersion=$Version" "/DSourceDir=$source" "/DInstallerOutputDir=$output" "/DAppConfigPath=$config" 'installer\setup.iss'
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
Write-Host "Installer output: $output"
