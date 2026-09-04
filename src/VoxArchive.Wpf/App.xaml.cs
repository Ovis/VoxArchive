using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoxArchive.Application.Abstractions;
using VoxArchive.Audio;
using VoxArchive.Audio.Abstractions;
using VoxArchive.Infrastructure;
using VoxArchive.Runtime;
using ZLogger;

namespace VoxArchive.Wpf;

public partial class App : System.Windows.Application
{
    private const string AppMutexName = "VoxArchiveRunningMutex";
    private IHost? _host;
    private Mutex? _mutex;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, AppMutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown(0);
            return;
        }
        _ = OnStartupAsync();
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

                    // Whisper/ReazonSpeechの双方を同じアトミック配置・検証基盤へ接続する。
                    // HttpClientとInstallerはアプリケーション寿命で共有し、Windowを閉じても進行中取得の所有権を失わないようにする。
                    services.AddSingleton<HttpClient>();
                    services.AddSingleton(sp =>
                    {
                        var installer = new TranscriptionModelPackageInstaller(sp.GetRequiredService<HttpClient>());
                        var logger = sp.GetRequiredService<ILogger<TranscriptionModelPackageInstaller>>();
                        installer.CleanupFailureHandler = (path, ex) =>
                            logger.LogWarning(ex, "Failed to clean transcription model staging directory. Path={Path}", path);
                        return installer;
                    });
                    services.AddSingleton<WhisperModelStore>(sp => new WhisperModelStore(sp.GetRequiredService<TranscriptionModelPackageInstaller>()));
                    services.AddSingleton<ITranscriptionModelProvider>(sp => sp.GetRequiredService<WhisperModelStore>());
                    services.AddSingleton<ReazonSpeechModelProvider>(sp => new ReazonSpeechModelProvider(
                        sp.GetRequiredService<TranscriptionModelPackageInstaller>(),
                        ReazonSpeechModelCatalog.All));
                    services.AddSingleton<ITranscriptionModelProvider>(sp => sp.GetRequiredService<ReazonSpeechModelProvider>());
                    services.AddSingleton<TranscriptionModelProviderResolver>();
                    services.AddSingleton<TranscriptionModelUsageTracker>();
                    services.AddSingleton<TranscriptionModelManager>();
                    services.AddSingleton<TranscriptionModelRequirementService>();

                    // 文字起こしエンジン固有処理から共通処理を分離し、後続の複数エンジン対応でも同じ実装を共有する。
                    services.AddSingleton<TranscriptionAudioPreparationService>();
                    services.AddSingleton<TranscriptionSpeechRegionDetector>();
                    services.AddSingleton<TranscriptionSpeakerLabelService>();
                    services.AddSingleton<TranscriptionExportService>();
                    services.AddSingleton<TranscriptionDocumentStore>();
                    services.AddSingleton<TranscriptionResultDiscoveryService>();
                    services.AddSingleton<WhisperTranscriptionService>();

                    // QueueはEngine IDだけをResolverへ渡し、Whisper/ReazonSpeech固有実装を直接参照しない。
                    // 実行前のモデル保証も共通サービスへ委譲し、手動/自動の双方で同じ検証規則を適用する。
                    services.AddSingleton<WhisperTranscriptionEngine>();
                    services.AddSingleton<ReazonSpeechTranscriptionEngine>();
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
            if (_host is not null)
            {
                try { _host.Services.GetService<ILogger<App>>()?.LogCritical(ex, "Application startup failed."); }
                catch { Debug.WriteLine($"[App] Failed to log startup exception: {ex}"); }
            }
            Shutdown(-1);
        }
    }

    private static async Task<RecordingRuntimeContext> EnsureStartupFfmpegPathAsync(RecordingRuntimeContext context, ILogger<App> logger)
    {
        if (!string.IsNullOrWhiteSpace(context.DefaultOptions.FfmpegExecutablePath)) return context;
        if (!FfmpegRuntimeChecker.IsAvailable(string.Empty, out _, out var resolvedPath)) return context;
        if (string.IsNullOrWhiteSpace(resolvedPath) || !Path.IsPathFullyQualified(resolvedPath)) return context;
        var updatedOptions = context.DefaultOptions with { FfmpegExecutablePath = resolvedPath };
        try
        {
            await context.SettingsService.SaveRecordingOptionsAsync(updatedOptions);
            logger.LogInformation("起動時に ffmpeg パスを自動保存しました: {Path}", resolvedPath);
            return context with { DefaultOptions = updatedOptions };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "起動時の ffmpeg パス自動保存に失敗しました。検出値={Path}", resolvedPath);
            return context;
        }
    }

    private static string BuildFfmpegMissingMessage(string detail)
    {
        var baseMessage = "ffmpeg が見つかりません。録音機能は利用できません。" + Environment.NewLine
            + "ffmpeg をインストールして PATH を通した後に再起動してください。" + Environment.NewLine + Environment.NewLine
            + "インストール例: winget install Gyan.FFmpeg";
        return string.IsNullOrWhiteSpace(detail) ? baseMessage : baseMessage + Environment.NewLine + Environment.NewLine + "詳細: " + detail;
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                // 正常終了経路ではモデル取得を途中で強制終了せず、CancellationTokenが伝播して
                // Atomic Installerのstaging掃除が完了するところまで待つ。掃除失敗はInstaller側でbest effortとして扱う。
                _host.Services.GetService<TranscriptionModelManager>()?.CancelActiveDownloadAndWaitAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _host.Services.GetService<ILogger<App>>()?.LogWarning(ex, "Model download cancellation threw during shutdown.");
            }

            try { _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                _host.Services.GetService<ILogger<App>>()?.LogWarning(ex, "Host stop threw an exception during shutdown.");
                Debug.WriteLine("[App] Host stop threw an exception during shutdown.");
            }
            _host.Dispose();
            _host = null;
        }
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _mutex = null;
        base.OnExit(e);
    }
}
