# 長時間録音検証レポート テンプレート

- 実施日: YYYY-MM-DD
- 実施者: 
- 実施環境:
  - OS: Windows 11
  - CPU:
  - RAM:
  - 音声デバイス(Speaker):
  - 音声デバイス(Mic):
- アプリバージョン(コミット):
- ffmpeg バージョン:

## 検証条件
- 収録モード: SpeakerLoopback / ProcessLoopback
- 対象PID(ProcessLoopback時):
- サンプルレート: 48000
- フレームサイズ(ms): 10
- TargetBuffer(ms): 200
- MaxCorrection(ppm): 300
- Kp:
- Ki:
- 保存先:

## 実行ケース

### Case-1: 1時間録音
- 開始時刻:
- 終了時刻:
- 録音時間(実測):
- ファイルサイズ:
- 生成FLAC再生可否: PASS / FAIL
- CH1=Speaker, CH2=Mic確認: PASS / FAIL
- 異常終了有無: なし / あり（内容）

### Case-2: 3時間録音
- 開始時刻:
- 終了時刻:
- 録音時間(実測):
- ファイルサイズ:
- 生成FLAC再生可否: PASS / FAIL
- CH1=Speaker, CH2=Mic確認: PASS / FAIL
- 異常終了有無: なし / あり（内容）

## メトリクス集計（CSV/JSONL）
- 入力ファイル:
  - recording.log:
  - recording-metrics.csv:
  - recording-metrics.jsonl:
- 集計結果:
  - 期間:
  - サンプル数:
  - 平均 Drift(ppm):
  - 最大 Drift(ppm):
  - 平均 MicBuffer(ms):
  - 最小 MicBuffer(ms):
  - 最大 MicBuffer(ms):
  - 平均 SpkBuffer(ms):
  - 最小 SpkBuffer(ms):
  - 最大 SpkBuffer(ms):
  - Underflow 最終値:
  - Overflow 最終値:

## リソース監視
- メモリ使用量(開始/中間/終了):
- CPU使用率(概算):
- 異常な増加有無: なし / あり（内容）

## 判定
- 1時間録音安定性: PASS / FAIL
- 3時間録音安定性: PASS / FAIL
- 実用上の同期安定性: PASS / FAIL
- 総合判定: PASS / FAIL

## 課題/改善点
1. 
2. 
3. 

## 添付
- スクリーンショット:
- ログ抜粋:
- 生成ファイルパス:
