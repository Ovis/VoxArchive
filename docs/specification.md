# 会議録音アプリ 完全仕様書

## 1. 文書の目的

本書は、Windows 上で動作する会議録音アプリケーションの実装仕様を定義するためのものである。実装対象は以下を満たすデスクトップアプリとする。

- Windows 専用アプリケーション
- GUI は WPF を使用する
- マイク入力とスピーカー出力を同時録音する
- 1 つの FLAC ファイルに 2ch 分離で保存する
- 長時間録音でもドリフト破綻しないことを重視する
- 後続の文字起こし・再生・音量調整に適した構造を採用する

本書は Codex 等のコード生成支援に渡せる粒度で、要求仕様、非機能要件、アーキテクチャ、モジュール責務、状態遷移、アルゴリズム、GUI 要件、エラー処理、ログ要件、実装上の注意点を網羅する。

---

## 2. システム概要

本アプリは、ローカル PC 上で行われる会議や通話について、以下 2 系統の音声を同時に取得して保存する。

- スピーカー出力音声
- マイク入力音声

保存時は混合せず、1 つの音声ファイル内に左右チャンネルとして分離保持する。

- CH1 / Left: スピーカー出力
- CH2 / Right: マイク入力

通常の音楽プレイヤー等で再生すると左右で別々に聞こえるが、専用再生機能では 2ch をモノラル合成して左右同一出力で再生できるようにすることを前提とする。

本仕様書では、まず録音機能を主対象とし、専用再生機能は将来拡張または別プログラムとして扱う。

---

## 3. 主要要求

### 3.1 機能要求

1. マイク入力を録音できること
2. スピーカー出力を WASAPI Loopback で録音できること
3. マイクとスピーカーを同時に録音できること
4. 出力は 1 ファイルであること
5. 出力ファイル内でマイクとスピーカーは混合せず分離保持すること
6. 出力フォーマットは FLAC とすること
7. 録音開始時にファイル名を自動決定すること
8. 長時間録音中にクロック差によるドリフトを補正すること
9. 録音状態、レベル、バッファ状態、ドリフト補正量を GUI で確認できること
10. 録音開始、停止、一時停止、再開が行えること
11. 録音中のエラーを GUI とログで確認できること
12. ログをファイルおよび標準ログ基盤に出力できること

### 3.2 非機能要求

1. Windows 専用であること
2. GUI は WPF であること
3. 録音処理は UI スレッドから分離されていること
4. 長時間録音でメモリリークが発生しないこと
5. 録音停止時にファイル破損をできるだけ防ぐこと
6. エラー時の状態遷移が明確であること

---

## 4. 技術選定
以下のうち対象プラットフォームは確定。その他については要件に対してより良いものがあればそちらを採用する。

### 4.1 対象プラットフォーム

- OS: Windows
- ランタイム: .NET（現行 LTS である .NET10 またはそれに準ずる安定版）
- GUI: WPF

### 4.2 音声取得

- WASAPI
- Speaker: Loopback Capture
- Mic: Capture

音声取得ライブラリは以下のいずれかを想定する。

- NAudio
- CSCore
- それ以外の Windows 音声 API ラッパー

ただし、実装の簡潔さと保守性を優先し、最初の実装では NAudio ベースを第一候補とする。

### 4.3 エンコード

- ffmpeg を外部プロセスとして起動し、stdin pipe 経由で PCM を渡す
- エンコード先は FLAC

### 4.4 ログ

- Microsoft.Extensions.Logging を使用する
  - ZLoggerの利用も検討
- 追加のロギング基盤は必須としない
- ファイル出力が必要な場合は Microsoft.Extensions.Logging を前提にした provider 構成で対応すること

### 4.5 アーキテクチャ

- WPF + MVVM
- UI 非依存の録音エンジン
- サービス層とドメイン層を分離

---

## 5. 出力ファイル仕様

### 5.1 フォーマット

- 形式: FLAC
- サンプルレート: 48000 Hz
- ビット深度: 16bit
- チャンネル数: 2

### 5.2 チャンネル定義

- CH1 / Left: スピーカー出力
- CH2 / Right: マイク入力

### 5.3 保存方針

- 録音中にリアルタイムで FLAC 圧縮する
- 一時ファイルは必須ではない
- 正常停止時は ffmpeg に標準入力終了を通知し、終了コードを確認する

### 5.4 ファイル名ルール

録音ボタン押下時刻をもとに、以下の形式で自動生成する。

`yyyyMMddHHmmss.flac`

例:

`20260313004512.flac`

補足:

- `HH` は 24 時間表記とする
- ファイル名は録音開始処理を開始したタイミングで確定する
- 保存先ディレクトリはユーザーが指定可能とする
- 既定保存先は設定画面で変更可能とする

---

## 6. ユースケース

### 6.1 録音開始

1. ユーザーがスピーカーデバイス、マイクデバイス、保存先を選択する
  - 選択情報は設定情報として保持し、毎度指定せずともつける状態とする
2. ユーザーが録音開始ボタンを押す
3. アプリは録音開始時刻からファイル名を生成する
4. アプリは ffmpeg を起動する
5. スピーカーおよびマイクのキャプチャを開始する
6. 処理スレッドが 2 系統の音声を同期しながら 2ch PCM を生成する
7. ffmpeg に PCM を渡し、FLAC として保存する
8. GUI は録音中状態へ遷移する

### 6.2 録音停止

1. ユーザーが停止ボタンを押す
2. キャプチャ受付を停止する
3. バッファ内の未処理データを可能な範囲で flush する
4. ffmpeg の stdin を閉じる
5. ffmpeg プロセスの終了を待つ
6. 正常終了なら録音停止状態へ遷移する
7. 異常終了ならエラー状態を記録する

### 6.3 一時停止

