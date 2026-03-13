# 実装計画（MVP）

## 1. ゴール定義（MVP）

MVP の完了条件は、以下を満たすこと。

1. WPF GUI から録音開始/停止ができる
2. Speaker + Mic を同時録音し、1つの FLAC に 2ch 分離保存できる
3. CH1=Speaker / CH2=Mic が正しく保持される
4. 1時間連続録音で破綻しない
5. ドリフト補正（PI + 可変レートリサンプラ）が動作する
6. 主要ログ（状態遷移、エラー、underflow/overflow）が取得できる
7. 追加仕様として Process Loopback を選択可能で、対象終了時に Speaker へフォールバックできる

---

## 2. 実装順（推奨）

## Phase 0: 土台

1. .NET 10 + WPF + MVVM のソリューション作成
2. レイヤ分割（Domain / Application / Audio / Encoding / Infrastructure / UI）
3. DI と Logging の初期設定
4. 設定ファイル（JSON）読み書きの実装

成果物:

- アプリ起動
- 設定ロード/保存
- ログ出力確認

## Phase 1: 録音コア（Speaker + Mic）

1. デバイス列挙サービス（Speaker/Mic）
2. RingBuffer（float）実装
3. Speaker Loopback キャプチャ実装
4. Mic キャプチャ実装
5. FrameBuilder（Speaker/Mic から stereo float 生成）
6. PCM16 変換

成果物:

- 生 PCM をメモリ上で継続生成
- レベル値・バッファ量取得

## Phase 2: FLAC 出力

1. ffmpeg 起動ラッパー（stdin/stderr/exit code 管理）
2. PCM16 stereo を ffmpeg stdin へ連続送信
3. 停止時 flush -> stdin close -> 終了待機
4. ファイル名 `yyyyMMddHHmmss.flac` 生成

成果物:

- Start/Stop で FLAC が生成され再生できる

## Phase 3: 同期安定化（ドリフト補正）

1. DriftCorrector（PI 制御）実装
2. VariableRateResampler（線形補間）実装
3. 100ms 周期制御で mic fill を target 近傍に維持
4. underflow/overflow カウントと復帰処理

成果物:

- 長時間録音時のズレ抑制
- 補正 ppm を統計として可視化可能

## Phase 4: UI 統合

1. MainWindow（通常モード）実装
2. Start/Stop/Pause/Resume 状態遷移実装
3. ボタン活性制御表の反映
4. レベルメーター・バッファ・drift 表示
5. 設定画面（保存先、デバイス、PI パラメータ、FLAC圧縮）

成果物:

- 仕様に沿った操作画面
- UI からフル録音操作可能

## Phase 5: Process Loopback（追加仕様）

1. `IOutputCaptureSource` 抽象化導入
2. `SpeakerLoopbackCaptureSource` / `ProcessLoopbackCaptureSource` 実装
3. 出力音源モード UI（スピーカー全体 / アプリ指定）
4. 録音開始時プロセス存在チェック
5. 録音中プロセス終了検知 -> Speaker へ自動フォールバック
6. 一方向遷移（Process -> Speaker のみ）保証

成果物:

- 追加仕様の主要ユースケース達成

## Phase 6: テストと安定化

1. 単体テスト（PI、Resampler、RingBuffer、状態遷移、ファイル名）
2. 結合テスト（同時録音、ffmpeg 異常系、デバイス喪失）
3. 長時間テスト（1h / 3h）
4. ログ粒度調整（高頻度抑制、1秒集計）

成果物:

- MVP 完了判定資料

---

## 3. モジュール単位の作業分解

## 3.1 Domain

対象:

- `RecordingOptions`
- `AudioDeviceInfo`
- `RecordingStatistics`
- `RecordingState`

タスク:

1. 値オブジェクト/enum 定義
2. デフォルト値設定（仕様表準拠）
3. バリデーション（サンプルレート、ppm 範囲、保存先）

完了条件:

- 他層が依存可能な型が固定化される

## 3.2 Application

対象:

- `IRecordingService`
- `IDeviceService`
- 各種 UseCase/Command 相当

タスク:

1. Start/Stop/Pause/Resume API 設計
2. 状態遷移ガード実装
3. 統計通知イベント/Observable 定義
4. UI 向けエラー通知の標準化

完了条件:

- UI は Application 層 API のみで録音制御できる

