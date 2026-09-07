namespace VoxArchive.Domain;

/// <summary>
/// 文字起こしモデルをEngine非依存のRequest境界で識別する安定IDを表す
/// </summary>
/// <remarks>
/// モデルの実体や精度形式はEngine側の責務とし、Requestでは利用者が選んだ論理モデルだけを識別する。
/// </remarks>
public readonly record struct TranscriptionModelId
{
    public string Value { get; }

    /// <summary>安定IDを生成する</summary>
    /// <param name="value">永続化や比較に使用する空でないモデルID</param>
    public TranscriptionModelId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim().ToLowerInvariant();
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