1. ユーザーが一時停止ボタンを押す
2. 処理方針は以下のいずれかで実装する
   - キャプチャ継続・出力停止
   - キャプチャ停止・再開時に再初期化
3. 初版では実装複雑性を下げるため、仕様上は必須とするが内部方式は実装しやすいものを採用してよい
4. 一時停止中は UI に明確に表示する

---

## 7. GUI 仕様

### 7.0 メインウィンドウの基本方針

本アプリのメインウィンドウは、録音中も常時表示され、他ウィンドウに埋もれず、かつ画面占有を最小限に抑えることを重視する。

#### 基本要件

- 常時最前面表示をサポートすること
- メインウィンドウは横長かつコンパクトなレイアウトを基本とすること
- 録音操作に必要な要素のみを常時表示すること
- 詳細設定や詳細状態は別ウィンドウへ逃がすこと
- 録音中は設定変更を制限すること

#### ウィンドウ方針

- 既定では `Topmost = true` 相当の動作を行うこと
- 画面端に寄せて配置しやすいサイズ感とすること
- 最小化せずとも邪魔になりにくいこと
- 常時表示用のコンパクトモードを標準とすること

#### 推奨サイズ感

- 横長
- 高さは低め
- おおむね 1 行または 2 行で主要操作が収まること

#### 常時表示する要素

- 録音開始 / 停止ボタン
- 一時停止 / 再開ボタン
- 録音経過時間
- マイクのミュートボタン
- スピーカーのミュートボタン
- マイクのデバイス切り替えボタン
- スピーカーのデバイス切り替えボタン
- 設定ボタン

#### 録音中の制約

- 録音中は設定ボタンを非活性とすること
- 録音中はデバイス切り替え操作を原則非活性とすること
- 録音中のミュート操作は可能とする

#### 補足

デバイス切り替えボタンは常時表示対象に含めるが、録音中は押下不可とする運用を基本とする。UI 上は存在を残しつつ無効化することで、停止後にすぐ切り替えられるようにする。

### 7.0.1 推奨レイアウト案 A（最有力）

横一列ベースのコンパクトバー形式。

```text
+--------------------------------------------------------------------------------+
| [録音開始/停止] [一時停止/再開]  00:12:34  | Mic [Mute] [Device] | Spk [Mute] [Device] | [設定] |
+--------------------------------------------------------------------------------+
```

特徴:

- 常時表示情報を 1 本のバーに集約できる
- 画面上部または下部に置きやすい
- 他アプリを大きく邪魔しない
- 録音アプリというより操作パネルに近い使い方ができる

### 7.0.2 推奨レイアウト案 B（2 段コンパクト）

上段に録音操作、下段にデバイス操作を置く。

```text
+--------------------------------------------------------------+
| [録音開始/停止] [一時停止/再開]  00:12:34       [設定]       |
| Mic [Mute] [Device]              Spk [Mute] [Device]         |
+--------------------------------------------------------------+
```

特徴:

- 横幅を少し抑えやすい
- 各要素の可読性が上がる
- レイアウト余裕がありボタンを大きくしやすい

初版では案 A を既定とし、実装都合や視認性の問題があれば案 B へ調整してよい。

### 7.0.3 詳細情報の扱い

以下の情報は常時表示必須とはしない。

- レベルメーター
- Buffer ms
- Drift ppm
- Underflow / Overflow
n- 出力ファイルパス詳細

これらは以下のいずれかで表示する。

- 折りたたみ可能な詳細領域
- ツールチップ
- 別ウィンドウの詳細画面
- ログ画面

常時表示レイアウトでは、操作性を優先し、詳細情報を詰め込みすぎないこと。

### 7.0.4 ボタン仕様

#### 録音開始 / 停止ボタン

- Stopped 時は「録音開始」
- Recording / Paused 時は「停止」
- 状態に応じて表示ラベルまたはアイコンを切り替えてよい

#### 一時停止 / 再開ボタン

- Recording 時は「一時停止」
- Paused 時は「再開」
- Stopped 時は非活性

#### ミュートボタン

- Mic ミュート
- Speaker ミュート
- 状態が一目で分かるトグル表示であること
- ミュート状態は録音対象から除外することを意味する

#### デバイス切り替えボタン

- Mic Device
- Speaker Device
- 押下時は簡易メニューまたは小型ダイアログを表示して切り替える
- 録音中は非活性

#### 設定ボタン

- 停止中のみ活性
- 録音中、一時停止中、開始中、停止中は非活性

### 7.0.5 常時前面表示

- メインウィンドウは常時前面表示を既定動作とする
- 必要なら設定で解除可能としてよい
- ただし初版では固定常時前面でもよい

### 7.0.6 見た目の方針

- 装飾よりも視認性と操作性を優先する
- ボタンは十分なクリック領域を持つこと
- 録音中であることが一目で分かること
- 経過時間は大きめの文字で表示すること
- ミュート状態や非活性状態は視覚的に明確であること

### 7.0.7 録音中の非活性制御

常時表示レイアウトにおける各操作の有効状態は以下の通りとする。

| State     | 録音開始/停止 | 一時停止/再開 | Mic Mute | Spk Mute | Mic Device | Spk Device | 設定 |
|-----------|----------------|----------------|----------|----------|------------|------------|------|
| Stopped   | ON             | OFF            | ON       | ON       | ON         | ON         | ON   |
| Starting  | OFF            | OFF            | OFF      | OFF      | OFF        | OFF        | OFF  |
| Recording | ON             | ON             | ON       | ON       | OFF        | OFF        | OFF  |
| Paused    | ON             | ON             | ON       | ON       | OFF        | OFF        | OFF  |
| Stopping  | OFF            | OFF            | OFF      | OFF      | OFF        | OFF        | OFF  |
| Error     | ON             | OFF            | ON       | ON       | ON         | ON         | ON   |

