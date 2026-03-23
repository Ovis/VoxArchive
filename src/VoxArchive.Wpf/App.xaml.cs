using System.Diagnostics;
using System.IO;
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
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoxArchive");
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
                        options.FilePathSelector = (timestamp, sequenceNumber) =>
                            Path.Combine(logsDir, $"app-{timestamp.ToLocalTime():yyyyMMdd}-{sequenceNumber:000}.log");
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
                    services.AddSingleton<WhisperTranscriptionService>();
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
                ModernDialog.Show(
                    window,
                    BuildFfmpegMissingMessage(ffmpegDetail),
                    "ffmpeg 未検出",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            if (_host is not null)
            {
                try
                {
                    var logger = _host.Services.GetService<ILogger<App>>();
                    logger?.LogCritical(ex, "Application startup failed.");
                }
                catch
                {
                    Debug.WriteLine($"[App] Failed to log startup exception: {ex}");
                }
            }

            Shutdown(-1);
        }
    }


    private static async Task<RecordingRuntimeContext> EnsureStartupFfmpegPathAsync(RecordingRuntimeContext context, ILogger<App> logger)
    {
        if (!string.IsNullOrWhiteSpace(context.DefaultOptions.FfmpegExecutablePath))
        {
            return context;
        }

        if (!FfmpegRuntimeChecker.IsAvailable(string.Empty, out _, out var resolvedPath))
        {
            return context;
        }

        if (string.IsNullOrWhiteSpace(resolvedPath) || !Path.IsPathFullyQualified(resolvedPath))
        {
            return context;
        }

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
        var baseMessage =
            "ffmpeg が見つかりません。録音機能は利用できません。" + Environment.NewLine +
            "ffmpeg をインストールして PATH を通した後に再起動してください。" + Environment.NewLine +
            Environment.NewLine +
            "インストール例: winget install Gyan.FFmpeg";

        if (string.IsNullOrWhiteSpace(detail))
        {
            return baseMessage;
        }

        return baseMessage + Environment.NewLine + Environment.NewLine + "詳細: " + detail;
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                var logger = _host.Services.GetService<ILogger<App>>();
                logger?.LogWarning(ex, "Host stop threw an exception during shutdown.");
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


