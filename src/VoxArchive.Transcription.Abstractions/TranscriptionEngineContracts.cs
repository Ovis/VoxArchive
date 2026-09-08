namespace VoxArchive.Transcription.Abstractions;

/// <summary>
/// エンジン固有設定のsnapshotであることを示すmarker契約
/// </summary>
public interface ITranscriptionEngineOptions;

/// <summary>
/// 文字起こしエンジンが要求する音声形式を表す
/// </summary>
public sealed record TranscriptionAudioRequirements(
    int SampleRate,
    int Channels,
    TranscriptionSampleFormat SampleFormat);

/// <summary>
/// 共通基盤とエンジンの間で扱うサンプル形式を定義する
/// </summary>
public enum TranscriptionSampleFormat
{
    Pcm16 = 0,
    Float32 = 1,
}

/// <summary>
/// Common側が所有する前処理済み音声への読み取り専用アクセスを提供する
/// </summary>
public interface IPreparedTranscriptionAudio : IAsyncDisposable
{
    TranscriptionAudioRequirements Format { get; }
    TimeSpan Duration { get; }

    /// <summary>
    /// Prepared Audio上の1chあたりの正確なsample数を取得する
    /// </summary>
    /// <remarks>
    /// production実装は生成済み音声の実データ長から値を保持する。
    /// default実装は既存のテストdoubleや外部実装との互換性のためのfallbackであり、
    /// sample座標を正本として扱う実処理ではoverrideされた正確な値を使用する。
    /// </remarks>
    long SampleCount => Math.Max(0L, (long)Math.Round(Duration.TotalSeconds * Format.SampleRate));

    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Engine固有settingsではない、1 Job共通の実行意図を保持する
/// </summary>
public sealed record TranscriptionEngineExecutionContext(bool DiagnosticsEnabled);

/// <summary>
/// エンジンへ渡す認識専用requestを表す
/// </summary>
/// <remarks>
/// VADはEngine非依存のCommon pipelineで一度だけ実行し、Engineは確定済みSpeechRegionから
/// 自身のRecognitionChunkを生成する。これによりSilero fallback等の共通判断をEngineごとに重複させない。
/// </remarks>
public sealed record TranscriptionEngineRequest(
    IPreparedTranscriptionAudio Audio,
    IReadOnlyList<SpeechRegion> SpeechRegions,
    ITranscriptionEngineOptions Options,
    TranscriptionEngineExecutionContext Context);

/// <summary>
/// エンジンが返す認識segmentを表す
/// </summary>
/// <param name="Start">Prepared Audio上のabsolute開始時刻</param>
/// <param name="End">Prepared Audio上のabsolute終了時刻</param>
/// <param name="Text">認識テキスト</param>
/// <param name="RecognitionChunkId">この結果を生成したRecognitionChunkのジョブ内ID。chunkを経由しないテスト等ではnullを許容する</param>
/// <param name="Metadata">エンジン固有の追加情報</param>
public sealed record RecognizedTranscriptionSegment(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    int? RecognitionChunkId = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>
/// エンジンの認識結果を表す
/// </summary>
/// <param name="Segments">認識segment一覧</param>
/// <param name="Metadata">canonical artifactへ保持するEngine固有metadata</param>
/// <param name="Diagnostics">詳細診断ON時だけ返すEngine非依存trace。通常処理ではnull</param>
public sealed record TranscriptionEngineResult(
    IReadOnlyList<RecognizedTranscriptionSegment> Segments,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    TranscriptionEngineDiagnosticTrace? Diagnostics = null);

/// <summary>
/// ASR Engineの最小実行契約を定義する
/// </summary>
public interface ITranscriptionEngine
{
    TranscriptionEngineId Id { get; }
    TranscriptionAudioRequirements AudioRequirements { get; }
    Task<TranscriptionEngineResult> TranscribeAsync(
        TranscriptionEngineRequest request,
        CancellationToken cancellationToken = default);
}
