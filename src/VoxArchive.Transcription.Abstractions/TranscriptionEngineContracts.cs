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
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Engine固有settingsではない、1 Job共通の実行意図を保持する
/// </summary>
public sealed record TranscriptionEngineExecutionContext(bool DiagnosticsEnabled);

/// <summary>
/// エンジンへ渡す認識専用requestを表す
/// </summary>
public sealed record TranscriptionEngineRequest(
    IPreparedTranscriptionAudio Audio,
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
public sealed record TranscriptionEngineResult(
    IReadOnlyList<RecognizedTranscriptionSegment> Segments,
    IReadOnlyDictionary<string, object?>? Metadata = null);

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
