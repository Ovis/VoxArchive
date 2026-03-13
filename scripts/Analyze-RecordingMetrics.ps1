param(
    [Parameter(Mandatory = $true)]
    [string]$MetricsCsv,

    [string]$OutPath = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $MetricsCsv)) {
    throw "Metrics CSV not found: $MetricsCsv"
}

$rows = Import-Csv $MetricsCsv
if (-not $rows -or $rows.Count -eq 0) {
    throw "Metrics CSV is empty: $MetricsCsv"
}

function ToDouble($v) {
    return [double]::Parse($v, [System.Globalization.CultureInfo]::InvariantCulture)
}

function ToInt64($v) {
    return [int64]::Parse($v, [System.Globalization.CultureInfo]::InvariantCulture)
}

$elapsed = $rows | ForEach-Object { ToDouble $_.elapsed_seconds }
$drift = $rows | ForEach-Object { ToDouble $_.drift_ppm }
$micBuf = $rows | ForEach-Object { ToDouble $_.mic_buffer_ms }
$spkBuf = $rows | ForEach-Object { ToDouble $_.speaker_buffer_ms }
$underflowLast = ToInt64 $rows[-1].underflow_count
$overflowLast = ToInt64 $rows[-1].overflow_count

$startTs = $rows[0].timestamp
$endTs = $rows[-1].timestamp
$durationSec = [math]::Round(($elapsed[-1] - $elapsed[0]), 3)

$avgDrift = [math]::Round((($drift | Measure-Object -Average).Average), 6)
$maxAbsDrift = [math]::Round((($drift | ForEach-Object { [math]::Abs($_) } | Measure-Object -Maximum).Maximum), 6)

$avgMicBuf = [math]::Round((($micBuf | Measure-Object -Average).Average), 3)
$minMicBuf = [math]::Round((($micBuf | Measure-Object -Minimum).Minimum), 3)
$maxMicBuf = [math]::Round((($micBuf | Measure-Object -Maximum).Maximum), 3)

$avgSpkBuf = [math]::Round((($spkBuf | Measure-Object -Average).Average), 3)
$minSpkBuf = [math]::Round((($spkBuf | Measure-Object -Minimum).Minimum), 3)
$maxSpkBuf = [math]::Round((($spkBuf | Measure-Object -Maximum).Maximum), 3)

$report = @()
$report += "# 録音メトリクス集計レポート"
$report += ""
$report += "- 入力: $MetricsCsv"
$report += "- 期間: $startTs -> $endTs"
$report += "- サンプル数: $($rows.Count)"
$report += "- 経過秒数: $durationSec"
$report += ""
$report += "## Drift"
$report += "- 平均 Drift(ppm): $avgDrift"
$report += "- 最大絶対 Drift(ppm): $maxAbsDrift"
$report += ""
$report += "## Mic Buffer"
$report += "- 平均(ms): $avgMicBuf"
$report += "- 最小(ms): $minMicBuf"
$report += "- 最大(ms): $maxMicBuf"
$report += ""
$report += "## Speaker Buffer"
$report += "- 平均(ms): $avgSpkBuf"
$report += "- 最小(ms): $minSpkBuf"
$report += "- 最大(ms): $maxSpkBuf"
$report += ""
$report += "## Counters"
$report += "- Underflow 最終値: $underflowLast"
$report += "- Overflow 最終値: $overflowLast"

if ([string]::IsNullOrWhiteSpace($OutPath)) {
    $OutPath = [System.IO.Path]::ChangeExtension($MetricsCsv, ".summary.md")
}

$report -join [Environment]::NewLine | Set-Content $OutPath
Write-Host "Summary written: $OutPath"
