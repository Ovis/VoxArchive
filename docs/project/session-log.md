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

## 2026-03-13 Session-13

### 実施
1. 	ests/VoxArchive.IntegrationTests を追加。
2. RecordingServiceIntegrationTests を作成し、以下4ケースを実装。
   - Start/Stop の状態遷移と出力パス生成
   - Pause/Resume の状態遷移
   - OutputSourceChanged イベント伝搬
   - Process mode + PID未指定時の例外
3. フェイク実装で Audio/Encoding 依存を切り離し、ローカルで安定実行可能にした。
4. dotnet test tests/VoxArchive.IntegrationTests/VoxArchive.IntegrationTests.csproj -m:1 で 4/4 成功。

## 2026-03-13 Session-14

### 実施
1. 	ests/VoxArchive.LongRunTests を追加し、10秒連続動作の長時間系テストを実装。
2. scripts/Run-QualityGate.ps1 を追加し、build + integration + long-run test を一括実行可能にした。
3. docs/testing/local-e2e-checklist.md を追加し、実機E2E手順を明文化。
4. dotnet build、dotnet test（Integration/LongRun）を実行し全通過を確認。

## 2026-03-13 Session-15

### 実施
1. CompositeRecordingTelemetrySink を追加し、複数シンク同時出力を実装。
2. CsvRecordingTelemetrySink と JsonlRecordingTelemetrySink を追加。
3. Runtime のテレメトリ配線を更新し、
   - recording.log
   - recording-metrics.csv
   - recording-metrics.jsonl
   を同時生成するように変更。
4. E2E手順書を更新して上記ファイル確認を明記。
5. build + integration test + long-run test 全通過を確認。

## 2026-03-13 Session-16

### 実施
1. docs/testing/reports/longrun-report-template.md を追加。
2. scripts/Analyze-RecordingMetrics.ps1 を追加し、CSVから集計Markdownを自動生成。
3. docs/testing/local-e2e-checklist.md に集計手順を追記。
4. サンプルCSVでスクリプト実行し、summary生成を確認。

## 2026-03-13 Session-17

### 実施
1. ProcessCatalogService を追加して実行中プロセス一覧取得を実装。
2. RecordingRuntimeContext に IProcessCatalogService を追加し、Runtime で注入。
3. WPFにプロセス選択コンボボックスを追加し、ProcessLoopback時に選択PIDを使用するよう更新。
4. プロセス一覧の手動更新コマンドを追加。
5. build + integration test + long-run test 全通過を確認。

## 2026-03-13 Session-18

### 実施
1. OutputCaptureController を強化し、Process開始失敗時に Speaker へ自動フォールバックするよう更新。
2. ProcessLoopbackCaptureService を拡張し、NAudio の WasapiProcessLoopbackCapture が存在する場合に反射で取り込み開始する実装を追加。
3. WPF開始時に対象プロセス不在なら、
   「スピーカー録音に切り替えて開始しますか？」確認ダイアログを表示。
4. Runtime/Process選択UI連携を維持したまま build + test（Integration/LongRun）全通過を確認。
## 2026-03-13 Session-19

### 実施
1. `docs/release/spec-completion-checklist.md` の判定ルールと完了条件を整理し、PASS/PENDINGを明示。
2. `scripts/longrun/Initialize-LongRunSession.ps1` を追加し、長時間検証セッションのログ配置を自動初期化できるようにした。
3. 権限制約環境でも停止しないよう、CIM取得をフォールバック付きに修正。
4. `docs/testing/longrun-operations.md` を追加し、1h/3h検証の運用手順を固定化。
5. `pwsh -File scripts/longrun/Initialize-LongRunSession.ps1 -SessionId dryrun-test` の実行成功を確認。

### 次アクション
1. 実機 1h 録音の証跡取得（CH1/CH2確認含む）。
2. 実機 3h 録音の証跡取得。
3. `spec-completion-checklist.md` のPENDING項目をPASSへ更新。


## 2026-03-13 Session-20

