using System.Text.Json;

namespace VoxArchive.Transcription;

/// <summary>
/// 1回の文字起こしJobについて、再現・比較に必要な詳細診断情報を保持する
/// </summary>
/// <remarks>
/// 診断schemaは文字起こし処理の内部型をそのままserializeせず、長期的に読み返せる明示DTOとして定義する。
/// sourceには絶対パスを保持せず、入力同一性はPrepared AudioのSHA-256で判定する。
/// </remarks>
public sealed record TranscriptionDiagnosticDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string ApplicationVersion { get; init; } = string.Empty;
    public string Status { get; init; } = "success";
    public string? FailedStage { get; init; }
    public required TranscriptionDiagnosticSource Source { get; init; }
    public JsonElement? Settings { get; init; }
    public TranscriptionDiagnosticVad? Vad { get; init; }
    public IReadOnlyList<TranscriptionDiagnosticRecognitionChunk> RecognitionChunks { get; init; } = [];
    public IReadOnlyList<TranscriptionDiagnosticAsrResult> AsrResults { get; init; } = [];
    public IReadOnlyList<TranscriptionDiagnosticSpeakerResult> SpeakerResults { get; init; } = [];
    public TranscriptionDiagnosticTimings Timings { get; init; } = new();
    public TranscriptionDiagnosticException? Exception { get; init; }
}

/// <summary>診断対象となったPrepared Audioの識別情報を保持する</summary>
public sealed record TranscriptionDiagnosticSource(
    string FileName,
    long FileSizeBytes,
    long DurationSamples,
    int SampleRate,
    string PreparedAudioSha256);

/// <summary>VADの実行結果とfallback情報を保持する</summary>
public sealed record TranscriptionDiagnosticVad
{
    public string Detector { get; init; } = string.Empty;
    public JsonElement? Settings { get; init; }
    public IReadOnlyList<TranscriptionDiagnosticSpeechRange> RawRegions { get; init; } = [];
    public IReadOnlyList<TranscriptionDiagnosticSpeechRegion> SpeechRegions { get; init; } = [];
    public bool FallbackUsed { get; init; }
    public string? FallbackReason { get; init; }
    public long ElapsedMilliseconds { get; init; }
}

/// <summary>VADが直接返したpadding前の発話範囲を保持する</summary>
public sealed record TranscriptionDiagnosticSpeechRange(
    int RawSpeechRegionId,
    long StartSample,
    long EndSample);

/// <summary>padding/merge後のSpeechRegionとraw lineageを保持する</summary>
public sealed record TranscriptionDiagnosticSpeechRegion(
    int SpeechRegionId,
    long StartSample,
    long EndSample,
    IReadOnlyList<TranscriptionDiagnosticSampleRange> CoreRanges,
    IReadOnlyList<int> SourceRawSpeechRegionIds);

/// <summary>sample座標の半開区間を保持する</summary>
public sealed record TranscriptionDiagnosticSampleRange(long StartSample, long EndSample);

/// <summary>ASRへ1回渡すRecognitionChunkと分割根拠を保持する</summary>
public sealed record TranscriptionDiagnosticRecognitionChunk
{
    public int RecognitionChunkId { get; init; }
    public int SpeechRegionId { get; init; }
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    public string SplitReason { get; init; } = string.Empty;
    public JsonElement? SplitDetails { get; init; }
    public long ElapsedMilliseconds { get; init; }
}

/// <summary>ASR結果のraw/canonical時刻と文字列を保持する</summary>
public sealed record TranscriptionDiagnosticAsrResult
{
    public int RecognitionChunkId { get; init; }
    public string? RawText { get; init; }
    public string? CanonicalText { get; init; }
    public JsonElement? TimestampTrace { get; init; }
    public bool Discarded { get; init; }
    public string? DiscardReason { get; init; }
}

/// <summary>既存話者判定の最終ラベルと判定根拠を保持する</summary>
public sealed record TranscriptionDiagnosticSpeakerResult
{
    public int RecognitionChunkId { get; init; }
    public string? SpeakerLabel { get; init; }
    public double? SpeakerChannelEnergy { get; init; }
    public double? MicrophoneChannelEnergy { get; init; }
}

/// <summary>主要pipeline stageのStopwatch計測値を保持する</summary>
public sealed record TranscriptionDiagnosticTimings
{
    public long OverallMilliseconds { get; init; }
    public long AudioPreparationMilliseconds { get; init; }
    public long VadMilliseconds { get; init; }
    public long ChunkGenerationMilliseconds { get; init; }
    public long AsrMilliseconds { get; init; }
    public long SpeakerLabelingMilliseconds { get; init; }
    public long CanonicalAndOutputMilliseconds { get; init; }
}

/// <summary>失敗Jobの例外種別とmessageだけを保持する</summary>
public sealed record TranscriptionDiagnosticException(string ExceptionType, string Message);
