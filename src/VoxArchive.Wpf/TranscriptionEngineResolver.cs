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
/// 登録済みの文字起こしエンジンから実行対象を選択する
/// </summary>
/// <remarks>
/// 現在はWhisperだけが存在するため常に同じエンジンを返す。
/// エンジン選択情報をRequestへ追加する後続PRでは、このクラスだけに選択規則を集約する。
/// </remarks>
public sealed class TranscriptionEngineResolver(WhisperTranscriptionEngine whisperEngine) : ITranscriptionEngineResolver
{
    /// <inheritdoc />
    public ITranscriptionEngine Resolve(TranscriptionJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return whisperEngine;
    }
}