### 実施
1. `scripts/Run-QualityGate.ps1` を改善し、`DOTNET_CLI_HOME` をリポジトリ内に固定して権限制約環境でも安定実行できるようにした。
2. `Run-QualityGate.ps1` を実行し、build + integration test + long-run test が全通過することを確認。
3. `scripts/release/Validate-SpecCompletionEvidence.ps1` を追加し、長時間検証セッション内の証跡ファイル有無から完了見込みを自動集計できるようにした。
4. `docs/testing/longrun-operations.md` を更新し、証跡ファイル作成と自動判定手順を追加した。
5. `.gitignore` を調整し、`docs/release` と `scripts/release` を追跡対象として固定した。

### 次アクション
1. 実機 1h/3h 録音を実施し、`report/evidence-*.md` を埋める。
2. `Validate-SpecCompletionEvidence.ps1` でサマリ生成し、`spec-completion-checklist.md` の PENDING を PASS 化する。

## 2026-03-13 Session-21

### 実施
1. `Initialize-LongRunSession.ps1` を拡張し、`evidence-*.md` テンプレート（status: PENDING）を自動生成するようにした。
2. `Validate-SpecCompletionEvidence.ps1` を拡張し、証跡ファイルの存在だけでなく `status: PASS` を満たした場合のみ完了扱いにする判定へ強化した。
3. `docs/testing/longrun-operations.md` を更新し、証跡ファイルの status 更新手順を明記した。
4. `dryrun-evidence` セッションを生成し、判定サマリが期待どおり `PENDING/MISSING` を出力することを確認した。

### 次アクション
1. 実機セッションで `evidence-*.md` を PASS 化し、完了条件の残項目を解消する。
2. 取得済み証跡を `docs/release/spec-completion-checklist.md` に反映する。

## 2026-03-13 Session-22

### 実施
1. `RecordingOptions` に `ChannelAlignmentMilliseconds` を追加。
2. `MainWindow` に `Offset(ms)` 入力欄を追加し、停止中のみ編集できるようにした。
3. `MainViewModel` でオフセット値を整数パースして `RecordingOptions` に保存するよう実装（-500〜500msにクランプ）。
4. `RecordingService` に固定オフセット補正を追加。
   - 正値: Speaker を遅延
   - 負値: Mic を遅延
5. 既存のサンプルレート正規化＋遅延抑制と合わせ、実運用での同期調整を可能にした。
6. WPF を別出力パスでビルドし、コンパイル成功を確認。

### 次アクション
1. 実機でオフセット値を段階調整し、最適値をプロファイル化する。
2. 将来的にデバイス別プリセット化（自動適用）を検討する。

## 2026-03-15 Session-UI-Library-01

### 実施
1. ライブラリ機能の設計書 `docs/library-design.md` を追加し、要件・DB・再生仕様を固定。
2. WPF 側にライブラリ基盤を追加。
   - `RecordingCatalogService`（SQLite同期、タイトル更新、リネーム、削除、除外）
   - `RecordingPlaybackService`（再生/停止/シーク）
   - `StereoGainSampleProvider`（Speaker/Mic 個別dBゲイン）
3. `LibraryWindow` / `LibraryViewModel` を追加し、一覧表示と編集操作を実装。
4. メイン画面にライブラリ起動ボタンを追加し、`MainViewModel` から起動連携。
5. ショートカット設定入力の安定化（無効組み合わせ時の例外終了防止、単体キー対応）。

### 成果
- 録音ファイル一覧、再生、タイトル編集、リネーム、削除、一覧除外の主要フローをローカル実装。
- チャンネル別ゲイン（dB）調整を再生系に実装。

### 既知事項
- 現在の実行環境では `dotnet build` が Restore フェーズで 0 error 表示のまま失敗する既知事象があり、CLI上で最終ビルド成否の自動判定が不安定。

### 次アクション
1. ライブラリ画面の実機動作確認（FLAC再生可否、編集系操作）。
2. 必要に応じて FLAC再生互換性のフォールバック（MediaFoundationReader等）を追加。
3. 検索/並び替え/ページングなど運用機能を段階追加。

## 2026-03-15 Session-Whisper-Design

