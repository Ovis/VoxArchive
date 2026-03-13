# バックログ

## In Progress
- [x] Application層: 状態遷移ガード実装
- [x] RecordingService の最小実装スケルトン作成

## Next
- [x] RecordingService を Audio/Encoding 実装へ接続
- [x] 録音パイプライン（Capture -> FrameBuilder -> Encoder）の統合
- [x] Processフォールバック通知を Application 層へ伝搬
- [x] DI構成と起動シーケンスの実装
- [x] WPF起動プロジェクトと最小操作UIの実装
- [x] デバイス列挙サービス実装（WASAPI）
- [x] 実キャプチャ実装（NAudio反射ロード + フォールバック）
- [x] WPF UI 詳細仕様の一部反映（デバイス選択/モード選択）
- [x] WPF UI 詳細仕様の一部反映（常時表示バー/ミニモード/レベルメータ）
- [x] 録音統計の1秒集計ログ化
- [x] 結合テスト整備（RecordingService中核フロー）
- [x] 長時間連続動作テスト基盤（10秒連続）
- [x] 長時間検証向けCSV/JSONL出力
- [x] 長時間検証レポート作成テンプレートと集計スクリプト
- [x] プロセス一覧取得とUI選択連携
- [x] プロセス不在時の開始確認ダイアログ
- [x] Process開始失敗時の自動Speakerフォールバック

## Later
- [x] RingBuffer 実装
- [x] Speaker/Mic キャプチャ抽象実装の骨格
- [x] FrameBuilder インターフェースに沿った最小実装
- [x] ffmpeg エンコーダラッパー最小実装
- [x] PIドリフト補正実装
- [x] 可変レートリサンプラ実装
- [x] Process Loopback 実装
- [x] Failover Coordinator 実装
- [ ] 実機での長時間録音テスト（1h/3h）
- [ ] NAudio導入時のE2E確認
