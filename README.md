# VoxArchive

スピーカー出力（またはプロセス指定録音）とマイク入力を同時録音し、FLAC 形式で保存する Windows 向けデスクトップアプリです。  
会議・通話などの音声を記録し、Whisper または ReazonSpeech による文字起こしも行えます。  
スピーカー出力とマイク入力をそれぞれ別チャンネルで格納するため、録音後にゲインを調整しながら再生が可能です。

## 主な機能

- 同時録音（出力 + マイク、2ch FLAC）
- 出力ソース切替
  - スピーカーモード（WASAPI ループバック）
  - プログラムモード（Process Loopback）
- 録音の開始 / 一時停止 / 再開 / 停止
- グローバルホットキー（録音開始/停止）
- ミニモード切替
- タスクトレイ常駐（閉じるでトレイ格納、右クリックで復帰/終了）
- ライブラリ機能
  - 一覧表示、再生、シーク、再生速度変更
  - タイトル編集、ファイル名変更
  - 一覧から削除 / ファイル削除 / Explorer で表示
  - モノラル保存（WAV / MP3 / FLAC）
- 文字起こし
  - Whisper.net / ReazonSpeech（sherpa-onnx）
  - 手動実行 / 録音後自動実行
  - モデル管理
  - 複数出力形式（txt / srt / vtt / canonical json）
  - 話者ラベル付与（録音時のCH1/CH2を利用）

## 動作環境

| 項目 | 要件 |
|---|---|
| OS | Windows 11 |
| ランタイム | .NET 10 Desktop Runtime |
| ffmpeg | 別途インストール必須（後述） |
| NVIDIA GPU（省略可） | WhisperのGPUアクセラレーションを利用する場合に使用 |

## 配布物

リリースでは以下を提供します。

- ZIP（自己完結）: `VoxArchive-<version>-win-x64.zip`
- ZIP（ランタイム非同梱）: `VoxArchive-<version>-win-x64-fd.zip`
- インストーラー（自己完結）: `VoxArchive-setup-<version>-sc.exe`
- インストーラー（ランタイム非同梱）: `VoxArchive-setup-<version>-fd.exe`

ランタイム非同梱版（`-fd`）は .NET Desktop Runtime が別途必要です。

## セットアップ

### 1. ffmpeg のインストール

VoxArchive は FLAC エンコードに ffmpeg を使用します。winget でインストールしてください。

```powershell
winget install Gyan.FFmpeg
ffmpeg -version
```

### 2. アプリ起動

- ZIP 版: 展開後に `VoxArchive.Wpf.exe` を実行
- インストーラー版: セットアップ実行後、インストール先の `VoxArchive.Wpf.exe` を実行

## 基本的な使い方

1. 出力デバイス（または対象プロセス）とマイクを選択
2. REC で録音開始
3. 必要に応じて一時停止 / 再開
4. STOP で録音停止
5. ライブラリで録音を確認・再生・編集

## 文字起こし

設定画面では、利用する文字起こしEngine、モデル、言語、実行方式、出力形式、優先度、通知、診断ログなどを設定できます。Engine固有設定はEngineごとのsettingsとして保存され、共通設定とは分離されています。

現在組み込まれているEngineは以下です。

- **Whisper** — Whisper.netを利用。複数モデルと言語指定、CPU/GPU実行に対応
- **ReazonSpeech** — sherpa-onnxを利用した日本語向け文字起こし。現在はCPU実行

ライブラリ画面から手動で文字起こし・再文字起こしを実行できます。録音完了後の自動文字起こしも設定できます。

文字起こし結果ではJSONを正本（canonical document）として保存し、TXT/SRT/VTTはその正本から生成します。既存ファイルとの互換性のため、Whisperは `録音名-small.json` のような従来のmodel suffix、ReazonSpeechは `録音名-reazonspeech-ja.json` の命名規則を維持します。

## 開発

### 前提

- .NET 10 SDK

### ビルド

```powershell
dotnet build VoxArchive.slnx -c Release
```

実行ファイルは `src/VoxArchive.Wpf/bin/Release/net10.0-windows/` に生成されます。

### 文字起こし基盤の構成

文字起こし処理はUIからEngine実装を分離し、次の責務境界で構成しています。

```text
VoxArchive.Wpf
    ↓
VoxArchive.Application.Abstractions
    ↓
VoxArchive.Application
    ↓
VoxArchive.Transcription
    ↓
VoxArchive.Transcription.Abstractions

Runtime Composition Root
    ├─ VoxArchive.Transcription.Whisper
    └─ VoxArchive.Transcription.ReazonSpeech
```

- `VoxArchive.Wpf` はApplicationのUse Caseだけを呼び出し、Whisper.netやsherpa-onnx、具体Engine型を参照しません。
- `VoxArchive.Application` はEngine選択、Admission、モデル準備、immutable job snapshot、Queueを担当します。
- `VoxArchive.Transcription` は音声準備、VAD、結果検証、話者ラベル、canonical JSON、TXT/SRT/VTT生成、モデル管理などEngine非依存処理を担当します。
- `VoxArchive.Transcription.Abstractions` はEngine、settings、model、language、diagnostics、artifact namingなどの契約だけを定義します。
- `VoxArchive.Transcription.Whisper` と `VoxArchive.Transcription.ReazonSpeech` は認識処理とEngine固有capabilityだけを保持します。
- `VoxArchive.Runtime` がComposition RootとしてEngine登録と依存関係の組み立てを行います。

Engineを追加する場合、CommonやWPFへEngine名を追加するのではなく、Abstractionsのcapabilityを実装してRuntimeへ登録する形を基本とします。

## 名前の由来

**Vox**（ヴォックス）はラテン語で「声・音」を意味する単語で、**Archive**（アーカイブ）は記録・保存を表します。

名前にはもうひとつの意味が込められています。
スピーカー出力とマイク入力で拾った **Voice**（声）を、ひとつのファイルに別々のチャンネルとして収める **Box**（箱）に見立て、それを **Archive** するという設計思想を充てたものです。

## ライセンス

VoxArchive は [MIT License](LICENSE) の下で公開されています。

本ソフトウェアは以下のサードパーティライブラリを使用しています。
