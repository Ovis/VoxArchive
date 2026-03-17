# Whisper 連携設計

最終更新: 2026-03-17

## 1. 目的
録音ファイルを手動/自動で文字起こしし、テキスト成果物を同一フォルダへ出力する。

## 2. コンポーネント
- WhisperModelStore
  - モデルの取得/削除/配置確認
- WhisperTranscriptionService
  - 環境チェック
  - 音声前処理
  - Whisper 実行
  - 形式別出力
- TranscriptionJobQueue
  - 非同期キュー処理
  - 重複投入抑止

## 3. 実行トリガー
- 手動: ライブラリの「文字起こし」
- 自動: 録音停止後（設定 ON 時）

## 4. 前処理
- 入力音声を一時 WAV 化
- Speaker/Mic バランス補正（設定ゲイン参照）
- クリップ防止スケーリング
- VAD による無音区間の抑制

## 5. 実行モード
- Auto
- CPU Only
- CUDA Preferred

CUDA Preferred でも runtime/driver 条件不足時は CPU フォールバック。

## 6. 出力
- 形式: txt/srt/vtt/json（複数可）
- ファイル名: 元録音名-モデル名.拡張子
- 出力先: 録音ファイルと同じフォルダ

## 7. 通知
- キュー投入時・完了時にトースト通知（設定 ON 時）

## 8. 失敗時方針
- 例外は TranscriptionJobResult に失敗として集約
- UI 側はボタン状態/ステータス更新を優先
- 実行ログは whisper-transcription.log へ出力
