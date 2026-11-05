param(
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [string]$OutputDirectory='artifacts/LazySkald-GameBar',
    [string]$CertificateThumbprint=''
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Set-Location $PSScriptRoot
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$msbuild=& $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (!$msbuild) { throw 'Install Visual Studio with Universal Windows Platform development and Windows SDK 10.0.22621.0.' }
$destination=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$arguments=@('src/ScreenReader.GameBar/ScreenReader.GameBar.csproj','/restore',"/p:Configuration=$Configuration",'/p:Platform=x64',
    "/p:RestorePackagesPath=$PSScriptRoot/.packages","/p:AppxPackageDir=$destination/",'/p:GenerateAppxPackageOnBuild=true','/v:minimal')
if ($CertificateThumbprint) { $arguments+='/p:AppxPackageSigningEnabled=true'; $arguments+="/p:PackageCertificateThumbprint=$CertificateThumbprint" }
& $msbuild @arguments
if ($LASTEXITCODE -ne 0) { throw 'Game Bar build failed.' }
# The unpacked layout can be registered with Developer Mode without a signing certificate.
$development=Join-Path $destination 'Development'
New-Item -ItemType Directory -Path $development -Force | Out-Null
$package=Get-ChildItem -LiteralPath $destination -Recurse -File -Filter '*.msix' |
    Where-Object { $_.Directory.Name -like '*_Test' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (!$package) { throw 'The built MSIX was not found.' }
Expand-Archive -LiteralPath $package.FullName -DestinationPath $development -Force
if (!(Test-Path -LiteralPath (Join-Path $development 'Assets/StoreLogo.png'))) { throw 'Incomplete development layout.' }
Copy-Item -LiteralPath 'tools/Install-GameBar.ps1','src/ScreenReader.GameBar/README.ru.md' -Destination $destination -Force
Copy-Item -LiteralPath 'licenses/GameBar-SDK.txt' -Destination $destination -Force
Write-Host "Ready: $destination"
Write-Host 'Unsigned local build: enable Windows Developer Mode, then run Install-GameBar.ps1 -Development.'
Write-Host 'For distribution, sign with a trusted CN=LazySkald code-signing certificate using -CertificateThumbprint.'
