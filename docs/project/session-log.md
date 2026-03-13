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

