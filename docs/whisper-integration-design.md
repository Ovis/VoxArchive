# Whisper連携設計（Whisper.net）

最終更新日: 2026-03-15
対象: `VoxArchive`（ローカルPC内のみで完結）

## 1. 目的
録音済みファイル（FLAC）に対し、Whisperで文字起こしを実行できるようにする。

- 手動実行: ライブラリ画面から選択ファイルを文字起こし
- 自動実行: 録音完了時にバックグラウンドで文字起こし
- 出力: 音声ファイルと同フォルダに同名ベースで保存

例:
- `sample.flac` -> `sample.txt`
- `sample.flac` -> `sample.srt`
- `sample.flac` -> `sample.vtt`
- `sample.flac` -> `sample.json`

## 2. 採用技術と方針
### 2.1 採用
- Whisperエンジン: `Whisper.net`
- Runtime: CPU/CUDA切替対応
- モデル取得: 設定画面から取得（Hugging Face由来）

### 2.2 非採用（現時点）
- 外部Whisper CLI（`whisper.cpp`, Python whisper）の利用
- モデル保存先の設定項目
  - 保存先は固定（`%LocalAppData%\\VoxArchive\\whisper\\models`）
- 既存テキストの扱い設定
  - 常に上書き

## 3. UI設計
## 3.1 ライブラリ画面（右メニュー）
追加項目:
- `文字起こし`（最下段）

動作:
- 選択中の1ファイルを対象に実行
- 実行中はボタン非活性
- 完了時は通知（設定でON時のみトースト）
- 失敗時はモダンダイアログで表示

備考:
- 文字起こし結果をライブラリ内に全文表示する機能は初期リリース対象外
- 結果確認は出力ファイルを既定アプリで開く運用（将来プレビュー拡張可）

## 3.2 設定画面
追加項目:
1. `文字起こし機能を有効化`（ON/OFF）
2. `録音完了後に自動文字起こし`（ON/OFF）
3. `実行モード`（`自動` / `CPU固定` / `CUDA優先`）
4. `モデル`（`tiny` / `base` / `small` / `medium` / `large-v3`）
5. `言語`（`auto` / `ja` / `en` など）
6. `出力形式`（複数選択）
   - `txt`
   - `srt`
   - `vtt`
   - `json`
7. `自動実行時優先度`（`低` / `通常`）
8. `手動実行時優先度`（`低` / `通常`）
9. `完了通知（トースト）`（ON/OFF）
10. `環境チェック` ボタン
11. `モデル管理`（ダウンロード / 削除）

## 4. ドメイン/アプリ設計
## 4.1 新規抽象
- `ITranscriptionService`
  - `Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)`
- `ITranscriptionJobQueue`
  - `Enqueue(...)`
  - 単一ワーカー（同時実行数1）

## 4.2 主要DTO
- `TranscriptionRequest`
  - `AudioFilePath`
  - `Language`
  - `OutputFormats`（flags）
  - `Model`
  - `ExecutionMode`（Auto/CpuOnly/CudaPreferred）
  - `Priority`（Low/Normal）
  - `TriggeredBy`（Manual/AutoAfterRecord）
- `TranscriptionResult`
  - `Succeeded`
  - `GeneratedFiles`
  - `ErrorMessage`
  - `StartedAt/FinishedAt`

## 4.3 実行フロー
手動:
1. ライブラリで対象選択
2. `文字起こし` 押下
3. `ITranscriptionJobQueue.Enqueue(Manual)`
4. 完了通知/失敗通知

自動:
1. 録音停止イベント受信
2. 設定で自動実行ONなら `Enqueue(AutoAfterRecord)`
3. バックグラウンド実行（優先度低）

## 4.4 エラーハンドリング
- 対象ファイルなし: 既存の欠損処理方針に合わせる
- モデル未取得: 「設定画面でモデル取得が必要」を表示
- CUDA初期化失敗:
  - `CUDA優先` + フォールバック許可ならCPU再試行
  - 不可なら失敗として返却
- 書き込み失敗: 出力先権限/同名ファイルロックを表示

## 5. インフラ設計（Whisper.net）
## 5.1 パッケージ
- `Whisper.net`
- `Whisper.net.Runtime`（CPU）
- `Whisper.net.Runtime.Cuda`（CUDA）

