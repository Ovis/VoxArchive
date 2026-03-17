# VoxArchive 仕様書（実装同期版）

最終更新: 2026-03-17

## 1. 目的
VoxArchive は、PC の再生音（Speaker）とマイク音（Mic）を同時に録音し、後から確認・編集・文字起こしまで行える Windows 向けデスクトップアプリである。

## 2. 実行環境
- OS: Windows 10/11
- UI: WPF (.NET 10)
- 音声: NAudio + WASAPI + Process Loopback API（Windows 実装）
- 文字起こし: Whisper.net

## 3. アプリ全体構成
- VoxArchive.Wpf: UI、ライブラリ管理、文字起こし連携
- VoxArchive.Runtime: 実行時ブートストラップ、サービス配線
- VoxArchive.Application: 録音ユースケース
- VoxArchive.Audio: キャプチャ/バッファ/補正/エンコード
- VoxArchive.Infrastructure: 設定永続化（JSON）とデバイス列挙

起動時に Generic Host を構築し、DI で MainViewModel ほかを解決する。

## 4. メイン画面仕様
### 4.1 録音制御
- 停止中: REC ボタン表示
- 録音中: 一時停止 + 停止ボタン表示
- 一時停止中: 再開 + 停止ボタン表示
- 経過時間表示は hh:mm:ss

### 4.2 録音モード
- スピーカーモード: 既定の出力デバイス（または選択デバイス）をループバック録音
- プログラムモード: 選択したプロセスのループバック録音
  - 対象プロセス喪失時はスピーカーモードへフォールバック

### 4.3 デバイス選択
- Speaker/Mic はアイコン + ポップアップ一覧で選択
- 一覧には システム既定 を含む
- システム既定選択時は録音開始時に実デバイスへ解決

### 4.4 ミュートとレベル表示
- Speaker/Mic それぞれ録音参加 ON/OFF 可能
- ミュート時は斜線表示
- レベルは dB 値を表示用に変換し、アイコンとリングの塗りで可視化

### 4.5 ミニモード
- ミニモードと通常モードを切替可能
- ミニモードでは録音操作と経過時間を中心に表示
- モードに応じてウィンドウ幅は ViewModel 側で固定値制御

### 4.6 タスクトレイ
- 閉じる/最小化時はトレイ格納
- トレイメニュー:
  - メイン画面を表示
  - ライブラリを表示
  - 終了

### 4.7 グローバルホットキー
- 録音開始/停止ショートカットを登録
- Ctrl+F12 を既定値として設定画面で変更可能
- キー組み合わせ不正時は例外で落とさず UI 上で扱う

## 5. 録音パイプライン
- 出力ソース: Speaker Loopback / Process Loopback
- 入力ソース: Mic Capture
- バッファ: RingBuffer
- ドリフト補正: PI 制御
- エンコード: FLAC（ffmpeg）
- マイク遅延補正: -1000ms ～ +1000ms

## 6. ライブラリ画面仕様
### 6.1 一覧
- 録音ファイルを DataGrid で表示
- ダブルクリックで再生/一時停止
- チェック列により一括操作可能

### 6.2 再生
- Speaker/Mic 個別ゲイン調整（dB）
- ミックスしてモノラル再生オプション
- シーク:
  - シークバー任意位置クリック
  - ステップ選択（5秒,10秒,15秒,30秒,1分,5分,10分） + 前後ボタン
- 再生速度:
  - 0.5 / 0.8 / 1.0 / 1.2 / 1.5 / 2.0 / 2.5 / 3.0 / 4.0
  - 等倍へ戻すボタン

### 6.3 編集
- タイトル保存（FLAC タグ更新 + カタログ更新）
- ファイル名リネーム
- 一覧から削除（実ファイルは残す）
- ファイル削除（実ファイル + 一覧）
- 右クリックメニュー:
  - Explorerで表示（/select, で該当ファイル選択）
  - 文字起こし
  - 文字起こし結果を開く
  - 一覧から削除
  - ファイル削除

### 6.4 モノラル保存
- 現在の Speaker/Mic ゲインを反映してモノラル変換出力
- 出力形式: WAV / MP3 / FLAC
- 生成ファイルはライブラリ自動登録しない

## 7. 文字起こし（Whisper）仕様
### 7.1 実行方式
- 手動: ライブラリ画面から実行
- 自動: 録音停止後にキュー投入（設定 ON 時）
- 同一ファイルの重複投入は TranscriptionJobQueue が拒否

### 7.2 入出力
- 入力: 録音ファイルを一時 WAV へ変換して Whisper に渡す
- 出力拡張子: 	xt, srt, tt, json（複数可）
- 出力ファイル名: 録音ファイル名-モデル名.*

### 7.3 前処理
- 再生用ゲイン初期値を参照して話者バランス補正
- クリップ防止の安全スケーリング
- VAD ベースの無音区間抑制を導入し、hallucination/repetition を緩和

### 7.4 モデル管理
- 設定画面でモデル取得/削除
- 保存先: %LOCALAPPDATA%/VoxArchive/whisper/models
- 取得元: HuggingFace（ggerganov/whisper.cpp）

### 7.5 実行モード
- Auto
- CPU Only
- CUDA Preferred

環境チェックで runtime/model/cuda 可否を表示し、CUDA 非可用時は CPU にフォールバックする。

## 8. 設定画面仕様
主な設定項目:
- マイク遅延補正（ms）
- 既定 Speaker/Mic 再生ゲイン（dB）
- 録音開始/停止ショートカット
- 保存先フォルダ
- 文字起こし有効化
- 録音完了後に自動文字起こし
- 完了通知表示
- 実行モード
- モデル
- 言語（コンボボックス選択）
- 出力形式（複数チェック）
- 自動/手動実行時優先度
- 環境チェック / モデル取得 / モデル削除

## 9. データ永続化
### 9.1 設定
- ファイル: %LOCALAPPDATA%/VoxArchive/settings.json
- 形式: RecordingOptions JSON

### 9.2 ライブラリカタログ
- スナップショット: %LOCALAPPDATA%/VoxArchive/library.json
- バックアップ: %LOCALAPPDATA%/VoxArchive/library.json.bak
- 操作ログ: %LOCALAPPDATA%/VoxArchive/library.ops.jsonl

耐障害設計:
- 操作ログは append + Flush(true)
- スナップショット更新は 	mp -> File.Replace/Move で原子的更新
- スナップショット破損時は .bak へフォールバック
- 操作ログ破損行はスキップ継続
- しきい値到達で compact

## 10. ログ
- アプリ例外・警告: %LOCALAPPDATA%/VoxArchive/app-errors.log
- Whisper 実行ログ: %LOCALAPPDATA%/VoxArchive/whisper-transcription.log

## 11. テスト
- テストフレームワーク: NUnit
- テスト種別:
  - IntegrationTests
  - LongRunTests

## 12. 既知の制約
- Process Loopback は Windows API 依存であり、環境差で失敗時は Speaker Loopback へフォールバック
- Whisper CUDA 実行は runtime/native 依存が満たされない場合 CPU 実行になる
- アプリ実行中はビルド時に DLL ロックが起きる場合がある

## 13. 非対象
- クラウド同期
- 自動アップデート
- 複数PC間の設定共有
