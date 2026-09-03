using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 1回の文字起こしジョブに必要な設定を、キュー投入時点の値として保持する
/// </summary>
/// <remarks>
/// 録音設定全体を保持すると、文字起こしと無関係な設定までジョブ実行層へ伝播するため、
/// ジョブで実際に参照する値だけを <see cref="TranscriptionJobOptions"/> にスナップショットする。
/// </remarks>
public sealed record TranscriptionJobRequest(
    string AudioFilePath,
    TranscriptionJobOptions Options,
    TranscriptionTrigger Trigger)
{
    /// <summary>
    /// 現在の録音設定から文字起こしジョブ用のスナップショットを作成する
    /// </summary>
    /// <param name="AudioFilePath">文字起こし対象の録音ファイル</param>
    /// <param name="Options">キュー投入時点の録音設定</param>
    /// <param name="Trigger">文字起こしを開始した契機</param>
    public TranscriptionJobRequest(
        string AudioFilePath,
        RecordingOptions Options,
        TranscriptionTrigger Trigger)
        : this(AudioFilePath, TranscriptionJobOptions.FromRecordingOptions(Options), Trigger)
    {
    }
}

/// <summary>
/// 文字起こしジョブが実行時に参照する設定だけを保持する
/// </summary>
/// <remarks>
/// この型は、設定画面で保持する <see cref="RecordingOptions"/> とジョブ実行を分離するための移行境界である。
/// Whisper固有処理を共通サービスへ切り出すまでは、既存の音声準備処理との互換性のため
/// <see cref="RecordingOptions"/> への変換も提供する。
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

    /// <summary>
    /// 既存のWhisper音声準備処理へ必要な再生ゲインを渡すため、一時的に録音設定へ変換する
    /// </summary>
    /// <remarks>
    /// この変換は音声準備処理が <see cref="RecordingOptions"/> を受け取る現在の実装との互換用であり、
    /// 文字起こしジョブが録音設定全体へ再依存するためのものではない。
    /// 共通のAudioPreparationServiceへ責務を分離する段階で削除する。
    /// </remarks>
    public static implicit operator RecordingOptions(TranscriptionJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new RecordingOptions
        {
            DefaultSpeakerPlaybackGainDb = options.DefaultSpeakerPlaybackGainDb,
            DefaultMicPlaybackGainDb = options.DefaultMicPlaybackGainDb
        };
    }
}
