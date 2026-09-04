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
/// 未知のIDを黙ってWhisperへフォールバックすると、異なるモデル設定で処理して結果を誤って上書きする可能性がある。
/// 登録済みEngineだけを明示的に解決し、設定・永続化の不整合は実行前に失敗させる。
/// </remarks>
public sealed class TranscriptionEngineResolver(
    WhisperTranscriptionEngine whisperEngine,
    ReazonSpeechTranscriptionEngine reazonSpeechEngine) : ITranscriptionEngineResolver
{
    /// <inheritdoc />
    public ITranscriptionEngine Resolve(TranscriptionJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EngineId == TranscriptionEngineId.Whisper)
        {
            return whisperEngine;
        }

        if (request.EngineId == TranscriptionEngineId.ReazonSpeech)
        {
            return reazonSpeechEngine;
        }

        throw new NotSupportedException($"未対応の文字起こしエンジンです: {request.EngineId}");
    }
}
