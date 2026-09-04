using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 1回の文字起こしジョブに必要な設定を、キュー投入時点の値として保持する
/// </summary>
/// <remarks>
/// 録音設定全体を保持すると、文字起こしと無関係な設定までジョブ実行層へ伝播するため、
/// ジョブで実際に参照する値だけを <see cref="TranscriptionJobOptions"/> にスナップショットする。
/// Engine/ModelはWhisper固有設定とは別に安定IDでも保持し、QueueやResolverが
/// <see cref="TranscriptionModel"/> を解釈しなくて済む境界を用意する。
/// </remarks>
public sealed record TranscriptionJobRequest(string AudioFilePath, TranscriptionJobOptions Options, TranscriptionTrigger Trigger)
{
    /// <summary>
    /// 実行対象エンジンの安定IDを取得する
    /// </summary>
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
        ArgumentNullException.ThrowIfNull(Options);

        // DefaultEngineは永続化用の安定IDなので、列挙型名や表示名へ変換せずそのままEngine解決へ渡す。
        // 未知IDをWhisperへ黙ってフォールバックさせると設定不整合を見逃すため、対応済みEngineだけを明示的に受理する。
        if (string.Equals(Options.Transcription.DefaultEngine, TranscriptionEngineId.ReazonSpeech.Value, StringComparison.OrdinalIgnoreCase))
        {
            EngineId = TranscriptionEngineId.ReazonSpeech;
            ModelId = new TranscriptionModelId(NormalizeReazonSpeechModelId(Options.Transcription.ReazonSpeech.Model));
            return;
        }

        if (string.Equals(Options.Transcription.DefaultEngine, TranscriptionEngineId.Whisper.Value, StringComparison.OrdinalIgnoreCase))
        {
            EngineId = TranscriptionEngineId.Whisper;
            ModelId = TranscriptionModelId.FromWhisperModel(Options.Transcription.Whisper.Model);
            return;
        }

        throw new NotSupportedException($"未対応の既定文字起こしEngineです: {Options.Transcription.DefaultEngine}");
    }

    private static string NormalizeReazonSpeechModelId(string? modelId)
        => string.IsNullOrWhiteSpace(modelId) ? "ja" : modelId.Trim().ToLowerInvariant();
}

/// <summary>
/// 文字起こしジョブが実行時に参照する設定だけを保持する
/// </summary>
/// <remarks>
/// この型は、設定画面で保持する <see cref="RecordingOptions"/> とジョブ実行を分離するための境界である。
/// Whisper固有設定は後続PRで型付きEngineOptionsへ移行するまで互換用に保持する。
/// ReazonSpeechは現時点でCPU固定・日本語モデル1種類なので、共通出力/ゲイン設定だけを利用する。
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
        var transcription = options.Transcription;
        var whisper = transcription.Whisper;
        return new TranscriptionJobOptions
        {
            TranscriptionDiagnosticsLogEnabled = transcription.DiagnosticsLogEnabled,
            TranscriptionExecutionMode = whisper.ExecutionMode,
            TranscriptionModel = whisper.Model,
            TranscriptionLanguage = whisper.Language,
            TranscriptionOutputFormats = transcription.OutputFormats,
            AutoTranscriptionPriority = transcription.AutoPriority,
            ManualTranscriptionPriority = transcription.ManualPriority,
            TranscriptionToastNotificationEnabled = transcription.ToastNotificationEnabled,
            DefaultSpeakerPlaybackGainDb = options.DefaultSpeakerPlaybackGainDb,
            DefaultMicPlaybackGainDb = options.DefaultMicPlaybackGainDb
        };
    }
}
