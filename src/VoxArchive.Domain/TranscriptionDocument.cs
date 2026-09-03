namespace VoxArchive.Domain;

/// <summary>
/// 文字起こし結果の正本として永続化するドキュメントを表す
/// </summary>
/// <remarks>
/// TXT/SRT/VTTは本ドキュメントから再生成できる派生物として扱う。
/// JSON上の識別子は列挙型名に依存させず、将来のエンジン追加後も安定した文字列を保持する。
/// </remarks>
public sealed record TranscriptionDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required TranscriptionSource Source { get; init; }
    public required TranscriptionIdentity Transcription { get; init; }
    public required TranscriptionRuntime Runtime { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<TranscriptionDocumentSegment> Segments { get; init; } = [];
}

/// <summary>
/// 文字起こし対象となった音声を識別する
/// </summary>
public sealed record TranscriptionSource(string FileName);

/// <summary>
/// 文字起こしを生成したエンジン・モデルと要求オプションを保持する
/// </summary>
public sealed record TranscriptionIdentity
{
    public required string Engine { get; init; }
    public required string Model { get; init; }
    public string? ModelVersion { get; init; }
    public string? ModelRevision { get; init; }
    public IReadOnlyDictionary<string, string?> Options { get; init; } = new Dictionary<string, string?>();
}

/// <summary>
/// 要求した実行方式と、実際に利用されたランタイムを保持する
/// </summary>
public sealed record TranscriptionRuntime
{
    public required string Requested { get; init; }
    public string? Actual { get; init; }
}

/// <summary>
/// 正本ドキュメントに保存する文字起こし区間を表す
/// </summary>
public sealed record TranscriptionDocumentSegment
{
    public double Start { get; init; }
    public double End { get; init; }
    public string? Speaker { get; init; }
    public string Text { get; init; } = string.Empty;
}
