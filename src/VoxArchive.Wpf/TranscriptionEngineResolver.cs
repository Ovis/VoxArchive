using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 現在の文字起こしRequestに対応するエンジンを解決する
/// </summary>
public interface ITranscriptionEngineResolver
{
    /// <summary>
    /// 指定されたRequestを処理するエンジンを返す
    /// </summary>
    ITranscriptionEngine Resolve(TranscriptionJobRequest request);
}

/// <summary>
/// Requestに保存された安定Engine IDから実行対象を選択する
/// </summary>
/// <remarks>
/// 現在登録されている実装はWhisperだけだが、未知のIDを黙ってWhisperへフォールバックしない。
/// エンジン追加時の設定ミスや永続化不整合を早期に検出するためである。
/// </remarks>
public sealed class TranscriptionEngineResolver(WhisperTranscriptionEngine whisperEngine) : ITranscriptionEngineResolver
{
    /// <inheritdoc />
    public ITranscriptionEngine Resolve(TranscriptionJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EngineId == TranscriptionEngineId.Whisper)
        {
            return whisperEngine;
        }

        throw new NotSupportedException($"未対応の文字起こしエンジンです: {request.EngineId}");
    }
}
