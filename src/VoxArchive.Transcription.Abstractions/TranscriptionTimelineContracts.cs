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
    /// <summary>
    /// 区間に含まれるsample数を取得する
    /// </summary>
    public long Length => EndSample - StartSample;
}

/// <summary>
/// VADが発話と判断した原音上の範囲を表す
/// </summary>
/// <param name="SpeechRegionId">ジョブ内で一意となる連番ID</param>
/// <param name="StartSample">paddingを含む開始sample位置</param>
/// <param name="EndSample">paddingを含む終了sample位置。区間は半開区間として扱う</param>
/// <param name="CoreRanges">VADが実際に発話として検出したpadding前の範囲</param>
/// <param name="SourceRawSpeechRegionIds">この区間の生成元となったraw region ID</param>
public sealed record SpeechRegion(
    int SpeechRegionId,
    long StartSample,
    long EndSample,
    IReadOnlyList<AudioSampleRange> CoreRanges,
    IReadOnlyList<int> SourceRawSpeechRegionIds)
{
    /// <summary>
    /// paddingを含む区間のsample数を取得する
    /// </summary>
    public long Length => EndSample - StartSample;
}

/// <summary>
/// ASRエンジンを1回呼び出すための原音上の範囲を表す
/// </summary>
/// <param name="RecognitionChunkId">ジョブ内で一意となる連番ID</param>
/// <param name="SpeechRegionId">生成元となったSpeechRegion ID</param>
/// <param name="StartSample">開始sample位置</param>
/// <param name="EndSample">終了sample位置。区間は半開区間として扱う</param>
public sealed record RecognitionChunk(
    int RecognitionChunkId,
    int SpeechRegionId,
    long StartSample,
    long EndSample)
{
    /// <summary>
    /// ASRへ渡す区間のsample数を取得する
    /// </summary>
    public long Length => EndSample - StartSample;
}

/// <summary>
/// VADが生成したSpeechRegionをASRエンジン固有の呼び出し単位へ分割する
/// </summary>
/// <remarks>
/// VADの責務へエンジン固有の入力長制約を持ち込まないため、
/// SpeechRegionとRecognitionChunkの変換を独立した契約として定義する。
/// </remarks>
public interface IRecognitionChunker
{
    /// <summary>
    /// 指定した発話区間から認識chunkを生成する
    /// </summary>
    /// <param name="audio">sample rateを含む前処理済み音声</param>
    /// <param name="speechRegions">VADが生成した発話区間</param>
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
    /// 発話区間を検出する
    /// </summary>
    /// <param name="audio">Common側が所有する前処理済み音声</param>
    /// <param name="cancellationToken">処理のキャンセルを通知するトークン</param>
    Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        IPreparedTranscriptionAudio audio,
        CancellationToken cancellationToken = default);
}