### 7.0.8 レベルメーターの追加方針

常時表示バーに簡易レベルメーターを含める。

#### 目的

- 録音できているかを即座に確認できること
- Mic / Speaker の入力有無や偏りを視覚的に把握できること
- ミュート状態や入力断を気づきやすくすること

#### 表示方式

- Mic 用レベルメーター
- Speaker 用レベルメーター
- それぞれ横長の簡易バー表示
- ただしウィンドウの横幅を過度に増やさないよう、Mic / Speaker コントロールの直下に細長く配置する

#### 推奨配置

Mic / Speaker はそれぞれ 2 段構成の小ブロックとし、上段に操作、下段に細いレベルバーを置く。

```text
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ [● REC] [▌▌ Pause]  00:12:34  | Mic [🔊] [▼] | Spk [🔊] [▼] | [⚙]                    │
│                                | [████░░░░]   | [██████░░]   |                        │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

#### 理由

- 横幅増加を抑えやすい
- Mic / Speaker ごとの対応関係が明確になる
- バーを十分な長さで確保しやすい
- 視線移動が少なく、状態確認しやすい

#### 更新頻度

- 50ms〜100ms 程度
- UI 更新負荷を上げすぎないこと

#### 補足

- ミュート中はレベルメーターの見た目を変えてもよい
- レベル値は RMS またはピークベースの簡易計算でよい
- dB 表示は初版では必須としない
- レベルバー高さは細めとし、常時表示バーの縦サイズ増加を最小限に抑えること

### 7.0.9 ミニモード

常時前面表示をさらに扱いやすくするため、通常モードとは別にミニモードを用意する。

#### 目的

- 画面占有をさらに減らすこと
- 録音中に最低限の操作だけをすぐ行えること
- 他アプリ利用中でも邪魔になりにくくすること

#### ミニモードの表示要素

- 録音開始 / 停止ボタン
- 一時停止 / 再開ボタン
- 録音経過時間

設定ボタン、デバイス切り替え、詳細表示、レベルメーターは表示しない。

#### ミニモードのモックアップ

```text
┌───────────────────────────────────────┐
│ [● REC / ■ STOP] [▌▌ / ▶]  00:12:34  │
└───────────────────────────────────────┘
```

#### ミニモード中の制約

- デバイス変更不可
- 設定画面へは遷移しない
- ミュート操作は通常モードでのみ行う
- 録音制御に特化する

#### ミニモード切り替え

- 通常モードから切り替え可能とする
- 停止中のみ切り替え可能でもよい
- 初版では停止中のみ切り替え可能として実装してよい

#### ミニモード時のウィンドウ方針

- 常時前面維持
- より小さい幅・高さ
- 位置を記憶して再表示できることが望ましい

### 7.0.10 通常モードの改訂モックアップ

レベルメーターを含む通常モードの推奨レイアウトは以下とする。

```text
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ [● REC] [▌▌ Pause]  00:12:34  | Mic [🔊] [▼] | Spk [🔊] [▼] | [⚙]                    │
│                                | [████░░░░]   | [██████░░]   |                        │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

#### レイアウト方針

- 全体は横長 1 段バー型を維持する
- Mic / Speaker 部分のみ内部的には 2 段構成とする
- これにより、常時前面バーの性格を保ちつつ横幅を抑える
- 経過時間、主要操作、設定ボタンは上段に置く

### 7.0.10.1 通常モードの具体レイアウト

通常モードは、外枠としては 2 行 6 列の Grid を基本とする。

#### 行構成

- Row 0: 主操作行
- Row 1: レベルメーター行

#### 列構成

- Column 0: 録音開始 / 停止ボタン
- Column 1: 一時停止 / 再開ボタン
- Column 2: 録音経過時間
- Column 3: Mic ブロック
- Column 4: Speaker ブロック
- Column 5: 設定ボタン

#### 列幅の推奨

- Column 0: Auto
- Column 1: Auto
- Column 2: 1*
- Column 3: Auto
- Column 4: Auto
- Column 5: Auto

経過時間列を伸縮領域にすることで、横幅変動を吸収する。

### 7.0.10.2 通常モードの Grid イメージ

```text
Grid
├ Row 0
│  ├ Col 0  [REC / STOP]
│  ├ Col 1  [Pause / Resume]
│  ├ Col 2  [Elapsed Time]
│  ├ Col 3  [Mic Header]
│  ├ Col 4  [Spk Header]
│  └ Col 5  [Settings]
└ Row 1
   ├ Col 3  [Mic LevelBar]
   └ Col 4  [Spk LevelBar]
```

### 7.0.10.3 通常モードの各要素サイズ目安

#### ウィンドウ

- Width: 720〜860 程度
- Height: 72〜92 程度
- ResizeMode: NoResize

#### ボタン

- 高さ: 32〜36
- 最小幅: 44〜72
- アイコンのみボタンは 32〜36 角程度でも可

#### 経過時間

- 幅: 120〜180 程度を推奨
- フォントサイズ: 18〜22 程度
- 太字寄り

#### レベルメーター

- 高さ: 6〜10 程度
- 幅: 90〜140 程度
- Mic / Spk で同サイズとする

### 7.0.10.4 通常モードの Mic / Speaker ブロック詳細

Mic / Speaker ブロックはそれぞれ内部に 2 行構成を持つ。

#### 上段

- ラベル（Mic または Spk）
- ミュートトグルボタン
- デバイス選択ボタン

#### 下段

- 細長いレベルメーター

#### 内部構造例

```text
MicBlock
├ Row 0: [Mic] [Mute] [Device▼]
└ Row 1: [LevelBar]
```

```text
SpkBlock
├ Row 0: [Spk] [Mute] [Device▼]
└ Row 1: [LevelBar]
```

