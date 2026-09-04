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

    /// <summary>現在のWhisper設定から安定IDへ変換する</summary>
    public static TranscriptionModelId FromWhisperModel(TranscriptionModel model) => model switch
    {
        TranscriptionModel.Tiny => new("tiny"),
        TranscriptionModel.Base => new("base"),
        TranscriptionModel.Small => new("small"),
        TranscriptionModel.Medium => new("medium"),
        TranscriptionModel.LargeV3 => new("large-v3"),
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "未対応のWhisperモデルです。")
    };

    /// <inheritdoc />
    public override string ToString() => Value;
}
