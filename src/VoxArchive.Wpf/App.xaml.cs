using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoxArchive.Application.Abstractions;
using VoxArchive.Audio.Abstractions;
using VoxArchive.Audio.NAudio;
using VoxArchive.Encoding.Abstractions;
using VoxArchive.Encoding.Ffmpeg;
using VoxArchive.Infrastructure;
using VoxArchive.Runtime;
using ZLogger;

namespace VoxArchive.Wpf;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        _ = OnStartupAsync();
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private async Task OnStartupAsync()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxArchive");
        Directory.CreateDirectory(appData);
        var logsDir = Path.Combine(appData, "logs");
        Directory.CreateDirectory(logsDir);
        var settingsPath = Path.Combine(appData, "settings.json");

        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.SetMinimumLevel(LogLevel.Information);
                    builder.AddZLoggerRollingFile(options =>
                    {
                        options.FilePathSelector = (timestamp, sequenceNumber) => Path.Combine(logsDir, $"app-{timestamp.ToLocalTime():yyyyMMdd}-{sequenceNumber:000}.log");
                        options.RollingSizeKB = 1024;
                    });
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ISettingsService>(_ => new JsonSettingsService(settingsPath));
                    services.AddSingleton<IDeviceService, WasapiDeviceService>();
                    services.AddSingleton<IProcessCatalogService, ProcessCatalogService>();
                    services.AddSingleton<ISpeakerCaptureService>(_ => NAudioRuntimeSupport.CreateSpeakerCaptureService());
                    services.AddSingleton<IMicCaptureService>(_ => NAudioRuntimeSupport.CreateMicCaptureService());
                    services.AddSingleton<IProcessLoopbackCaptureService, ProcessLoopbackCaptureService>();
                    services.AddSingleton<IRecordingServiceFactory, RecordingServiceFactory>();
                    services.AddSingleton<LocalRecordingBootstrapper>();
                    services.AddSingleton<RecordingRuntimeContextHolder>();
                    services.AddTransient<MainViewModel>(sp =>
                    {
                        var holder = sp.GetRequiredService<RecordingRuntimeContextHolder>();
                        var context = holder.Context ?? throw new InvalidOperationException("Recording runtime context is not initialized.");
                        return ActivatorUtilities.CreateInstance<MainViewModel>(sp, context);
                    });

                    services.AddSingleton(new RecordingCatalogService(Path.Combine(appData, "library.json")));
                    services.AddSingleton<WhisperModelStore>();
                    // Concrete型を既存Whisper UI/Service向けに残しつつ、同じSingletonを共通Providerとして公開する。
                    // ReazonSpeech追加時はITranscriptionModelProviderを追加登録するだけでResolverから選択できる。
                    services.AddSingleton<ITranscriptionModelProvider>(sp => sp.GetRequiredService<WhisperModelStore>());
                    services.AddSingleton<TranscriptionModelProviderResolver>();
                    // 文字起こしエンジン固有処理から共通処理を分離し、後続の複数エンジン対応でも同じ実装を共有する。
                    services.AddSingleton<TranscriptionAudioPreparationService>();
                    services.AddSingleton<TranscriptionSpeechRegionDetector>();
                    services.AddSingleton<TranscriptionSpeakerLabelService>();
                    services.AddSingleton<TranscriptionExportService>();
                    services.AddSingleton<TranscriptionDocumentStore>();
                    services.AddSingleton<TranscriptionResultDiscoveryService>();
                    services.AddSingleton<WhisperTranscriptionService>();

                    // QueueからWhisperへの直接依存を外し、エンジン選択をResolver/Orchestratorへ集約する。
                    // 現在はWhisperだけを登録し、認識挙動を変えずに後続のReazonSpeech追加点を用意する。
                    services.AddSingleton<WhisperTranscriptionEngine>();
                    services.AddSingleton<ITranscriptionEngineResolver, TranscriptionEngineResolver>();
                    services.AddSingleton<TranscriptionOrchestrator>();
                    services.AddSingleton<TranscriptionJobQueue>();
                    services.AddTransient<IRecordingPlaybackService, RecordingPlaybackService>();
                    services.AddTransient<MainWindow>();
                })
                .Build();

            await _host.StartAsync();
            var bootstrapper = _host.Services.GetRequiredService<LocalRecordingBootstrapper>();
            var context = await bootstrapper.InitializeAsync();
            var holder = _host.Services.GetRequiredService<RecordingRuntimeContextHolder>();
            holder.Context = context;
            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Application startup completed.");
            var window = _host.Services.GetRequiredService<MainWindow>();
            window.DataContext = _host.Services.GetRequiredService<MainViewModel>();
            window.Show();

            if (!FfmpegRuntimeChecker.IsAvailable(context.DefaultOptions.FfmpegExecutablePath, out var ffmpegDetail))
            {
                logger.LogWarning("ffmpeg is not available at startup. detail={Detail}", ffmpegDetail);
                ModernDialog.Show(window, BuildFfmpegMissingMessage(ffmpegDetail), "ffmpeg 未検出", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"VoxArchive の起動に失敗しました。\n{ex.Message}",
                "VoxArchive",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static string BuildFfmpegMissingMessage(string detail)
    {
        var baseMessage =
            "ffmpeg が見つかりません。録音を開始できません。" + Environment.NewLine +
            "ffmpeg をインストールして PATH を通した後に再試行してください。" + Environment.NewLine +
            Environment.NewLine +
            "インストール例: winget install Gyan.FFmpeg";

        return string.IsNullOrWhiteSpace(detail)
            ? baseMessage
            : baseMessage + Environment.NewLine + Environment.NewLine + "詳細: " + detail;
    }
}