#### 実装指針

- Mic / Spk ブロックは UserControl 化してよい
- ラベル幅は固定寄りにして揃える
- Device ボタンは `▼` 単体より、必要なら `Device` 表記付きでもよい
- 初版は `▼` 単体でもよいが、ツールチップで用途を補うこと

### 7.0.10.5 通常モードの簡易モックアップ（具体版）

```text
┌───────────────────────────────────────────────────────────────────────────────┐
│ [● REC] [▌▌ Pause]   00:12:34        Mic [🔊] [▼]    Spk [🔊] [▼]        [⚙] │
│                                      [████░░░░░░]     [██████░░░░]            │
└───────────────────────────────────────────────────────────────────────────────┘
```

### 7.0.10.6 通常モードの WPF レイアウト指針

以下のような構成を推奨する。

- 外枠: Border
- 中身: Grid
- 列定義: Auto, Auto, *, Auto, Auto, Auto
- 行定義: Auto, Auto

要素配置:

- REC ボタン: Row 0, Col 0
- Pause ボタン: Row 0, Col 1
- ElapsedTime TextBlock: Row 0, Col 2
- MicBlock: Row 0-1, Col 3
- SpkBlock: Row 0-1, Col 4
- Settings ボタン: Row 0, Col 5

MicBlock / SpkBlock は内部で 2 行構成を持つため、外側 Grid では RowSpan=2 として扱ってよい。

### 7.0.10.7 ミニモードの具体レイアウト

ミニモードは 1 行 3 列または 4 列の単純な Grid とする。

#### 表示要素

- 録音開始 / 停止
- 一時停止 / 再開
- 経過時間

#### モックアップ

```text
┌───────────────────────────────────────┐
│ [● REC / ■ STOP] [▌▌ / ▶]  00:12:34  │
└───────────────────────────────────────┘
```

#### 列構成

- Column 0: 録音開始 / 停止
- Column 1: 一時停止 / 再開
- Column 2: 経過時間

#### 推奨サイズ

- Width: 280〜380 程度
- Height: 44〜56 程度

### 7.0.10.8 モード切替仕様

- 通常モードとミニモードを切り替え可能にする
- 初版では停止中のみ切り替え可能としてよい
- モードごとにウィンドウ位置を記憶してよい

### 7.0.10.9 WPF 実装向け補足

#### 推奨コントロール

- Window
- Border
- Grid
- StackPanel
- Button / ToggleButton
- TextBlock
- ProgressBar または Rectangle ベースの簡易レベルバー

#### レベルバー実装案

初版は以下のいずれかで十分。

- `ProgressBar` を細くカスタムして使う
- `Border + Rectangle` の幅バインドで自前描画する

会議録音用途では凝ったメーターは不要であり、視認性優先とする。

#### デバイスボタン実装案

- 単純な Button + ContextMenu
- または ToggleButton + Popup

初版は ContextMenu 方式を推奨する。

### 7.0.11 状態別表示方針

#### 通常モード

- 録音操作
- 経過時間
- Mic / Speaker ミュート
- Mic / Speaker デバイスボタン
- 簡易レベルメーター
- 設定ボタン

#### ミニモード

- 録音操作
- 一時停止 / 再開
- 経過時間

### 7.0.12 将来拡張

将来的に以下を追加してもよい。

- 最小化時に小型フローティングバーへ切り替えるモード
- 詳細情報の折りたたみ表示
- 通知領域アイコンとの連携
- ミニモードと通常モードの即時切り替え

## 7.1 GUI フレームワーク

## 7.1 GUI フレームワーク

- WPF を使用すること
- Windows Forms は使用しないこと
- MVVM パターンを採用すること
- 音声処理ロジックを View / Code-behind に持ち込まないこと

## 7.2 画面一覧

### 7.2.1 メイン画面

主画面。以下の表示・操作ができること。

#### デバイス選択エリア

- Speaker Device 選択コンボボックス
- Mic Device 選択コンボボックス
- デバイス再読込ボタン

#### 保存設定エリア

- 保存先フォルダ表示
- 保存先フォルダ選択ボタン
- 次回生成予定ファイル名表示（任意。未実装でも可）

#### 録音状態エリア

- 録音状態
- 経過時間
- 推定ファイルサイズ
- 現在の出力ファイルパス

#### 音量メーターエリア

- Speaker レベルメーター
- Mic レベルメーター

#### 同期状態エリア

- Speaker Buffer ms
- Mic Buffer ms
- Drift Correction ppm
- Underflow Count
- Overflow Count

#### 操作エリア

- Start
- Pause
- Resume
- Stop
- 設定画面を開く
- 出力フォルダを開く

### 7.2.2 設定画面

以下を設定可能とする。

- 保存先既定フォルダ
- FLAC compression level
- sample rate（初版既定は 48000。実装上固定でもよい）
- frame size ms
- target buffer ms
- max correction ppm
- Kp
- Ki
- ログファイル出力有無
- ログレベル

設定値はファイルまたは標準的な .NET 設定機構で永続化する。

### 7.2.3 ログ表示画面またはパネル

以下を確認できること。

- 直近ログ
- 直近エラー
- ffmpeg 起動ログの要約
- underflow / overflow 発生記録
- ドリフト補正量

## 7.3 WPF 実装方針

- CommunityToolkit.Mvvm 等の MVVM 補助ライブラリを使用する
- ICommand ベースで操作を実装すること
- UI 更新は Dispatcher / SynchronizationContext 経由で行うこと
- UI スレッド上で重い処理をしないこと

## 7.4 ViewModel 要件

### MainWindowViewModel が保持するべき代表プロパティ

