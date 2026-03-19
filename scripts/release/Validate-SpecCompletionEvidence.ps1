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

function Get-EvidenceStatus {
    param(
        [string]$FullPath
    )

    if (-not (Test-Path $FullPath)) {
        return "MISSING"
    }

    $content = Get-Content -Raw $FullPath
    if ($content -match '(?im)^status\s*:\s*PASS\s*$') {
        return "PASS"
    }

    if ($content -match '(?im)^status\s*:\s*FAIL\s*$') {
        return "FAIL"
    }

    return "PENDING"
}

function New-ArtifactResult {
    param(
        [string]$Name,
        [string]$RelativePath,
        [string]$Description,
        [string]$SessionRoot,
        [bool]$UseEvidenceStatus
    )

    $fullPath = Join-Path $SessionRoot $RelativePath
    if ($UseEvidenceStatus) {
        $status = Get-EvidenceStatus -FullPath $fullPath
        $satisfied = $status -eq "PASS"
        $exists = $status -ne "MISSING"
    }
    else {
        $exists = Test-Path $fullPath
        $status = if ($exists) { "PASS" } else { "MISSING" }
        $satisfied = $exists
    }

    return [pscustomobject]@{
        Name = $Name
        RelativePath = $RelativePath
        Exists = $exists
        Status = $status
        Satisfied = $satisfied
        Description = $Description
    }
}

$sessionRoot = Resolve-SessionRoot -Base $BaseDir -Id $SessionId
$sessionName = Split-Path $sessionRoot -Leaf

$artifacts = @(
    New-ArtifactResult -Name "メトリクスCSV" -RelativePath "metrics/recording-metrics.csv" -Description "統計CSV" -SessionRoot $sessionRoot -UseEvidenceStatus:$false
    New-ArtifactResult -Name "メトリクスJSONL" -RelativePath "metrics/recording-metrics.jsonl" -Description "統計JSONL" -SessionRoot $sessionRoot -UseEvidenceStatus:$false
    New-ArtifactResult -Name "録音ログ" -RelativePath "metrics/recording.log" -Description "テキストログ" -SessionRoot $sessionRoot -UseEvidenceStatus:$false
    New-ArtifactResult -Name "長時間レポート" -RelativePath "report/longrun-report.md" -Description "1h/3h検証レポート" -SessionRoot $sessionRoot -UseEvidenceStatus:$false
    New-ArtifactResult -Name "1h証跡" -RelativePath "report/evidence-1h.md" -Description "1時間録音の結果（status: PASS必須）" -SessionRoot $sessionRoot -UseEvidenceStatus:$true
    New-ArtifactResult -Name "3h証跡" -RelativePath "report/evidence-3h.md" -Description "3時間録音の結果（status: PASS必須）" -SessionRoot $sessionRoot -UseEvidenceStatus:$true
    New-ArtifactResult -Name "CH分離証跡" -RelativePath "report/evidence-channels.md" -Description "CH1=Speaker / CH2=Mic（status: PASS必須）" -SessionRoot $sessionRoot -UseEvidenceStatus:$true
    New-ArtifactResult -Name "Processフォールバック証跡" -RelativePath "report/evidence-process-fallback.md" -Description "Process->Speaker 切替確認（status: PASS必須）" -SessionRoot $sessionRoot -UseEvidenceStatus:$true
)

$artifactByName = @{}
foreach ($artifact in $artifacts) {
    $artifactByName[$artifact.Name] = $artifact
}

$requirement2 = if ($artifactByName["メトリクスCSV"].Satisfied -and $artifactByName["メトリクスJSONL"].Satisfied -and $artifactByName["録音ログ"].Satisfied) { "PASS" } else { "PENDING" }
$requirement3 = if ($artifactByName["CH分離証跡"].Satisfied) { "PASS" } else { "PENDING" }
$requirement4 = if ($artifactByName["1h証跡"].Satisfied -and $artifactByName["3h証跡"].Satisfied) { "PASS" } else { "PENDING" }
$processFallback = if ($artifactByName["Processフォールバック証跡"].Satisfied) { "PASS" } else { "PENDING" }

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
    $lines.Add("- [$($artifact.Status)] $($artifact.Name): $($artifact.RelativePath) ($($artifact.Description))")
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
    foreach ($artifact in $artifacts | Where-Object { -not $_.Satisfied }) {
        $lines.Add("- 要対応: $($artifact.Name) ($($artifact.Status)) -> $($artifact.RelativePath)")
    }
}

$lines -join "`r`n" | Set-Content $OutputFile
Write-Host "Generated summary: $OutputFile"