### 実施
1. Whisper連携方針を `Whisper.net` ベースで確定。
2. `docs/whisper-integration-design.md` を新規追加し、以下を定義。
   - UI設計（ライブラリ手動実行 / 録音後自動実行 / 設定項目）
   - 実装方針（抽象、サービス、ジョブキュー、ランタイム切替）
   - エラー処理、受け入れ条件、テスト観点
   - コンパクション対策用の引き継ぎチェック
3. `docs/project/backlog.md` に Whisper関連の未完了タスクを追加。

### 次アクション
1. `RecordingOptions` 拡張と設定画面項目の実装。
2. Whisper.net 実装（モデル管理/環境チェック/実行サービス）。
3. ライブラリの `文字起こし` ボタンと録音後自動実行の導線接続。

## 2026-03-15 Session-Whisper-Impl-01

### 実施
1. Domain を拡張し、文字起こし設定を `RecordingOptions` へ追加。
   - `TranscriptionEnabled`
   - `AutoTranscriptionAfterRecord`
   - `TranscriptionExecutionMode`
   - `TranscriptionModel`
   - `TranscriptionLanguage`
   - `TranscriptionOutputFormats`
   - `AutoTranscriptionPriority`
   - `ManualTranscriptionPriority`
   - `TranscriptionToastNotificationEnabled`
2. WPF に文字起こし基盤を追加。
   - `WhisperModelStore`（モデル配置/取得/削除）
   - `WhisperTranscriptionService`（Whisper.net をリフレクション経由で実行）
   - `TranscriptionJobQueue`（単一ワーカー）
3. MainViewModel に自動文字起こし導線を追加。
   - 録音停止後のライブラリ登録完了時に自動キュー投入
   - ジョブ完了時の通知/エラー表示
4. LibraryViewModel / LibraryWindow に手動文字起こし導線を追加。
   - 右メニューと右クリックメニューに `文字起こし` を追加
   - 実行中の非活性制御、完了ステータス更新
5. SettingsWindow を拡張し、文字起こし設定とモデル管理UIを追加。
   - 実行モード、モデル、言語、出力形式（複数）
   - 自動/手動優先度、通知有無
   - 環境チェック、モデル取得、モデル削除
6. 通知ハブ `AppNotificationHub` を追加し、タスクトレイバルーン通知を実装。
7. `dotnet build src/VoxArchive.Wpf/VoxArchive.Wpf.csproj --no-restore` でビルド成功を確認。

### 既知事項
- Whisper.net パッケージをプロジェクト参照として追加していないため、実行環境に `Whisper.net` が存在しない場合は環境チェックで未検出となる。
- 依存が存在する環境では、リフレクション経由で実行を試みる。

### 次アクション
1. Whisper.net 実体（NuGet runtime 含む）を導入した実機で E2E 検証。
2. CUDA優先時の実行経路とフォールバック挙動を実測確認。

## 2026-03-16 Session-Whisper-Debug-AsyncEnum

### 実施
1. 文字起こし失敗（`IAsyncEnumerable の列挙取得に失敗しました。`）に対して、`WhisperTranscriptionService` の反射列挙処理を互換強化。
   - `IAsyncEnumerable<T>` インターフェイス経由の列挙取得へ変更
   - `Task` / `ValueTask` 戻り値のアンラップ処理を追加
   - 同期 `IEnumerable` 戻り値のフォールバック読み取りを追加
2. 調査用ログ出力を追加。
   - ログファイル: `%LocalAppData%\\VoxArchive\\logs\\whisper-transcription.log`
   - 開始/成功/例外、戻り値型などを追記
3. 失敗メッセージにログファイルパスを含め、ユーザーが即座に追跡可能な状態へ改善。

### 次アクション
1. 実機で文字起こしを再実行し、同ログに記録される戻り値型と例外詳細を確認。
2. もし失敗継続なら、ログを基に `ProcessAsync` シグネチャ差分へ追加対応。
- 追記: 文字起こし入力が FLAC のままだと `Whisper.net.Wave.CorruptedWaveException (Invalid wave file RIFF header)` で失敗することを確認。
- 対応: `WhisperTranscriptionService` で入力を自動WAV化（必要時: FLAC→WAV/16kHz）してから Whisper へ渡す処理を追加。
