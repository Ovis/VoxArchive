param(
    [string]$SessionId = "",
    [string]$BaseDir = "logs/longrun",
    [string]$OutputFile = ""
)

$ErrorActionPreference = "Stop"

function Resolve-SessionRoot {
    param(
        [string]$Base,
        [string]$Id
    )

    if (-not [string]::IsNullOrWhiteSpace($Id)) {
        return Join-Path $Base $Id
    }

    if (-not (Test-Path $Base)) {
        throw "BaseDir が存在しません: $Base"
    }

    $latest = Get-ChildItem -Path $Base -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $latest) {
        throw "BaseDir にセッションが存在しません: $Base"
    }

    return $latest.FullName
}

function New-ArtifactResult {
    param(
        [string]$Name,
        [string]$RelativePath,
        [string]$Description,
        [string]$SessionRoot
    )

    $fullPath = Join-Path $SessionRoot $RelativePath
    $exists = Test-Path $fullPath
    return [pscustomobject]@{
        Name = $Name
        RelativePath = $RelativePath
        Exists = $exists
        Description = $Description
    }
}

$sessionRoot = Resolve-SessionRoot -Base $BaseDir -Id $SessionId
$sessionName = Split-Path $sessionRoot -Leaf

$artifacts = @(
    New-ArtifactResult -Name "メトリクスCSV" -RelativePath "metrics/recording-metrics.csv" -Description "統計CSV" -SessionRoot $sessionRoot
    New-ArtifactResult -Name "メトリクスJSONL" -RelativePath "metrics/recording-metrics.jsonl" -Description "統計JSONL" -SessionRoot $sessionRoot
    New-ArtifactResult -Name "録音ログ" -RelativePath "metrics/recording.log" -Description "テキストログ" -SessionRoot $sessionRoot
    New-ArtifactResult -Name "長時間レポート" -RelativePath "report/longrun-report.md" -Description "1h/3h検証レポート" -SessionRoot $sessionRoot
    New-ArtifactResult -Name "1h証跡" -RelativePath "report/evidence-1h.md" -Description "1時間録音の結果" -SessionRoot $sessionRoot
    New-ArtifactResult -Name "3h証跡" -RelativePath "report/evidence-3h.md" -Description "3時間録音の結果" -SessionRoot $sessionRoot
    New-ArtifactResult -Name "CH分離証跡" -RelativePath "report/evidence-channels.md" -Description "CH1=Speaker / CH2=Mic の確認" -SessionRoot $sessionRoot
    New-ArtifactResult -Name "Processフォールバック証跡" -RelativePath "report/evidence-process-fallback.md" -Description "Process->Speaker 切替確認" -SessionRoot $sessionRoot
)

$existsMap = @{}
foreach ($artifact in $artifacts) {
    $existsMap[$artifact.Name] = $artifact.Exists
}

$requirement2 = if ($existsMap["メトリクスCSV"] -and $existsMap["メトリクスJSONL"] -and $existsMap["録音ログ"]) { "PASS" } else { "PENDING" }
$requirement3 = if ($existsMap["CH分離証跡"]) { "PASS" } else { "PENDING" }
$requirement4 = if ($existsMap["1h証跡"] -and $existsMap["3h証跡"]) { "PASS" } else { "PENDING" }
$processFallback = if ($existsMap["Processフォールバック証跡"]) { "PASS" } else { "PENDING" }

if ([string]::IsNullOrWhiteSpace($OutputFile)) {
    $OutputFile = Join-Path $sessionRoot "spec-evidence-summary.md"
}

$generatedAt = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Spec Evidence Summary")
$lines.Add("")
$lines.Add("- generated_at: $generatedAt")
$lines.Add("- session: $sessionName")
$lines.Add("- root: $sessionRoot")
$lines.Add("")
$lines.Add("## Artifacts")
foreach ($artifact in $artifacts) {
    $status = if ($artifact.Exists) { "PASS" } else { "MISSING" }
    $lines.Add("- [$status] $($artifact.Name): $($artifact.RelativePath) ($($artifact.Description))")
}
$lines.Add("")
$lines.Add("## Completion Projection")
$lines.Add("- 完了条件2（2系統FLAC保存）: $requirement2")
$lines.Add("- 完了条件3（CH1/CH2保持）: $requirement3")
$lines.Add("- 完了条件4（1h以上安定）: $requirement4")
$lines.Add("- 追加仕様（Processフォールバック証跡）: $processFallback")
$lines.Add("")
$lines.Add("## Next")
if ($requirement2 -eq "PASS" -and $requirement3 -eq "PASS" -and $requirement4 -eq "PASS" -and $processFallback -eq "PASS") {
    $lines.Add("- すべての必須証跡が揃っています。`docs/release/spec-completion-checklist.md` を更新してください。")
}
else {
    foreach ($artifact in $artifacts | Where-Object { -not $_.Exists }) {
        $lines.Add("- 未取得: $($artifact.Name) -> $($artifact.RelativePath)")
    }
}

$lines -join "`r`n" | Set-Content $OutputFile
Write-Host "Generated summary: $OutputFile"