## 3.3 Audio.Capture

対象:

- Speaker loopback capture
- Mic capture
- Process loopback capture

タスク:

1. NAudio ベースで各キャプチャ実装
2. コールバック最小化（buffer push のみ）
3. デバイス喪失・例外時の通知
4. Process 終了監視とイベント化

完了条件:

- 各ソースが mono float をリングバッファへ供給できる

## 3.4 Audio.Buffering

対象:

- `IRingBuffer` 実装

タスク:

1. lock もしくは lock-free 方針決定
2. `Write` / `Read` / `ReadWithZeroPadding`
3. `Count` / `Capacity` / 統計
4. 長時間動作時の割り当て抑制

完了条件:

- 欠損時ゼロ埋めを含めて安定動作する

## 3.5 Audio.Sync

対象:

- `IDriftCorrector`
- `IVariableRateResampler`

タスク:

1. PI 制御計算
2. ppm 上限クリップ
3. 線形補間 resampling
4. 制御周期（100ms）実装

完了条件:

- mic fill が target 近傍に収束する

## 3.6 Audio.Frame

対象:

- `IFrameBuilder`

タスク:

1. speaker 1 frame 読み出し
2. mic を ratio 適用して同フレーム長に整形
3. L/R interleave
4. float -> s16le 変換

完了条件:

- ffmpeg に渡せる byte[] を安定生成できる

## 3.7 Encoding

対象:

- `IFfmpegFlacEncoder`

タスク:

1. ffmpeg プロセス起動
2. stdin 非同期書き込み
3. stderr 収集ログ化
4. 正常停止/異常終了処理

完了条件:

- エンコード成否を確実に判定できる

## 3.8 Failover

対象:

- `OutputCaptureController`
- `OutputCaptureFailoverCoordinator`

タスク:

1. 出力音源モード管理
2. 録音開始時プロセス有無チェック
3. 録音中プロセス終了時の切替実行
4. 切替通知（UI/ログ）

完了条件:

- Process -> Speaker の一方向フォールバックが保証される

## 3.9 UI (WPF + MVVM)

対象:

- MainWindow / SettingsWindow
- ViewModel 群

タスク:

1. 通常モードレイアウト実装
2. 状態別の有効/無効制御
3. レベル/バッファ/drift 表示
4. 出力音源モードとプロセス選択 UI
5. エラー表示・通知文言

完了条件:

- 仕様の主要ユースケースを GUI で操作できる

## 3.10 Infrastructure

対象:

- 設定永続化
- ログ出力先
- 時刻/ファイルシステム抽象

タスク:

1. `appsettings.json` とユーザー設定 JSON の統合
2. 既定保存先、最後のデバイス記憶
3. ファイルログ provider（必要時）

完了条件:

- 再起動後も設定が維持される

## 3.11 Test

対象:

- Unit / Integration

タスク:

1. PI/Resampler/RingBuffer 単体テスト
2. 状態遷移テスト
3. ffmpeg ラッパーの異常系テスト
4. 長時間テスト手順書作成

完了条件:

- 回帰確認可能な最小テスト基盤がある

---

## 4. 最短で価値を出す実装順（2スプリント想定）

## Sprint 1

1. Phase 0〜2 完了（録音して FLAC 生成まで）
2. 最低限 UI（開始/停止、保存先、デバイス選択）

## Sprint 2

1. Phase 3〜5 完了（同期補正 + Process Loopback + フォールバック）
2. 長時間試験とログ強化

---

## 5. 実装リスクと先回り対策

1. `ffmpeg` 不在/パス不整合
   - 起動前チェックと明確なエラーメッセージ
2. WASAPI デバイス差異
   - 起動時に詳細ログ（デバイスID/形式）を出力
3. Process Loopback の環境差
   - Windows 11 前提チェック、非対応時は UI で無効化
4. 長時間でのバッファ破綻
   - underflow/overflow の集計監視と警告閾値
5. 録音中切替による同期不安定
   - Process -> Speaker の一方向のみ許可

---

## 6. 直近の着手タスク（今日から開始）

1. ソリューションひな形作成（WPF + Class Library 分割）
2. Domain / Application の契約インターフェース先行定義
3. Speaker/Mic キャプチャ + ffmpeg 出力の最短パス実装
4. Start/Stop の E2E を先に通す
5. その後に drift 補正と Process Loopback を追加

