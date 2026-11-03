param([string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$qa = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\installer-qa'))
$install = Join-Path $qa 'installed'
if (-not $install.StartsWith($qa + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe QA target.' }
$registryPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\48BCE782-588B-4992-8CCD-021C421878D0_is1'
$hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry64)
function Installed-Version {
    $key = $hive.OpenSubKey($registryPath)
    if (-not $key) { return '' }
    try { return [string]$key.GetValue('DisplayVersion','') } finally { $key.Dispose() }
}
if (Installed-Version) { throw 'A QA installation already exists. Remove that isolated QA installation before repeating the test.' }
function Run-Package([string]$file,[string[]]$arguments) {
    $process = Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    $process.WaitForExit(); $process.Refresh()
    if ($null -ne $process.ExitCode -and $process.ExitCode -ne 0) { throw "Package failed: $file ($($process.ExitCode))" }
}
function Wait-Version([string]$expected) {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ((Installed-Version) -eq $expected) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Registry version did not become '$expected'."
}
$fixtureExe = Join-Path $root 'src\ScreenReader.App\bin\Release\net10.0-windows10.0.22621.0\LazySkald.exe'
New-Item -ItemType Directory -Path "$qa\v1","$qa\v2","$qa\v3","$qa\user-state" -Force | Out-Null
Copy-Item -LiteralPath $fixtureExe -Destination "$qa\v1\LazySkald.exe" -Force
Copy-Item -LiteralPath $fixtureExe -Destination "$qa\v2\LazySkald.exe" -Force
Copy-Item -LiteralPath $fixtureExe -Destination "$qa\v3\LazySkald.exe" -Force
Set-Content -LiteralPath "$qa\v1\old-owned.txt" -Value 'old package marker'
Set-Content -LiteralPath "$qa\v2\new-owned.txt" -Value 'new package marker'
Set-Content -LiteralPath "$qa\v3\latest-owned.txt" -Value 'latest package marker'
Set-Content -LiteralPath "$qa\user-state\settings.json" -Value '{"preserve":true}'
$build = @{ SkipPublish=$true; OutputDirectory='artifacts/installer-qa/packages'; ConfigFile='tests/installer/FixtureConfig.iss' }
if ($CompilerPath) { $build.CompilerPath=$CompilerPath }
& ./build-installer.ps1 @build -SourceDirectory artifacts/installer-qa/v1 -Version 0.5.0
& ./build-installer.ps1 @build -SourceDirectory artifacts/installer-qa/v2 -Version 0.5.1
& ./build-installer.ps1 @build -SourceDirectory artifacts/installer-qa/v3 -Version 0.5.2
$package1 = Join-Path $qa 'packages\ScreenReaderInstallerQA-0.5.0-Setup-x64.exe'
$package2 = Join-Path $qa 'packages\ScreenReaderInstallerQA-0.5.1-Setup-x64.exe'
$package3 = Join-Path $qa 'packages\ScreenReaderInstallerQA-0.5.2-Setup-x64.exe'
$common = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/LANG=english',('/DIR="' + $install + '"'))
try {
    Run-Package $package1 ($common + ('/LOG="' + $qa + '\install-v1.log"'))
    Wait-Version '0.5.0'
    if (-not (Test-Path -LiteralPath "$install\old-owned.txt")) { throw 'First package marker missing.' }
    Write-Host 'PASS: first version installed under isolated AppId.'
    Run-Package $package2 ($common + ('/LOG="' + $qa + '\upgrade-v2.log"'))
    Wait-Version '0.5.1'
    if (Test-Path -LiteralPath "$install\old-owned.txt") { throw 'Previous owned file survived: uninstall-before-install failed.' }
    if (-not (Test-Path -LiteralPath "$install\new-owned.txt")) { throw 'New package marker missing.' }
    if (-not (Select-String -LiteralPath "$qa\upgrade-v2.log" -Pattern 'Previous version removed successfully' -Quiet)) { throw 'Previous uninstall not confirmed by setup log.' }
    Write-Host 'PASS: old version uninstalled before new files, registry updated to one installation.'
    Run-Package $package3 ($common + ('/LOG="' + $qa + '\upgrade-v3.log"'))
    Wait-Version '0.5.2'
    if ((Test-Path -LiteralPath "$install\new-owned.txt") -or -not (Test-Path -LiteralPath "$install\latest-owned.txt")) { throw 'Second upgrade did not replace the owned file set.' }
    Write-Host 'PASS: a second successive upgrade uses the registered uninstaller correctly.'
    if (-not (Test-Path -LiteralPath "$qa\user-state\settings.json")) { throw 'External user settings were removed.' }
    Write-Host 'PASS: user data outside the application folder preserved.'
} finally {
    $key = $hive.OpenSubKey($registryPath)
    if ($key) {
        try { $uninstaller = ([string]$key.GetValue('UninstallString','')).Trim('"') } finally { $key.Dispose() }
        if (-not [string]::Equals([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($uninstaller)),$install,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected QA uninstaller location.' }
        if ([IO.Path]::GetFileName($uninstaller) -notlike 'unins*.exe') { throw 'Unexpected QA uninstaller name.' }
        Run-Package $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
    }
    Wait-Version ''
    $hive.Dispose()
}
if (Test-Path -LiteralPath "$install\LazySkald.exe") { throw 'Uninstall left the installed executable behind.' }
Write-Host 'PASS: isolated QA installation fully uninstalled.'
