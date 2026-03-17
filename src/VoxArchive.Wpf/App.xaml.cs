using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoxArchive.Application;
using VoxArchive.Application.Abstractions;
using VoxArchive.Audio;
using VoxArchive.Audio.Abstractions;
using VoxArchive.Infrastructure;
using VoxArchive.Runtime;
using ZLogger;

namespace VoxArchive.Wpf;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoxArchive");
        Directory.CreateDirectory(appData);

        var logsDir = Path.Combine(appData, "logs");
        Directory.CreateDirectory(logsDir);

        var settingsPath = Path.Combine(appData, "settings.json");
        var appErrorLogPath = Path.Combine(logsDir, "app-errors.log");

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
                    services.AddSingleton<ISpeakerCaptureService>(_ => NaudioRuntimeSupport.CreateSpeakerCaptureService());
                    services.AddSingleton<IMicCaptureService>(_ => NaudioRuntimeSupport.CreateMicCaptureService());
                    services.AddSingleton<IProcessLoopbackCaptureService, ProcessLoopbackCaptureService>();
                    services.AddSingleton<LocalRecordingBootstrapper>();

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

            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Application startup completed.");

            var window = _host.Services.GetRequiredService<MainWindow>();
            window.DataContext = ActivatorUtilities.CreateInstance<MainViewModel>(_host.Services, context);
            window.Show();
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    appErrorLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [Fatal] [App] 起動失敗: {ex}" + Environment.NewLine);
            }
            catch
            {
            }

            Shutdown(-1);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            catch
            {
            }

            _host.Dispose();
            _host = null;
        }

        base.OnExit(e);
    }
}
