param(
    [string]$SourceDirectory = '',
    [switch]$StartWithSystem,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { throw 'This installer requires Windows.' }
if (-not $SourceDirectory) { $SourceDirectory = Join-Path $PSScriptRoot 'artifacts\publish\win-x64' }
if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory 'Govori.CrossPlatform.exe'))) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $SourceDirectory }
$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path
$dataDirectory = Join-Path $env:LOCALAPPDATA 'Dictophone.CrossPlatform'
$installationDirectory = Join-Path $dataDirectory 'app'
$executable = Join-Path $installationDirectory 'Govori.CrossPlatform.exe'
New-Item -ItemType Directory -Path $installationDirectory -Force | Out-Null
$settingsPath = Join-Path $dataDirectory 'settings.json'
$settings = if (Test-Path -LiteralPath $settingsPath) { Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{ Language = 'system'; StartWithSystem = $false } }
$historyPath = Join-Path $dataDirectory 'history.json'
if (Test-Path -LiteralPath $historyPath) {
    $history = Get-Content -LiteralPath $historyPath -Raw | ConvertFrom-Json
    $legacyStatus = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\Govori.Desktop\Localization\ru.json') -Raw | ConvertFrom-Json).'History.transcribing'
    if (@($history | Where-Object { $_.Status -in @('transcribing', $legacyStatus) }).Count -gt 0) { throw 'Finish transcription before installing an update.' }
}
$recordingsDirectory = Join-Path $dataDirectory 'recordings'
if (Test-Path -LiteralPath $recordingsDirectory) {
    foreach ($file in Get-ChildItem -LiteralPath $recordingsDirectory -Filter '*.wav') {
        try { $probe = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None); $probe.Dispose() }
        catch { throw 'Finish recording before installing an update.' }
    }
}
foreach ($process in @(Get-Process -Name 'Govori.CrossPlatform' -ErrorAction SilentlyContinue)) {
    # Verification uses an isolated data folder and must finish before install.
    Stop-Process -Id $process.Id; $process.WaitForExit()
}
foreach ($file in Get-ChildItem -LiteralPath $SourceDirectory) { Copy-Item -LiteralPath $file.FullName -Destination $installationDirectory -Recurse -Force }
if ((Get-FileHash -LiteralPath $executable).Hash -ne (Get-FileHash -LiteralPath (Join-Path $SourceDirectory 'Govori.CrossPlatform.exe')).Hash) { throw 'Installed binary does not match the published build.' }
$credentialsPath = Join-Path $dataDirectory '.env'
if (-not (Test-Path -LiteralPath $credentialsPath)) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot '.env.example') -Destination $credentialsPath }
if (-not $settings.Language) { $settings | Add-Member -NotePropertyName Language -NotePropertyValue 'system' -Force }
if ($StartWithSystem) { $settings | Add-Member -NotePropertyName StartWithSystem -NotePropertyValue $true -Force }
$temporarySettings = $settingsPath + '.tmp'
[IO.File]::WriteAllText($temporarySettings, ($settings | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporarySettings -Destination $settingsPath -Force
$startupDirectory = [Environment]::GetFolderPath('Startup')
$startupShortcut = Join-Path $startupDirectory 'Govori.lnk'
if ($settings.StartWithSystem) {
    New-Item -ItemType Directory -Path $startupDirectory -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($startupShortcut)
        $shortcut.TargetPath = $executable
        $shortcut.Arguments = '--tray'
        $shortcut.WorkingDirectory = $installationDirectory
        $shortcut.Description = 'Govori'
        $shortcut.IconLocation = $executable + ',0'
        $shortcut.Save()
    } finally {
        if ($null -ne $shortcut) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
} elseif (Test-Path -LiteralPath $startupShortcut) {
    Remove-Item -LiteralPath $startupShortcut
}
$startupKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
try {
    # Migrate the old Run entry after saving the startup shortcut.
    $startupKey.DeleteValue('Govori.CrossPlatform', $false)
} finally { $startupKey.Dispose() }
if (-not $NoLaunch) { Start-Process -FilePath $executable -ArgumentList '--tray' -WorkingDirectory $installationDirectory -WindowStyle Hidden }
Write-Host "Installed: $executable"
Write-Host "Start at login: $($settings.StartWithSystem -eq $true)"
