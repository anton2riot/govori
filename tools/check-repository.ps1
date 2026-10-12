param(
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $RepositoryRoot
try {
    $files = @(git -c core.quotePath=false ls-files --cached --others --exclude-standard | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) { throw 'Run this check from an initialized Git checkout.' }
    $stagedFiles = @(git -c core.quotePath=false ls-files --cached)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the Git index.' }
    $trackedIgnored = @(git -c core.quotePath=false ls-files --cached --ignored --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect tracked ignored files.' }
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($file in $trackedIgnored) { $failures.Add("Ignored file is already tracked: $file") }

    # Only application assets are allowed as binary files. Scan them for tokens too.
    $binaryAssets = @('src/Govori.Desktop/Assets/govori.png', 'src/Govori.Desktop/Assets/govori.ico')
    $publicHosts = @('generativelanguage.googleapis.com', 'github.com', 'schemas.microsoft.com', 'www.apple.com')
    $secretPatterns = @(
        '\bAIza[0-9A-Za-z_-]{35}\b',
        '\b(?:sk-(?:proj-|svcacct-)?|gh[pousr]_|github_pat_|xox[baprs]-)[A-Za-z0-9_-]{20,}\b',
        '\b(?:AKIA|ASIA)[A-Z0-9]{16}\b',
        '\b[0-9]/[A-Za-z0-9]{16,}:[A-Za-z0-9]{16,}\b',
        ('-----BEGIN(?: [A-Z]+)* PRIVATE' + ' KEY-----')
    )
    $credentialName = '(?:(?:[A-Za-z0-9]+[_-])*(?:api[_-]?key|access[_-]?token|auth[_-]?token|token|password|client[_-]?secret|private[_-]?key|secret|pat)|(?:Gemini|Google|OpenAI|Asana)ApiKey)'
    $quotedCredential = '(?im)(?<![\w])(?<name>' + $credentialName + ')[ \t]*["'']?[ \t]*[:=][ \t]*["''](?<value>[^"''\r\n]+)["'']'
    $envCredential = '(?im)^[ \t]*(?:export[ \t]+)?' + $credentialName + '[ \t]*=[ \t]*([^\r\n]+)'
    $absolutePath = '(?i)(?<![A-Za-z0-9])(?:[A-Z]:[\\/]+|\\\\[A-Za-z0-9][A-Za-z0-9._-]*[\\/]+|/(?:Users|home)/[^/\s"''<>]+/|file:/{2})'
    $localFile = '(?i)(?:^|/)(?:history|settings|credentials|service-account[^/]*)\.json(?:\..*)?$|(?:^|/)gemini-key\.bin(?:\..*)?$|(?:^|/)\.env(?:$|\.)|(?:^|/)id_(?:rsa|dsa|ecdsa|ed25519)(?:$|\.)|\.(?:exe|dll|pdb|wav|mp3|m4a|ogg|flac|aac|pem|key|p12|pfx|jks|keystore|log|tmp|damaged|zip|7z|tar|gz)$'
    $localDirectory = '(?i)(?:^|/)(?:bin|obj|artifacts|app|vendor|output|test-results|recordings|backups|\.vs|\.idea|\.avalonia-build-tasks)(?:/|$)'

    function Test-PublicContent([string]$Content, [string]$Label, [bool]$Binary = $false) {
        foreach ($pattern in $secretPatterns) {
            if ($Content -match $pattern) { $failures.Add("Possible secret: $Label"); break }
        }
        if ($Binary) { return }
        foreach ($match in [regex]::Matches($Content, $quotedCredential)) {
            # The two reviewed API-key field captions are UI text, not credentials.
            if ($Label -match 'src/Govori.Desktop/Localization/(?:en|ru)\.json$' -and $match.Groups['name'].Value -ceq 'ApiKey' -and $match.Groups['value'].Value -cin @('Gemini API key', 'Ключ Gemini API')) { continue }
            $failures.Add("Non-empty credential literal: $Label"); break
        }
        foreach ($match in [regex]::Matches($Content, $envCredential)) {
            $value = $match.Groups[1].Value.Trim()
            if ($value -notmatch '^(?:["'']{2})?\s*(?:#.*)?$') { $failures.Add("Non-empty credential assignment: $Label"); break }
        }
        if ($Content -match $absolutePath) { $failures.Add("Absolute machine path or file link: $Label") }
        foreach ($match in [regex]::Matches($Content, '(?i)\b(?:https?|ssh|git|ftp)://([^/\\\s"''<>`]+)')) {
            if ($match.Groups[1].Value.ToLowerInvariant() -notin $publicHosts) {
                $failures.Add("Unreviewed URL host or credentials in URL: $Label"); break
            }
        }
    }

    function Read-StagedBytes([string]$File) {
        $start = [Diagnostics.ProcessStartInfo]::new('git')
        $start.WorkingDirectory = (Get-Location).Path
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.ArgumentList.Add('show')
        $start.ArgumentList.Add(":" + $File)
        $process = [Diagnostics.Process]::Start($start)
        $buffer = [IO.MemoryStream]::new()
        try {
            $errorTask = $process.StandardError.ReadToEndAsync()
            $process.StandardOutput.BaseStream.CopyTo($buffer)
            $process.WaitForExit()
            $null = $errorTask.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0) { throw "Could not read staged file: $File" }
            return ,$buffer.ToArray()
        } finally { $buffer.Dispose(); $process.Dispose() }
    }

    foreach ($file in $files) {
        if (($file -match $localFile -and $file -notmatch '(?:^|/)\.env\.example$') -or $file -match $localDirectory) {
            $failures.Add("Local, generated or sensitive file: $file")
        }
        Test-PublicContent $file "file name: $file"
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            if ((Get-Item -LiteralPath $file -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                $failures.Add("Review symbolic link before publishing: $file")
                continue
            }
            $bytes = [IO.File]::ReadAllBytes((Join-Path (Get-Location).Path $file))
            if ($bytes -contains 0 -and $file -notin $binaryAssets) { $failures.Add("Unexpected binary file: $file") }
            Test-PublicContent ([Text.Encoding]::UTF8.GetString($bytes)) "working tree: $file" ($file -in $binaryAssets)
        }
        # A clean working copy does not guarantee that an older staged copy is clean.
        if ($file -in $stagedFiles) {
            [byte[]]$stagedBytes = Read-StagedBytes $file
            if ($stagedBytes -contains 0 -and $file -notin $binaryAssets) { $failures.Add("Unexpected binary in Git index: $file") }
            Test-PublicContent ([Text.Encoding]::UTF8.GetString($stagedBytes)) "Git index: $file" ($file -in $binaryAssets)
        }
    }
    if ($failures.Count) {
        $failures | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
        throw 'Repository check failed. No secret values were printed.'
    }
    Write-Host "Repository check passed: $($files.Count) publishable files; working tree and Git index checked."
} finally { Pop-Location }
