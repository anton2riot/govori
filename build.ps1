param(
    [string]$Runtime = 'win-x64',
    [switch]$Verify,
    [string]$LiveAudio = '',
    [string]$OutputDirectory = '',
    [string]$BuildArtifactsDirectory = '',
    [string]$VerificationDirectory = ''
)
$ErrorActionPreference = 'Stop'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if ($LiveAudio) { $LiveAudio = (Resolve-Path -LiteralPath $LiveAudio).Path }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "artifacts\publish\$Runtime" }
if (-not $BuildArtifactsDirectory) { $BuildArtifactsDirectory = Join-Path $PSScriptRoot 'artifacts\dotnet' }
if (-not $VerificationDirectory) { $VerificationDirectory = Join-Path $PSScriptRoot 'artifacts\verification' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$BuildArtifactsDirectory = [IO.Path]::GetFullPath($BuildArtifactsDirectory)
$VerificationDirectory = [IO.Path]::GetFullPath($VerificationDirectory)
Push-Location $PSScriptRoot
try {
    dotnet publish src/Govori.Desktop/Govori.Desktop.csproj -c Release -r $Runtime --self-contained true --artifacts-path $BuildArtifactsDirectory -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o $OutputDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Could not build Govori.' }
    if ($Verify) {
        if ($Runtime -ne 'win-x64' -or -not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { throw 'Desktop verification requires Windows x64.' }
        $arguments = @('--verify', '--tray', '--data-dir', ('"' + $VerificationDirectory + '"'))
        if ($LiveAudio) { $arguments += @('--live-api', ('"' + $LiveAudio + '"')) }
        $process = Start-Process -FilePath (Join-Path $OutputDirectory 'Govori.CrossPlatform.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(60000)) { Stop-Process -Id $process.Id; throw 'Verification timed out after 60 seconds.' }
        Get-Content -LiteralPath (Join-Path $VerificationDirectory 'verification.txt') -ErrorAction SilentlyContinue
        if ($process.ExitCode -ne 0) { throw "Verification failed. See $VerificationDirectory" }
    }
    Write-Host "Built: $OutputDirectory"
} finally { Pop-Location }
