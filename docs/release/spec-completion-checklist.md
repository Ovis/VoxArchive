# 仕様完了条件チェックリスト

最終更新: 2026-03-13

## 判定ルール
- `PASS`: 実装と検証証跡あり
- `PENDING`: 実装済みだが実機証跡不足
- `TODO`: 未実装

## 完了条件（specification.md 25章）

1. WPF GUI から録音開始 / 停止ができる
- 状態: PASS
- 根拠:
  - `src/VoxArchive.Wpf/MainViewModel.cs`
  - `src/VoxArchive.Wpf/MainWindow.xaml`

2. スピーカーとマイクの 2 系統が FLAC 1 ファイルに保存される
- 状態: PENDING
- 根拠:
  - 実装経路あり（RecordingService -> FrameBuilder -> FfmpegFlacEncoder）
  - 実機E2E証跡が未添付

3. CH1=Speaker, CH2=Mic が保持される
- 状態: PENDING
- 根拠:
  - `FrameBuilder` で L=Speaker / R=Mic interleave 実装済み
  - 実機ファイルのチャンネル検証レポート未添付

4. 1時間以上の録音が実用上安定している
- 状態: PENDING
- 根拠:
  - 10秒長時間系テストは自動化済み
  - 実機1h/3h試験の結果未添付

5. ドリフト補正が実装されている
- 状態: PASS
- 根拠:
  - `PiDriftCorrector` / `LinearVariableRateResampler`
  - `RecordingService` で周期適用済み

6. 基本的なログが取得できる
- 状態: PASS
- 根拠:
  - `recording.log` / `recording-metrics.csv` / `recording-metrics.jsonl`

7. エラー時に UI へ通知される
- 状態: PASS
- 根拠:
  - `IRecordingService.ErrorOccurred` を ViewModel で表示

8. ファイル名が `yyyyMMddHHmmss.flac` で自動決定される
- 状態: PASS
- 根拠:
  - `RecordingService.BuildOutputFilePath`

## 追加仕様（Process Loopback）

- 出力音源モード選択 UI（スピーカー / アプリ指定）
  - 状態: PASS
- 対象プロセス不在時の開始確認ダイアログ
  - 状態: PASS
- 録音中の Process -> Speaker 自動フォールバック
  - 状態: PASS
- 自動復帰なし（一方向遷移）
  - 状態: PASS
- 実音声取得の実機証跡
  - 状態: PENDING

## リリース前に必須の残作業

1. 実機 1h 録音のレポート作成
2. 実機 3h 録音のレポート作成
3. CH1/CH2 分離の再生確認証跡添付
4. Process Loopback 実機でのフォールバック動作証跡添付

## 証跡保管先
- `logs/longrun/<session-id>/`
- `docs/testing/reports/`

## 自動検証補助
- 品質ゲート: `pwsh -File scripts/Run-QualityGate.ps1`
- 実機証跡サマリ: `pwsh -File scripts/release/Validate-SpecCompletionEvidence.ps1 -SessionId <session-id>`