- AvailableSpeakerDevices
- AvailableMicDevices
- SelectedSpeakerDevice
- SelectedMicDevice
- OutputDirectory
- CurrentOutputFilePath
- RecordingState
- ElapsedTime
- EstimatedFileSize
- SpeakerLevel
- MicLevel
- SpeakerBufferMs
- MicBufferMs
- DriftCorrectionPpm
- UnderflowCount
- OverflowCount
- ErrorMessage
- IsBusy

### MainWindowViewModel が提供するべき代表コマンド

- RefreshDevicesCommand
- SelectOutputDirectoryCommand
- StartRecordingCommand
- StopRecordingCommand
- PauseRecordingCommand
- ResumeRecordingCommand
- OpenSettingsCommand
- OpenOutputFolderCommand

## 7.5 UI 更新頻度

- レベルメーター: 50ms〜100ms 程度
- 経過時間: 1 秒ごと
- バッファ量、ppm、カウント類: 500ms〜1 秒ごと

高頻度更新による UI 負荷を避けること。

---

## 8. アーキテクチャ

ソリューション構成の一例を以下とする。

```text
MeetingRecorder.slnx

MeetingRecorder.App
  WPF UI

MeetingRecorder.Application
  アプリケーションサービス

MeetingRecorder.Domain
  モデル、列挙体、設定定義

MeetingRecorder.Audio
  音声取得、リングバッファ、同期補正、PCM 生成

MeetingRecorder.Encoding
  ffmpeg 連携

MeetingRecorder.Infrastructure
  設定、ログ、ファイルシステム、時刻取得など
```

### 8.1 層責務

#### App

- View
- ViewModel
- UI 起動
- DI 構成

#### Application

- 録音開始停止フロー制御
- セッション管理
- UI 用 DTO 変換
- 各サービスの統合

#### Domain

- RecordingState
- RecordingOptions
- AudioDeviceInfo
- RecordingSessionInfo
- 統計情報モデル

#### Audio

- WASAPI Capture
- リングバッファ
- 可変レートリサンプラ
- PI 制御
- ステレオ PCM 生成

#### Encoding

- ffmpeg プロセス管理
- stdin 書き込み
- 終了コード処理

#### Infrastructure

- ログ
- 設定保存
- パス操作
- システム時刻取得の抽象化

---

## 9. 録音パイプライン

### 9.1 処理フロー

```text
Speaker Loopback Capture --> speaker ring buffer ----
                                                   \
                                                    --> frame builder --> interleave --> ffmpeg stdin --> FLAC file
                                                   /
Mic Capture -------------> mic ring buffer ------>
                             |
                             +--> variable rate resampler + PI control
```

### 9.2 基本方針

- スピーカー側をマスター時間軸とする
- マイク側をスレーブとし、可変レートで追従させる
- 出力は固定長フレームで生成する

### 9.3 フレーム仕様

- 基本フレーム長: 10ms
- 48kHz 時のサンプル数: 480 samples / channel

### 9.4 PCM 生成仕様

- 内部処理は float を推奨する
- ffmpeg へ渡す直前で s16le に変換する
- 変換時はクリッピングを行う

---

## 10. バッファ設計

### 10.1 リングバッファ

以下 2 つを持つ。

- speakerBuffer
- micBuffer

型は `float` とする。

### 10.2 要件

- スレッドセーフであること
- 長時間動作で GC プレッシャーが過大にならないこと
- 読み出し時に不足分をゼロ埋めできること
- 現在保持しているサンプル数を取得できること

### 10.3 ターゲットバッファ量

マイクバッファの目標保持量は初期値として以下を採用する。

- 200ms

サンプル数換算:

- 48000 Hz の場合 9600 samples

---

## 11. ドリフト補正仕様

## 11.1 背景

スピーカー出力デバイスとマイク入力デバイスは通常別クロックで駆動するため、両者が 48000 Hz を名乗っていても実効レートはわずかに異なる。このズレにより長時間録音時に音声同期が崩れる。

## 11.2 基本方針

- Speaker を master
- Mic を slave
- Mic の取り出しレートを微調整することで micBuffer の充填量を一定付近に保つ

## 11.3 制御方式

PI 制御を用いる。

### 誤差

`error = micFillSamples - targetFillSamples`

### 補正量

`correction = Kp * error + Ki * integral(error)`

### 最終比率

`ratio = 1.0 + correction`

### 補正上限

初期値:

- ±300ppm
- 数値としては ±0.0003

## 11.4 初期パラメータ

- sampleRate = 48000
- frameMs = 10
- frameSamples = 480
- targetBufferMs = 200
- maxCorrection = 300e-6
- Kp = 2e-8
- Ki = 1e-12

これらは設定画面から変更できるようにする。

## 11.5 制御周期

- 100ms ごとを基本とする
- 毎フレーム制御でもよいが、初版では 100ms を推奨する

## 11.6 Underflow / Overflow 時の扱い

### Underflow

- 必要サンプル数が取れない場合はゼロ埋めする
- 発生回数をカウントする
- ログ出力する

### Overflow

- ratio 調整で吸収を試みる
- 著しく過剰な場合のみ古いサンプルをドロップして復帰させる
- ドロップは最終手段とする
- 発生回数、ドロップ量をログに残す

---

## 12. 可変レートリサンプラ仕様

### 12.1 対象

- Mic 系統のみ

### 12.2 入出力

- 入力: float mono
- 出力: float mono

### 12.3 アルゴリズム

- 線形補間を初版の標準とする
- 位相位置 `pos` を保持する
- 出力サンプル生成ごとに `pos += ratio` を行う
- 整数部で入力進行、小数部で補間する

### 12.4 品質要件

- 音声会議用途として問題ない品質であること
- ドリフト補正のための微小補正が主目的であり、高品位音楽再生級のリサンプラは必須ではない

---

## 13. ffmpeg 連携仕様

### 13.1 起動方法

ffmpeg を外部プロセスで起動し、標準入力へ s16le の stereo PCM を書き込む。

