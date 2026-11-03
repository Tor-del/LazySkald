$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$compilerDirectory = Join-Path $PSScriptRoot 'innosetup'
$compiler = Join-Path $compilerDirectory 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) { Write-Output $compiler; return }
$downloads = Join-Path $PSScriptRoot 'downloads'
New-Item -ItemType Directory -Path $downloads,$compilerDirectory -Force | Out-Null
$setup = Join-Path $downloads 'innosetup-6.7.4.exe'
$url = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe'
$expectedHash = 'A549929319F9120716677C685535CA4671E4B937B5FDC87E2585F32D8EB30E8C'
if (-not (Test-Path -LiteralPath $setup)) { Invoke-WebRequest -Uri $url -OutFile $setup }
if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Inno Setup download checksum mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $setup
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*') { throw 'Inno Setup signature validation failed.' }
$process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/CURRENTUSER','/TASKS=',('/DIR="' + $compilerDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
$process.WaitForExit()
for ($attempt = 0; $attempt -lt 40 -and -not (Test-Path -LiteralPath $compiler); $attempt++) { Start-Sleep -Milliseconds 250 }
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Inno Setup compiler was not installed.' }
Write-Output $compiler
