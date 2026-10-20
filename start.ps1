$ErrorActionPreference = 'Stop'
$installed = Join-Path $env:LOCALAPPDATA 'Dictophone.CrossPlatform\app\Govori.CrossPlatform.exe'
if (-not (Test-Path -LiteralPath $installed)) { & (Join-Path $PSScriptRoot 'install.ps1') -NoLaunch }
Start-Process -FilePath $installed -WorkingDirectory (Split-Path $installed) -WindowStyle Hidden
