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
            // Whisperは既存互換のSHA検証Installerを維持する。
            var installer = new TranscriptionModelPackageInstaller(sp.GetRequiredService<HttpClient>());
            var logger = sp.GetRequiredService<ILogger<TranscriptionModelPackageInstaller>>();
            installer.CleanupFailureHandler = (path, ex) =>
                logger.LogWarning(ex, "Failed to clean transcription model staging directory. Path={Path}", path);
            return installer;
        });
        services.AddSingleton(sp =>
        {
            // Silero/ReazonSpeechは実native load成功を利用可能条件とするため、SHA検証とは分離したtransactionを使う。
            var transaction = new ManagedModelFileTransaction(sp.GetRequiredService<HttpClient>());
            var logger = sp.GetRequiredService<ILogger<ManagedModelFileTransaction>>();
            transaction.CleanupFailureHandler = (path, ex) =>
                logger.LogWarning(ex, "Failed to clean managed model operation directory. Path={Path}", path);
            return transaction;
        });

        // Commonは音声準備、VAD、canonical化、話者判定、結果検証、artifact生成だけを所有する。
        services.AddSingleton<TranscriptionAudioPreparationService>();
        services.AddSingleton<TranscriptionSpeechRegionDetector>();
        services.AddSingleton<TranscriptionModelUsageTracker>();

        // Sileroが実行可能なら優先し、未配置・初期化失敗・推論失敗時のみ既存の音量ベースVADへ戻す。
        // モデル管理はASR Engineへ偽装せず専用managerで扱い、同じUsageTrackerでASRモデル操作・文字起こしと排他する。
        services.AddSingleton(sp => new SileroVadDetector(SileroVadModelPath.GetDefault()));
        services.AddSingleton<SileroVadModelManager>();
        services.AddSingleton<ISpeechRegionDetectorModelManager>(sp => sp.GetRequiredService<SileroVadModelManager>());
        services.AddSingleton<SileroPreferredSpeechRegionDetector>(sp => new(
            sp.GetRequiredService<SileroVadDetector>(),
            sp.GetRequiredService<TranscriptionSpeechRegionDetector>(),
            sp.GetRequiredService<ILogger<SileroPreferredSpeechRegionDetector>>()));
        services.AddSingleton<ISpeechRegionDetector>(sp => sp.GetRequiredService<SileroPreferredSpeechRegionDetector>());

        // Host起動時にVoxArchive所有のmodel transaction一時領域を1回だけ掃除する。
        // WPFからSilero/ReazonSpeech具象型を直接触らせず、Runtime Composition Root内で起動処理まで閉じる。
        services.AddHostedService<TranscriptionModelStartupCleanupService>();

        services.AddSingleton<TranscriptionEngineResultCanonicalizer>();
        services.AddSingleton<TranscriptionSpeakerLabelService>();
        services.AddSingleton<TranscriptionEngineResultValidator>();
        services.AddSingleton<TranscriptionDocumentStore>();
        services.AddSingleton<TranscriptionExportService>();
        services.AddSingleton<TranscriptionArtifactService>();
        services.AddSingleton<TranscriptionDiagnosticWriter>();

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
        services.AddSingleton<ReazonSpeechModelSelectionCapability>();
        services.AddSingleton<ReazonSpeechLanguageCapability>();
        services.AddSingleton<ReazonSpeechArtifactNamingCapability>();
        services.AddSingleton<ReazonSpeechAdvancedSettingsCapability>();
        services.AddSingleton<ITranscriptionEngineAdvancedSettingsCapability>(
            sp => sp.GetRequiredService<ReazonSpeechAdvancedSettingsCapability>());

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
                sp.GetRequiredService<ReazonSpeechArtifactNamingCapability>(),
                null,
                sp.GetRequiredService<ReazonSpeechModelSelectionCapability>()),
        ]));

        services.AddSingleton<TranscriptionModelManager>();
        services.AddSingleton<TranscriptionOrchestrator>();
        services.AddSingleton<TranscriptionJobAdmissionService>();
        services.AddSingleton<TranscriptionJobQueue>();
        services.AddSingleton<ITranscriptionEngineSettingsService, TranscriptionEngineSettingsService>();
        services.AddSingleton<ITranscriptionEngineAdvancedSettingsService, TranscriptionEngineAdvancedSettingsService>();
        services.AddSingleton<ITranscriptionApplicationService, TranscriptionApplicationService>();
        services.AddSingleton<ISpeechRegionDetectorModelApplicationService, SpeechRegionDetectorModelApplicationService>();
        return services;
    }
}
