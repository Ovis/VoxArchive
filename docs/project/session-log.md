# セッションログ

## 2026-03-13 Session-01

### 実施
1. 仕様書 `docs/specification.md` を読解し、実装要件を整理。
2. `docs/implementation-plan.md` を作成し、MVP実装順とモジュール分解を定義。
3. 契約先行方針で `src` 配下に以下の Abstractions/Domain を追加。
   - VoxArchive.Domain
   - VoxArchive.Application.Abstractions
   - VoxArchive.Audio.Abstractions
   - VoxArchive.Encoding.Abstractions
4. `.NET 10` のソリューションと `csproj` を作成し、参照設定を追加。
5. `dotnet build VoxArchive.sln -m:1` でビルド成功を確認。

### 成果
- 契約定義とビルド土台が完成。

### 既知事項
- この環境では `dotnet build` の並列実行時に不安定なケースがあるため、当面 `-m:1` を標準運用する。

### 次アクション
1. Application層の状態遷移ガード実装。
2. RecordingServiceスケルトン実装。
3. 最短E2E（Start/Stop）へ進む。

### 2026-03-13 Session-01 追記
- Application 層に RecordingService の最小実装を追加（状態遷移とイベント通知を実装）。
- backlog を更新し、状態遷移ガードとRecordingServiceスケルトンを完了扱いに変更。

## 2026-03-13 Session-02

### 実施
1. src/VoxArchive.Audio プロジェクトを追加し、ソリューションへ組み込み。
2. FloatRingBuffer を実装（固定容量、スレッドセーフ、ゼロ埋め読み出し対応）。
3. PiDriftCorrector を実装（Kp/Ki、ppm上限制限、ratio算出）。
4. LinearVariableRateResampler を実装（線形補間、ratio追従）。
5. dotnet build VoxArchive.sln -m:1 で成功確認。

### 次アクション
1. Speaker/Mic キャプチャ抽象実装の骨格追加。
2. FrameBuilder 最小実装追加。
3. ffmpeg エンコーダラッパー最小実装追加。

## 2026-03-13 Session-03

### 実施
1. src/VoxArchive.Encoding を追加し、FfmpegFlacEncoder 最小実装を作成。
2. src/VoxArchive.Audio に FrameBuilder を実装（L/Rインターリーブ + PCM16変換 + レベル算出）。
3. SpeakerCaptureService / MicCaptureService の骨格実装を追加。
4. backlog を更新し Next 項目を完了化。

## 2026-03-13 Session-04

### 実施
1. IProcessLoopbackCaptureService を追加し Audio 抽象を拡張。
2. IOutputCaptureController に SourceChanged イベントを追加。
3. Audio 実装として以下を追加。
   - ProcessLoopbackCaptureService（PID監視）
   - SpeakerLoopbackCaptureSource / ProcessLoopbackCaptureSource 
   - OutputCaptureController（自動フォールバック）
   - OutputCaptureFailoverCoordinator（開始時判定と切替制御）
4. dotnet build VoxArchive.sln -m:1 成功（警告なし）。


## 2026-03-13 Session-05

### 実施
1. VoxArchive.Application に Audio.Abstractions / Encoding.Abstractions 参照を追加。
2. RecordingService を実装接続版へ刷新。
   - IOutputCaptureController / IMicCaptureService / IRingBuffer / IDriftCorrector / IFrameBuilder / IFfmpegFlacEncoder を統合。
   - Start: モード解決 -> encoder開始 -> capture開始 -> 処理ループ開始。
   - Pause/Resume: capture停止/再開で制御。
   - Stop: 処理停止 -> capture停止 -> encoder停止。
   - 統計更新と OutputSourceChanged 伝搬を実装。
3. dotnet build VoxArchive.sln -m:1 成功を確認。


## 2026-03-13 Session-06

### 実施
1. VoxArchive.Infrastructure と VoxArchive.Runtime を追加。
2. JsonSettingsService を実装し、録音設定のJSON永続化を導入。
3. LocalRecordingBootstrapper を実装し、ローカル構成ルートを追加。
4. IDriftCorrector に Configure を追加し、RecordingService 開始時にパラメータ反映するよう修正。
5. FrameBuilder を可変フレームサイズ対応に変更。
6. dotnet build VoxArchive.sln -m:1 成功。

### 次アクション
1. WASAPIベースのデバイス列挙サービス実装。
2. NAudio実キャプチャへ置換。
3. WPFアプリプロジェクトを作成し Runtime を接続。

## 2026-03-13 Session-07

### 実施
1. VoxArchive.Wpf プロジェクトを追加してソリューションへ接続。
2. App 起動時に LocalRecordingBootstrapper を実行し Runtime 初期化する処理を追加。
3. MainViewModel / DelegateCommand を追加し、Start/Pause/Resume/Stop の最小操作を実装。
4. MainWindow.xaml を録音操作+状態表示の簡易UIへ更新。
5. dotnet build VoxArchive.sln -m:1 成功。

## 2026-03-13 Session-08

### 実施
1. WasapiDeviceService を追加し、WASAPI(COM)でSpeaker/Micデバイス列挙を実装。
2. 既定デバイス判定（Multimedia role）と FriendlyName 取得を実装。
3. RecordingRuntimeContext に IDeviceService を追加。
4. LocalRecordingBootstrapper に WasapiDeviceService を組み込み、設定未指定時は既定デバイスIDを適用。
5. dotnet build VoxArchive.sln -m:1 警告なしで成功。

## 2026-03-13 Session-09

### 実施
1. NaudioRuntimeSupport を追加し、NAudio有無で Capture 実装を切替。
2. NaudioSpeakerCaptureService / NaudioMicCaptureService を追加。
3. NaudioCaptureUtils を追加し、反射で WasapiLoopbackCapture / WasapiCapture を起動。
4. DataAvailable の PCM を mono float へ変換して CaptureChunk として通知。
5. NAudio未導入時は既存 SpeakerCaptureService / MicCaptureService へフォールバックする構成へ変更。
6. dotnet build VoxArchive.sln -m:1 成功。

## 2026-03-13 Session-10

### 実施
1. MainWindow に Speaker/Mic デバイス選択UIを追加。
2. 出力モード選択（SpeakerLoopback / ProcessLoopback）と PID 入力UIを追加。
3. MainViewModel に IDeviceService 連携を実装し、起動時デバイス列挙・既定選択を反映。
4. Start時にUI選択値を RecordingOptions に反映して保存・録音開始するよう更新。
5. dotnet build VoxArchive.sln -m:1 成功。

## 2026-03-13 Session-11

### 実施
1. UIをバー型に再構成し、Start/Stop と Pause/Resume を統合ボタン化。
2. Speaker/Mic レベルメーター（ProgressBar）を追加。
3. ミニモード（詳細領域の表示切替、ウィンドウサイズ縮小）を実装。
4. 録音中はデバイス選択・モード選択を無効化。
5. dotnet build VoxArchive.sln -m:1 成功。

## 2026-03-13 Session-12

### 実施
1. IRecordingTelemetrySink を Application Abstractions に追加。
2. FileRecordingTelemetrySink を追加し、状態・エラー・統計をローカルファイルに記録。
3. 統計ログは1秒間隔でサンプリング出力する実装にした。
4. RecordingService にテレメトリシンク連携を追加。
5. Runtime から ecording.log を注入するよう更新。
6. dotnet build VoxArchive.sln -m:1 成功。