## 5.2 ランタイム選択
- `自動`: CUDA利用可ならCUDA、不可ならCPU
- `CPU固定`: CPUのみ
- `CUDA優先`: CUDAを優先し失敗時CPUフォールバック

## 5.3 モデル管理
- 固定保存先: `%LocalAppData%\\VoxArchive\\whisper\\models`
- モデルダウンロード: 設定画面操作で実行
- 存在判定: モデルファイル有無をチェック

## 5.4 出力生成
- 既存ファイルは常に上書き
- 形式別出力:
  - txt: プレーンテキスト
  - srt/vtt: タイムスタンプ付き
  - json: セグメント情報

## 6. 永続化設計
`RecordingOptions` 拡張項目:
- `TranscriptionEnabled: bool`
- `AutoTranscriptionAfterRecord: bool`
- `TranscriptionExecutionMode: enum`
- `TranscriptionModel: enum/string`
- `TranscriptionLanguage: string`
- `TranscriptionOutputFormats: flags`
- `AutoTranscriptionPriority: enum`
- `ManualTranscriptionPriority: enum`
- `TranscriptionToastNotificationEnabled: bool`

注記:
- DBへの全文保存は初期リリース対象外
- 必要なら将来 `recording_transcriptions` テーブルを追加

## 7. 通知設計
- トースト通知は設定ON時のみ
- 通知タイミング:
  - 成功（生成ファイル名）
  - 失敗（要約エラー）

## 8. 実装タスク分解（作業漏れ防止）
## 8.1 フェーズA: 契約と設定
- [ ] `Domain/Application` に文字起こし用モデル・enum追加
- [ ] `RecordingOptions` 拡張
- [ ] `JsonSettingsService` の読込/保存更新
- [ ] 設定画面UIへ項目追加

## 8.2 フェーズB: Whisper基盤
- [ ] Whisper.net依存追加
- [ ] `WhisperTranscriptionService` 実装
- [ ] モデル存在判定/ダウンロード/削除実装
- [ ] 環境チェック実装（CPU/CUDA/モデル）

## 8.3 フェーズC: 実行導線
- [ ] `ITranscriptionJobQueue` と単一ワーカー実装
- [ ] ライブラリ右メニューへ `文字起こし` 追加
- [ ] 手動キュー投入を実装
- [ ] 録音停止時の自動キュー投入を実装

## 8.4 フェーズD: UXと運用
- [ ] 実行中状態表示（ボタン非活性）
- [ ] 成功/失敗ダイアログの整備
- [ ] トースト通知ON/OFF実装
- [ ] ログ出力（実行時間、失敗理由）追加

## 8.5 フェーズE: 検証
- [ ] build成功
- [ ] 手動文字起こしE2E（txt）
- [ ] 複数形式（srt/vtt/json）E2E
- [ ] 自動文字起こしE2E
- [ ] CUDA優先->CPUフォールバック検証

## 9. 受け入れ条件（Definition of Done）
1. ライブラリ画面から手動文字起こしを実行できる
2. 録音完了後の自動文字起こしが設定で動作する
3. 出力形式を複数選択でき、同時に出力される
4. 出力は同フォルダ同名ベースで上書きされる
5. CPU/CUDA切替が設定で機能する
6. 実行失敗時に理由をユーザーに提示する
7. ローカル環境で build が通る

## 10. テスト観点
- 正常系:
  - 単一形式/複数形式の出力
  - 手動/自動導線
- 異常系:
  - 音声ファイル欠損
  - モデル未取得
  - CUDA初期化失敗
  - 出力ファイル書込不可
- 回帰:
  - 録音機能・ライブラリ機能の既存操作に影響しない

## 11. セッション引き継ぎチェック（コンパクション対策）
次セッション開始時に必ず確認:
1. 本ファイルの `8. 実装タスク分解` の未完了項目
2. `docs/project/backlog.md` の Whisper関連項目
3. `docs/project/session-log.md` の最新エントリ
4. 現在ブランチが `zissou` であること
5. ローカル作業のみであること（外部影響なし）

進捗更新ルール:
- フェーズ単位で `session-log` に記録
- タスク完了ごとに `backlog` のチェック更新
- 各フェーズで1コミット以上（日本語タイトル）
