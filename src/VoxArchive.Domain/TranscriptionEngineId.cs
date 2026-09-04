namespace VoxArchive.Domain;

/// <summary>
/// 文字起こしエンジンを設定・Request・永続化境界で識別する安定IDを表す
/// </summary>
/// <remarks>
/// 列挙型名をそのまま永続化すると将来のリネームが設定互換性へ影響するため、
/// 外部境界では明示的な文字列IDを使用する。
/// </remarks>
public readonly record struct TranscriptionEngineId
{
    /// <summary>Whisperエンジンの安定ID</summary>
    public static TranscriptionEngineId Whisper { get; } = new("whisper");

    /// <summary>ReazonSpeechエンジンの安定ID</summary>
    public static TranscriptionEngineId ReazonSpeech { get; } = new("reazonspeech");

    /// <summary>ID文字列を取得する</summary>
    public string Value { get; }

    /// <summary>
    /// 安定IDを生成する
    /// </summary>
    /// <param name="value">永続化や比較に使用する空でないID文字列</param>
    public TranscriptionEngineId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim().ToLowerInvariant();
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