### 13.2 代表コマンド例

```bash
ffmpeg -f s16le -ar 48000 -ac 2 -i - -c:a flac -compression_level 8 output.flac
```

### 13.3 要件

- 標準入力をリダイレクトすること
- 標準エラー出力を取得し、ログへ出力できること
- 異常終了コードを検出できること
- 録音停止時に stdin を正常クローズすること

### 13.4 compression level

- 初期値は 8 とする
- 設定画面で変更可能とする

---

## 14. 状態遷移仕様

### 14.1 RecordingState

以下の enum を基準とする。

```csharp
public enum RecordingState
{
    Stopped,
    Starting,
    Recording,
    Pausing,
    Paused,
    Stopping,
    Error
}
```

### 14.2 状態遷移

- Stopped -> Starting -> Recording
- Recording -> Pausing -> Paused
- Paused -> Recording
- Recording -> Stopping -> Stopped
- Paused -> Stopping -> Stopped
- 任意状態 -> Error

### 14.3 UI ボタン制御

| State     | Start | Pause | Resume | Stop |
|-----------|-------|-------|--------|------|
| Stopped   | ON    | OFF   | OFF    | OFF  |
| Starting  | OFF   | OFF   | OFF    | OFF  |
| Recording | OFF   | ON    | OFF    | ON   |
| Pausing   | OFF   | OFF   | OFF    | OFF  |
| Paused    | OFF   | OFF   | ON     | ON   |
| Stopping  | OFF   | OFF   | OFF    | OFF  |
| Error     | ON    | OFF   | OFF    | OFF  |

---

## 15. ログ仕様

## 15.1 基本方針

- Microsoft.Extensions.Logging を使用する
- ログカテゴリを適切に分ける
- GUI 表示用ログと永続ログは同じ基盤から出力する

## 15.2 ログ対象

以下は最低限ログ対象とする。

- アプリ起動 / 終了
- 録音開始 / 停止 / 一時停止 / 再開
- デバイス列挙結果
- 選択デバイス情報
- ffmpeg 起動コマンド要約
- ffmpeg 標準エラー出力
- ドリフト補正 ratio / ppm
- underflow / overflow
- 例外
- ファイル書き込みエラー
- デバイス喪失

## 15.3 ログレベル

- Trace: 高頻度内部詳細
- Debug: 制御値、周期情報
- Information: 状態遷移、通常操作
- Warning: underflow / overflow、再試行、異常兆候
- Error: 失敗、異常終了
- Critical: 継続不能な障害

## 15.4 ログ出力先

- デバッグコンソールまたは既定 logger provider
- ファイル出力（必要に応じて有効化）

## 15.5 高頻度ログの制御

- フレーム単位のログは常時出さない
- 1 秒ごとの集計ログを基本とする

例:

- micFillMs
- speakerFillMs
- currentPpm
- underflowCount
- overflowCount

---

## 16. エラー処理仕様

### 16.1 想定エラー

- マイクデバイス取得失敗
- スピーカーデバイス取得失敗
- Loopback 開始失敗
- Mic Capture 開始失敗
- ffmpeg 起動失敗
- ffmpeg 異常終了
- 保存先作成失敗
- 書き込み権限エラー
- リングバッファ異常
- ドリフト補正破綻
- デバイス切断

### 16.2 エラー時の共通方針

- 例外をログへ出す
- UI へユーザー向けメッセージを表示する
- 状態を Error へ遷移する
- 必要なら安全停止処理を行う

### 16.3 ffmpeg 異常終了時

- 標準エラー出力をログに残す
- ExitCode を記録する
- 生成ファイルが存在する場合はそのパスを保持する
- ファイルが不完全である可能性を UI に伝える

---

## 17. 設定仕様

### 17.1 永続化対象

- 保存先ディレクトリ
- 最後に選択した speaker device
- 最後に選択した mic device
- FLAC compression level
- targetBufferMs
- maxCorrectionPpm
- Kp
- Ki
- ログ設定

### 17.2 永続化方式

- JSON 設定ファイル等の標準的方式を採用する
- Microsoft.Extensions.Configuration と整合する構造が望ましい

---

## 18. 実装モジュール仕様

## 18.1 Domain

### RecordingOptions

保持項目例:

- OutputDirectory
- SampleRate
- BitDepth
- FrameMilliseconds
- TargetBufferMilliseconds
- MaxCorrectionPpm
- Kp
- Ki
- FlacCompressionLevel
- SpeakerDeviceId
- MicDeviceId

### AudioDeviceInfo

- DeviceId
- FriendlyName
- IsDefault
- DeviceKind

### RecordingStatistics

- ElapsedTime
- EstimatedFileSize
- SpeakerLevel
- MicLevel
- SpeakerBufferMs
- MicBufferMs
- DriftCorrectionPpm
- UnderflowCount
- OverflowCount

## 18.2 Audio

### ISpeakerCaptureService

責務:

- Loopback Capture の開始 / 停止
- speaker ring buffer への供給

### IMicCaptureService

責務:

- Mic Capture の開始 / 停止
- mic ring buffer への供給

### IRingBuffer

責務:

- サンプルの書き込み
- 指定数読み込み
- 不足時ゼロ埋め読み込み
- 保持サンプル数の取得

### IDriftCorrector

責務:

- mic buffer fill を見て ratio を計算
- PI 制御の保持

### IVariableRateResampler

責務:

- mic を ratio に応じて出力サンプル数へ変換

### IFrameBuilder

責務:

- speaker 1 フレーム取得
- mic 1 フレーム生成
- stereo interleave
- PCM16 変換

## 18.3 Encoding

### IFfmpegFlacEncoder

責務:

- ffmpeg 起動
- 標準入力へ書き込み
- 停止処理
- エラー処理

