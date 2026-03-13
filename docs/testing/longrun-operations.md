# LongRun 運用

## セッション初期化

```powershell
pwsh -File scripts/longrun/Initialize-LongRunSession.ps1
```

実行後に `logs/longrun/session-YYYYMMDD-HHMMSS/` が生成される。

## 実施フロー

1. 生成された `README.md` の paths を確認
2. WPF アプリの保存先を `metrics` ディレクトリへ設定
3. 1h 録音を実施
4. 3h 録音を実施
5. CSV 集計

```powershell
pwsh -File scripts/Analyze-RecordingMetrics.ps1 -MetricsCsv <metrics/recording-metrics.csv>
```

6. `report/longrun-report.md` へ結果を転記
7. `docs/release/spec-completion-checklist.md` の PENDING 項目を PASS 化
