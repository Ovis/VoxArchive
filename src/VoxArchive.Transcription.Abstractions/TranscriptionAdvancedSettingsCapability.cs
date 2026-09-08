namespace VoxArchive.Transcription.Abstractions;

/// <summary>
/// Engine固有の詳細設定を安定IDと文字列値としてApplicationへ公開するoptional capability
/// </summary>
/// <remarks>
/// Application/WPFがEngine固有options型やJSON schemaを解釈しないための境界である。
/// 値の型変換・既定値・validationはEngine側に残し、Presentationは安定IDを使ってUIを構成する。
/// </remarks>
public interface ITranscriptionEngineAdvancedSettingsCapability
{
    /// <summary>このcapabilityが属するEngine IDを取得する</summary>
    TranscriptionEngineId EngineId { get; }

    /// <summary>typed optionsからUI編集用の安定ID/valueを取得する</summary>
    IReadOnlyDictionary<string, string> GetValues(ITranscriptionEngineOptions options);

    /// <summary>UI編集値をtyped optionsへ反映した新しいsnapshotを返す</summary>
    ITranscriptionEngineOptions ApplyValues(
        ITranscriptionEngineOptions options,
        IReadOnlyDictionary<string, string> values);
}
