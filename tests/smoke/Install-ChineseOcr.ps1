# Tries to install a Chinese OCR recognizer for Windows.Media.Ocr on a CI runner.
# Never fails the build: the smoke tests detect what is available and SKIP accordingly.
# Run with Windows PowerShell 5.1 (WinRT projection) as administrator.
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

function Get-OcrLanguages {
    try {
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
        return @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag })
    } catch {
        Write-Host "  (listing OCR recognizers failed: $($_.Exception.Message))"
        return @()
    }
}

function Test-ChineseOcr {
    return @(Get-OcrLanguages | Where-Object { $_ -like 'zh*' }).Count -gt 0
}

function Invoke-Bounded([string]$label, [scriptblock]$script, [int]$timeoutSeconds) {
    Write-Host "--- $label (timeout ${timeoutSeconds}s)"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $job = Start-Job -ScriptBlock $script
    if (Wait-Job $job -Timeout $timeoutSeconds) {
        Receive-Job $job 2>&1 | ForEach-Object { Write-Host "  $_" }
    } else {
        Write-Host "  TIMEOUT after ${timeoutSeconds}s"
        Stop-Job $job
    }
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    Write-Host ("  took {0:n0}s" -f $watch.Elapsed.TotalSeconds)
}

function Show-Capabilities([string]$label) {
    Write-Host "--- Language.OCR capabilities $label"
    try {
        Get-WindowsCapability -Online -Name 'Language.OCR*' | Format-Table Name, State -AutoSize | Out-String | Write-Host
    } catch {
        Write-Host "  $($_.Exception.Message)"
    }
}

Write-Host "OS: $([Environment]::OSVersion.VersionString)"
Write-Host "OCR recognizers before: $((Get-OcrLanguages) -join ', ')"
Show-Capabilities 'before'

if (-not (Test-ChineseOcr)) {
    Invoke-Bounded 'Add-WindowsCapability Language.OCR~~~zh-CN~0.0.1.0' {
        Add-WindowsCapability -Online -Name 'Language.OCR~~~zh-CN~0.0.1.0' | Out-String
    } 300
}

if (-not (Test-ChineseOcr)) {
    # Hosted images switch Windows Update off; features on demand need it.
    Write-Host '--- enabling Windows Update services and retrying'
    try {
        Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' -Name 'UseWUServer' -ErrorAction SilentlyContinue
        Set-Service wuauserv -StartupType Manual -ErrorAction SilentlyContinue
        Start-Service wuauserv -ErrorAction SilentlyContinue
        Get-Service wuauserv, bits, UsoSvc -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType -AutoSize | Out-String | Write-Host
    } catch {
        Write-Host "  $($_.Exception.Message)"
    }
    Invoke-Bounded 'Add-WindowsCapability zh-CN (second attempt)' {
        Add-WindowsCapability -Online -Name 'Language.OCR~~~zh-CN~0.0.1.0' | Out-String
    } 300
}

if (-not (Test-ChineseOcr)) {
    Invoke-Bounded 'Add-WindowsCapability Language.OCR~~~zh-TW~0.0.1.0' {
        Add-WindowsCapability -Online -Name 'Language.OCR~~~zh-TW~0.0.1.0' | Out-String
    } 240
}

if (-not (Test-ChineseOcr)) {
    if (Get-Command Install-Language -ErrorAction SilentlyContinue) {
        Invoke-Bounded 'Install-Language zh-CN' { Install-Language zh-CN | Out-String } 420
    } else {
        Write-Host '--- Install-Language cmdlet is not available on this image'
    }
}

Show-Capabilities 'after'
Write-Host "OCR recognizers after: $((Get-OcrLanguages) -join ', ')"
if (Test-ChineseOcr) {
    Write-Host 'RESULT: Chinese OCR recognizer is available.'
} else {
    Write-Host 'RESULT: NO Chinese OCR recognizer could be installed; OCR checks will use a fallback language and SKIP the Chinese assertions.'
}
exit 0
