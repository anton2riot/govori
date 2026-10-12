$ErrorActionPreference = 'Stop'
$installed = Join-Path $env:LOCALAPPDATA 'Dictophone.CrossPlatform\app\Govori.CrossPlatform.exe'
$executable = if (Test-Path -LiteralPath $installed) { $installed } else { Join-Path $PSScriptRoot 'artifacts\publish\win-x64\Govori.CrossPlatform.exe' }
if (-not (Test-Path -LiteralPath $executable)) { & (Join-Path $PSScriptRoot 'build.ps1') }
Start-Process -FilePath $executable -WorkingDirectory $PSScriptRoot
