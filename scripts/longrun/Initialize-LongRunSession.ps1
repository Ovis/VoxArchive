param(
    [string]$SessionId = "",
    [string]$BaseDir = "logs/longrun"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SessionId)) {
    $SessionId = "session-" + (Get-Date -Format "yyyyMMdd-HHmmss")
}

$root = Join-Path $BaseDir $SessionId
$reportDir = Join-Path $root "report"
$metricsDir = Join-Path $root "metrics"
$systemDir = Join-Path $root "system"

New-Item -ItemType Directory -Force -Path $root,$reportDir,$metricsDir,$systemDir | Out-Null

Copy-Item "docs/testing/reports/longrun-report-template.md" (Join-Path $reportDir "longrun-report.md") -Force

$gitHash = git rev-parse --short HEAD
$branch = git branch --show-current
$dotnetVersion = dotnet --version

function Get-SafeCimInfo {
    param(
        [string]$ClassName,
        [string[]]$Properties
    )

    try {
        return Get-CimInstance $ClassName | Select-Object -Property $Properties
    }
    catch {
        return @{
            unavailable = $true
            class = $ClassName
            reason = $_.Exception.Message
        }
    }
}

$osInfo = Get-SafeCimInfo -ClassName "Win32_OperatingSystem" -Properties @("Caption", "Version", "BuildNumber")
$cpuInfo = Get-SafeCimInfo -ClassName "Win32_Processor" -Properties @("Name", "NumberOfCores", "NumberOfLogicalProcessors")
$memInfo = Get-SafeCimInfo -ClassName "Win32_ComputerSystem" -Properties @("TotalPhysicalMemory")

@{
    session_id = $SessionId
    created_at = (Get-Date).ToString("o")
    git_branch = $branch
    git_commit = $gitHash
    dotnet_version = $dotnetVersion
    os = $osInfo
    cpu = $cpuInfo
    memory = $memInfo
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $systemDir "environment.json")

@"
# LongRun Session

- session_id: $SessionId
- created_at: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
- branch: $branch
- commit: $gitHash
- dotnet: $dotnetVersion

## paths
- report: $reportDir\longrun-report.md
- metrics csv: $metricsDir\recording-metrics.csv
- metrics jsonl: $metricsDir\recording-metrics.jsonl
- recording log: $metricsDir\recording.log
- environment: $systemDir\environment.json

## next
1. アプリ実行前に保存先を上記 metrics ディレクトリへ設定
2. 1h / 3h 録音実施
3. Analyze-RecordingMetrics.ps1 で summary 作成
"@ | Set-Content (Join-Path $root "README.md")

Write-Host "Initialized long-run session: $root"
