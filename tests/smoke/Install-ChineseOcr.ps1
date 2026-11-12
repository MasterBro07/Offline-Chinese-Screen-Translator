# Tries to install a Chinese OCR recognizer for Windows.Media.Ocr on a CI runner.
# Never fails the build: the smoke tests detect what is available and SKIP accordingly.
# Run with Windows PowerShell 5.1 (WinRT projection) as administrator.
#
#   -Start   starts the installation in a detached background process (it needs several
#            minutes on a hosted runner; the build runs meanwhile) and returns at once.
#   -Wait    waits for that process (at most -TimeoutSeconds), prints its log and the result.
param(
    [switch]$Start,
    [switch]$Wait,
    [switch]$Worker,
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$logFile = Join-Path $env:TEMP 'ocr-install.log'
$pidFile = Join-Path $env:TEMP 'ocr-install.pid'

function Get-OcrLanguages {
    try {
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
        return @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag })
    } catch {
        return @()
    }
}

function Test-ChineseOcr {
    return @(Get-OcrLanguages | Where-Object { $_ -like 'zh*' }).Count -gt 0
}

function Get-OcrCapabilities {
    try {
        return (Get-WindowsCapability -Online -Name 'Language.OCR*' |
            Where-Object { $_.State -ne 'NotPresent' -or $_.Name -like '*zh-*' } |
            ForEach-Object { "$($_.Name)=$($_.State)" }) -join '; '
    } catch {
        return "query failed: $($_.Exception.Message)"
    }
}

if ($Worker) {
    # Detached worker: one long attempt (interrupting DISM leaves the capability half installed),
    # then one alternative.
    function Log([string]$text) { Add-Content -Path $logFile -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $text) }
    Log "worker started; recognizers: $((Get-OcrLanguages) -join ', '); capabilities: $(Get-OcrCapabilities)"
    foreach ($name in 'Language.OCR~~~zh-CN~0.0.1.0', 'Language.OCR~~~zh-CN~0.0.1.0', 'Language.OCR~~~zh-TW~0.0.1.0') {
        if (Test-ChineseOcr) { break }
        Log "Add-WindowsCapability $name ..."
        try {
            $result = Add-WindowsCapability -Online -Name $name -ErrorAction Stop
            Log "  finished: RestartNeeded=$($result.RestartNeeded)"
        } catch {
            Log "  failed: $($_.Exception.Message)"
        }
        Log "  recognizers: $((Get-OcrLanguages) -join ', '); capabilities: $(Get-OcrCapabilities)"
    }
    Log 'worker finished'
    exit 0
}

if ($Start) {
    Remove-Item $logFile, $pidFile -ErrorAction SilentlyContinue
    Write-Host "OS: $([Environment]::OSVersion.VersionString)"
    Write-Host "OCR recognizers before: $((Get-OcrLanguages) -join ', ')"
    if (Test-ChineseOcr) {
        Write-Host 'A Chinese recognizer is already installed.'
        exit 0
    }

    $process = Start-Process -FilePath 'powershell.exe' -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Worker')
    Set-Content -Path $pidFile -Value $process.Id
    Write-Host "Started the background installation (pid $($process.Id))."
    exit 0
}

if ($Wait) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    if (Test-Path $pidFile) {
        $id = [int](Get-Content $pidFile)
        while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            if (-not (Get-Process -Id $id -ErrorAction SilentlyContinue)) { break }
            if (Test-ChineseOcr) { break }
            Start-Sleep -Seconds 5
        }
        if (Get-Process -Id $id -ErrorAction SilentlyContinue) {
            if (Test-ChineseOcr) {
                Write-Host 'The recognizer is available; leaving the worker to finish on its own.'
            } else {
                Write-Host "TIMEOUT: the installation did not finish within $TimeoutSeconds s; stopping the worker."
                Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Write-Host ("Waited {0:n0} s. Worker log:" -f $watch.Elapsed.TotalSeconds)
    if (Test-Path $logFile) { Get-Content $logFile | ForEach-Object { Write-Host "  $_" } }
    Write-Host "OCR recognizers after: $((Get-OcrLanguages) -join ', ')"
    if (Test-ChineseOcr) {
        Write-Host 'RESULT: Chinese OCR recognizer is available.'
    } else {
        Write-Host 'RESULT: NO Chinese OCR recognizer could be installed; OCR checks use a fallback language and SKIP the Chinese assertions.'
    }
    exit 0
}

Write-Host 'Use -Start or -Wait.'
exit 0
