using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 1回の文字起こしジョブに必要な設定を、キュー投入時点の値として保持する
/// </summary>
/// <remarks>
/// 録音設定全体を保持すると、文字起こしと無関係な設定までジョブ実行層へ伝播するため、
/// ジョブで実際に参照する値だけを <see cref="TranscriptionJobOptions"/> にスナップショットする。
/// Engine/ModelはWhisper固有設定とは別に安定IDでも保持し、後続の複数エンジン対応でQueueやResolverが
/// <see cref="TranscriptionModel"/> を解釈しなくて済む境界を用意する。
/// </remarks>
public sealed record TranscriptionJobRequest(string AudioFilePath, TranscriptionJobOptions Options, TranscriptionTrigger Trigger)
{
    /// <summary>
    /// 実行対象エンジンの安定IDを取得する
    /// </summary>
    /// <remarks>
    /// 現在の設定UIはWhisperだけを扱うため既定値はWhisperとする。
    /// 新しいエンジンからRequestを作る経路では明示的に上書きする。
    /// </remarks>
    public TranscriptionEngineId EngineId { get; init; } = TranscriptionEngineId.Whisper;

    /// <summary>
    /// 実行対象モデルの安定IDを取得する
    /// </summary>
    public TranscriptionModelId ModelId { get; init; } = TranscriptionModelId.FromWhisperModel(Options.TranscriptionModel);

    /// <summary>
    /// 現在の録音設定から文字起こしジョブ用のスナップショットを作成する
    /// </summary>
    /// <param name="AudioFilePath">文字起こし対象の録音ファイル</param>
    /// <param name="Options">キュー投入時点の録音設定</param>
    /// <param name="Trigger">文字起こしを開始した契機</param>
    public TranscriptionJobRequest(string AudioFilePath, RecordingOptions Options, TranscriptionTrigger Trigger)
        : this(AudioFilePath, TranscriptionJobOptions.FromRecordingOptions(Options), Trigger)
    {
    }
}

/// <summary>
/// 文字起こしジョブが実行時に参照する設定だけを保持する
/// </summary>
/// <remarks>
/// この型は、設定画面で保持する <see cref="RecordingOptions"/> とジョブ実行を分離するための境界である。
/// Whisper固有設定は後続PRで型付きEngineOptionsへ移行するまで互換用に保持する。
/// </remarks>
public sealed record TranscriptionJobOptions
{
    public bool TranscriptionDiagnosticsLogEnabled { get; init; }
    public TranscriptionExecutionMode TranscriptionExecutionMode { get; init; }
    public TranscriptionModel TranscriptionModel { get; init; }
    public string TranscriptionLanguage { get; init; } = "ja";
    public TranscriptionOutputFormats TranscriptionOutputFormats { get; init; }
    public TranscriptionPriority AutoTranscriptionPriority { get; init; }
    public TranscriptionPriority ManualTranscriptionPriority { get; init; }
    public bool TranscriptionToastNotificationEnabled { get; init; }
    public double DefaultSpeakerPlaybackGainDb { get; init; }
    public double DefaultMicPlaybackGainDb { get; init; }

    /// <summary>
    /// 録音設定から、文字起こし処理が現在参照している値だけをコピーする
    /// </summary>
    public static TranscriptionJobOptions FromRecordingOptions(RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TranscriptionJobOptions
        {
            TranscriptionDiagnosticsLogEnabled = options.TranscriptionDiagnosticsLogEnabled,
            TranscriptionExecutionMode = options.TranscriptionExecutionMode,
            TranscriptionModel = options.TranscriptionModel,
            TranscriptionLanguage = options.TranscriptionLanguage,
            TranscriptionOutputFormats = options.TranscriptionOutputFormats,
            AutoTranscriptionPriority = options.AutoTranscriptionPriority,
            ManualTranscriptionPriority = options.ManualTranscriptionPriority,
            TranscriptionToastNotificationEnabled = options.TranscriptionToastNotificationEnabled,
            DefaultSpeakerPlaybackGainDb = options.DefaultSpeakerPlaybackGainDb,
            DefaultMicPlaybackGainDb = options.DefaultMicPlaybackGainDb
        };
    }
}
