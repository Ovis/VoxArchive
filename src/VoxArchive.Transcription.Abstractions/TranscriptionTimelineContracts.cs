using System.Text.Json;

namespace VoxArchive.Transcription.Abstractions;

/// <summary>
/// 16kHz等へ正規化済みの文字起こし音声上における半開区間を表す
/// </summary>
/// <remarks>
/// 時間を秒で保持するとエンジン間の丸め方法によって境界がずれるため、
/// 共通pipelineではsample位置を正本として扱う。
/// </remarks>
public readonly record struct AudioSampleRange(long StartSample, long EndSample)
{
    /// <summary>区間に含まれるsample数を取得する</summary>
    public long Length => EndSample - StartSample;
}

/// <summary>
/// VADが発話と判断した原音上の範囲を表す
/// </summary>
public sealed record SpeechRegion(
    int SpeechRegionId,
    long StartSample,
    long EndSample,
    IReadOnlyList<AudioSampleRange> CoreRanges,
    IReadOnlyList<int> SourceRawSpeechRegionIds)
{
    /// <summary>paddingを含む区間のsample数を取得する</summary>
    public long Length => EndSample - StartSample;
}

/// <summary>
/// ASRエンジンを1回呼び出すための原音上の範囲を表す
/// </summary>
public sealed record RecognitionChunk(
    int RecognitionChunkId,
    int SpeechRegionId,
    long StartSample,
    long EndSample)
{
    /// <summary>ASRへ渡す区間のsample数を取得する</summary>
    public long Length => EndSample - StartSample;
}

/// <summary>
/// VAD設定をQueue投入時点で固定するopaque snapshotを表す
/// </summary>
/// <remarks>
/// Common AbstractionsへSilero固有型を持ち込まず、Silero projectがschemaとJSONを解釈する。
/// JsonElementはAdmission時にCloneした値を渡し、設定画面の変更が実行中ジョブへ混入しないようにする。
/// </remarks>
public sealed record SpeechRegionDetectorSettingsSnapshot(int SchemaVersion, JsonElement Settings);

/// <summary>
/// VADが生成したSpeechRegionをASRエンジン固有の呼び出し単位へ分割する
/// </summary>
public interface IRecognitionChunker
{
    /// <summary>指定した発話区間から認識chunkを生成する</summary>
    IReadOnlyList<RecognitionChunk> CreateChunks(
        IPreparedTranscriptionAudio audio,
        IReadOnlyList<SpeechRegion> speechRegions);
}

/// <summary>
/// 前処理済み音声から認識対象となる発話区間を検出する
/// </summary>
public interface ISpeechRegionDetector
{
    /// <summary>
    /// ジョブ開始時に固定された設定を使用して発話区間を検出する
    /// </summary>
    /// <param name="audio">Common側が所有する前処理済み音声</param>
    /// <param name="settings">Queue投入時点で固定されたVAD設定</param>
    /// <param name="cancellationToken">処理のキャンセルを通知するトークン</param>
    Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        CancellationToken cancellationToken = default);
}
