# 録音ファイル管理・再生機能 設計書

## 1. 目的

本書は、既存の録音アプリに対して「録音ファイルの管理・再生」機能を追加するための設計を定義する。
録音 UI と管理 UI を分離し、録音中の操作性を阻害しない構成を採用する。

## 2. スコープ

- 録音ファイル一覧表示
- 再生 / 停止 / シーク
- Speaker チャンネルと Mic チャンネルの個別ゲイン調整
- タイトル編集（FLAC メタデータ TITLE 連携）
- ファイル名リネーム
- 実ファイル削除
- 一覧から除外（実ファイルは残す）

## 3. UI 方針

- メイン録音画面は現状維持（コンパクト表示）
- ファイル管理は別画面 `LibraryWindow` として実装
- 遷移はメイン画面からライブラリ画面を開く方式

### 3.1 ライブラリ画面構成

- 上部: 検索・再スキャン・ソート
- 中央: 録音ファイル一覧
- 右または下部: 詳細ペイン（再生コントロール・編集）

### 3.2 一覧カラム（初版）

- タイトル
- ファイル名
- 長さ
- 更新日時
- サイズ
- 状態（通常 / 除外）

## 4. データ管理方針

SQLite を採用する。

理由:
- タイトル、除外状態などのアプリ管理情報を安定保持できる
- 将来の拡張（タグ、検索、お気に入り）に対応しやすい

ただし真実のソースはファイル実体とし、ライブラリ起動時/手動更新時に保存先フォルダをスキャンして同期する。

## 5. DB 設計（初版）

### 5.1 テーブル `recordings`

- `id` TEXT PRIMARY KEY (UUID)
- `file_path` TEXT UNIQUE NOT NULL
- `file_name` TEXT NOT NULL
- `title` TEXT NULL
- `duration_ms` INTEGER NOT NULL
- `sample_rate` INTEGER NOT NULL
- `channels` INTEGER NOT NULL
- `file_size_bytes` INTEGER NOT NULL
- `last_write_utc` TEXT NOT NULL
- `is_hidden` INTEGER NOT NULL DEFAULT 0
- `created_utc` TEXT NOT NULL
- `updated_utc` TEXT NOT NULL

### 5.2 同期ルール

- 新規ファイル: DB へ追加
- 消失ファイル: DB から削除
- 変更ファイル（サイズ/更新時刻差分）: メタ情報再読込
- DB の `is_hidden` は保持

## 6. 再生仕様

### 6.1 チャンネル扱い

- Left = Speaker
- Right = Mic

### 6.2 音量調整

- スライダー値は dB ゲイン
- 範囲: `-60dB ～ +24dB`（初版）
- 各チャンネルに独立適用
- 出力前にクリップ保護（ハードクリップ）を行う

### 6.3 再生操作

- 再生 / 停止
- 再生位置シーク
- 再生中の現在位置表示

## 7. 編集仕様

### 7.1 タイトル編集

- DB `title` を更新
- FLAC タグ `TITLE` を更新
- 失敗時はエラー表示し、DB/タグ不整合をログへ記録

### 7.2 リネーム

- 同一フォルダ内でファイル名変更
- 成功時に DB の `file_path`, `file_name`, `updated_utc` 更新

### 7.3 削除

- 実ファイル削除 + DB 行削除
- 確認ダイアログ必須

### 7.4 一覧から除外

- DB `is_hidden = 1`
- 実ファイルは残す
- フィルタで再表示可能

## 8. エラー処理

- ファイル I/O 失敗: ユーザー通知 + ログ
- タグ書き込み失敗: ユーザー通知 + ログ
- 再生デバイス初期化失敗: ユーザー通知 + リトライ導線
- DB 例外: ライブラリ画面でエラー表示し、録音機能への影響を隔離

## 9. アーキテクチャ追加

- `VoxArchive.Library`（新規）
  - `LibraryService`（一覧同期・CRUD）
  - `LibraryPlaybackService`（再生・ゲイン制御）
  - `FlacMetadataService`（タイトル読書き）
- `VoxArchive.Wpf`
  - `LibraryWindow`
  - `LibraryViewModel`

## 10. 実装フェーズ

1. ライブラリ画面の雛形と一覧表示
2. SQLite 追加とフォルダ同期
3. 再生（再生/停止/シーク）
4. チャンネル別 dB ゲイン
5. タイトル編集（FLAC TITLE 連携）
6. リネーム / 削除 / 除外
7. 例外処理・ログ強化

## 11. 受け入れ条件（初版）

- 保存先フォルダの FLAC が一覧に表示される
- 任意ファイルを再生できる
- Speaker/Mic のゲインを個別変更できる
- タイトル編集が一覧と FLAC タグに反映される
- リネーム・削除・除外が仕様通り動作する
- 失敗時にアプリが落ちずエラー表示される
