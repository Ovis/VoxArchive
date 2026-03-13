# ローカルE2E手順

## 目的
実機環境で録音フロー（Speaker+Mic、Processフォールバック、ログ出力）を確認する。

## 前提
1. Windows 11
2. `ffmpeg` が PATH 上で実行可能
3. `dotnet build VoxArchive.sln -m:1` が成功すること

## 実行手順
1. `dotnet run --project src/VoxArchive.Wpf/VoxArchive.Wpf.csproj`
2. Speaker/Mic デバイスを選択
3. 出力モードを選択
4. 30秒録音して停止
5. 出力 FLAC を再生し、左右で Speaker/Mic が分離されていることを確認
6. `recording.log` に状態遷移と1秒統計が出力されることを確認

## Processフォールバック確認
1. 出力モードを `ProcessLoopback` にする
2. 対象プロセス PID を指定して録音開始
3. 対象プロセスを終了する
4. UI のメッセージに `出力切替` が表示されることを確認
5. `recording.log` に切替前後の状態が残ることを確認

## 長時間確認
1. 1時間録音を実施
2. メモリ使用量を定期確認
3. 録音停止後にFLAC再生可能であることを確認
4. 必要に応じて3時間録音を追加実施