## 18.4 Application

### IRecordingService

責務:

- 録音開始
- 録音停止
- 一時停止
- 再開
- 状態通知
- 統計通知

### IDeviceService

責務:

- Speaker / Mic デバイス列挙
- 既定デバイス取得

---

## 19. スレッド構成

以下を基本構成とする。

- UI Thread
- Speaker Capture Thread
- Mic Capture Thread
- Processing Thread
- ffmpeg stderr reading task

### 19.1 原則

- UI スレッドで音声処理を行わない
- Capture コールバック内で重い処理を行わない
- 長時間ブロックを避ける

---

## 20. 録音開始シーケンス詳細

1. GUI から StartRecordingCommand が実行される
2. ViewModel が RecordingService.StartAsync を呼ぶ
3. RecordingService が状態を Starting に変更する
4. オプション検証を行う
5. 保存先フォルダの存在確認および作成を行う
6. 現在時刻からファイル名 `yyyyMMddHHmmss.flac` を生成する
7. ffmpeg を起動する
8. ring buffer を初期化する
9. speaker capture を開始する
10. mic capture を開始する
11. processing thread を開始する
12. 状態を Recording に変更する
13. UI に出力ファイルパスを通知する

---

## 21. 録音停止シーケンス詳細

1. GUI から StopRecordingCommand が実行される
2. ViewModel が RecordingService.StopAsync を呼ぶ
3. RecordingService が状態を Stopping に変更する
4. capture 側へ停止指示を出す
5. processing thread に終了指示を出す
6. 未送信 PCM があれば可能な範囲で送出する
7. ffmpeg stdin を閉じる
8. ffmpeg 終了を待つ
9. 正常終了なら状態を Stopped にする
10. 異常があれば状態を Error にする

---

## 22. 一時停止・再開仕様

一時停止は初版では以下のいずれかで実装する。

### 方針 A

- キャプチャ継続
- 処理と出力のみ停止

利点:

- 再開が簡単

欠点:

- 一時停止中にもメモリ消費が増える可能性がある

### 方針 B

- キャプチャ停止
- 再開時に再開始

利点:

- 実装が単純

欠点:

- 再開時に微小な不連続が起こりうる

初版は方針 B を採用してよい。

---

## 23. 再生機能に関する将来拡張

本仕様書の主対象は録音だが、出力構造上、以下の再生機能を将来追加しやすい設計とする。

- CH1 / CH2 をモノラル合成して再生
- CH1 のみ再生
- CH2 のみ再生
- CH1 / CH2 の個別ゲイン調整
- 波形表示
- シーク再生

モノラル合成時の基本式:

`mix = CH1 * a + CH2 * b`

出力:

- L = mix
- R = mix

---

## 24. テスト仕様

## 24.1 単体テスト対象

- PI 制御計算
- VariableRateResampler の出力数と連続性
- RingBuffer の読み書き
- ファイル名生成
- 状態遷移制御

## 24.2 結合テスト対象

- Speaker + Mic 同時録音
- ffmpeg 起動と停止
- 録音開始から停止までの正常系
- underflow 発生時の継続動作
- デバイス切断時の異常系

## 24.3 長時間テスト

- 1 時間録音
- 3 時間録音

確認項目:

- アプリが落ちないこと
- メモリ使用量が異常増加しないこと
- 音ズレが実用上問題ないこと
- FLAC ファイルが再生可能であること
- CH1 / CH2 の役割が正しいこと

## 24.4 GUI テスト

- デバイス選択ができること
- 録音状態が反映されること
- ボタン有効状態が正しいこと
- エラー表示が行われること

---

## 25. 完了条件

以下を満たした時点で初版実装完了とする。

1. WPF GUI から録音開始 / 停止ができる
2. スピーカーとマイクの 2 系統が FLAC 1 ファイルに保存される
3. CH1=Speaker, CH2=Mic が保持される
4. 1 時間以上の録音が実用上安定している
5. ドリフト補正が実装されている
6. 基本的なログが取得できる
7. エラー時に UI へ通知される
8. ファイル名が `yyyyMMddHHmmss.flac` で自動決定される

---

## 26. Codex 向け実装指示

以下の方針でコードを生成すること。

### 26.1 全体方針

- Create a Windows-only desktop application using WPF and MVVM.
- Do not use Windows Forms.
- Keep audio capture, drift correction, frame generation, and ffmpeg integration outside the UI layer.
- Use dependency injection.
- Use Microsoft.Extensions.Logging for logging.
- Do not introduce Serilog unless explicitly necessary.

### 26.2 音声仕様

- Capture speaker output using WASAPI loopback.
- Capture microphone input using WASAPI capture.
- Normalize both streams to 48kHz mono float internally.
- Build stereo PCM frames where left is speaker and right is microphone.
- Encode to FLAC in real time using ffmpeg via stdin.

### 26.3 同期仕様

- Use speaker as the master clock.
- Use microphone as the slave stream.
- Implement drift correction using a PI controller.
- Maintain microphone buffer fill near the configured target.
- Use a variable-rate resampler with linear interpolation for the microphone stream.

### 26.4 GUI 仕様

- Main screen must provide device selection, output folder selection, start/pause/resume/stop controls, levels, buffer status, drift ppm, and error display.
- Follow MVVM.
- Keep the UI responsive.

### 26.5 ログ仕様

- Use Microsoft.Extensions.Logging abstractions.
- Emit informational logs for state transitions.
- Emit warnings for underflow/overflow.
- Emit errors for startup failures, ffmpeg failures, and device failures.

---

## 27. 実装上の補足事項

