namespace VoxArchive.Transcription.Abstractions;

/// <summary>
/// UIへ公開する論理モデルの安定IDと表示名を表す
/// </summary>
public sealed record TranscriptionModelDescriptor(
    TranscriptionModelId ModelId,
    string DisplayName,
    string? ArtifactVersion = null,
    string? Revision = null,
    string? License = null);

/// <summary>
/// モデル配置状態の検証レベルを定義する
/// </summary>
public enum TranscriptionModelInspectionLevel
{
    Existence = 0,
    Size = 1,
    Hash = 2,
}

/// <summary>
/// モデルパッケージの配置状態を定義する
/// </summary>
public enum TranscriptionModelPackageState
{
    Missing = 0,
    Installed = 1,
    Incomplete = 2,
    Corrupt = 3,
}

/// <summary>
/// 指定レベルで確認したモデル配置状態を保持する
/// </summary>
public sealed record TranscriptionModelInspection(
    TranscriptionModelPackageState State,
    TranscriptionModelInspectionLevel Level);

/// <summary>
/// モデル取得全体の転送進捗を表す
/// </summary>
public sealed record TranscriptionModelTransferProgress(long BytesReceived, long TotalBytes)
{
    /// <summary>0～100の進捗率を取得する</summary>
    public double Percent => TotalBytes <= 0
        ? 0d
        : Math.Clamp(BytesReceived * 100d / TotalBytes, 0d, 100d);
}

/// <summary>
/// 1論理モデルとして利用する物理ファイル群を保持する
/// </summary>
public sealed record TranscriptionModelInstallation(
    TranscriptionEngineId EngineId,
    TranscriptionModelId ModelId,
    IReadOnlyList<string> Files)
{
    /// <summary>単一ファイルモデルで利用する先頭ファイルを取得する</summary>
    public string PrimaryFile => Files.Count > 0
        ? Files[0]
        : throw new InvalidOperationException("モデルを構成するファイルがありません。");
}

/// <summary>
/// Providerが固定するモデル配布物の定義を表す
/// </summary>
public sealed record TranscriptionModelPackageDefinition(
    TranscriptionEngineId EngineId,
    TranscriptionModelId ModelId,
    string DisplayName,
    string ArtifactVersion,
    string Revision,
    string License,
    IReadOnlyList<TranscriptionModelFileDefinition> Files);

/// <summary>
/// モデルを構成する1ファイルの取得・検証条件を表す
/// </summary>
public sealed record TranscriptionModelFileDefinition(
    Uri SourceUrl,
    string DestinationName,
    long Size,
    string Sha256);
