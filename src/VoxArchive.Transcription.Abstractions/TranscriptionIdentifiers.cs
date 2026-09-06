namespace VoxArchive.Transcription.Abstractions;

/// <summary>
/// 文字起こしエンジンを識別する値を保持する
/// </summary>
public readonly record struct TranscriptionEngineId
{
    /// <summary>
    /// 指定した識別子からエンジンIDを生成する
    /// </summary>
    /// <param name="value">永続化やRegistryのキーとして使用する安定した識別子</param>
    public TranscriptionEngineId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Engine ID must not be empty.", nameof(value));
        }

        Value = value.Trim();
    }

    /// <summary>
    /// 永続化可能な識別子を取得する
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// エンジン内部のモデルを識別する値を保持する
/// </summary>
public readonly record struct TranscriptionModelId
{
    /// <summary>
    /// 指定した識別子からモデルIDを生成する
    /// </summary>
    /// <param name="value">対象エンジン内で一意となる識別子</param>
    public TranscriptionModelId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Model ID must not be empty.", nameof(value));
        }

        Value = value.Trim();
    }

    /// <summary>
    /// 永続化可能な識別子を取得する
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Engine IDとModel IDの組み合わせで物理モデルを一意に識別する
/// </summary>
public readonly record struct TranscriptionModelKey(
    TranscriptionEngineId EngineId,
    TranscriptionModelId ModelId);
