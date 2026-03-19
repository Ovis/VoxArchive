# VoxArchive

スピーカー出力（またはプロセス指定録音）とマイク入力を同時録音し、FLAC 形式で保存する Windows 向けデスクトップアプリです。
会議・通話などの音声を記録し、Whisper による自動文字起こしも行えます。
スピーカー出力とマイク入力をそれぞれ別チャンネルで格納するため、録音後にゲインを調整しながら再生が可能です。

## 機能

- **同時録音** — スピーカー出力（WASAPI ループバック）とマイク入力を 2ch FLAC で保存
- **プロセス指定録音** — 特定のアプリの音声のみをキャプチャ（Process Loopback）
- **一時停止・再開** — 録音を中断せずに一時停止し、同一ファイルへ続きを追記
- **グローバルホットキー** — 録音開始・停止をキーボードショートカットで操作
- **音声文字起こし** — Whisper.net（CPU / CUDA 対応）による自動または手動の文字起こし
- **ライブラリ管理** — 録音ファイルの一覧表示・再生・タイトル編集・モノラル変換エクスポート
- **ミニモード** — コンパクト表示で画面を占有せずに録音状態を確認

## 動作環境

| 項目 | 要件 |
|---|---|
| OS | Windows 11 |
| ランタイム | .NET 10 Desktop Runtime |
| ffmpeg | 別途インストール必須（後述） |
| CUDA（省略可） | CUDA Toolkit 13（文字起こしの GPU アクセラレーション用） |

## セットアップ

### 1. ffmpeg のインストール

VoxArchive は FLAC エンコードに ffmpeg を使用します。winget でインストールしてください。

```powershell
winget install Gyan.FFmpeg
```

インストール後、`ffmpeg` コマンドがパスに通っていることを確認してください。

```powershell
ffmpeg -version
```

### 2. VoxArchive のインストール

[Releases](../../releases) から最新の `VoxArchive-vX.X.X.zip` をダウンロードし、任意のフォルダに展開して `VoxArchive.exe` を実行してください。

### 3. 文字起こし機能の有効化（省略可）

1. アプリ内の設定画面を開く
2. 「文字起こし」タブで使用するモデルを選択
3. 「モデルを取得」ボタンをクリックしてダウンロード

GPU（CUDA）を使用する場合は、NVIDIA ドライバおよび CUDA Toolkit 13.x がインストールされている必要があります。

## 使い方

### 基本的な録音

1. アプリを起動し、スピーカーデバイスとマイクデバイスを選択
2. **REC** ボタンをクリック（またはホットキー）で録音開始
3. **STOP** ボタンで録音停止
4. 録音ファイルはデフォルトで `ドキュメント\VoxArchive` に保存されます

### 出力ソースの切替

- **スピーカーループバック** — PC 全体の出力音声を録音
- **プロセス指定** — プロセス一覧から対象アプリを選択して録音

### ライブラリ

メニューの「ライブラリ」から録音一覧を開けます。

- 録音の再生・スピーカー/マイクの個別ゲイン調整
- タイトル編集（FLAC タグに反映）
- モノラル変換して WAV/MP3/FLAC でエクスポート
- 文字起こしの手動実行・結果ファイルの確認

## ビルド方法

### 前提条件

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio 2022 以降 または Visual Studio Code

### 手順

```bash
git clone https://github.com/Ovis/VoxArchive.git
cd VoxArchive
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

| ライブラリ | ライセンス |
|---|---|
| [NAudio](https://github.com/naudio/NAudio) | [MIT](LICENSES/NAudio-MIT.txt) |
| [TagLibSharp](https://github.com/mono/taglib-sharp) | [LGPL-2.1](LICENSES/TagLibSharp-LGPL-2.1.txt) |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | [MIT](LICENSES/Whisper.net-MIT.txt) |
| [whisper.cpp](https://github.com/ggerganov/whisper.cpp) | [MIT](LICENSES/whisper.cpp-MIT.txt) |
| [ZLogger](https://github.com/Cysharp/ZLogger) | [MIT](LICENSES/ZLogger-MIT.txt) |
| Microsoft.Extensions.* | [MIT](LICENSES/dotnet-MIT.txt) |

**外部依存：** ffmpeg は本ソフトウェアに同梱されていません。別途インストールしてください。ffmpeg のライセンスは [ffmpeg.org/legal.html](https://ffmpeg.org/legal.html) を参照してください。
