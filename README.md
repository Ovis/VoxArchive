# VoxArchive

スピーカー出力（またはプロセス指定録音）とマイク入力を同時録音し、FLAC 形式で保存する Windows 向けデスクトップアプリです。  
会議・通話などの音声を記録し、Whisper による自動文字起こしも行えます。  
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
- 文字起こし（Whisper.net）
  - 手動実行 / 録音後自動実行
  - 複数出力形式（txt / srt / vtt / json）
  - CPU / CUDA 優先モード

## 動作環境

| 項目 | 要件 |
|---|---|
| OS | Windows 11 |
| ランタイム | .NET 10 Desktop Runtime |
| ffmpeg | 別途インストール必須（後述） |
| CUDA（省略可） | CUDA Toolkit 13（文字起こしの GPU アクセラレーション用） |

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

設定画面で以下を設定できます。

- 有効/無効
- 実行モード（自動 / CPU固定 / CUDA優先）
- モデル
- 言語
- 出力形式
- 優先度（自動実行時 / 手動実行時）
- 完了通知

ライブラリ画面から手動で文字起こしを実行できます。  
GPU（CUDA）を使用する場合は、NVIDIA ドライバおよび CUDA Toolkit 13.x がインストールされている必要があります。

## 開発

### 前提

- .NET 10 SDK

### ビルド

```powershell
dotnet build VoxArchive.slnx -c Release
```

実行ファイルは `src/VoxArchive.Wpf/bin/Release/net10.0-windows/` に生成されます。

## 名前の由来

**Vox**（ヴォックス）はラテン語で「声・音」を意味する単語で、**Archive**（アーカイブ）は記録・保存を表します。

名前にはもうひとつの意味が込められています。
スピーカー出力とマイク入力で拾った **Voice**（声）を、ひとつのファイルに別々のチャンネルとして収める **Box**（箱）に見立て、それを **Archive** するという設計思想を充てたものです。

## ライセンス

VoxArchive は [MIT License](LICENSE) の下で公開されています。

本ソフトウェアは以下のサードパーティライブラリを使用しています。
