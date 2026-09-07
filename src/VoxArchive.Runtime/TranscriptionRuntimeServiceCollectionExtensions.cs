using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VoxArchive.Application;
using VoxArchive.Application.Abstractions;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.ReazonSpeech;
using VoxArchive.Transcription.SileroVad;
using VoxArchive.Transcription.Whisper;

namespace VoxArchive.Runtime;

/// <summary>
/// 文字起こし基盤とEngine実装をRuntime Composition Rootへ登録する
/// </summary>
public static class TranscriptionRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Common pipeline、Application use case、Whisper、ReazonSpeechをDIへ登録する
    /// </summary>
    /// <param name="services">アプリケーションのサービスコレクション</param>
    public static IServiceCollection AddVoxArchiveTranscription(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<HttpClient>();
        services.AddSingleton(sp =>
        {
            var installer = new TranscriptionModelPackageInstaller(sp.GetRequiredService<HttpClient>());
            var logger = sp.GetRequiredService<ILogger<TranscriptionModelPackageInstaller>>();
            installer.CleanupFailureHandler = (path, ex) =>
                logger.LogWarning(ex, "Failed to clean transcription model staging directory. Path={Path}", path);
            return installer;
        });

        // Commonは音声準備、VAD、話者判定、結果検証、artifact生成だけを所有する。
        services.AddSingleton<TranscriptionAudioPreparationService>();
        services.AddSingleton<TranscriptionSpeechRegionDetector>();

        // Sileroが実行可能なら優先し、未配置・初期化失敗・推論失敗時のみ既存の音量ベースVADへ戻す。
        // モデルパスは設定値にせず、後続のモデル管理機能と共有するLocalApplicationData配下の固定規則を使用する。
        services.AddSingleton(sp => new SileroVadDetector(SileroVadModelPath.GetDefault()));
        services.AddSingleton<SileroPreferredSpeechRegionDetector>(sp => new(
            sp.GetRequiredService<SileroVadDetector>(),
            sp.GetRequiredService<TranscriptionSpeechRegionDetector>(),
            sp.GetRequiredService<ILogger<SileroPreferredSpeechRegionDetector>>()));
        services.AddSingleton<ISpeechRegionDetector>(sp => sp.GetRequiredService<SileroPreferredSpeechRegionDetector>());

        services.AddSingleton<TranscriptionSpeakerLabelService>();
        services.AddSingleton<TranscriptionEngineResultValidator>();
        services.AddSingleton<TranscriptionDocumentStore>();
        services.AddSingleton<TranscriptionExportService>();
        services.AddSingleton<TranscriptionArtifactService>();
        services.AddSingleton<TranscriptionModelUsageTracker>();

        // Whisper固有の実装・capabilityはWhisper project内に閉じ込める。
        services.AddSingleton<WhisperRecognitionChunker>();
        services.AddSingleton<WhisperProcessorFactory>();
        services.AddSingleton<WhisperRecognizer>();
        services.AddSingleton<WhisperTranscriptionEngine>();
        services.AddSingleton<WhisperEngineSettingsProvider>();
        services.AddSingleton<WhisperModelProvider>();
        services.AddSingleton<WhisperModelRequirementResolver>();
        services.AddSingleton<WhisperRuntimeProbe>();
        services.AddSingleton<WhisperEngineDiagnostics>();
        services.AddSingleton<WhisperLanguageCapability>();
        services.AddSingleton<WhisperExecutionValidator>();
        services.AddSingleton<WhisperArtifactNamingCapability>();
        services.AddSingleton<WhisperExecutionModeCapability>();

        // ReazonSpeech固有の実装・capabilityはReazonSpeech project内に閉じ込める。
        services.AddSingleton<ReazonSpeechRecognitionChunker>();
        services.AddSingleton<ReazonSpeechRecognizer>();
        services.AddSingleton<ReazonSpeechTranscriptionEngine>();
        services.AddSingleton<ReazonSpeechEngineSettingsProvider>();
        services.AddSingleton<ReazonSpeechModelProvider>();
        services.AddSingleton<ReazonSpeechModelRequirementResolver>();
        services.AddSingleton<ReazonSpeechLanguageCapability>();
        services.AddSingleton<ReazonSpeechArtifactNamingCapability>();

        services.AddSingleton(sp => new TranscriptionEngineRegistry(
        [
            new TranscriptionEngineRegistration(
                sp.GetRequiredService<WhisperTranscriptionEngine>(),
                sp.GetRequiredService<WhisperEngineSettingsProvider>(),
                sp.GetRequiredService<WhisperModelProvider>(),
                sp.GetRequiredService<WhisperModelRequirementResolver>(),
                sp.GetRequiredService<WhisperEngineDiagnostics>(),
                sp.GetRequiredService<WhisperLanguageCapability>(),
                sp.GetRequiredService<WhisperExecutionValidator>(),
                sp.GetRequiredService<WhisperArtifactNamingCapability>(),
                sp.GetRequiredService<WhisperExecutionModeCapability>()),
            new TranscriptionEngineRegistration(
                sp.GetRequiredService<ReazonSpeechTranscriptionEngine>(),
                sp.GetRequiredService<ReazonSpeechEngineSettingsProvider>(),
                sp.GetRequiredService<ReazonSpeechModelProvider>(),
                sp.GetRequiredService<ReazonSpeechModelRequirementResolver>(),
                null,
                sp.GetRequiredService<ReazonSpeechLanguageCapability>(),
                null,
                sp.GetRequiredService<ReazonSpeechArtifactNamingCapability>()),
        ]));

        services.AddSingleton<TranscriptionModelManager>();
        services.AddSingleton<TranscriptionOrchestrator>();
        services.AddSingleton<TranscriptionJobAdmissionService>();
        services.AddSingleton<TranscriptionJobQueue>();
        services.AddSingleton<ITranscriptionEngineSettingsService, TranscriptionEngineSettingsService>();
        services.AddSingleton<ITranscriptionApplicationService, TranscriptionApplicationService>();
        return services;
    }
}
