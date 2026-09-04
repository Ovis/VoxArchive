using VoxArchive.Domain;

namespace VoxArchive.Infrastructure;

/// <summary>
/// VoxArchiveが管理する文字起こしモデルの固定定義を表す
/// </summary>
/// <param name="EngineId">モデルを利用する文字起こしEngineの安定ID</param>
/// <param name="ModelId">利用者が選択する論理モデルの安定ID</param>
/// <param name="DisplayName">UIへ表示するモデル名</param>
/// <param name="ArtifactVersion">配布物としてのモデルバージョン</param>
/// <param name="Revision">取得元を固定するリビジョン</param>
/// <param name="License">モデル配布物のライセンス識別子</param>
/// <param name="Files">モデルを構成する全ファイル</param>
public sealed record TranscriptionModelDefinition(
    TranscriptionEngineId EngineId,
    TranscriptionModelId ModelId,
    string DisplayName,
    string ArtifactVersion,
    string Revision,
    string License,
    IReadOnlyList<TranscriptionModelFileDefinition> Files);

/// <summary>
/// 文字起こしモデルを構成する1ファイルの取得・検証条件を表す
/// </summary>
/// <param name="SourceUrl">リビジョンを固定した取得元URL</param>
/// <param name="DestinationName">モデルディレクトリ内でのファイル名</param>
/// <param name="Size">期待するファイルサイズ</param>
/// <param name="Sha256">期待するSHA-256。64桁の16進数で指定する</param>
public sealed record TranscriptionModelFileDefinition(
    Uri SourceUrl,
    string DestinationName,
    long Size,
    string Sha256);