1. 初版では録音の安定性を優先し、過度な機能追加は避けること
2. 一時停止機能は内部実装が複雑な場合、停止・再開始方式でもよいが、UI 上は Pause / Resume として扱える設計が望ましい
3. ログや統計は後から可観測性を高められるよう、サービス層から集約可能にしておくこと
4. GUI は見た目よりも状態可視化を優先すること
5. 録音対象デバイスの変更は録音中には許可しないこと

---

## 28. 既定値一覧

| 項目 | 既定値 |
|---|---|
| Sample Rate | 48000 |
| Bit Depth | 16 |
| Channel Count | 2 |
| Frame Size | 10ms |
| Target Buffer | 200ms |
| Max Correction | 300ppm |
| Kp | 2e-8 |
| Ki | 1e-12 |
| FLAC Compression Level | 8 |
| File Name Format | yyyyMMddHHmmss.flac |

---

## 29. 仕様の優先順位

実装優先順位は以下とする。

1. 基本録音
2. ffmpeg FLAC 化
3. WPF GUI
4. 状態管理
5. ドリフト補正
6. ログ整備
7. 一時停止 / 再開
8. ログ画面や詳細設定の充実

---

## 30. 最終要約

このアプリは、Windows 上で会議のスピーカー出力音声とマイク入力音声を同時録音し、それらを 1 つの FLAC ファイル内に 2ch 分離して保存するための WPF アプリケーションである。

最重要ポイントは以下である。

- GUI は WPF + MVVM
- 録音エンジンは UI 非依存
- CH1=Speaker、CH2=Mic
- FLAC リアルタイム圧縮
- Speaker を基準にした drift correction
- Microsoft.Extensions.Logging ベースのログ
- ファイル名は録音開始時刻の `yyyyMMddHHmmss.flac`

この方針に従って実装すれば、長時間録音・文字起こし前提・後処理前提の用途に適した録音アプリを構築できる。



---

# 追加仕様: 出力音源のアプリ指定録音 (Process Loopback)

## 概要

本アプリは従来の「スピーカー全体録音」に加えて、**特定アプリケーションが出力する音声のみを録音するモード**を提供する。  
これは Windows の **Process Loopback Capture (WASAPI)** を利用して実現する。

目的は以下。

- Zoom / Slack / Teams などの会議アプリの音声のみ録音する
- OS通知音や他アプリの音声を録音対象から除外する

本機能は **Windows 11 を前提環境**とする。

---

# 出力音源モード

録音対象の出力音は以下の2種類のモードから選択できる。

## 1. スピーカー録音

既定の再生デバイスに出力される **システム全体のミックス音声**を録音する。

特徴

- 互換性が高い
- すべての音声を録音できる
- 他アプリ音や通知音も含まれる

## 2. アプリ指定録音

ユーザーが選択した **実行中アプリケーションのプロセスツリー**から出力される音声のみ録音する。

特徴

- 会議アプリの音声のみ録音可能
- 通知音などを除外できる
- Process Loopback Capture を使用

対象は

- 指定したプロセス
- その子プロセス

である。

---

# UI仕様

## 出力音源選択

録音設定に以下の選択UIを追加する。

```
出力音源

( ) スピーカー全体
( ) アプリから選択
```

「アプリから選択」を選択した場合、現在起動中のプロセス一覧を表示する。

## プロセス一覧

表示項目

- アプリ名
- 実行ファイル名
- PID
- ウィンドウタイトル（取得可能な場合）

ユーザーはここから録音対象アプリを選択する。

---

# 録音開始時の挙動

アプリ指定録音モードで録音開始する場合、対象アプリが起動している必要がある。

対象プロセスが存在しない場合は以下の確認を表示する。

```
選択したアプリは現在起動していません。
スピーカー録音に切り替えて開始しますか？
```

選択肢

- 続行（スピーカー録音）
- キャンセル

---

# 録音中のフォールバック

アプリ指定録音中に対象プロセスが終了した場合、録音を停止せず **スピーカー録音へ自動切り替え**する。

処理内容

1. プロセス終了検知
2. Process Loopback Capture 停止
3. Speaker Loopback Capture 起動
4. 録音継続

ユーザー通知

```
対象アプリが終了したためスピーカー録音に切り替えました。
```

ログにも記録する。

---

# 再起動時の挙動

録音中に対象アプリが再起動した場合でも **アプリ録音へ自動復帰は行わない。**

理由

- 録音ソースの再切替は音ズレの原因となる
- 音声同期の安定性を優先する

したがって録音開始後は

```
Process Loopback → Speaker Loopback

のみ許可
```

とする。

---

# 録音エンジン設計

出力音取得部分は抽象化して実装する。

## インターフェース

```
IOutputCaptureSource
```

## 実装

```
SpeakerLoopbackCaptureSource
ProcessLoopbackCaptureSource
```

## 制御クラス

```
OutputCaptureController
OutputCaptureFailoverCoordinator
```

### SpeakerLoopbackCaptureSource

役割

- WASAPI Loopback でスピーカー音取得

### ProcessLoopbackCaptureSource

役割

- 指定プロセスの音声取得
- プロセス監視
- 終了イベント通知

### OutputCaptureController

役割

- 現在のキャプチャソース管理
- 音声フレームを録音パイプラインへ送る

### OutputCaptureFailoverCoordinator

役割

- 起動時のプロセス存在確認
- 録音中の自動フォールバック

---

# 状態遷移

```
録音開始
  │
  ├─対象プロセスあり
  │     │
  │     └ ProcessLoopbackCapture
  │            │
  │            └(プロセス終了)
  │
  └ SpeakerLoopbackCapture
```

ProcessLoopback → SpeakerLoopback の **一方向遷移のみ許可**。

---

# 重要設計要件

本アプリの録音処理では以下を最重要要件とする。

**音ズレを極力発生させないこと。**

そのため

- 録音中のキャプチャソース切替は最小限
- 自動復帰は行わない
- 音声パイプラインの再初期化は安全に実施

とする。
